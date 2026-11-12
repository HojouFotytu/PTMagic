using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Measures executed taker-buy quote volume within the latest fully closed,
  /// UTC-aligned MarketTrend duration using the shared normalized kline store.
  /// Ratios are dimensionless; no independent exchange acquisition occurs.
  /// </summary>
  public static class TakerFlowFeatureEngine
  {
    public static bool IsMetric(string metric)
    {
      string name = MarketTrendMetrics.Normalize(metric);
      return name == MarketTrendMetrics.TakerBuyRatio ||
        name == MarketTrendMetrics.TakerImbalance;
    }

    public static bool HasMetrics(IReadOnlyCollection<string> metrics) =>
      metrics != null && metrics.Any(IsMetric);

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> Calculate(
      IEnumerable<string> assetSymbols, IReadOnlyCollection<string> enabled,
      MarketDataStore store, TimeSpan interval, int trendMinutes, DateTimeOffset asOfUtc)
    {
      var result = new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(
        StringComparer.OrdinalIgnoreCase);
      if (!HasMetrics(enabled)) return new ReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(result);

      string[] requested = enabled.Where(IsMetric).Select(MarketTrendMetrics.Normalize)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
      foreach (string symbol in (assetSymbols ?? Array.Empty<string>())
        .Where(x => !String.IsNullOrWhiteSpace(x))
        .Select(x => x.Trim().ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase))
      {
        MarketDataSeriesSnapshot snapshot = store?.GetSnapshot(symbol, interval, asOfUtc);
        FeatureValueSnapshot buy = CalculateRatio(snapshot, trendMinutes, asOfUtc);
        var values = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (string metric in requested)
        {
          if (metric == MarketTrendMetrics.TakerBuyRatio)
          {
            values[metric] = buy;
          }
          else
          {
            values[metric] = !buy.IsValid || !buy.Value.HasValue
              ? new FeatureValueSnapshot
              {
                CalculatedAtUtc = buy.CalculatedAtUtc,
                SourceDataTimestampUtc = buy.SourceDataTimestampUtc,
                SampleCount = buy.SampleCount,
                IsStale = buy.IsStale,
                InvalidReason = buy.InvalidReason
              }
              : new FeatureValueSnapshot
              {
                CalculatedAtUtc = buy.CalculatedAtUtc,
                SourceDataTimestampUtc = buy.SourceDataTimestampUtc,
                SampleCount = buy.SampleCount,
                IsValid = true,
                Value = 2.0 * buy.Value.Value - 1.0
              };
          }
        }
        result[symbol] = new ReadOnlyDictionary<string, FeatureValueSnapshot>(values);
      }
      return new ReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(result);
    }

    private static bool TryFindClosedCandle(IReadOnlyList<MarketCandle> candles,
      long expectedOpenMilliseconds, out MarketCandle candle)
    {
      int left = 0, right = candles.Count - 1;
      while (left <= right)
      {
        int middle = left + (right - left) / 2;
        MarketCandle current = candles[middle];
        long open = current.OpenTimeUtc.ToUnixTimeMilliseconds();
        if (open == expectedOpenMilliseconds)
        {
          candle = current;
          return true;
        }
        if (open < expectedOpenMilliseconds) left = middle + 1;
        else right = middle - 1;
      }
      candle = null;
      return false;
    }

    private static FeatureValueSnapshot CalculateRatio(
      MarketDataSeriesSnapshot snapshot, int minutes, DateTimeOffset asOfUtc)
    {
      FeatureValueSnapshot Invalid(string reason, int samples = 0, bool stale = false) =>
        new FeatureValueSnapshot
        {
          CalculatedAtUtc = asOfUtc, SampleCount = samples, IsStale = stale,
          InvalidReason = reason
        };

      if (snapshot == null || snapshot.Interval <= TimeSpan.Zero)
        return Invalid("No normalized source candles are available for taker flow.");
      if (minutes <= 0 || TimeSpan.FromMinutes(minutes).Ticks % snapshot.Interval.Ticks != 0)
        return Invalid("Taker flow requires TrendMinutes to be a multiple of the normalized candle interval.");
      if (snapshot.IsStale)
        return Invalid("Normalized source candles for taker flow are stale.", stale: true);

      long intervalMs = checked((long)snapshot.Interval.TotalMilliseconds);
      long trendMs = checked((long)TimeSpan.FromMinutes(minutes).TotalMilliseconds);
      long asOfMs = asOfUtc.ToUniversalTime().ToUnixTimeMilliseconds();
      long firstOpen = asOfMs / trendMs * trendMs - trendMs;
      DateTimeOffset expectedCloseUtc = DateTimeOffset.FromUnixTimeMilliseconds(firstOpen + trendMs - 1);
      IReadOnlyList<MarketCandle> candles = snapshot.ClosedCandles;

      double total = 0;
      double bought = 0;
      int count = 0;
      for (long open = firstOpen; open < firstOpen + trendMs; open += intervalMs)
      {
        if (!TryFindClosedCandle(candles, open, out MarketCandle candle) ||
          candle.CloseTimeUtc.ToUnixTimeMilliseconds() != open + intervalMs - 1 ||
          !candle.IsClosed || !candle.TakerBuyQuoteVolume.HasValue)
          return Invalid("Incomplete or unverified taker-buy quote-volume history for the latest complete MarketTrend period.", count);

        double q = candle.QuoteVolume;
        double b = candle.TakerBuyQuoteVolume.Value;
        if (!double.IsFinite(q) || q < 0 || !double.IsFinite(b) || b < 0 || b > q)
          return Invalid("Invalid taker-buy or total quote volume in the requested period.", count);
        total += q;
        bought += b;
        count++;
      }
      if (!double.IsFinite(total) || !double.IsFinite(bought) || total <= 0 ||
        bought < 0 || bought > total)
        return Invalid("Taker flow requires finite positive total quote volume and valid taker-buy volume.", count);

      double ratio = bought / total;
      if (!double.IsFinite(ratio) || ratio < 0 || ratio > 1)
        return Invalid("Taker-buy ratio is outside the valid [0,1] interval.", count);

      return new FeatureValueSnapshot
      {
        CalculatedAtUtc = asOfUtc,
        SourceDataTimestampUtc = expectedCloseUtc,
        SampleCount = count,
        Value = ratio,
        IsValid = true
      };
    }
  }
}
