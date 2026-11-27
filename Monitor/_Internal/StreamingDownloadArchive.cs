using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Monitor._Internal
{
  // Capture a fixed list of archive members. Only append-only files have a
  // discovery-time byte boundary; immutable/replaced snapshots are read at
  // their opened length so a newer gzip or JSON snapshot is never cut in half.
  public static class StreamingDownloadArchive
  {
    private const int BufferSize = 81920;
    public sealed record Member(string Path, string Entry, long? AppendBoundary, bool CompleteJsonLines = false);

    private static bool IsDirectoryWithoutLink(string path)
    {
      try
      {
        var attributes = File.GetAttributes(path);
        return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;
      }
      catch (FileNotFoundException) { return false; }
      catch (DirectoryNotFoundException) { return false; }
    }

    private static Member Capture(string path, string entry, bool appendOnly, bool jsonLines = false)
    {
      try
      {
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
          return null;
        return new Member(path, entry, appendOnly ? info.Length : null, jsonLines);
      }
      catch (FileNotFoundException) { return null; }
      catch (DirectoryNotFoundException) { return null; }
    }

    public static List<Member> CaptureLogs(IEnumerable<(string Path, string Prefix)> folders,
      DateTime? cutoffUtc, DateTime endUtc, CancellationToken cancellationToken)
    {
      var result = new List<Member>();
      foreach (var folder in folders)
      {
        if (!IsDirectoryWithoutLink(folder.Path)) continue;
        foreach (string path in Directory.EnumerateFiles(folder.Path, "*", SearchOption.TopDirectoryOnly))
        {
          cancellationToken.ThrowIfCancellationRequested();
          if (!LogArchiveDateFilter.Includes(path, cutoffUtc, endUtc)) continue;
          var member = Capture(path, folder.Prefix + System.IO.Path.GetFileName(path), true);
          if (member != null) result.Add(member);
        }
      }
      return result;
    }

    public static bool IncludesData(string relativePath, DateTime? cutoffUtc, DateTime endUtc, DateTime lastWriteUtc)
    {
      if (!cutoffUtc.HasValue) return true;
      string normalized = relativePath.Replace('\\', '/');
      string name = Path.GetFileName(normalized);
      if (normalized.StartsWith("DecisionHistory/", StringComparison.OrdinalIgnoreCase))
      {
        // UTC daily JSONL segments are retained whole, including boundary day.
        if (name.Length > 11 && name[10] == '-' &&
          DateTime.TryParseExact(name.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out DateTime day))
          return day.Date >= cutoffUtc.Value.Date && day.Date <= endUtc.Date;
        return lastWriteUtc >= cutoffUtc.Value && lastWriteUtc <= endUtc;
      }
      if (normalized.StartsWith("Exchange/", StringComparison.OrdinalIgnoreCase) &&
          name.StartsWith("MarketData_", StringComparison.OrdinalIgnoreCase) &&
          name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
      {
        // Native MarketData names are UTC minute timestamps.
        if (DateTime.TryParseExact(name.Substring(11, name.Length - 16), "yyyy-MM-dd_HH.mm",
          CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime timestamp))
          return timestamp >= cutoffUtc.Value && timestamp <= endUtc;
        return lastWriteUtc >= cutoffUtc.Value && lastWriteUtc <= endUtc;
      }
      // Current analytics, configuration acknowledgements, metadata and both
      // provider caches are not historical archive segments. Preserve them.
      return true;
    }

    public static List<Member> CaptureData(string dataFolder, DateTime? cutoffUtc, DateTime endUtc,
      CancellationToken cancellationToken)
    {
      var result = new List<Member>();
      if (!IsDirectoryWithoutLink(dataFolder)) return result;
      var pending = new Stack<string>();
      pending.Push(dataFolder);
      while (pending.Count > 0)
      {
        cancellationToken.ThrowIfCancellationRequested();
        string folder = pending.Pop();
        if (!IsDirectoryWithoutLink(folder)) continue;
        foreach (string path in Directory.EnumerateFileSystemEntries(folder))
        {
          cancellationToken.ThrowIfCancellationRequested();
          FileAttributes attributes;
          try { attributes = File.GetAttributes(path); }
          catch (FileNotFoundException) { continue; }
          catch (DirectoryNotFoundException) { continue; }
          if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
          if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
          string relative = Path.GetRelativePath(dataFolder, path).Replace('\\', '/');
          if (!IncludesData(relative, cutoffUtc, endUtc, File.GetLastWriteTimeUtc(path))) continue;
          bool jsonLines = relative.StartsWith("DecisionHistory/", StringComparison.OrdinalIgnoreCase) &&
            relative.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
          var member = Capture(path, "_data/" + relative, jsonLines, jsonLines);
          if (member != null) result.Add(member);
        }
      }
      return result;
    }

    private static CompressionLevel CompressionFor(string path)
    {
      string ext = Path.GetExtension(path).ToLowerInvariant();
      return ext is ".gz" or ".zip" or ".7z" or ".bz2" or ".xz" or ".zst" or ".png" or ".jpg" or ".jpeg"
        ? CompressionLevel.NoCompression : CompressionLevel.Fastest;
    }

    // Scan only the suffix necessary to find a complete JSONL record boundary.
    // A writer may be appending a large raid when the download begins.
    private static async Task<long> CompleteLineBoundaryAsync(FileStream stream, long limit,
      byte[] buffer, CancellationToken cancellationToken)
    {
      long end = limit;
      while (end > 0)
      {
        cancellationToken.ThrowIfCancellationRequested();
        int size = (int)Math.Min(end, buffer.Length);
        long start = end - size;
        stream.Position = start;
        await stream.ReadExactlyAsync(buffer.AsMemory(0, size), cancellationToken);
        int last = Array.LastIndexOf(buffer, (byte)'\n', size - 1, size);
        if (last >= 0) return start + last + 1;
        end = start;
      }
      return 0;
    }

    public static async Task<int> WriteAsync(Stream destination, IReadOnlyList<Member> members,
      CancellationToken cancellationToken)
    {
      // .NET 10 asynchronous ZIP APIs support non-seekable HTTP response streams.
      // Do not require Kestrel AllowSynchronousIO or a temporary archive file.
      int written = 0;
      byte[] buffer = new byte[BufferSize];
      await using (var archive = await ZipArchive.CreateAsync(destination, ZipArchiveMode.Create,
        leaveOpen: true, entryNameEncoding: null, cancellationToken: cancellationToken))
      {
        foreach (var member in members)
        {
          cancellationToken.ThrowIfCancellationRequested();
          FileStream source;
          try
          {
            if ((File.GetAttributes(member.Path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
              continue;
            source = new FileStream(member.Path, FileMode.Open, FileAccess.Read,
              FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
          }
          catch (FileNotFoundException) { continue; }
          catch (DirectoryNotFoundException) { continue; }
          await using (source)
          {
            long limit = source.Length;
            if (member.AppendBoundary.HasValue)
              limit = Math.Min(limit, member.AppendBoundary.Value);
            if (member.CompleteJsonLines)
              limit = await CompleteLineBoundaryAsync(source, limit, buffer, cancellationToken);
            source.Position = 0;
            var entry = archive.CreateEntry(member.Entry, CompressionFor(member.Path));
            await using (var output = await entry.OpenAsync(cancellationToken))
            {
              long remaining = limit;
              while (remaining > 0)
              {
                int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), cancellationToken);
                if (read == 0) throw new EndOfStreamException("Archive source shortened during download.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
              }
            }
            written++;
          }
        }
      }
      await destination.FlushAsync(cancellationToken);
      return written;
    }
  }
}
