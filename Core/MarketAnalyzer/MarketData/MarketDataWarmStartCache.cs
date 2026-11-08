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
  /// Optional, fail-open, per-exchange disk snapshots of validated closed OHLCV candles.
  /// Cache files are never authoritative: the normal anchored refresh checks gaps,
  /// retention, and freshness again before calculations consume the restored series.
  /// </summary>
  public sealed class MarketDataWarmStartCache
  {
    private const int Magic = 0x50544D43;
    private const int Version = 1;
    private const int EndMarker = 0x43414348;
    private const int MaximumFileCandles = 500000;
    private const long MaximumCompressedFileBytes = 128L * 1024 * 1024;
    private readonly string _rootDirectory;
    private readonly TimeSpan _minimumSaveInterval;

    public MarketDataWarmStartCache(string rootDirectory, TimeSpan? minimumSaveInterval = null)
    {
      if (String.IsNullOrWhiteSpace(rootDirectory))
        throw new ArgumentException("A normalized warm-start cache directory is required.", nameof(rootDirectory));

      _rootDirectory = Path.GetFullPath(rootDirectory);
      _minimumSaveInterval = minimumSaveInterval ?? TimeSpan.FromMinutes(30);
      if (_minimumSaveInterval < TimeSpan.Zero)
        throw new ArgumentOutOfRangeException(nameof(minimumSaveInterval));
    }

    public string RootDirectory => _rootDirectory;

    public int TryRestore(string exchange, string symbol, TimeSpan interval,
      DateTimeOffset asOfUtc, MarketDataStore store, LogHelper log)
    {
      if (store == null) throw new ArgumentNullException(nameof(store));
      if (log == null) throw new ArgumentNullException(nameof(log));

      // Do not overwrite fresher observations collected in this process.
      if (store.TryGetLatestCandle(symbol, interval, out _))
        return 0;

      string path = CachePath(exchange, symbol, interval);
      if (!File.Exists(path))
        return 0;

      try
      {
        FileInfo file = new FileInfo(path);
        if (file.Length <= 0 || file.Length > MaximumCompressedFileBytes)
          throw new InvalidDataException("Unexpected cache file size.");

        List<MarketCandle> candles = new List<MarketCandle>();
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (GZipStream gzip = new GZipStream(stream, CompressionMode.Decompress))
        using (BinaryReader reader = new BinaryReader(gzip, Encoding.UTF8))
        {
          if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
            throw new InvalidDataException("Unknown normalized cache schema.");

          string fileExchange = reader.ReadString();
          string fileSymbol = reader.ReadString();
          long fileInterval = reader.ReadInt64();
          string normalizedExchange = NormalizeExchange(exchange);
          string normalizedSymbol = NormalizeSymbol(symbol);
          if (!String.Equals(fileExchange, normalizedExchange, StringComparison.Ordinal)
            || !String.Equals(fileSymbol, normalizedSymbol, StringComparison.Ordinal)
            || fileInterval != interval.Ticks)
            throw new InvalidDataException("Normalized cache identity mismatch.");

          int count = reader.ReadInt32();
          if (count <= 0 || count > MaximumFileCandles)
            throw new InvalidDataException("Invalid normalized cache candle count.");

          long previousOpenMilliseconds = -1;
          DateTimeOffset boundary = asOfUtc.ToUniversalTime();
          for (int index = 0; index < count; index++)
          {
            long openMilliseconds = reader.ReadInt64();
            long closeMilliseconds = reader.ReadInt64();
            MarketCandle candle = new MarketCandle
            {
              Symbol = normalizedSymbol,
              Interval = interval,
              OpenTimeUtc = DateTimeOffset.FromUnixTimeMilliseconds(openMilliseconds),
              CloseTimeUtc = DateTimeOffset.FromUnixTimeMilliseconds(closeMilliseconds),
              Open = reader.ReadDouble(),
              High = reader.ReadDouble(),
              Low = reader.ReadDouble(),
              Close = reader.ReadDouble(),
              BaseVolume = reader.ReadDouble(),
              QuoteVolume = reader.ReadDouble(),
              TradeCount = reader.ReadInt64(),
              IsClosed = true
            };

            // Valid Binance bars use a UTC-aligned open and inclusive close.
            // Never accept a future/unclosed observation as cached history.
            long intervalMilliseconds = checked((long)interval.TotalMilliseconds);
            if (intervalMilliseconds <= 0
              || openMilliseconds % intervalMilliseconds != 0
              || closeMilliseconds != checked(openMilliseconds + intervalMilliseconds - 1)
              || openMilliseconds <= previousOpenMilliseconds
              || candle.CloseTimeUtc > boundary)
              throw new InvalidDataException("Invalid cached candle time or ordering.");

            MarketDataStore.ValidateCandle(candle);
            candles.Add(candle);
            previousOpenMilliseconds = openMilliseconds;
          }

          if (reader.ReadInt32() != EndMarker || reader.BaseStream.ReadByte() != -1)
            throw new InvalidDataException("Normalized cache has an incomplete or unexpected trailer.");
        }

        // Parsing and validation complete before making any series visible.
        store.UpsertCandles(candles);
        return candles.Count;
      }
      catch (Exception exception) when (!(exception is OutOfMemoryException))
      {
        log.DoLogWarn("Normalized warm-start cache ignored for '" + symbol +
          "' (" + exception.GetType().Name + "); exchange backfill will repair the series.");
        // A damaged checkpoint must not block its first successful replacement
        // merely because the file was modified inside the save throttle window.
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return 0;
      }
    }

    public bool TrySave(string exchange, string symbol, TimeSpan interval,
      DateTimeOffset asOfUtc, MarketDataStore store, LogHelper log)
    {
      if (store == null) throw new ArgumentNullException(nameof(store));
      if (log == null) throw new ArgumentNullException(nameof(log));

      string path = CachePath(exchange, symbol, interval);
      string tempPath = null;
      try
      {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (_minimumSaveInterval > TimeSpan.Zero && File.Exists(path))
        {
          DateTime lastWrite = File.GetLastWriteTimeUtc(path);
          if (lastWrite <= now.UtcDateTime && now.UtcDateTime - lastWrite < _minimumSaveInterval)
            return false;
        }

        MarketDataSeriesSnapshot snapshot = store.GetSnapshot(symbol, interval, asOfUtc);
        MarketCandle[] complete = snapshot.ClosedCandles
          .Where(candle => candle.CloseTimeUtc <= DateTimeOffset.UtcNow)
          .ToArray();
        if (complete.Length == 0)
          return false;

        if (complete.Length > MaximumFileCandles)
          complete = complete.Skip(complete.Length - MaximumFileCandles).ToArray();

        Directory.CreateDirectory(Path.GetDirectoryName(path));
        tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (FileStream stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
          using (GZipStream gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
          using (BinaryWriter writer = new BinaryWriter(gzip, Encoding.UTF8))
          {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(NormalizeExchange(exchange));
            writer.Write(NormalizeSymbol(symbol));
            writer.Write(interval.Ticks);
            writer.Write(complete.Length);
            foreach (MarketCandle candle in complete)
            {
              MarketDataStore.ValidateCandle(candle);
              writer.Write(candle.OpenTimeUtc.ToUnixTimeMilliseconds());
              writer.Write(candle.CloseTimeUtc.ToUnixTimeMilliseconds());
              writer.Write(candle.Open);
              writer.Write(candle.High);
              writer.Write(candle.Low);
              writer.Write(candle.Close);
              writer.Write(candle.BaseVolume);
              writer.Write(candle.QuoteVolume);
              writer.Write(candle.TradeCount);
            }
            writer.Write(EndMarker);
          }
          stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, path, overwrite: true);
        return true;
      }
      catch (Exception exception) when (!(exception is OutOfMemoryException))
      {
        log.DoLogWarn("Normalized warm-start cache could not save '" + symbol +
          "' (" + exception.GetType().Name + "); market analysis will continue.");
        return false;
      }
      finally
      {
        if (tempPath != null)
        {
          try { if (File.Exists(tempPath)) File.Delete(tempPath); }
          catch (IOException) { }
          catch (UnauthorizedAccessException) { }
        }
      }
    }

    private string CachePath(string exchange, string symbol, TimeSpan interval)
    {
      string normalizedExchange = NormalizeExchange(exchange);
      string normalizedSymbol = NormalizeSymbol(symbol);
      if (interval <= TimeSpan.Zero)
        throw new ArgumentOutOfRangeException(nameof(interval));
      string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedSymbol)));
      return Path.Combine(_rootDirectory, normalizedExchange, interval.Ticks.ToString(), id + ".gz");
    }

    private static string NormalizeExchange(string exchange)
    {
      if (String.Equals(exchange, "BinanceFutures", StringComparison.OrdinalIgnoreCase)) return "BINANCEFUTURES";
      if (String.Equals(exchange, "Binance", StringComparison.OrdinalIgnoreCase)) return "BINANCE";
      if (String.Equals(exchange, "BinanceUS", StringComparison.OrdinalIgnoreCase)) return "BINANCEUS";
      throw new ArgumentException("Only supported normalized exchanges can use the warm-start cache.", nameof(exchange));
    }

    private static string NormalizeSymbol(string symbol)
    {
      if (String.IsNullOrWhiteSpace(symbol))
        throw new ArgumentException("A market symbol is required.", nameof(symbol));
      return symbol.Trim().ToUpperInvariant();
    }
  }
}
