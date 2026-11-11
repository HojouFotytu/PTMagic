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
  public sealed class FuturesFundingDiskSnapshot
  {
    public IReadOnlyList<FuturesFundingObservation> Rows { get; init; } =
      Array.Empty<FuturesFundingObservation>();
    public IReadOnlyList<(long Start, long End)> CompleteIntervals { get; init; } =
      Array.Empty<(long, long)>();
  }

  /// <summary>
  /// Versioned, compressed, atomic persistence for settled USD-M funding
  /// observations and verified query-coverage intervals. Persisted coverage
  /// means only that Binance returned an exhausted bounded query; it never
  /// fabricates a settlement or turns an unknown interval into zero.
  /// </summary>
  public sealed class FuturesFundingDiskCache
  {
    private const int Magic = 0x464E4443;
    private const int Version = 1;
    private const int EndMarker = 0x43444E46;
    private const int MaximumRows = 500000;
    private const int MaximumIntervals = 100000;
    private const long MaximumCompressedBytes = 16L * 1024 * 1024;
    private static readonly TimeSpan MinimumSaveInterval = TimeSpan.FromMinutes(10);
    private readonly string _directory;

    public FuturesFundingDiskCache(string directory)
    {
      if (String.IsNullOrWhiteSpace(directory))
        throw new ArgumentException("A funding cache directory is required.", nameof(directory));
      _directory = Path.GetFullPath(directory);
    }

    public string CachePath(string symbol)
    {
      string normalized = FuturesOpenInterest.NormalizeSymbol(symbol);
      string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
      return Path.Combine(_directory, "BINANCEFUTURES", hash + ".gz");
    }

    public FuturesFundingDiskSnapshot TryRestore(string symbol, DateTimeOffset asOfUtc, LogHelper log)
    {
      string normalized = FuturesOpenInterest.NormalizeSymbol(symbol);
      string path = CachePath(normalized);
      if (!File.Exists(path)) return new FuturesFundingDiskSnapshot();

      try
      {
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > MaximumCompressedBytes)
          throw new InvalidDataException("Funding cache compressed size is invalid.");

        var rows = new List<FuturesFundingObservation>();
        var intervals = new List<(long Start, long End)>();
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (GZipStream gzip = new GZipStream(stream, CompressionMode.Decompress))
        using (BinaryReader reader = new BinaryReader(gzip, Encoding.UTF8))
        {
          if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version ||
            !String.Equals(reader.ReadString(), normalized, StringComparison.Ordinal))
            throw new InvalidDataException("Funding cache schema or symbol identity mismatch.");

          int rowCount = reader.ReadInt32();
          if (rowCount < 0 || rowCount > MaximumRows)
            throw new InvalidDataException("Funding cache row count is outside allowed bounds.");

          long previousTime = -1;
          string previousType = "";
          long maximumTime = asOfUtc.ToUniversalTime().ToUnixTimeMilliseconds();
          for (int index = 0; index < rowCount; index++)
          {
            long milliseconds = reader.ReadInt64();
            string rateType = reader.ReadString();
            decimal nativeRate = ParseDecimal(reader.ReadString(), allowNegative: true);
            bool hasMarkPrice = reader.ReadBoolean();
            decimal? markPrice = hasMarkPrice
              ? ParseDecimal(reader.ReadString(), allowNegative: false)
              : null;
            var row = new FuturesFundingObservation
            {
              Symbol = normalized,
              FundingTimeUtc = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds),
              RateType = rateType,
              NativeRate = nativeRate,
              MarkPrice = markPrice
            };
            FuturesFunding.Validate(row, normalized);
            string typeKey = rateType.ToUpperInvariant();
            if (milliseconds < previousTime ||
              (milliseconds == previousTime && String.CompareOrdinal(typeKey, previousType) <= 0) ||
              milliseconds > maximumTime)
              throw new InvalidDataException("Funding cache rows are unordered, duplicated or future-dated.");
            previousTime = milliseconds;
            previousType = typeKey;
            rows.Add(row);
          }

          int intervalCount = reader.ReadInt32();
          if (intervalCount < 0 || intervalCount > MaximumIntervals)
            throw new InvalidDataException("Funding cache coverage count is outside allowed bounds.");
          for (int index = 0; index < intervalCount; index++)
          {
            long start = reader.ReadInt64();
            long end = reader.ReadInt64();
            if (start <= 0 || end < start || end > maximumTime)
              throw new InvalidDataException("Funding cache contains an invalid or future coverage interval.");
            intervals.Add((start, end));
          }

          if (reader.ReadInt32() != EndMarker || reader.BaseStream.ReadByte() != -1)
            throw new InvalidDataException("Funding cache has an invalid trailer.");
        }

        if (rows.Count == 0 && intervals.Count == 0)
          throw new InvalidDataException("Funding cache contains no observations or verified coverage.");

        return new FuturesFundingDiskSnapshot
        {
          Rows = rows.AsReadOnly(),
          CompleteIntervals = intervals.AsReadOnly()
        };
      }
      catch (Exception exception) when (exception is not OutOfMemoryException)
      {
        log?.DoLogWarn("Invalid funding cache ignored for '" + normalized + "' (" +
          exception.GetType().Name + "); normal bounded refresh will repair it.");
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return new FuturesFundingDiskSnapshot();
      }
    }

    public bool TrySave(string symbol, IEnumerable<FuturesFundingObservation> history,
      IEnumerable<(long Start, long End)> completeIntervals, DateTimeOffset asOfUtc, LogHelper log)
    {
      string normalized = FuturesOpenInterest.NormalizeSymbol(symbol);
      string path = CachePath(normalized);
      string temporary = null;
      try
      {
        FuturesFundingObservation[] rows = (history ?? Array.Empty<FuturesFundingObservation>())
          .OrderBy(row => row.FundingTimeUtc)
          .ThenBy(row => row.RateType?.ToUpperInvariant(), StringComparer.Ordinal)
          .ToArray();
        (long Start, long End)[] intervals = (completeIntervals ?? Array.Empty<(long, long)>())
          .Distinct().OrderBy(interval => interval.Start).ThenBy(interval => interval.End).ToArray();
        if (rows.Length == 0 && intervals.Length == 0) return false;
        if (rows.Length > MaximumRows || intervals.Length > MaximumIntervals)
          throw new InvalidDataException("Funding cache exceeds its defensive record limits.");

        long maximumTime = asOfUtc.ToUniversalTime().ToUnixTimeMilliseconds();
        long previousTime = -1;
        string previousType = "";
        foreach (FuturesFundingObservation row in rows)
        {
          FuturesFunding.Validate(row, normalized);
          long milliseconds = row.FundingTimeUtc.ToUnixTimeMilliseconds();
          string typeKey = row.RateType.ToUpperInvariant();
          if (milliseconds > maximumTime || milliseconds < previousTime ||
            (milliseconds == previousTime && String.CompareOrdinal(typeKey, previousType) <= 0))
            throw new InvalidDataException("Funding cache save received unordered, duplicate or future rows.");
          previousTime = milliseconds;
          previousType = typeKey;
        }
        foreach (var interval in intervals)
        {
          if (interval.Start <= 0 || interval.End < interval.Start || interval.End > maximumTime)
            throw new InvalidDataException("Funding cache save received an invalid coverage interval.");
        }

        if (File.Exists(path) &&
          DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < MinimumSaveInterval)
          return false;

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
            writer.Write(rows.Length);
            foreach (FuturesFundingObservation row in rows)
            {
              writer.Write(row.FundingTimeUtc.ToUnixTimeMilliseconds());
              writer.Write(row.RateType);
              writer.Write(row.NativeRate.ToString(CultureInfo.InvariantCulture));
              writer.Write(row.MarkPrice.HasValue);
              if (row.MarkPrice.HasValue)
                writer.Write(row.MarkPrice.Value.ToString(CultureInfo.InvariantCulture));
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
          throw new InvalidDataException("Funding cache compressed file exceeds its defensive size limit.");
        File.Move(temporary, path, overwrite: true);
        return true;
      }
      catch (Exception exception) when (exception is not OutOfMemoryException)
      {
        log?.DoLogWarn("Funding cache save failed for '" + normalized + "' (" +
          exception.GetType().Name + "); analysis will continue.");
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

    private static decimal ParseDecimal(string raw, bool allowNegative)
    {
      if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value) ||
        (!allowNegative && value < 0))
        throw new InvalidDataException("Funding cache contains an invalid decimal value.");
      return value;
    }
  }
}
