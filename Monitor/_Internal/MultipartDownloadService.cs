using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Monitor._Internal
{
  // Private per-Monitor spool, not in wwwroot or _data. One active export
  // avoids simultaneous multi-gigabyte staging on the same instance.
  // Each finished part remains immutable for exact retry/range requests.
  public sealed class MultipartDownloadService : BackgroundService
  {
    public const int IdleMinutes = 30; // matches Monitor session idle policy
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private readonly string _root;
    private readonly ILogger<MultipartDownloadService> _log;
    private MultipartZipBatch _batch;
    private string _owner;
    private FileStream _lease;

    public MultipartDownloadService(string monitorBasePath, ILogger<MultipartDownloadService> log)
    {
      _root = Path.Combine(monitorBasePath, "_download_exports");
      _log = log;
    }

    private void EnsureRoot()
    {
      if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_root);
      else Directory.CreateDirectory(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
      if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
        throw new IOException("Multipart export spool is a link.");
    }

    private bool Owns(string owner, string exportId) =>
      _batch != null && String.Equals(_owner, owner, StringComparison.Ordinal) &&
      String.Equals(_batch.ExportId, exportId, StringComparison.Ordinal);

    private void RequireOwner(string owner, string exportId)
    {
      if (!Owns(owner, exportId)) throw new KeyNotFoundException("Export unavailable or expired.");
      _batch.LastActivityUtc = DateTime.UtcNow;
    }

    private void Release()
    {
      MultipartZipBatch batch = _batch;
      _batch = null;
      _owner = null;
      try { _lease?.Dispose(); }
      finally { _lease = null; }
      if (batch != null)
      {
        try { batch.Delete(); }
        catch (IOException ex) { _log.LogWarning(ex, "Multipart spool cleanup deferred."); }
        catch (UnauthorizedAccessException ex) { _log.LogWarning(ex, "Multipart spool cleanup deferred."); }
      }
    }

    private void Expire()
    {
      if (_batch != null && DateTime.UtcNow - _batch.LastActivityUtc >= TimeSpan.FromMinutes(IdleMinutes))
        Release();
    }

    public async Task<MultipartZipBatch.State> StartAsync(string owner, string prefix,
      IReadOnlyList<StreamingDownloadArchive.Member> files, long maxBytes, CancellationToken token)
    {
      await _gate.WaitAsync(token);
      try
      {
        Expire();
        if (_batch != null) throw new InvalidOperationException("Release the active multipart export first.");
        EnsureRoot();
        string id = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(_root, id);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        FileStream lease = null;
        try
        {
          lease = new FileStream(Path.Combine(directory, ".lease"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None);
          // Filename ID is shared by every ZIP, even if start times collide.
          string fullPrefix = prefix + "-" + id.Substring(0, 12);
          var batch = new MultipartZipBatch(directory, fullPrefix, id, files, maxBytes);
          _lease = lease;
          _batch = batch;
          _owner = owner;
          return batch.Snapshot();
        }
        catch
        {
          lease?.Dispose();
          try { Directory.Delete(directory, true); } catch { }
          throw;
        }
      }
      finally { _gate.Release(); }
    }

    public async Task<MultipartZipBatch.State> StatusAsync(string owner, CancellationToken token)
    {
      await _gate.WaitAsync(token);
      try
      {
        Expire();
        if (_batch == null || !String.Equals(owner, _owner, StringComparison.Ordinal)) return null;
        _batch.LastActivityUtc = DateTime.UtcNow;
        return _batch.Snapshot();
      }
      finally { _gate.Release(); }
    }

    public async Task<MultipartZipBatch.State> PrepareAsync(string owner, string exportId, CancellationToken token)
    {
      await _gate.WaitAsync(token);
      try
      {
        Expire();
        RequireOwner(owner, exportId);
        try
        {
          return await Task.Factory.StartNew(() => _batch.PrepareNext(token), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        finally { _batch.LastActivityUtc = DateTime.UtcNow; }
      }
      finally { _gate.Release(); }
    }

    public async Task<(FileStream Stream, MultipartZipBatch.Part Part)> OpenAsync(
      string owner, string exportId, int number, CancellationToken token)
    {
      await _gate.WaitAsync(token);
      try
      {
        Expire();
        RequireOwner(owner, exportId);
        return _batch.OpenPart(number);
      }
      finally { _gate.Release(); }
    }

    public async Task ReleaseAsync(string owner, string exportId, CancellationToken token)
    {
      await _gate.WaitAsync(token);
      try
      {
        Expire();
        RequireOwner(owner, exportId);
        Release();
      }
      finally { _gate.Release(); }
    }

    // Clean abandoned private directories after restart/crash. Only known
    // GUID folders older than the idle interval are considered, never a
    // currently leased batch (including a separate Monitor process).
    private void CleanAbandoned()
    {
      if (!Directory.Exists(_root) || (File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0) return;
      foreach (string directory in Directory.EnumerateDirectories(_root))
      {
        if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) ||
            (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
            (_batch != null && Path.GetFileName(directory) == _batch.ExportId) ||
            DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) < TimeSpan.FromMinutes(IdleMinutes))
          continue;
        try
        {
          string path = Path.Combine(directory, ".lease");
          if (File.Exists(path))
          {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            using (var lease = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
          }
          Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { /* Active lease or a concurrently removed directory. */ }
        catch (UnauthorizedAccessException ex) { _log.LogWarning(ex, "Multipart spool cleanup deferred."); }
      }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
      try
      {
        while (!stoppingToken.IsCancellationRequested)
        {
          await _gate.WaitAsync(stoppingToken);
          try
          {
            Expire();
            CleanAbandoned();
          }
          catch (Exception ex) { _log.LogWarning(ex, "Multipart spool cleanup failed."); }
          finally { _gate.Release(); }
          await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
      await base.StopAsync(cancellationToken);
      await _gate.WaitAsync(cancellationToken);
      try { Release(); }
      finally { _gate.Release(); }
    }
  }
}
