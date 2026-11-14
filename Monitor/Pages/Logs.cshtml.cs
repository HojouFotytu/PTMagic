using System;
using System.IO;
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

    // A single authenticated SSE connection follows one log file. The filesystem watcher
    // signals changes, while the occasional timeout recovers missed notifications.
    public async Task OnGetStreamAsync(string source, string cursor = "", long offset = -1)
    {
      base.Init();
      if (!IsLoggedIn(HttpContext) || !LogsEnabled)
      {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
      }
      if (source != "analyzer" && source != "monitor")
      {
        Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
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

      bool initialAnalyzerSnapshot = source == "analyzer" && String.IsNullOrEmpty(activeCursor) && nextOffset < 0;
      try
      {
        if (initialAnalyzerSnapshot)
        {
          string[] previousSummary = ReadPreviousRaidSummary(folder);
          await SendSnapshotAsync(previousSummary, cancellation);
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
                // A fresh Analyzer viewer starts at EOF. Only the previous completed
                // raid summary is loaded; never replay an in-progress raid.
                nextOffset = initialAnalyzerSnapshot ? stream.Length : Math.Max(0, stream.Length - InitialTailBytes);
                initialAnalyzerSnapshot = false;
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
    }

    private async Task SendSnapshotAsync(string[] lines, CancellationToken cancellation)
    {
      string data = JsonSerializer.Serialize(new { lines });
      await Response.WriteAsync("event: snapshot\ndata: " + data + "\n\n", cancellation);
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
