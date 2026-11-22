using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Monitor.Pages
{
  public class LogsModel : _Internal.BasePageModelSecure
  {
    private const int MaxChunkBytes = 32768;
    private const int InitialTailBytes = 32768;
    public bool LogsEnabled => PTMagicConfiguration?.GeneralSettings?.Monitor?.IsPasswordProtected == true;

    public IActionResult OnGet()
    {
      base.Init();
      if (!IsLoggedIn(HttpContext)) return RedirectToPage("/Login");
      return Page();
    }

    // Download complete source files, independently of the filtered/bounded log
    // viewer. Keep the ZIP on a temporary disk stream to avoid buffering all
    // log history in memory; ASP.NET disposes the stream after the download.
    public async Task<IActionResult> OnGetDownloadAsync(CancellationToken cancellationToken)
    {
      base.Init();
      if (!IsLoggedIn(HttpContext) || !LogsEnabled)
        return StatusCode(StatusCodes.Status403Forbidden);

      string analyzerFolder = Path.Combine(PTMagicBasePath, "_logs");
      string monitorFolder = Path.Combine(PTMagicMonitorBasePath, "_logs");
      if (!Directory.Exists(monitorFolder))
        monitorFolder = Path.Combine(AppContext.BaseDirectory, "_logs");

      var folders = new[]
      {
        (Path: analyzerFolder, Prefix: "PTMagic/"),
        (Path: monitorFolder, Prefix: "Monitor/")
      };
      var files = folders
        .Where(folder => Directory.Exists(folder.Path))
        .SelectMany(folder => Directory.EnumerateFiles(folder.Path, "*", SearchOption.TopDirectoryOnly)
          // Logs stored as links must not expose files outside the log folder.
          .Where(path => (System.IO.File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
          .Select(path => (Path: path, Entry: folder.Prefix + Path.GetFileName(path))))
        .ToArray();

      if (files.Length == 0)
        return NotFound("No log files are available to download.");

      string temporaryPath = Path.Combine(Path.GetTempPath(),
        "ptmagic-logs-" + Guid.NewGuid().ToString("N") + ".zip");
      FileStream archiveStream = new FileStream(temporaryPath, new FileStreamOptions
      {
        Mode = FileMode.CreateNew,
        Access = FileAccess.ReadWrite,
        Share = FileShare.None,
        BufferSize = 81920,
        Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose
      });

      try
      {
        int archivedFiles = 0;
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
        {
          foreach (var file in files)
          {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
              // Allow active NLog writers and daily rotation to continue.
              using var logStream = new FileStream(file.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
              ZipArchiveEntry entry = archive.CreateEntry(file.Entry, CompressionLevel.Fastest);
              using Stream entryStream = entry.Open();
              await logStream.CopyToAsync(entryStream, 81920, cancellationToken);
              archivedFiles++;
            }
            catch (FileNotFoundException)
            {
              // A dated log can rotate between discovery and opening.
            }
          }
        }

        if (archivedFiles == 0)
        {
          await archiveStream.DisposeAsync();
          return NotFound("No log files are available to download.");
        }

        archiveStream.Position = 0;
        // Use the Analyzer's configured instance name, matching PT Data ZIP naming.
        string instanceName = PTMagicConfiguration.GeneralSettings.Application.InstanceName ?? "";
        string safeInstanceName = System.Text.RegularExpressions.Regex
          .Replace(instanceName.Trim(), @"[^a-zA-Z0-9_-]+", "-")
          .Trim('-', '_');
        if (safeInstanceName.Length > 64) safeInstanceName = safeInstanceName.Substring(0, 64).TrimEnd('-', '_');
        if (String.IsNullOrEmpty(safeInstanceName)) safeInstanceName = "Instance";
        string fileName = "PTMagic-Logs-" + safeInstanceName + "-" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ssZ") + ".zip";
        return File(archiveStream, "application/zip", fileName);
      }
      catch
      {
        await archiveStream.DisposeAsync();
        throw;
      }
    }


    // Download the complete on-disk _data tree for this PTMagic instance.
    // Keep the archive disk-backed, preserve subfolders, and never follow links.
    public async Task<IActionResult> OnGetDownloadDataAsync(CancellationToken cancellationToken)
    {
      base.Init();
      if (!IsLoggedIn(HttpContext) || !LogsEnabled)
        return StatusCode(StatusCodes.Status403Forbidden);

      string dataFolder = Path.Combine(PTMagicBasePath, Core.Main.Constants.PTMagicPathData);
      if (!Directory.Exists(dataFolder)
        || (System.IO.File.GetAttributes(dataFolder) & FileAttributes.ReparsePoint) != 0)
        return NotFound("The PTMagic _data folder is unavailable.");

      string temporaryPath = Path.Combine(Path.GetTempPath(),
        "ptmagic-data-" + Guid.NewGuid().ToString("N") + ".zip");
      FileStream archiveStream = new FileStream(temporaryPath, new FileStreamOptions
      {
        Mode = FileMode.CreateNew,
        Access = FileAccess.ReadWrite,
        Share = FileShare.None,
        BufferSize = 81920,
        Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose
      });

      try
      {
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
        {
          archive.CreateEntry("_data/");
          var pendingFolders = new Stack<string>();
          pendingFolders.Push(dataFolder);
          while (pendingFolders.Count > 0)
          {
            cancellationToken.ThrowIfCancellationRequested();
            string folder = pendingFolders.Pop();
            foreach (string path in Directory.EnumerateFileSystemEntries(folder))
            {
              cancellationToken.ThrowIfCancellationRequested();
              FileAttributes attributes;
              try
              {
                attributes = System.IO.File.GetAttributes(path);
              }
              catch (FileNotFoundException) { continue; }
              catch (DirectoryNotFoundException) { continue; }

              // Exclude symbolic links and junctions, including linked directories.
              if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
              string entryName = "_data/" + Path.GetRelativePath(dataFolder, path).Replace('\\', '/');
              if ((attributes & FileAttributes.Directory) != 0)
              {
                archive.CreateEntry(entryName.TrimEnd('/') + "/");
                pendingFolders.Push(path);
                continue;
              }

              try
              {
                // Opening before CreateEntry avoids an empty member if a file vanishes.
                using var dataStream = new FileStream(path, FileMode.Open, FileAccess.Read,
                  FileShare.ReadWrite | FileShare.Delete, 81920,
                  FileOptions.Asynchronous | FileOptions.SequentialScan);
                ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                using Stream entryStream = entry.Open();
                await dataStream.CopyToAsync(entryStream, 81920, cancellationToken);
              }
              catch (FileNotFoundException) { /* Data file removed during enumeration. */ }
              catch (DirectoryNotFoundException) { /* Parent removed during enumeration. */ }
            }
          }
        }

        archiveStream.Position = 0;
        string instanceName = PTMagicConfiguration.GeneralSettings.Application.InstanceName ?? "";
        string safeInstanceName = System.Text.RegularExpressions.Regex
          .Replace(instanceName.Trim(), @"[^a-zA-Z0-9_-]+", "-")
          .Trim('-', '_');
        if (safeInstanceName.Length > 64)
          safeInstanceName = safeInstanceName.Substring(0, 64).TrimEnd('-', '_');
        if (String.IsNullOrEmpty(safeInstanceName)) safeInstanceName = "Instance";
        string fileName = "PTMagic-_data-" + safeInstanceName + "-"
          + DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ssZ") + ".zip";
        return File(archiveStream, "application/zip", fileName);
      }
      catch
      {
        await archiveStream.DisposeAsync();
        throw;
      }
    }

    // A single authenticated SSE connection follows one log file. The filesystem watcher
    // signals changes, while the occasional timeout recovers missed notifications.
    public async Task<IActionResult> OnGetStreamAsync(string source, string cursor = "", long offset = -1)
    {
      base.Init();
      if (!IsLoggedIn(HttpContext) || !LogsEnabled)
      {
        return StatusCode(StatusCodes.Status403Forbidden);
      }
      if (source != "analyzer" && source != "monitor")
      {
        return BadRequest();
      }

      Response.ContentType = "text/event-stream";
      Response.Headers["Cache-Control"] = "no-cache, no-transform";
      Response.Headers["X-Accel-Buffering"] = "no";
      Response.Headers["Content-Encoding"] = "identity";

      string basePath = source == "analyzer" ? PTMagicBasePath : PTMagicMonitorBasePath;
      string folder = Path.Combine(basePath, "_logs");
      if (source == "monitor" && !Directory.Exists(folder))
        folder = Path.Combine(AppContext.BaseDirectory, "_logs");

      using var signal = new SemaphoreSlim(0, 1);
      FileSystemWatcher watcher = null;
      string activeCursor = cursor ?? "";
      long nextOffset = offset;
      var cancellation = HttpContext.RequestAborted;

      void Wake()
      {
        try { if (signal.CurrentCount == 0) signal.Release(); }
        catch (ObjectDisposedException) { }
        catch (SemaphoreFullException) { }
      }

      bool freshConnection = String.IsNullOrEmpty(activeCursor) && nextOffset < 0;
      bool initialAnalyzerSnapshot = source == "analyzer" && freshConnection;
      bool initialMonitorSnapshot = source == "monitor" && freshConnection;
      bool initialAnalyzerWithoutPreviousRaid = false;
      try
      {
        if (initialAnalyzerSnapshot)
        {
          string[] previousSummary = ReadPreviousRaidSummary(folder);
          initialAnalyzerWithoutPreviousRaid = previousSummary.Length == 0;
          await SendSnapshotAsync(previousSummary, cancellation);
        }
        if (initialMonitorSnapshot)
        {
          await SendMonitorSnapshotAsync(ReadMonitorHistory(folder), cancellation);
        }
        while (!cancellation.IsCancellationRequested)
        {
          if (watcher == null && Directory.Exists(folder))
          {
            watcher = new FileSystemWatcher(folder, "*.txt")
            {
              NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
              IncludeSubdirectories = false
            };
            watcher.Changed += (_, _) => Wake();
            watcher.Created += (_, _) => Wake();
            watcher.Renamed += (_, _) => Wake();
            watcher.Deleted += (_, _) => Wake();
            watcher.Error += (_, _) => Wake();
            watcher.EnableRaisingEvents = true;
          }

          try
          {
            string path = Directory.Exists(folder)
              ? Directory.EnumerateFiles(folder, "????-??-??.txt", SearchOption.TopDirectoryOnly)
                  .Where(p => DateTime.TryParseExact(Path.GetFileNameWithoutExtension(p), "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _))
                  .OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal)
                  .FirstOrDefault()
              : null;

            if (path != null)
            {
              using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
              string fileCursor = Path.GetFileName(path) + ":" + System.IO.File.GetCreationTimeUtc(path).Ticks;
              bool reset = !String.Equals(activeCursor, fileCursor, StringComparison.Ordinal)
                || nextOffset < 0 || nextOffset > stream.Length;
              if (reset)
              {
                activeCursor = fileCursor;
                // With a previous completed raid, start at EOF as before.
                // Without one, retain the ongoing first startup and raid:
                // otherwise a viewer opened during the first raid sees nothing.
                nextOffset = initialAnalyzerSnapshot && initialAnalyzerWithoutPreviousRaid ? 0
                  : (initialAnalyzerSnapshot || initialMonitorSnapshot) ? stream.Length
                  : Math.Max(0, stream.Length - InitialTailBytes);
                initialAnalyzerSnapshot = false;
                initialMonitorSnapshot = false;
              }

              // Drain bounded chunks; do not read or serialize the entire log file.
              int batches = 0;
              do
              {
                long start = nextOffset;
                stream.Seek(start, SeekOrigin.Begin);
                int count = (int)Math.Min(MaxChunkBytes, stream.Length - start);
                if (count <= 0)
                {
                  if (reset)
                    await SendAsync(Array.Empty<string>(), true, activeCursor, nextOffset, cancellation);
                  break;
                }

                byte[] buffer = new byte[count];
                int read = 0;
                while (read < count)
                {
                  int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), cancellation);
                  if (n == 0) break;
                  read += n;
                }
                int lastNewline = read == 0 ? -1 : Array.LastIndexOf(buffer, (byte)'\n', read - 1, read);
                if (lastNewline < 0)
                {
                  // Extremely long/incomplete lines must not hold the stream at one offset.
                  if (read == MaxChunkBytes) nextOffset = start + read;
                  if (reset) await SendAsync(Array.Empty<string>(), true, activeCursor, nextOffset, cancellation);
                  break;
                }

                int first = 0;
                if (reset && start > 0)
                {
                  int firstNewline = Array.IndexOf(buffer, (byte)'\n', 0, lastNewline + 1);
                  first = firstNewline + 1;
                }
                string[] lines = Encoding.UTF8.GetString(buffer, first, lastNewline + 1 - first)
                  .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                  .Select(line => line.TrimEnd('\r')).ToArray();
                nextOffset = start + lastNewline + 1;
                await SendAsync(lines, reset, activeCursor, nextOffset, cancellation);
                reset = false;
                batches++;
              } while (batches < 8 && nextOffset < stream.Length && !cancellation.IsCancellationRequested);

              // If the file is growing faster than eight chunks, continue immediately.
              if (nextOffset < stream.Length && batches >= 8) Wake();
            }
            else if (!String.IsNullOrEmpty(activeCursor))
            {
              activeCursor = "";
              nextOffset = -1;
              await SendAsync(Array.Empty<string>(), true, "", -1, cancellation);
            }
          }
          catch (IOException) { /* Writer/rotation race: retry on next notification. */ }
          catch (UnauthorizedAccessException) { /* Retry on next recovery check. */ }

          // Watcher-first delivery. Five seconds is only a missed-event recovery check,
          // not the live refresh rate.
          bool notified = await signal.WaitAsync(TimeSpan.FromSeconds(5), cancellation);
          if (!notified)
          {
            await Response.WriteAsync(": keepalive\n\n", cancellation);
            await Response.Body.FlushAsync(cancellation);
          }
        }
      }
      catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
      catch (IOException) when (cancellation.IsCancellationRequested) { }
      finally
      {
        if (watcher != null) watcher.Dispose();
      }
      // Streaming already wrote the SSE response. Never let Razor Pages
      // attempt to render Logs.cshtml after its headers have started.
      return new EmptyResult();
    }

    private async Task SendSnapshotAsync(string[] lines, CancellationToken cancellation)
    {
      string data = JsonSerializer.Serialize(new { lines });
      await Response.WriteAsync("event: snapshot\ndata: " + data + "\n\n", cancellation);
      await Response.Body.FlushAsync(cancellation);
    }

    private sealed class MonitorHistory
    {
      public string[] recent { get; set; } = Array.Empty<string>();
      public string[] issues { get; set; } = Array.Empty<string>();
    }

    private static bool IsIssue(string line)
    {
      return line.Contains(" WARN - ", StringComparison.OrdinalIgnoreCase)
        || line.Contains(" ERROR - ", StringComparison.OrdinalIgnoreCase)
        || line.Contains(" FATAL - ", StringComparison.OrdinalIgnoreCase)
        || line.Contains(" CRITICAL - ", StringComparison.OrdinalIgnoreCase);
    }

    // Pull a bounded lookback of recent Monitor output and diagnostics; never
    // retain whole files or generate a large DOM from a quiet, long-running Monitor.
    private static MonitorHistory ReadMonitorHistory(string folder)
    {
      var recent = new System.Collections.Generic.Queue<string>();
      var issues = new System.Collections.Generic.Queue<string>();
      if (!Directory.Exists(folder)) return new MonitorHistory();
      string[] files = Directory.EnumerateFiles(folder, "????-??-??.txt", SearchOption.TopDirectoryOnly)
        .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal).TakeLast(7).ToArray();
      foreach (string path in files)
      {
        try
        {
          using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
          const int maxBytesPerFile = 2 * 1024 * 1024;
          int count = (int)Math.Min(stream.Length, maxBytesPerFile);
          long start = stream.Length - count;
          stream.Seek(start, SeekOrigin.Begin);
          byte[] data = new byte[count];
          int read = 0;
          while (read < count)
          {
            int n = stream.Read(data, read, count - read);
            if (n == 0) break;
            read += n;
          }
          string text = Encoding.UTF8.GetString(data, 0, read);
          if (start > 0)
          {
            int firstNewline = text.IndexOf('\n');
            text = firstNewline >= 0 ? text.Substring(firstNewline + 1) : "";
          }
          foreach (string raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
          {
            string line = raw.TrimEnd('\r');
            recent.Enqueue(line);
            if (recent.Count > 500) recent.Dequeue();
            if (IsIssue(line))
            {
              issues.Enqueue(line);
              if (issues.Count > 200) issues.Dequeue();
            }
          }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
      }
      return new MonitorHistory { recent = recent.ToArray(), issues = issues.ToArray() };
    }

    private async Task SendMonitorSnapshotAsync(MonitorHistory history, CancellationToken cancellation)
    {
      string data = JsonSerializer.Serialize(history);
      await Response.WriteAsync("event: monitor-snapshot\ndata: " + data + "\n\n", cancellation);
      await Response.Body.FlushAsync(cancellation);
    }

    // Only read a bounded portion of recent logs, and return solely a completed
    // RAID SUMMARY block, never historical raid details.
    private static string[] ReadPreviousRaidSummary(string folder)
    {
      if (!Directory.Exists(folder)) return Array.Empty<string>();
      string[] files = Directory.EnumerateFiles(folder, "????-??-??.txt", SearchOption.TopDirectoryOnly)
        .OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal)
        .Take(3).ToArray();
      foreach (string path in files)
      {
        try
        {
          using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
          const int maxBytes = 4 * 1024 * 1024;
          int count = (int)Math.Min(stream.Length, maxBytes);
          stream.Seek(-count, SeekOrigin.End);
          byte[] data = new byte[count];
          int read = 0;
          while (read < count)
          {
            int n = stream.Read(data, read, count - read);
            if (n == 0) break;
            read += n;
          }
          string text = Encoding.UTF8.GetString(data, 0, read);
          int end = text.LastIndexOf("DO NOT CLOSE THIS WINDOW! THIS IS THE BOT THAT ANALYZES TRENDS AND CHANGES SETTINGS!", StringComparison.Ordinal);
          if (end < 0) continue;
          int start = text.LastIndexOf("RAID SUMMARY", end, StringComparison.Ordinal);
          if (start < 0) continue;
          int lineStart = text.LastIndexOf('\n', start);
          lineStart = lineStart < 0 ? 0 : lineStart + 1;
          int lineEnd = text.IndexOf('\n', end);
          if (lineEnd < 0) lineEnd = text.Length;
          return text.Substring(lineStart, lineEnd - lineStart)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r')).ToArray();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
      }
      return Array.Empty<string>();
    }

    private async Task SendAsync(string[] lines, bool reset, string cursor, long offset,
      CancellationToken cancellation)
    {
      string data = JsonSerializer.Serialize(new { lines, reset, cursor, offset });
      await Response.WriteAsync("data: " + data + "\n\n", cancellation);
      await Response.Body.FlushAsync(cancellation);
    }
  }
}
