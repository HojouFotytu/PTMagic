using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Thread-safe rolling in-memory store for normalized per-symbol candle series.
  /// </summary>
  public sealed class MarketDataStore
  {
    private readonly ConcurrentDictionary<MarketSeriesKey, SeriesBuffer> _series = new ConcurrentDictionary<MarketSeriesKey, SeriesBuffer>();
    private int _maxCandlesPerSeries;

    public MarketDataStore(int maxCandlesPerSeries = 2000)
    {
      if (maxCandlesPerSeries <= 0)
      {
        throw new ArgumentOutOfRangeException(nameof(maxCandlesPerSeries), "Maximum candles per series must be greater than zero.");
      }

      _maxCandlesPerSeries = maxCandlesPerSeries;
    }

    public int MaxCandlesPerSeries => _maxCandlesPerSeries;
    public int SeriesCount => _series.Count;

    public void SetMaxCandlesPerSeries(int maxCandlesPerSeries)
    {
      if (maxCandlesPerSeries <= 0)
      {
        throw new ArgumentOutOfRangeException(nameof(maxCandlesPerSeries), "Maximum candles per series must be greater than zero.");
      }

      _maxCandlesPerSeries = maxCandlesPerSeries;
      foreach (SeriesBuffer buffer in _series.Values)
      {
        buffer.Trim(maxCandlesPerSeries);
      }
    }

    public void UpsertCandle(MarketCandle candle)
    {
      ValidateCandle(candle);

      MarketSeriesKey key = new MarketSeriesKey(candle.Symbol, candle.Interval);
      SeriesBuffer buffer = _series.GetOrAdd(key, _ => new SeriesBuffer());
      buffer.Upsert(candle, MaxCandlesPerSeries);
    }

    public void UpsertCandles(IEnumerable<MarketCandle> candles)
    {
      if (candles == null)
      {
        throw new ArgumentNullException(nameof(candles));
      }

      foreach (MarketCandle candle in candles)
      {
        UpsertCandle(candle);
      }
    }

    public MarketDataSeriesSnapshot GetSnapshot(string symbol, TimeSpan interval, DateTimeOffset asOfUtc, TimeSpan? staleAfter = null)
    {
      return GetSnapshot(new MarketSeriesKey(symbol, interval), asOfUtc, staleAfter);
    }

    public MarketDataSeriesSnapshot GetSnapshot(MarketSeriesKey key, DateTimeOffset asOfUtc, TimeSpan? staleAfter = null)
    {
      TimeSpan effectiveStaleAfter = staleAfter ?? key.Interval + key.Interval;
      if (effectiveStaleAfter <= TimeSpan.Zero)
      {
        throw new ArgumentOutOfRangeException(nameof(staleAfter), "Stale-data tolerance must be greater than zero.");
      }

      MarketCandle[] candles = _series.TryGetValue(key, out SeriesBuffer buffer)
        ? buffer.GetCandles()
        : Array.Empty<MarketCandle>();

      return new MarketDataSeriesSnapshot(key, candles, asOfUtc, effectiveStaleAfter);
    }

    public bool TryGetLatestCandle(string symbol, TimeSpan interval, out MarketCandle candle)
    {
      MarketSeriesKey key = new MarketSeriesKey(symbol, interval);
      if (_series.TryGetValue(key, out SeriesBuffer buffer))
      {
        return buffer.TryGetLatest(out candle);
      }

      candle = null;
      return false;
    }

    public bool TryGetLatestClosedCandle(string symbol, TimeSpan interval, out MarketCandle candle)
    {
      MarketSeriesKey key = new MarketSeriesKey(symbol, interval);
      if (_series.TryGetValue(key, out SeriesBuffer buffer))
      {
        return buffer.TryGetLatestClosed(out candle);
      }

      candle = null;
      return false;
    }

    public IReadOnlyList<MarketSeriesKey> GetSeriesKeys()
    {
      return _series.Keys
        .OrderBy(key => key.Symbol, StringComparer.OrdinalIgnoreCase)
        .ThenBy(key => key.Interval)
        .ToArray();
    }

    public bool RemoveSeries(string symbol, TimeSpan interval)
    {
      return _series.TryRemove(new MarketSeriesKey(symbol, interval), out _);
    }

    public void Clear()
    {
      _series.Clear();
    }

    internal static void ValidateCandle(MarketCandle candle)
    {
      if (candle == null)
      {
        throw new ArgumentNullException(nameof(candle));
      }
      if (String.IsNullOrWhiteSpace(candle.Symbol))
      {
        throw new ArgumentException("Candle symbol is required.", nameof(candle));
      }
      if (candle.Interval <= TimeSpan.Zero)
      {
        throw new ArgumentException("Candle interval must be greater than zero.", nameof(candle));
      }
      if (candle.OpenTimeUtc == default || candle.CloseTimeUtc == default || candle.CloseTimeUtc <= candle.OpenTimeUtc)
      {
        throw new ArgumentException("Candle timestamps are invalid.", nameof(candle));
      }
      if (candle.OpenTimeUtc.Offset != TimeSpan.Zero || candle.CloseTimeUtc.Offset != TimeSpan.Zero)
      {
        throw new ArgumentException("Candle timestamps must be normalized to UTC.", nameof(candle));
      }
      if (!IsValidPrice(candle.Open) || !IsValidPrice(candle.High) || !IsValidPrice(candle.Low) || !IsValidPrice(candle.Close))
      {
        throw new ArgumentException("Candle OHLC prices must be finite positive values.", nameof(candle));
      }
      if (candle.High < Math.Max(candle.Open, candle.Close) || candle.Low > Math.Min(candle.Open, candle.Close) || candle.High < candle.Low)
      {
        throw new ArgumentException("Candle OHLC values are internally inconsistent.", nameof(candle));
      }
      if (!double.IsFinite(candle.BaseVolume) || candle.BaseVolume < 0 || !double.IsFinite(candle.QuoteVolume) || candle.QuoteVolume < 0)
      {
        throw new ArgumentException("Candle volumes must be finite non-negative values.", nameof(candle));
      }
      if (candle.TradeCount < 0)
      {
        throw new ArgumentException("Candle trade count cannot be negative.", nameof(candle));
      }
    }

    private static bool IsValidPrice(double value)
    {
      return double.IsFinite(value) && value > 0;
    }

    private sealed class SeriesBuffer
    {
      private readonly object _sync = new object();
      private readonly SortedDictionary<long, MarketCandle> _candles = new SortedDictionary<long, MarketCandle>();

      public void Upsert(MarketCandle candle, int maxCandles)
      {
        long key = candle.OpenTimeUtc.ToUnixTimeMilliseconds();

        lock (_sync)
        {
          _candles[key] = candle;
          TrimUnsafe(maxCandles);
        }
      }

      public void Trim(int maxCandles)
      {
        lock (_sync)
        {
          TrimUnsafe(maxCandles);
        }
      }

      private void TrimUnsafe(int maxCandles)
      {
        while (_candles.Count > maxCandles)
        {
          long oldestKey = _candles.Keys.First();
          _candles.Remove(oldestKey);
        }
      }

      public MarketCandle[] GetCandles()
      {
        lock (_sync)
        {
          return _candles.Values.ToArray();
        }
      }

      public bool TryGetLatest(out MarketCandle candle)
      {
        lock (_sync)
        {
          if (_candles.Count == 0)
          {
            candle = null;
            return false;
          }

          candle = _candles.Values.Last();
          return true;
        }
      }

      public bool TryGetLatestClosed(out MarketCandle candle)
      {
        lock (_sync)
        {
          foreach (MarketCandle candidate in _candles.Values.Reverse())
          {
            if (candidate.IsClosed)
            {
              candle = candidate;
              return true;
            }
          }

          candle = null;
          return false;
        }
      }
    }
  }
}
