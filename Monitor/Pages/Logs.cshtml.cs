using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace Monitor.Pages
{
  public class LogsModel : _Internal.BasePageModelSecure
  {
    private const int MaxChunkBytes = 65536;
    public bool LogsEnabled => PTMagicConfiguration?.GeneralSettings?.Monitor?.IsPasswordProtected == true;

    public IActionResult OnGet()
    {
      base.Init();
      if (!IsLoggedIn(HttpContext)) return RedirectToPage("/Login");
      return Page();
    }

    // Incremental, bounded reads: no client-supplied paths, no entire-file loading.
    public IActionResult OnGetTail(string source, string cursor, long offset = -1)
    {
      base.Init();
      if (!IsLoggedIn(HttpContext) || !LogsEnabled) return StatusCode(403);
      if (source != "analyzer" && source != "monitor") return BadRequest();

      Response.Headers["Cache-Control"] = "no-store";
      string basePath = source == "analyzer" ? PTMagicBasePath : PTMagicMonitorBasePath;
      string folder = Path.Combine(basePath, "_logs");

      // NLog's ${basedir} can point to the executable folder when it differs
      // from the content root, particularly in a development build.
      if (source == "monitor" && !Directory.Exists(folder))
        folder = Path.Combine(AppContext.BaseDirectory, "_logs");

      if (!Directory.Exists(folder))
        return new JsonResult(new { cursor = "", offset = 0, lines = Array.Empty<string>(), status = "Log directory not found." });

      try
      {
        string path = Directory.EnumerateFiles(folder, "????-??-??.txt", SearchOption.TopDirectoryOnly)
          .Where(p => DateTime.TryParseExact(Path.GetFileNameWithoutExtension(p), "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _))
          .OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal)
          .FirstOrDefault();

        if (path == null)
          return new JsonResult(new { cursor = "", offset = 0, lines = Array.Empty<string>(), status = "No log file found." });

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
          FileShare.ReadWrite | FileShare.Delete);
        string fileCursor = Path.GetFileName(path) + ":" + System.IO.File.GetCreationTimeUtc(path).Ticks;
        long length = stream.Length;
        bool initial = cursor != fileCursor || offset < 0 || offset > length;
        long start = initial ? Math.Max(0, length - MaxChunkBytes) : offset;
        stream.Seek(start, SeekOrigin.Begin);
        int count = (int)Math.Min(MaxChunkBytes, length - start);
        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
          int n = stream.Read(buffer, read, count - read);
          if (n == 0) break;
          read += n;
        }

        int lastNewline = read == 0 ? -1 : Array.LastIndexOf(buffer, (byte)'\n', read - 1, read);
        if (read == 0 || lastNewline < 0)
          return new JsonResult(new { cursor = fileCursor, offset = start, lines = Array.Empty<string>(), status = "" });

        int firstByte = 0;
        // A tail starting mid-file may begin inside a line. Discard that fragment.
        if (initial && start > 0)
        {
          int firstNewline = Array.IndexOf(buffer, (byte)'\n', 0, lastNewline + 1);
          firstByte = firstNewline + 1;
        }
        string decoded = Encoding.UTF8.GetString(buffer, firstByte, lastNewline + 1 - firstByte);
        string[] lines = decoded.Split('\n', StringSplitOptions.RemoveEmptyEntries)
          .Select(line => line.TrimEnd('\r')).ToArray();

        return new JsonResult(new
        {
          cursor = fileCursor,
          offset = start + lastNewline + 1,
          lines,
          status = ""
        });
      }
      catch (IOException)
      {
        return StatusCode(503);
      }
      catch (UnauthorizedAccessException)
      {
        return StatusCode(503);
      }
    }
  }
}
