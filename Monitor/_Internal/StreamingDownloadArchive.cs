using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Core.Helper;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Monitor._Internal
{
  // Capture a fixed list of archive members. Only append-only files have a
  // discovery-time byte boundary; immutable/replaced snapshots are read at
  // their opened length so a newer gzip or JSON snapshot is never cut in half.
  public static class StreamingDownloadArchive
  {
    private const int BufferSize = 81920;
    public sealed record Member(string Path, string Entry, long? AppendBoundary, bool CompleteJsonLines = false,
      DateTime? RecordedFromUtc = null, DateTime? RecordedThroughUtc = null);

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

    private static Member Capture(string path, string entry, bool appendOnly, bool jsonLines = false,
      DateTime? recordedFromUtc = null, DateTime? recordedThroughUtc = null)
    {
      try
      {
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
          return null;
        return new Member(path, entry, appendOnly ? info.Length : null, jsonLines, recordedFromUtc, recordedThroughUtc);
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
        // New immutable files encode the exact RecordedAtUtc. Legacy
        // day segments are coarse-selected here, then filtered by record time.
        if (DecisionHistoryFileNames.TryCompactTimestamp(name, out DateTime recorded))
          return recorded >= cutoffUtc.Value && recorded <= endUtc;
        if (DecisionHistoryFileNames.TryLegacyDate(name, out DateTime day))
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
          bool history = relative.StartsWith("DecisionHistory/", StringComparison.OrdinalIgnoreCase);
          // In-progress atomic gzip files are not published raid evidence.
          if (history && relative.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
          if (!IncludesData(relative, cutoffUtc, endUtc, File.GetLastWriteTimeUtc(path))) continue;
          bool jsonLines = history && relative.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
          bool legacy = history && DecisionHistoryFileNames.TryLegacyDate(path, out _);
          var member = Capture(path, "_data/" + relative, jsonLines, jsonLines,
            legacy ? cutoffUtc : null, legacy && cutoffUtc.HasValue ? endUtc : null);
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
    private static long CompleteLineBoundary(FileStream stream, long limit,
      byte[] buffer, CancellationToken cancellationToken)
    {
      long end = limit;
      while (end > 0)
      {
        cancellationToken.ThrowIfCancellationRequested();
        int size = (int)Math.Min(end, buffer.Length);
        long start = end - size;
        stream.Position = start;
        stream.ReadExactly(buffer.AsSpan(0, size));
        int last = Array.LastIndexOf(buffer, (byte)'\n', size - 1, size);
        if (last >= 0) return start + last + 1;
        end = start;
      }
      return 0;
    }

    // Legacy records can be tens of MiB. Scan fixed-size blocks and keep
    // only the small header prefix and selected byte ranges. Never load a
    // full legacy raid into Monitor memory to apply the date filter.
    private static DateTime RecordedAt(ReadOnlySpan<byte> prefix)
    {
      var reader = new Utf8JsonReader(prefix, isFinalBlock: false, state: default);
      while (reader.Read())
      {
        if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 &&
            reader.ValueTextEquals("RecordedAtUtc"))
        {
          if (reader.Read() && reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out var time))
            return time.UtcDateTime;
          break;
        }
      }
      throw new InvalidDataException("Legacy DecisionHistory record has no readable RecordedAtUtc in its header.");
    }

    private static List<(long Start, long Length)> LegacyRanges(FileStream source, long limit,
      DateTime fromUtc, DateTime throughUtc, byte[] buffer, CancellationToken cancellationToken)
    {
      // RecordedAtUtc is emitted near the start of every native schema 1
      // record. A malformed/unknown header fails explicitly, not by guessing.
      byte[] prefix = new byte[65536];
      var ranges = new List<(long Start, long Length)>();
      int prefixLength = 0;
      long position = 0, lineStart = 0;
      source.Position = 0;
      while (position < limit)
      {
        cancellationToken.ThrowIfCancellationRequested();
        int count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - position));
        if (count == 0) throw new EndOfStreamException("Legacy history shortened during download.");
        int index = 0;
        while (index < count)
        {
          int newline = Array.IndexOf(buffer, (byte)'\n', index, count - index);
          int end = newline < 0 ? count : newline;
          int copy = Math.Min(end - index, prefix.Length - prefixLength);
          if (copy > 0)
          {
            Buffer.BlockCopy(buffer, index, prefix, prefixLength, copy);
            prefixLength += copy;
          }
          if (newline < 0) break;
          long lineEnd = position + newline + 1;
          if (prefixLength > 0)
          {
            DateTime timestamp = RecordedAt(prefix.AsSpan(0, prefixLength));
            if (timestamp >= fromUtc && timestamp <= throughUtc)
            {
              if (ranges.Count > 0 && ranges[^1].Start + ranges[^1].Length == lineStart)
              {
                var previous = ranges[^1];
                ranges[^1] = (previous.Start, lineEnd - previous.Start);
              }
              else ranges.Add((lineStart, lineEnd - lineStart));
            }
          }
          lineStart = lineEnd;
          prefixLength = 0;
          index = newline + 1;
        }
        position += count;
      }
      // No LF means an interrupted append. Preserve it on disk, never
      // claim it is a complete exported record.
      return ranges;
    }

    private const int ChunkSize = 64 * 1024;
    private const int QueuedChunks = 4;
    private readonly record struct Chunk(byte[] Buffer, int Count);

    // ZipArchive/DeflateStream may issue synchronous writes even when an
    // entry is disposed asynchronously. Isolate ALL ZIP operations from the
    // HTTP stream. Only a dedicated producer may block on this bounded queue;
    // the HTTP consumer uses WriteAsync exclusively.
    private sealed class ZipProducerStream : Stream
    {
      private readonly ChannelWriter<Chunk> _writer;
      private readonly CancellationToken _cancellation;
      private byte[] _buffer;
      private int _used;

      public ZipProducerStream(ChannelWriter<Chunk> writer, CancellationToken cancellation)
      {
        _writer = writer;
        _cancellation = cancellation;
      }

      public override bool CanRead => false;
      public override bool CanSeek => false;
      public override bool CanWrite => true;
      public override long Length => throw new NotSupportedException();
      public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
      public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
      public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
      public override void SetLength(long value) => throw new NotSupportedException();

      public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

      public override void Write(ReadOnlySpan<byte> bytes)
      {
        while (!bytes.IsEmpty)
        {
          _cancellation.ThrowIfCancellationRequested();
          _buffer ??= ArrayPool<byte>.Shared.Rent(ChunkSize);
          int count = Math.Min(bytes.Length, ChunkSize - _used);
          bytes.Slice(0, count).CopyTo(_buffer.AsSpan(_used, count));
          _used += count;
          bytes = bytes.Slice(count);
          if (_used == ChunkSize) Flush();
        }
      }

      public override void Flush()
      {
        _cancellation.ThrowIfCancellationRequested();
        if (_used == 0) return;
        // Ownership transfers only after the queue accepts the chunk.
        _writer.WriteAsync(new Chunk(_buffer, _used), _cancellation).AsTask().GetAwaiter().GetResult();
        _buffer = null;
        _used = 0;
      }

      protected override void Dispose(bool disposing)
      {
        if (disposing && _buffer != null)
        {
          ArrayPool<byte>.Shared.Return(_buffer);
          _buffer = null;
          _used = 0;
        }
        base.Dispose(disposing);
      }
    }

    private static int WriteZip(Stream destination, IReadOnlyList<Member> members,
      CancellationToken cancellationToken)
    {
      int written = 0;
      byte[] buffer = new byte[BufferSize];
      using (var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true))
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
              FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);
          }
          catch (FileNotFoundException) { continue; }
          catch (DirectoryNotFoundException) { continue; }
          using (source)
          {
            long limit = source.Length;
            if (member.AppendBoundary.HasValue)
              limit = Math.Min(limit, member.AppendBoundary.Value);
            List<(long Start, long Length)> ranges;
            if (member.RecordedFromUtc.HasValue && member.RecordedThroughUtc.HasValue)
            {
              ranges = LegacyRanges(source, limit, member.RecordedFromUtc.Value,
                member.RecordedThroughUtc.Value, buffer, cancellationToken);
              if (ranges.Count == 0) continue;
            }
            else
            {
              if (member.CompleteJsonLines)
                limit = CompleteLineBoundary(source, limit, buffer, cancellationToken);
              ranges = new List<(long, long)> { (0, limit) };
            }
            var entry = archive.CreateEntry(member.Entry, CompressionFor(member.Path));
            using (var output = entry.Open())
            {
              foreach (var range in ranges)
              {
                source.Position = range.Start;
                long remaining = range.Length;
                while (remaining > 0)
                {
                  cancellationToken.ThrowIfCancellationRequested();
                  int read = source.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                  if (read == 0) throw new EndOfStreamException("Archive source shortened during download.");
                  output.Write(buffer, 0, read);
                  remaining -= read;
                }
              }
            }
            written++;
          }
        }
      }
      // The ZIP central directory is now complete; publish any remaining bytes.
      destination.Flush();
      return written;
    }

    public static async Task<int> WriteAsync(Stream destination, IReadOnlyList<Member> members,
      CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      var channel = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(QueuedChunks)
      {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = true,
        AllowSynchronousContinuations = false
      });

      // A dedicated thread performs synchronous ZIP compression/finalization
      // and waits for backpressure without blocking an ASP.NET request thread.
      Task<int> producer = Task.Factory.StartNew(() =>
      {
        try
        {
          using var output = new ZipProducerStream(channel.Writer, stop.Token);
          int written = WriteZip(output, members, stop.Token);
          channel.Writer.TryComplete();
          return written;
        }
        catch (Exception ex)
        {
          channel.Writer.TryComplete(ex);
          throw;
        }
      }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

      try
      {
        await foreach (Chunk chunk in channel.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
        {
          try
          {
            await destination.WriteAsync(chunk.Buffer.AsMemory(0, chunk.Count), stop.Token).ConfigureAwait(false);
          }
          finally
          {
            ArrayPool<byte>.Shared.Return(chunk.Buffer);
          }
        }
        int written = await producer.ConfigureAwait(false);
        await destination.FlushAsync(stop.Token).ConfigureAwait(false);
        return written;
      }
      finally
      {
        // Disconnects, write failures and cancellation must unblock a producer
        // waiting on the full queue. Join it and reclaim any unconsumed buffers.
        stop.Cancel();
        try { await producer.ConfigureAwait(false); }
        catch { /* Preserve the original producer/consumer exception. */ }
        while (channel.Reader.TryRead(out Chunk chunk))
          ArrayPool<byte>.Shared.Return(chunk.Buffer);
      }
    }
  }
}
