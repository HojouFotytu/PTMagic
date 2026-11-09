using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Core.Helper;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Separate, fail-open and versioned disk storage for Binance USD-M historical
  /// open interest. Never confuses native OI units with quote notional, and never
  /// treats persisted observations as fresh without the ordinary exact-endpoint test.
  /// </summary>
  public sealed class FuturesOpenInterestDiskCache
  {
    private const int Magic = 0x4F494348;
    private const int Version = 1;
    private const int EndMarker = 0x4843494F;
    private const int MaximumRows = 8066; // Max configured 672h + overlap; smaller than provider month.
    private const long MaximumCompressedBytes = 4L * 1024 * 1024;
    private readonly string _directory;

    public FuturesOpenInterestDiskCache(string directory)
    {
      if (String.IsNullOrWhiteSpace(directory))
        throw new ArgumentException("An open-interest cache directory is required.", nameof(directory));
      _directory = Path.GetFullPath(directory);
    }

    public string CachePath(string symbol)
    {
      string normalized = FuturesOpenInterest.NormalizeSymbol(symbol);
      string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
      return Path.Combine(_directory, "BINANCEFUTURES", "5m", hash + ".gz");
    }

    public IReadOnlyList<FuturesOpenInterestObservation> TryRestore(string symbol,
      DateTimeOffset asOfUtc, LogHelper log)
    {
      string path = CachePath(symbol);
      if (!File.Exists(path)) return Array.Empty<FuturesOpenInterestObservation>();

      try
      {
        if (new FileInfo(path).Length <= 0 || new FileInfo(path).Length > MaximumCompressedBytes)
          throw new InvalidDataException("Invalid OI disk-cache size.");
        List<FuturesOpenInterestObservation> rows = new List<FuturesOpenInterestObservation>();
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (GZipStream gzip = new GZipStream(stream, CompressionMode.Decompress))
        using (BinaryReader reader = new BinaryReader(gzip, Encoding.UTF8))
        {
          if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version ||
            !String.Equals(reader.ReadString(), FuturesOpenInterest.NormalizeSymbol(symbol), StringComparison.Ordinal))
            throw new InvalidDataException("OI cache schema/identity mismatch.");
          int count = reader.ReadInt32();
          if (count < 1 || count > MaximumRows)
            throw new InvalidDataException("OI cache row count is outside allowed bounds.");
          long previous = -1;
          for (int i = 0; i < count; i++)
          {
            long milliseconds = reader.ReadInt64();
            string native = reader.ReadString();
            string value = reader.ReadString();
            if (milliseconds <= previous || milliseconds > asOfUtc.ToUniversalTime().ToUnixTimeMilliseconds() ||
                milliseconds % (long)FuturesOpenInterest.SourcePeriod.TotalMilliseconds != 0)
              throw new InvalidDataException("OI cache is unordered, unaligned or has a future timestamp.");
            decimal quantity = ParseDecimal(native);
            decimal reported = ParseDecimal(value);
            rows.Add(new FuturesOpenInterestObservation
            {
              Symbol = FuturesOpenInterest.NormalizeSymbol(symbol),
              Exchange = "BinanceFutures", Period = FuturesOpenInterest.SourcePeriod,
              TimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds),
              NativeOpenInterest = quantity, ReportedOpenInterestValue = reported,
              IsHistoricalStatistic = true
            });
            previous = milliseconds;
          }
          if (reader.ReadInt32() != EndMarker || reader.BaseStream.ReadByte() != -1)
            throw new InvalidDataException("OI cache has an invalid trailer.");
        }
        return rows.AsReadOnly();
      }
      catch (Exception exception) when (exception is not OutOfMemoryException)
      {
        log?.DoLogWarn("Invalid open-interest cache ignored for '" + symbol + "' (" +
          exception.GetType().Name + "); normal bounded refresh will repair it.");
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return Array.Empty<FuturesOpenInterestObservation>();
      }
    }

    public bool TrySave(string symbol, IEnumerable<FuturesOpenInterestObservation> history, LogHelper log)
    {
      string path = CachePath(symbol);
      string temporary = null;
      try
      {
        FuturesOpenInterestObservation[] rows = (history ?? Array.Empty<FuturesOpenInterestObservation>())
          .OrderBy(row => row.TimestampUtc).ToArray();
        if (rows.Length == 0) return false;
        if (rows.Length > MaximumRows) rows = rows.Skip(rows.Length - MaximumRows).ToArray();

        // Save at most every 10 minutes; the in-memory store remains current.
        if (File.Exists(path) &&
          DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromMinutes(10))
          return false;

        long previous = -1;
        foreach (var row in rows)
        {
          if (row == null || row.Period != FuturesOpenInterest.SourcePeriod ||
            !row.IsHistoricalStatistic || row.NativeOpenInterest < 0 ||
            !row.ReportedOpenInterestValue.HasValue || row.ReportedOpenInterestValue.Value < 0 ||
            !String.Equals(row.Symbol, FuturesOpenInterest.NormalizeSymbol(symbol), StringComparison.Ordinal) ||
            !String.Equals(row.Exchange, "BinanceFutures", StringComparison.Ordinal) ||
            row.TimestampUtc.ToUnixTimeMilliseconds() <= previous ||
            row.TimestampUtc.ToUnixTimeMilliseconds() % (long)FuturesOpenInterest.SourcePeriod.TotalMilliseconds != 0)
            throw new InvalidDataException("OI cache save received invalid or unaligned rows.");
          previous = row.TimestampUtc.ToUnixTimeMilliseconds();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path));
        temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
          using (GZipStream gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
          using (BinaryWriter writer = new BinaryWriter(gzip, Encoding.UTF8))
          {
            writer.Write(Magic); writer.Write(Version);
            writer.Write(FuturesOpenInterest.NormalizeSymbol(symbol));
            writer.Write(rows.Length);
            foreach (var row in rows)
            {
              writer.Write(row.TimestampUtc.ToUnixTimeMilliseconds());
              writer.Write(row.NativeOpenInterest.ToString(System.Globalization.CultureInfo.InvariantCulture));
              writer.Write(row.ReportedOpenInterestValue.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            writer.Write(EndMarker);
          }
          stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
        return true;
      }
      catch (Exception exception) when (exception is not OutOfMemoryException)
      {
        log?.DoLogWarn("Open-interest cache save failed for '" + symbol + "' (" +
          exception.GetType().Name + "); analysis will continue.");
        return false;
      }
      finally
      {
        if (temporary != null)
        {
          try { if (File.Exists(temporary)) File.Delete(temporary); }
          catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
      }
    }

    private static decimal ParseDecimal(string raw)
    {
      if (!decimal.TryParse(raw, System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out decimal value) || value < 0)
        throw new InvalidDataException("Invalid historical open-interest amount in cache.");
      return value;
    }
  }
}
