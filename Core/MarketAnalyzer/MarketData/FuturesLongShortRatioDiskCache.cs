using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Core.Helper;

namespace Core.MarketAnalyzer.MarketData
{
  public sealed class FuturesLongShortRatioDiskSnapshot
  {
    public IReadOnlyList<FuturesLongShortRatioObservation> Rows { get; init; } =
      Array.Empty<FuturesLongShortRatioObservation>();
    public IReadOnlyList<(long Start, long End)> CompleteIntervals { get; init; } =
      Array.Empty<(long, long)>();
  }

  /// <summary>
  /// Versioned, compressed, atomic disk cache for each USD-M ratio source and
  /// symbol. Verified query-coverage intervals are persisted with raw provider
  /// observations so restart recovery does not re-request already exhausted
  /// ranges or mistake absent 5m observations for valid data.
  /// </summary>
  public sealed class FuturesLongShortRatioDiskCache
  {
    private const int Magic = 0x4C534443;
    private const int Version = 1;
    private const int EndMarker = 0x4344534C;
    private const int MaximumRows = 500000;
    private const int MaximumIntervals = 100000;
    private const long MaximumCompressedBytes = 8L * 1024 * 1024;
    private static readonly TimeSpan MinimumSaveInterval = TimeSpan.FromMinutes(10);
    private readonly string _directory;

    public FuturesLongShortRatioDiskCache(string directory)
    {
      if (String.IsNullOrWhiteSpace(directory))
        throw new ArgumentException("A long/short ratio cache directory is required.", nameof(directory));
      _directory = Path.GetFullPath(directory);
    }

    public string CachePath(FuturesLongShortRatioSource source, string symbol)
    {
      if (!Enum.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
      string normalized = FuturesLongShortRatio.NormalizeSymbol(symbol);
      string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
      return Path.Combine(_directory, "BINANCEFUTURES", source.ToString(), "5m", hash + ".gz");
    }

    public FuturesLongShortRatioDiskSnapshot TryRestore(FuturesLongShortRatioSource source,
      string symbol, DateTimeOffset asOfUtc, LogHelper log)
    {
      string normalized = FuturesLongShortRatio.NormalizeSymbol(symbol);
      string path = CachePath(source, normalized);
      if (!File.Exists(path)) return new FuturesLongShortRatioDiskSnapshot();

      try
      {
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > MaximumCompressedBytes)
          throw new InvalidDataException("Long/short ratio cache compressed size is invalid.");

        var rows = new List<FuturesLongShortRatioObservation>();
        var intervals = new List<(long Start, long End)>();
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (GZipStream gzip = new GZipStream(stream, CompressionMode.Decompress))
        using (BinaryReader reader = new BinaryReader(gzip, Encoding.UTF8))
        {
          if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version ||
            !String.Equals(reader.ReadString(), normalized, StringComparison.Ordinal) ||
            reader.ReadInt32() != (int)source)
            throw new InvalidDataException("Long/short ratio cache schema, symbol or source identity mismatch.");

          int rowCount = reader.ReadInt32();
          if (rowCount < 0 || rowCount > MaximumRows)
            throw new InvalidDataException("Long/short ratio cache row count is outside allowed bounds.");

          long previous = -1;
          long maximumTime = asOfUtc.ToUniversalTime().ToUnixTimeMilliseconds();
          for (int index = 0; index < rowCount; index++)
          {
            long milliseconds = reader.ReadInt64();
            var row = new FuturesLongShortRatioObservation
            {
              Exchange = "BinanceFutures",
              Symbol = normalized,
              Source = source,
              TimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds),
              Period = FuturesLongShortRatio.SourcePeriod,
              LongShortRatio = ParseDecimal(reader.ReadString(), allowNegative: false),
              LongSideShare = ParseDecimal(reader.ReadString(), allowNegative: false),
              ShortSideShare = ParseDecimal(reader.ReadString(), allowNegative: false)
            };
            FuturesLongShortRatio.Validate(row, source, normalized);
            if (milliseconds <= previous || milliseconds > maximumTime)
              throw new InvalidDataException("Long/short ratio cache rows are unordered, duplicated or future-dated.");
            previous = milliseconds;
            rows.Add(row);
          }

          int intervalCount = reader.ReadInt32();
          if (intervalCount < 0 || intervalCount > MaximumIntervals)
            throw new InvalidDataException("Long/short ratio cache coverage count is outside allowed bounds.");
          for (int index = 0; index < intervalCount; index++)
          {
            long start = reader.ReadInt64();
            long end = reader.ReadInt64();
            if (start <= 0 || end < start || end > maximumTime)
              throw new InvalidDataException("Long/short ratio cache contains an invalid or future coverage interval.");
            intervals.Add((start, end));
          }

          if (reader.ReadInt32() != EndMarker || reader.BaseStream.ReadByte() != -1)
            throw new InvalidDataException("Long/short ratio cache has an invalid trailer.");
        }

        if (rows.Count == 0 && intervals.Count == 0)
          throw new InvalidDataException("Long/short ratio cache contains no observations or verified coverage.");

        return new FuturesLongShortRatioDiskSnapshot
        {
          Rows = rows.AsReadOnly(),
          CompleteIntervals = intervals.AsReadOnly()
        };
      }
      catch (Exception exception) when (exception is not OutOfMemoryException)
      {
        log?.DoLogWarn("Invalid long/short ratio cache ignored for '" + normalized + "' / " +
          source + " (" + exception.GetType().Name + "); normal bounded refresh will repair it.");
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return new FuturesLongShortRatioDiskSnapshot();
      }
    }

    public bool TrySave(FuturesLongShortRatioSource source, string symbol,
      IEnumerable<FuturesLongShortRatioObservation> history,
      IEnumerable<(long Start, long End)> completeIntervals, DateTimeOffset asOfUtc, LogHelper log)
    {
      string normalized = FuturesLongShortRatio.NormalizeSymbol(symbol);
      string path = CachePath(source, normalized);
      string temporary = null;
      try
      {
        FuturesLongShortRatioObservation[] rows = (history ?? Array.Empty<FuturesLongShortRatioObservation>())
          .OrderBy(row => row.TimestampUtc).ToArray();
        (long Start, long End)[] intervals = (completeIntervals ?? Array.Empty<(long, long)>())
          .Distinct().OrderBy(interval => interval.Start).ThenBy(interval => interval.End).ToArray();
        if (rows.Length == 0 && intervals.Length == 0) return false;
        if (rows.Length > MaximumRows || intervals.Length > MaximumIntervals)
          throw new InvalidDataException("Long/short ratio cache exceeds its defensive record limits.");

        long maximumTime = asOfUtc.ToUniversalTime().ToUnixTimeMilliseconds();
        long previous = -1;
        foreach (FuturesLongShortRatioObservation row in rows)
        {
          FuturesLongShortRatio.Validate(row, source, normalized);
          long milliseconds = row.TimestampUtc.ToUnixTimeMilliseconds();
          if (milliseconds <= previous || milliseconds > maximumTime)
            throw new InvalidDataException("Long/short ratio cache save received unordered, duplicate or future rows.");
          previous = milliseconds;
        }
        foreach (var interval in intervals)
        {
          if (interval.Start <= 0 || interval.End < interval.Start || interval.End > maximumTime)
            throw new InvalidDataException("Long/short ratio cache save received an invalid coverage interval.");
        }

        if (File.Exists(path) &&
          DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < MinimumSaveInterval)
        {
          FuturesLongShortRatioDiskSnapshot existing = TryRestore(source, normalized, asOfUtc, log);
          if (MatchesSnapshot(existing, rows, intervals)) return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path));
        temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
          using (GZipStream gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
          using (BinaryWriter writer = new BinaryWriter(gzip, Encoding.UTF8))
          {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(normalized);
            writer.Write((int)source);
            writer.Write(rows.Length);
            foreach (FuturesLongShortRatioObservation row in rows)
            {
              writer.Write(row.TimestampUtc.ToUnixTimeMilliseconds());
              writer.Write(row.LongShortRatio.ToString(CultureInfo.InvariantCulture));
              writer.Write(row.LongSideShare.ToString(CultureInfo.InvariantCulture));
              writer.Write(row.ShortSideShare.ToString(CultureInfo.InvariantCulture));
            }
            writer.Write(intervals.Length);
            foreach (var interval in intervals)
            {
              writer.Write(interval.Start);
              writer.Write(interval.End);
            }
            writer.Write(EndMarker);
          }
          stream.Flush(flushToDisk: true);
        }
        if (new FileInfo(temporary).Length > MaximumCompressedBytes)
          throw new InvalidDataException("Long/short ratio cache compressed file exceeds its defensive size limit.");
        File.Move(temporary, path, overwrite: true);
        return true;
      }
      catch (Exception exception) when (exception is not OutOfMemoryException)
      {
        log?.DoLogWarn("Long/short ratio cache save failed for '" + normalized + "' / " +
          source + " (" + exception.GetType().Name + "); analysis will continue.");
        return false;
      }
      finally
      {
        if (temporary != null)
        {
          try { if (File.Exists(temporary)) File.Delete(temporary); }
          catch (IOException) { }
          catch (UnauthorizedAccessException) { }
        }
      }
    }

    private static bool MatchesSnapshot(FuturesLongShortRatioDiskSnapshot snapshot,
      IReadOnlyList<FuturesLongShortRatioObservation> rows,
      IReadOnlyList<(long Start, long End)> intervals)
    {
      if (snapshot.Rows.Count != rows.Count || snapshot.CompleteIntervals.Count != intervals.Count)
        return false;
      for (int index = 0; index < rows.Count; index++)
      {
        FuturesLongShortRatioObservation saved = snapshot.Rows[index];
        FuturesLongShortRatioObservation current = rows[index];
        if (saved.TimestampUtc.ToUnixTimeMilliseconds() != current.TimestampUtc.ToUnixTimeMilliseconds() ||
          saved.LongShortRatio != current.LongShortRatio ||
          saved.LongSideShare != current.LongSideShare ||
          saved.ShortSideShare != current.ShortSideShare)
          return false;
      }
      for (int index = 0; index < intervals.Count; index++)
      {
        if (snapshot.CompleteIntervals[index].Start != intervals[index].Start ||
          snapshot.CompleteIntervals[index].End != intervals[index].End)
          return false;
      }
      return true;
    }

    private static decimal ParseDecimal(string raw, bool allowNegative)
    {
      if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value) ||
        (!allowNegative && value < 0))
        throw new InvalidDataException("Long/short ratio cache contains an invalid decimal value.");
      return value;
    }
  }
}
