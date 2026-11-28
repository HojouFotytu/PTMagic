using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Monitor._Internal
{
  // Private per-Monitor spool, not in wwwroot or _data. One active
  // preparation cursor; finished parts remain available for browser retries
  // even after a later export starts. Automatic idle cleanup needs no UI.
  public sealed class MultipartDownloadService : BackgroundService
  {
    public const int IdleMinutes = 30; // matches Monitor session idle policy
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private readonly string _root;
    private readonly ILogger<MultipartDownloadService> _log;
    private sealed record RetainedExport(string Owner, MultipartZipBatch Batch, FileStream Lease)
    {
      public Dictionary<int, MultipartTransferReceipt> Transfers { get; } = new Dictionary<int, MultipartTransferReceipt>();
    }
    private readonly Dictionary<string, RetainedExport> _exports = new Dictionary<string, RetainedExport>(StringComparer.Ordinal);
    private RetainedExport _active;

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

    private RetainedExport RequireOwner(string owner, string exportId)
    {
      if (String.IsNullOrEmpty(exportId) || !_exports.TryGetValue(exportId, out var export) ||
          !String.Equals(export.Owner, owner, StringComparison.Ordinal))
        throw new KeyNotFoundException("Export unavailable or expired.");
      export.Batch.LastActivityUtc = DateTime.UtcNow;
      return export;
    }

    private void Release(RetainedExport export)
    {
      _exports.Remove(export.Batch.ExportId);
      if (ReferenceEquals(_active, export)) _active = null;
      export.Lease.Dispose();
      try { export.Batch.Delete(); }
      catch (IOException ex) { _log.LogWarning(ex, "Multipart spool cleanup deferred."); }
      catch (UnauthorizedAccessException ex) { _log.LogWarning(ex, "Multipart spool cleanup deferred."); }
    }

    private void Expire()
    {
      foreach (var export in new List<RetainedExport>(_exports.Values))
        if (DateTime.UtcNow - export.Batch.LastActivityUtc >= TimeSpan.FromMinutes(IdleMinutes) &&
            !export.Transfers.Values.Any(transfer => transfer.IsSending))
          Release(export);
    }

    public async Task<MultipartZipBatch.State> StartAsync(string owner, string prefix,
      IReadOnlyList<StreamingDownloadArchive.Member> files, long maxBytes, CancellationToken token,
      MultipartExportSelection selection = null)
    {
      await _gate.WaitAsync(token);
      try
      {
        Expire();
        if (_active != null && !_active.Batch.Snapshot().Complete &&
            !String.Equals(_active.Owner, owner, StringComparison.Ordinal))
          throw new InvalidOperationException("Another session has an active multipart export.");
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
          var batch = new MultipartZipBatch(directory, fullPrefix, id, files, maxBytes, selection);
          var previous = _active;
          var export = new RetainedExport(owner, batch, lease);
          _exports.Add(id, export);
          _active = export;
          // An explicit new request supersedes the old preparation cursor.
          // Never remove already published parts merely because another
          // export starts: the browser may still be requesting them.
          if (previous != null && previous.Batch.Snapshot().Parts.Length == 0)
            Release(previous);
          _log.LogInformation("Multipart export {ExportId}: {Kind}, range {Range}, from {FromUtc} through {ThroughUtc}, maximum {MaxBytes} bytes, {Sources} sources.",
            id, selection?.Kind, selection?.HistoryRange, selection?.FromUtc, selection?.ThroughUtc, maxBytes, files.Count);
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
        if (_active == null || !String.Equals(owner, _active.Owner, StringComparison.Ordinal)) return null;
        _active.Batch.LastActivityUtc = DateTime.UtcNow;
        return _active.Batch.Snapshot();
      }
      finally { _gate.Release(); }
    }

    public async Task<MultipartZipBatch.State> PrepareAsync(string owner, string exportId, CancellationToken token)
    {
      await _gate.WaitAsync(token);
      try
      {
        Expire();
        var export = RequireOwner(owner, exportId);
        if (!ReferenceEquals(_active, export)) throw new KeyNotFoundException("Export superseded by a newer request.");
        try
        {
          var before = export.Batch.Snapshot();
          var state = await Task.Factory.StartNew(() => export.Batch.PrepareNext(token), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
          if (!before.Complete)
            _log.LogInformation("Multipart export {ExportId}: {Processed}/{Total} sources, {Parts} ZIPs, {Bytes} bytes prepared, complete={Complete}.",
              state.ExportId, state.ProcessedSources, state.TotalSources, state.Parts.Length,
              state.Parts.Sum(part => part.Bytes), state.Complete);
          return state;
        }
        finally { export.Batch.LastActivityUtc = DateTime.UtcNow; }
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
        return RequireOwner(owner, exportId).Batch.OpenPart(number);
      }
      finally { _gate.Release(); }
    }

    public async Task<(FileStream Stream, MultipartZipBatch.Part Part, MultipartTransferReceipt Receipt)> OpenTrackedAsync(
      string owner, string exportId, int number, CancellationToken token)
    {
      await _gate.WaitAsync(token);
      try
      {
        Expire();
        var export = RequireOwner(owner, exportId);
        var opened = export.Batch.OpenPart(number);
        try
        {
          // A new full request has its own receipt. Concurrent browser retry
          // requests are never allowed to replace an in-flight tracked receipt.
          if (export.Transfers.TryGetValue(number, out var previous) && previous.IsSending)
            throw new InvalidOperationException("This ZIP part is already being sent.");
          var receipt = new MultipartTransferReceipt(opened.Part.Bytes);
          export.Transfers[number] = receipt;
          return (opened.Stream, opened.Part, receipt);
        }
        catch
        {
          opened.Stream.Dispose();
          throw;
        }
      }
      finally { _gate.Release(); }
    }

    public async Task<MultipartTransferReceipt.State> TransferStatusAsync(string owner, string exportId,
      int number, CancellationToken token)
    {
      await _gate.WaitAsync(token);
      try
      {
        Expire();
        var export = RequireOwner(owner, exportId);
        var state = export.Batch.Snapshot();
        if (number < 1 || number > state.Parts.Length) throw new KeyNotFoundException("Part not prepared.");
        return export.Transfers.TryGetValue(number, out var receipt)
          ? receipt.Snapshot()
          : new MultipartTransferReceipt.State("waiting", 0, state.Parts[number - 1].Bytes);
      }
      finally { _gate.Release(); }
    }

    public async Task ReleaseAsync(string owner, string exportId, CancellationToken token)
    {
      await _gate.WaitAsync(token);
      try
      {
        Expire();
        Release(RequireOwner(owner, exportId));
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
            _exports.ContainsKey(Path.GetFileName(directory)) ||
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
      try
      {
        foreach (var export in new List<RetainedExport>(_exports.Values)) Release(export);
      }
      finally { _gate.Release(); }
    }
  }
}
