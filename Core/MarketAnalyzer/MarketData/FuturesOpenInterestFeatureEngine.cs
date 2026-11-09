using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Phase 2 per-asset changes calculated from independently acquired
  /// Binance Futures historical OI. Missing provider rows remain UNAVAILABLE.
  /// </summary>
  public static class FuturesOpenInterestFeatureEngine
  {
    public static bool IsMetric(string metric)
    {
      string name = MarketTrendMetrics.Normalize(metric);
      return name == MarketTrendMetrics.OpenInterestChange ||
        name == MarketTrendMetrics.OpenInterestValueChange;
    }

    public static bool HasMetrics(IReadOnlyCollection<string> metrics) =>
      metrics != null && metrics.Any(IsMetric);

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> Calculate(
      IEnumerable<string> assetSymbols, IReadOnlyCollection<string> enabledMetrics,
      int trendMinutes, DateTimeOffset asOfUtc, string exchange,
      FuturesOpenInterestDataService dataService, int retentionHours)
    {
      if (!HasMetrics(enabledMetrics))
        return new ReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(
          new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>());
      bool supported = String.Equals(exchange, "BinanceFutures", StringComparison.OrdinalIgnoreCase);
      string[] metrics = enabledMetrics.Where(IsMetric).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
      var result = new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(
        StringComparer.OrdinalIgnoreCase);
      foreach (string symbol in (assetSymbols ?? Array.Empty<string>()).Where(s => !String.IsNullOrWhiteSpace(s))
        .Select(FuturesOpenInterest.NormalizeSymbol).Distinct(StringComparer.OrdinalIgnoreCase))
      {
        var values = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (string metric in metrics)
        {
          string reason = !supported
            ? "Open-interest metrics are available only on Binance USD-M Futures, not Binance Spot or Binance US."
            : dataService == null
              ? "Open-interest collection was not initialized for this raid."
              : retentionHours < 1 || retentionHours > FuturesOpenInterestDataService.MaximumRetentionHours
                ? "OpenInterestRetentionHours is outside its supported range."
                : (long)trendMinutes > (long)retentionHours * 60
                  ? "TrendMinutes exceeds OpenInterestRetentionHours."
                  : "";
          if (reason.Length > 0)
          {
            values[metric] = new FeatureValueSnapshot { CalculatedAtUtc = asOfUtc, InvalidReason = reason };
            continue;
          }

          IReadOnlyList<FuturesOpenInterestObservation> observations =
            dataService.Snapshot(symbol, asOfUtc, retentionHours);
          FeatureValueSnapshot feature = FuturesOpenInterest.CalculateChangePercent(
            observations, symbol, trendMinutes, asOfUtc,
            useReportedValue: metric == MarketTrendMetrics.OpenInterestValueChange);
          if (!feature.IsValid)
            feature = new FeatureValueSnapshot
            {
              CalculatedAtUtc = asOfUtc, SampleCount = feature.SampleCount, IsValid = false,
              SourceDataTimestampUtc = feature.SourceDataTimestampUtc,
              InvalidReason = feature.InvalidReason + " " +
                dataService.ExplainUnavailable(symbol, trendMinutes, asOfUtc, retentionHours)
            };
          values[metric] = feature;
        }
        result[symbol] = new ReadOnlyDictionary<string, FeatureValueSnapshot>(values);
      }
      return new ReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(result);
    }
  }
}
