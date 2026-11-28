using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Monitor._Internal
{
  /// <summary>
  /// One export, fixed source membership, one independently readable ZIP
  /// prepared per call. A positive byte limit is checked against the actual
  /// finalized ZIP (central directory included), not an estimated size.
  /// No source file or RAID is divided. Retry reads the published part.
  /// The caller serializes access and manages the private spool lifetime.
  /// </summary>
  public sealed class MultipartZipBatch
  {
    public sealed record Part(int Number, string FileName, long Bytes, int Files,
      bool ExceedsLimit, string OversizedEntry);
    public sealed record State(string ExportId, string Prefix, long MaxBytes,
      int TotalSources, int ProcessedSources, bool Complete, Part[] Parts);

    private readonly StreamingDownloadArchive.Member[] _members;
    private readonly List<Part> _parts = new List<Part>();
    private readonly string _directory;
    private readonly string _prefix;
    private readonly long _maxBytes;
    private int _next;
    public string ExportId { get; }
    public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;

    public MultipartZipBatch(string directory, string prefix, string exportId,
      IReadOnlyList<StreamingDownloadArchive.Member> members, long maxBytes)
    {
      if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
      if (members == null || members.Count == 0) throw new ArgumentException("No export sources.", nameof(members));
      if (!Guid.TryParseExact(exportId, "N", out _) || String.IsNullOrWhiteSpace(prefix) ||
          prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || prefix.Contains('/') || prefix.Contains('\\'))
        throw new ArgumentException("Invalid export identity.");
      _directory = directory;
      _prefix = prefix;
      _maxBytes = maxBytes;
      ExportId = exportId;
      _members = members.OrderBy(member => member.Entry, StringComparer.Ordinal).ToArray();
      if (_members.Select(m => m.Entry).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _members.Length)
        throw new InvalidDataException("Export source names collide.");
      Directory.CreateDirectory(_directory);
    }

    public static bool TryLimit(int maxZipMiB, out long maxBytes)
    {
      maxBytes = 0;
      if (maxZipMiB < 0) return false;
      maxBytes = maxZipMiB * 1024L * 1024L;
      return true;
    }

    public State Snapshot() => new State(ExportId, _prefix, _maxBytes, _members.Length,
      _next, _next == _members.Length, _parts.ToArray());

    // Start from a conservative source-length packing estimate. ZIP local
    // and central headers, data descriptors and ZIP64 metadata are covered
    // by this planning allowance; the measured finished file is authoritative.
    // Highly compressible inputs can produce smaller-than-limit parts.
    private int CandidateCount(CancellationToken cancellationToken)
    {
      long planned = 98; // ZIP/ZIP64 end records
      int count = 0;
      for (int index = _next; index < _members.Length; index++)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var member = _members[index];
        long length = member.AppendBoundary ?? new FileInfo(member.Path).Length;
        long headers = 30 + 46 + 2L * Encoding.UTF8.GetByteCount(member.Entry) + 2 * 28 + 24;
        // Always attempt the first member, including a potentially oversized
        // compressible file. Avoid integer overflow when checking the budget.
        if (count > 0 && (planned >= _maxBytes || headers > _maxBytes - planned ||
            length > _maxBytes - planned - headers)) break;
        count++;
        if (length > long.MaxValue - headers - planned) break;
        planned += headers + length;
        if (planned >= _maxBytes) break;
      }
      return count;
    }

    public State PrepareNext(CancellationToken cancellationToken)
    {
      while (_next < _members.Length)
      {
        int count = CandidateCount(cancellationToken);
        while (true)
        {
          cancellationToken.ThrowIfCancellationRequested();
          int number = _parts.Count + 1;
          string fileName = _prefix + "-part-" + number.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + ".zip";
          string ready = Path.Combine(_directory, fileName);
          string temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tmp");
          try
          {
            int written;
            long bytes;
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
              FileShare.None, 81920, FileOptions.SequentialScan))
            {
              written = StreamingDownloadArchive.WriteZip(output,
                new ArraySegment<StreamingDownloadArchive.Member>(_members, _next, count),
                cancellationToken, requireSources: true);
              bytes = output.Length;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (written == 0)
            {
              // All candidate legacy segments have no complete in-range
              // records. This is a valid empty selection, not a missing file.
              _next += count;
              break;
            }
            if (bytes > _maxBytes && count > 1)
            {
              // Only a truly oversized SINGLE finalized ZIP may exceed the
              // limit. Retry a smaller prefix without dropping any members.
              count = Math.Max(1, count / 2);
              continue;
            }
            var part = new Part(number, fileName, bytes, written, bytes > _maxBytes,
              bytes > _maxBytes ? _members[_next].Entry : null);
            File.Move(temporary, ready);
            _parts.Add(part);
            _next += count;
            return Snapshot();
          }
          finally
          {
            if (File.Exists(temporary)) File.Delete(temporary);
          }
        }
      }
      return Snapshot();
    }

    public (FileStream Stream, Part Part) OpenPart(int number)
    {
      if (number < 1 || number > _parts.Count) throw new KeyNotFoundException("Part not prepared.");
      Part part = _parts[number - 1];
      var stream = new FileStream(Path.Combine(_directory, part.FileName), FileMode.Open,
        FileAccess.Read, FileShare.Read | FileShare.Delete, 81920,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
      if (stream.Length != part.Bytes)
      {
        stream.Dispose();
        throw new IOException("Prepared ZIP size changed.");
      }
      return (stream, part);
    }

    public void Delete()
    {
      if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
  }
}
