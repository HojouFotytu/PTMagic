using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    // Both endpoints use the same bounded, async streaming writer.
    public async Task<IActionResult> OnGetDownloadAsync(int historyCount, string historyUnit,
      CancellationToken cancellationToken, int maxZipMiB = 0)
    {
      base.Init();
      if (!IsLoggedIn(HttpContext) || !LogsEnabled)
        return StatusCode(StatusCodes.Status403Forbidden);
      if (!ModelState.IsValid || maxZipMiB != 0)
        return BadRequest("For a size-limited ZIP, use the download button on the Logs/Data page.");
      DateTime endUtc = DateTime.UtcNow;
      if (!TryDownloadPeriod(historyCount, historyUnit, endUtc, out DateTime? cutoffUtc))
        return BadRequest("Choose a positive whole number of days, weeks, or months.");

      var files = CaptureExportSources(false, cutoffUtc, endUtc, cancellationToken);
      if (files.Count == 0) return NotFound("No log files are available to download.");
      return await StreamDownloadAsync(files, "PTMagic-Logs-", endUtc, cancellationToken);
    }

    public async Task<IActionResult> OnGetDownloadDataAsync(int historyCount, string historyUnit,
      CancellationToken cancellationToken, int maxZipMiB = 0)
    {
      base.Init();
      if (!IsLoggedIn(HttpContext) || !LogsEnabled)
        return StatusCode(StatusCodes.Status403Forbidden);
      if (!ModelState.IsValid || maxZipMiB != 0)
        return BadRequest("For a size-limited ZIP, use the download button on the Logs/Data page.");
      DateTime endUtc = DateTime.UtcNow;
      if (!TryDownloadPeriod(historyCount, historyUnit, endUtc, out DateTime? cutoffUtc))
        return BadRequest("Choose a positive whole number of days, weeks, or months.");

      var files = CaptureExportSources(true, cutoffUtc, endUtc, cancellationToken);
      if (files.Count == 0) return NotFound("No PTMagic _data files are available to download.");
      return await StreamDownloadAsync(files, "PTMagic-_data-", endUtc, cancellationToken);
    }

    private List<_Internal.StreamingDownloadArchive.Member> CaptureExportSources(bool data,
      DateTime? cutoffUtc, DateTime endUtc, CancellationToken cancellationToken)
    {
      if (data)
        return _Internal.StreamingDownloadArchive.CaptureData(
          Path.Combine(PTMagicBasePath, Core.Main.Constants.PTMagicPathData),
          cutoffUtc, endUtc, cancellationToken);
      string analyzerFolder = Path.Combine(PTMagicBasePath, "_logs");
      string monitorFolder = Path.Combine(PTMagicMonitorBasePath, "_logs");
      if (!Directory.Exists(monitorFolder)) monitorFolder = Path.Combine(AppContext.BaseDirectory, "_logs");
      return _Internal.StreamingDownloadArchive.CaptureLogs(
        new[] { (analyzerFolder, "PTMagic/"), (monitorFolder, "Monitor/") },
        cutoffUtc, endUtc, cancellationToken);
    }

    private _Internal.MultipartDownloadService Multipart =>
      HttpContext.RequestServices.GetRequiredService<_Internal.MultipartDownloadService>();

    private string MultipartOwner => HttpContext.Session.Id + "|" + Path.GetFullPath(PTMagicBasePath);

    private IActionResult MultipartJson(object value) =>
      new JsonResult(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private bool MultipartAuthorized()
    {
      base.Init();
      Response.Headers["Cache-Control"] = "no-store, no-transform";
      return IsLoggedIn(HttpContext) && LogsEnabled;
    }

    public async Task<IActionResult> OnGetMultipartStatusAsync(CancellationToken cancellationToken)
    {
      if (!MultipartAuthorized()) return StatusCode(403);
      return MultipartJson(await Multipart.StatusAsync(MultipartOwner, cancellationToken));
    }

    public async Task<IActionResult> OnPostMultipartStartAsync([FromForm] string kind,
      [FromForm] int? historyCount, [FromForm] string historyUnit, [FromForm] int? maxZipMiB,
      CancellationToken cancellationToken)
    {
      if (!MultipartAuthorized()) return StatusCode(403);
      DateTime endUtc = DateTime.UtcNow;
      if (!ModelState.IsValid || !_Internal.MultipartExportSelection.TryCreate(kind, historyCount,
          historyUnit, maxZipMiB, endUtc, out var selection))
        return BadRequest("An explicit valid history period and positive whole-number maximum ZIP size in MiB are required.");
      try
      {
        bool data = kind == "data";
        var files = CaptureExportSources(data, selection.FromUtc, selection.ThroughUtc, cancellationToken);
        if (files.Count == 0) return NotFound("No source files are available for the selected period.");
        string prefix = ExportFileStem(data ? "PTMagic-_data-" : "PTMagic-Logs-", endUtc) + "-" + selection.FileTag;
        return MultipartJson(await Multipart.StartAsync(MultipartOwner, prefix, files, selection.MaxBytes, cancellationToken, selection));
      }
      catch (InvalidOperationException)
      {
        return StatusCode(409, "Another session has an active multipart export. Try again after it finishes or expires.");
      }
      catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
      {
        HttpContext.RequestServices.GetService<ILogger<LogsModel>>()?.LogWarning(ex, "Multipart export could not start.");
        return StatusCode(500, "Could not prepare export storage or read the source directory.");
      }
    }

    public async Task<IActionResult> OnPostMultipartNextAsync(string exportId, CancellationToken cancellationToken)
    {
      if (!MultipartAuthorized()) return StatusCode(403);
      try { return MultipartJson(await Multipart.PrepareAsync(MultipartOwner, exportId, cancellationToken)); }
      catch (KeyNotFoundException) { return NotFound("Export unavailable or expired."); }
      catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
      {
        HttpContext.RequestServices.GetService<ILogger<LogsModel>>()?.LogWarning(ex, "Multipart ZIP preparation failed.");
        return StatusCode(500, "ZIP preparation failed. Check server disk space and source files; previously prepared downloads remain available for browser retries. Click the download button to start a new export.");
      }
    }

    public async Task<IActionResult> OnGetMultipartTransferStatusAsync(string exportId, int number,
      CancellationToken cancellationToken)
    {
      if (!MultipartAuthorized()) return StatusCode(403);
      try
      {
        return MultipartJson(await Multipart.TransferStatusAsync(MultipartOwner, exportId, number, cancellationToken));
      }
      catch (KeyNotFoundException) { return NotFound("Part unavailable or expired."); }
    }

    public async Task<IActionResult> OnGetMultipartPartAsync(string exportId, int number,
      CancellationToken cancellationToken, bool tracked = false)
    {
      if (!MultipartAuthorized()) return StatusCode(403);
      try
      {
        // Browser range retries and existing manual links retain the standard
        // range-enabled file result. Only the initial full automatic request
        // is used to advance the single-click download sequence.
        if (!tracked || Request.Headers.ContainsKey("Range"))
        {
          var part = await Multipart.OpenAsync(MultipartOwner, exportId, number, cancellationToken);
          return new FileStreamResult(part.Stream, "application/zip")
          {
            FileDownloadName = part.Part.FileName,
            EnableRangeProcessing = true
          };
        }

        var opened = await Multipart.OpenTrackedAsync(MultipartOwner, exportId, number, cancellationToken);
        try
        {
          await using (opened.Stream)
          {
            Response.ContentType = "application/zip";
            Response.ContentLength = opened.Part.Bytes;
            Response.Headers["Content-Disposition"] = "attachment; filename=\"" + opened.Part.FileName + "\"";
            Response.Headers["Accept-Ranges"] = "bytes";
            Response.Headers["X-Accel-Buffering"] = "no";
            HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await Response.StartAsync(cancellationToken);
            await opened.Receipt.SendAsync(opened.Stream, Response.Body,
              () => Response.CompleteAsync(), cancellationToken);
          }
          return new EmptyResult();
        }
        catch (Exception ex)
        {
          opened.Receipt.Fail();
          if (ex is not OperationCanceledException)
            HttpContext.RequestServices.GetService<ILogger<LogsModel>>()?
              .LogWarning(ex, "Multipart ZIP transmission failed for part {PartNumber}.", number);
          if (Response.HasStarted || HttpContext.RequestAborted.IsCancellationRequested)
          {
            HttpContext.Abort();
            return new EmptyResult();
          }
          return StatusCode(500, "ZIP transmission failed.");
        }
      }
      catch (KeyNotFoundException) { return NotFound("Part unavailable or expired."); }
      catch (InvalidOperationException) { return StatusCode(409, "Part is already being sent."); }
      catch (IOException) { return NotFound("Prepared part is unavailable. Start a new export if necessary."); }
    }

    public async Task<IActionResult> OnPostMultipartReleaseAsync(string exportId, CancellationToken cancellationToken)
    {
      if (!MultipartAuthorized()) return StatusCode(403);
      try
      {
        await Multipart.ReleaseAsync(MultipartOwner, exportId, cancellationToken);
        return MultipartJson(new { released = true });
      }
      catch (KeyNotFoundException) { return NotFound("Export unavailable or expired."); }
    }

    private static bool TryDownloadPeriod(int count, string unit, DateTime endUtc, out DateTime? cutoffUtc)
    {
      // Legacy links with no period still request all available history.
      bool noRange = String.IsNullOrEmpty(unit) && count == 0;
      return _Internal.ExportHistoryPeriod.TryFromInput(count, noRange ? "all" : unit,
        endUtc, out _, out cutoffUtc);
    }

    private string ExportFileStem(string prefix, DateTime endUtc)
    {
      string instanceName = PTMagicConfiguration.GeneralSettings.Application.InstanceName ?? "";
      string safeInstanceName = System.Text.RegularExpressions.Regex
        .Replace(instanceName.Trim(), @"[^a-zA-Z0-9_-]+", "-").Trim('-', '_');
      if (safeInstanceName.Length > 64) safeInstanceName = safeInstanceName.Substring(0, 64).TrimEnd('-', '_');
      if (String.IsNullOrEmpty(safeInstanceName)) safeInstanceName = "Instance";
      return prefix + safeInstanceName + "-" + endUtc.ToString("yyyy-MM-ddTHH-mm-ssZ");
    }

    private async Task<IActionResult> StreamDownloadAsync(
      IReadOnlyList<_Internal.StreamingDownloadArchive.Member> files, string prefix,
      DateTime endUtc, CancellationToken cancellationToken)
    {
      string fileName = ExportFileStem(prefix, endUtc) + ".zip";

      Response.ContentType = "application/zip";
      Response.Headers["Content-Disposition"] = "attachment; filename=\"" + fileName + "\"";
      Response.Headers["Cache-Control"] = "no-store, no-transform";
      Response.Headers["X-Accel-Buffering"] = "no";
      HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
      try
      {
        await Response.StartAsync(cancellationToken);
        int written = await _Internal.StreamingDownloadArchive.WriteAsync(Response.Body, files, cancellationToken);
        if (written == 0) throw new IOException("All archive members disappeared before opening.");
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || HttpContext.RequestAborted.IsCancellationRequested)
      {
        HttpContext.Abort();
      }
      catch (Exception ex) when (Response.HasStarted)
      {
        // An HTTP error page cannot replace an already started ZIP. Abort
        // instead of serving an apparently successful truncated archive.
        HttpContext.RequestServices.GetService<ILogger<LogsModel>>()?
          .LogWarning(ex, "PTMagic ZIP download failed after response start.");
        HttpContext.Abort();
      }
      return new EmptyResult();
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
