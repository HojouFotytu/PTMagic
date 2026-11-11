using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Per-asset Binance long/short ratios. Each 5m provider ratio is converted
  /// to its implied long-side share; the arithmetic mean of those shares over
  /// TrendMinutes is converted back to a ratio. This is symmetric around 1.0
  /// and avoids arithmetic-averaging distortion from extreme ratio values.
  /// </summary>
  public static class FuturesLongShortRatioFeatureEngine
  {
    private static readonly string[] MetricNames =
    {
      MarketTrendMetrics.GlobalLongShortAccountRatio,
      MarketTrendMetrics.TopTraderLongShortAccountRatio,
      MarketTrendMetrics.TopTraderLongShortPositionRatio
    };

    public static bool IsMetric(string metric) =>
      MetricNames.Contains(MarketTrendMetrics.Normalize(metric), StringComparer.OrdinalIgnoreCase);

    public static bool HasMetrics(IReadOnlyCollection<string> metrics) =>
      metrics != null && metrics.Any(IsMetric);

    public static FuturesLongShortRatioSource SourceForMetric(string metric)
    {
      string name = MarketTrendMetrics.Normalize(metric);
      if (name == MarketTrendMetrics.GlobalLongShortAccountRatio)
        return FuturesLongShortRatioSource.GlobalAccount;
      if (name == MarketTrendMetrics.TopTraderLongShortAccountRatio)
        return FuturesLongShortRatioSource.TopTraderAccount;
      if (name == MarketTrendMetrics.TopTraderLongShortPositionRatio)
        return FuturesLongShortRatioSource.TopTraderPosition;
      throw new ArgumentException("Unknown long/short ratio metric: " + name, nameof(metric));
    }

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> Calculate(
      IEnumerable<string> assetSymbols, IReadOnlyCollection<string> enabledMetrics,
      int trendMinutes, DateTimeOffset asOfUtc, string exchange,
      FuturesLongShortRatioDataService dataService, bool apiKeyConfigured)
    {
      var result = new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(
        StringComparer.OrdinalIgnoreCase);
      if (!HasMetrics(enabledMetrics))
        return new ReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(result);

      string[] metrics = enabledMetrics.Where(IsMetric)
        .Select(MarketTrendMetrics.Normalize)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
      bool futures = String.Equals(exchange, "BinanceFutures", StringComparison.OrdinalIgnoreCase);

      foreach (string symbol in (assetSymbols ?? Array.Empty<string>())
        .Where(value => !String.IsNullOrWhiteSpace(value))
        .Select(FuturesLongShortRatio.NormalizeSymbol)
        .Distinct(StringComparer.OrdinalIgnoreCase))
      {
        var values = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (string metric in metrics)
        {
          FuturesLongShortRatioSource source = SourceForMetric(metric);
          string reason = !futures
            ? "Long/short ratio metrics require Binance USD-M Futures, not Binance Spot or Binance US."
            : FuturesLongShortRatio.RequiresApiKey(source) && !apiKeyConfigured
              ? "This top-trader ratio endpoint requires an optional read-only Binance Futures API key in General Settings."
              : dataService == null
                ? "Long/short ratio collection was not initialized for this raid."
                : "";

          if (reason.Length > 0)
          {
            values[metric] = new FeatureValueSnapshot { CalculatedAtUtc = asOfUtc, InvalidReason = reason };
            continue;
          }

          IReadOnlyList<FuturesLongShortRatioObservation> history =
            dataService.Snapshot(source, symbol, trendMinutes, asOfUtc);
          FeatureValueSnapshot feature = CalculateWindowRatio(
            history, source, symbol, trendMinutes, asOfUtc);
          if (!feature.IsValid)
          {
            values[metric] = new FeatureValueSnapshot
            {
              CalculatedAtUtc = asOfUtc,
              SourceDataTimestampUtc = feature.SourceDataTimestampUtc,
              SampleCount = feature.SampleCount,
              EligibleSampleCount = feature.EligibleSampleCount,
              CoveragePercent = feature.CoveragePercent,
              InvalidReason = feature.InvalidReason + " " +
                dataService.ExplainUnavailable(symbol, source, trendMinutes, asOfUtc)
            };
          }
          else
          {
            values[metric] = feature;
          }
        }

        result[symbol] = new ReadOnlyDictionary<string, FeatureValueSnapshot>(values);
      }

      return new ReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(result);
    }

    public static FeatureValueSnapshot CalculateWindowRatio(
      IEnumerable<FuturesLongShortRatioObservation> history,
      FuturesLongShortRatioSource source, string requestedSymbol,
      int trendMinutes, DateTimeOffset asOfUtc)
    {
      FeatureValueSnapshot Invalid(string message, int samples = 0, int expected = 0,
        DateTimeOffset? latest = null) => new FeatureValueSnapshot
      {
        CalculatedAtUtc = asOfUtc.ToUniversalTime(),
        SourceDataTimestampUtc = latest,
        SampleCount = samples,
        EligibleSampleCount = expected > 0 ? expected : null,
        CoveragePercent = expected > 0 ? samples * 100.0 / expected : null,
        InvalidReason = message
      };

      if (trendMinutes < 5 || trendMinutes % 5 != 0)
        return Invalid("Long/short ratio requires a positive whole 5m TrendMinutes horizon.");
      if (trendMinutes > FuturesLongShortRatio.MaximumHistoryMinutes)
        return Invalid("The configured TrendMinutes horizon exceeds Binance's 30-day long/short ratio history limit.");
      if (history == null)
        return Invalid("No Binance long/short ratio observations were supplied.");

      string symbol = FuturesLongShortRatio.NormalizeSymbol(requestedSymbol);
      long intervalMs = (long)FuturesLongShortRatio.SourcePeriod.TotalMilliseconds;
      long pinMs = asOfUtc.ToUniversalTime().ToUnixTimeMilliseconds();
      long endMs = pinMs - pinMs % intervalMs;
      long startMs = endMs - (long)trendMinutes * 60000L;
      int expected = trendMinutes / 5;

      var series = new SortedDictionary<long, FuturesLongShortRatioObservation>();
      foreach (FuturesLongShortRatioObservation row in history)
      {
        try { FuturesLongShortRatio.Validate(row, source, symbol); }
        catch (Exception ex) when (ex is ArgumentException || ex is InvalidDataException)
        {
          return Invalid("Long/short ratio observations are not a homogeneous Binance USD-M 5m series: " + ex.Message, series.Count, expected);
        }

        long timestamp = row.TimestampUtc.ToUnixTimeMilliseconds();
        if (timestamp <= startMs || timestamp > endMs) continue;
        if (series.TryGetValue(timestamp, out FuturesLongShortRatioObservation previous))
        {
          if (previous.LongShortRatio != row.LongShortRatio ||
            previous.LongSideShare != row.LongSideShare ||
            previous.ShortSideShare != row.ShortSideShare)
            return Invalid("Conflicting long/short ratio observations share an identical timestamp.", series.Count, expected);
        }
        else
        {
          series.Add(timestamp, row);
        }
      }

      DateTimeOffset latestTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(endMs);
      if (series.Count != expected)
        return Invalid("Long/short ratio history is incomplete: " + series.Count + "/" + expected +
          " observations are present for the exact " + trendMinutes + " minute window.", series.Count, expected,
          series.Count > 0 ? series.Last().Value.TimestampUtc : null);

      double longShareSum = 0.0;
      for (int index = 1; index <= expected; index++)
      {
        long timestamp = startMs + index * intervalMs;
        if (!series.TryGetValue(timestamp, out FuturesLongShortRatioObservation observation))
          return Invalid("Missing exact 5m long/short ratio endpoint at " +
            DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToString("O") + ".", series.Count, expected,
            series.Count > 0 ? series.Last().Value.TimestampUtc : null);

        double ratio = (double)observation.LongShortRatio;
        if (!double.IsFinite(ratio) || ratio < 0.0)
          return Invalid("Long/short ratio contains a negative or nonfinite provider value.", series.Count, expected,
            observation.TimestampUtc);

        double impliedLongShare = ratio / (1.0 + ratio);
        if (!double.IsFinite(impliedLongShare) || impliedLongShare < 0.0 || impliedLongShare > 1.0)
          return Invalid("Long/short ratio could not be converted to a finite directional share.", series.Count, expected,
            observation.TimestampUtc);
        longShareSum += impliedLongShare;
      }

      double meanLongShare = longShareSum / expected;
      if (!double.IsFinite(meanLongShare) || meanLongShare < 0.0 || meanLongShare >= 1.0)
        return Invalid("The mean long-side share does not produce a finite long/short ratio.", series.Count, expected, latestTimestamp);

      double meanRatio = meanLongShare / (1.0 - meanLongShare);
      if (!double.IsFinite(meanRatio) || meanRatio < 0.0)
        return Invalid("The calculated long/short ratio is outside the finite numeric range.", series.Count, expected, latestTimestamp);

      return new FeatureValueSnapshot
      {
        CalculatedAtUtc = asOfUtc.ToUniversalTime(),
        SourceDataTimestampUtc = latestTimestamp,
        SampleCount = expected,
        EligibleSampleCount = expected,
        CoveragePercent = 100.0,
        Value = meanRatio,
        IsValid = true
      };
    }
  }
}
