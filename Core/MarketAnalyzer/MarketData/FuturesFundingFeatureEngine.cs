using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// FundingRate: latest REGULAR settlement within the MarketTrend window.
  /// FundingSum: sum of all REGULAR settlements in that window.
  /// Both are percentage points (native decimal rate times 100); no assumed
  /// 8-hour cadence, invented zero for incomplete source, or annualization.
  /// </summary>
  public static class FuturesFundingFeatureEngine
  {
    public static bool IsMetric(string metric)
    {
      string name = MarketTrendMetrics.Normalize(metric);
      return name == MarketTrendMetrics.FundingRate ||
        name == MarketTrendMetrics.FundingSum;
    }

    public static bool HasMetrics(IReadOnlyCollection<string> metrics) =>
      metrics != null && metrics.Any(IsMetric);

    public static bool IsGlobalMetric(string metric)
    {
      string name = MarketTrendMetrics.Normalize(metric);
      return name == MarketTrendMetrics.MedianFundingRate ||
        name == MarketTrendMetrics.PositiveFundingBreadth ||
        name == MarketTrendMetrics.NegativeFundingBreadth;
    }

    public static bool HasGlobalMetrics(IReadOnlyCollection<string> metrics) =>
      metrics != null && metrics.Any(IsGlobalMetric);

    public static bool HasAnyMetrics(IReadOnlyCollection<string> metrics) =>
      HasMetrics(metrics) || HasGlobalMetrics(metrics);

    /// <summary>
    /// Aggregate latest source-verified Regular settlement rates across exactly
    /// the frozen global market population; unverified rates never become zero.
    /// </summary>
    public static IReadOnlyDictionary<string, FeatureValueSnapshot> CalculateGlobal(
      IEnumerable<string> globalSymbols, IReadOnlyCollection<string> enabled,
      IReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> assetMetrics,
      DateTimeOffset asOfUtc)
    {
      var results = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
      if (!HasGlobalMetrics(enabled))
        return new ReadOnlyDictionary<string, FeatureValueSnapshot>(results);

      string[] symbols = (globalSymbols ?? Array.Empty<string>())
        .Where(symbol => !String.IsNullOrWhiteSpace(symbol))
        .Select(FuturesFunding.NormalizeSymbol)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
      var valid = new List<FeatureValueSnapshot>();
      foreach (string symbol in symbols)
      {
        if (assetMetrics != null && assetMetrics.TryGetValue(symbol, out var features) &&
          features != null && features.TryGetValue(MarketTrendMetrics.FundingRate, out var rate) &&
          rate != null && rate.IsValid && !rate.IsStale && rate.Value.HasValue &&
          Double.IsFinite(rate.Value.Value))
          valid.Add(rate);
      }

      double[] ordered = valid.Select(rate => rate.Value.Value).OrderBy(rate => rate).ToArray();
      DateTimeOffset? oldestSource = valid.Count > 0 &&
        valid.All(rate => rate.SourceDataTimestampUtc.HasValue)
        ? valid.Min(rate => rate.SourceDataTimestampUtc.Value)
        : (DateTimeOffset?)null;

      foreach (string metric in enabled.Where(IsGlobalMetric).Select(MarketTrendMetrics.Normalize)
        .Distinct(StringComparer.OrdinalIgnoreCase))
      {
        double? value = null;
        if (ordered.Length > 0)
        {
          switch (metric)
          {
            case MarketTrendMetrics.MedianFundingRate:
              int middle = ordered.Length / 2;
              value = ordered.Length % 2 == 1 ? ordered[middle]
                : ordered[middle - 1] + (ordered[middle] - ordered[middle - 1]) / 2.0;
              break;
            case MarketTrendMetrics.PositiveFundingBreadth:
              value = ordered.Count(rate => rate > 0) * 100.0 / ordered.Length;
              break;
            case MarketTrendMetrics.NegativeFundingBreadth:
              value = ordered.Count(rate => rate < 0) * 100.0 / ordered.Length;
              break;
          }
        }
        var raw = new FeatureValueSnapshot
        {
          CalculatedAtUtc = asOfUtc, SourceDataTimestampUtc = oldestSource,
          Value = value, IsValid = value.HasValue && Double.IsFinite(value.Value),
          SampleCount = valid.Count,
          InvalidReason = value.HasValue ? "" : "No valid latest Regular funding settlement rates."
        };
        results[metric] = MarketMetricCoverage.Apply(raw, valid.Count, symbols.Length, asOfUtc);
      }
      return new ReadOnlyDictionary<string, FeatureValueSnapshot>(results);
    }

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> Calculate(
      IEnumerable<string> assetSymbols, IReadOnlyCollection<string> enabled,
      int trendMinutes, DateTimeOffset asOfUtc, string exchange,
      FuturesFundingDataService data)
    {
      var result = new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(StringComparer.OrdinalIgnoreCase);
      if (!HasMetrics(enabled))
        return new ReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(result);
      string[] metrics = enabled.Where(IsMetric).Select(MarketTrendMetrics.Normalize)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
      bool futures = String.Equals(exchange, "BinanceFutures", StringComparison.OrdinalIgnoreCase);
      foreach (string symbol in (assetSymbols ?? Array.Empty<string>()).Where(x => !String.IsNullOrWhiteSpace(x))
        .Select(FuturesFunding.NormalizeSymbol).Distinct(StringComparer.OrdinalIgnoreCase))
      {
        var values = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
        bool valid = false;
        IReadOnlyList<FuturesFundingObservation> history = Array.Empty<FuturesFundingObservation>();
        if (futures && data != null)
          valid = data.TrySnapshot(symbol, trendMinutes, asOfUtc, out history);
        var regular = history.Where(row => row.IsRegular).OrderBy(row => row.FundingTimeUtc).ToArray();
        // Older/partial payloads can omit rateType. Do not turn an
        // unclassified settlement into a fabricated zero regular rate.
        bool unclassified = history.Any(row =>
          !row.IsRegular && !String.Equals(row.RateType, "Special", StringComparison.OrdinalIgnoreCase));

        foreach (string metric in metrics)
        {
          string error = !futures ? "Funding metrics require BinanceFutures, not Spot/BinanceUS."
            : !valid ? "Funding history is not completely verified over this MarketTrend window."
            : unclassified ? "Funding history includes an unclassified rateType; cannot infer Regular or Special."
            : metric == MarketTrendMetrics.FundingRate && regular.Length == 0
              ? "No regular funding settlement occurred inside the configured MarketTrend window."
              : "";
          if (error.Length > 0)
          {
            values[metric] = new FeatureValueSnapshot { CalculatedAtUtc = asOfUtc, InvalidReason = error };
            continue;
          }
          try
          {
            decimal rate = metric == MarketTrendMetrics.FundingRate
              ? regular[regular.Length - 1].NativeRate
              : regular.Aggregate(0m, (sum, row) => checked(sum + row.NativeRate));
            double percentagePoints = (double)(rate * 100m);
            values[metric] = new FeatureValueSnapshot
            {
              CalculatedAtUtc = asOfUtc, Value = percentagePoints, IsValid = Double.IsFinite(percentagePoints),
              SourceDataTimestampUtc = regular.Length == 0 ? null : regular[regular.Length - 1].FundingTimeUtc,
              SampleCount = regular.Length,
              InvalidReason = Double.IsFinite(percentagePoints) ? "" : "Funding metric is not finite."
            };
          }
          catch (OverflowException)
          {
            values[metric] = new FeatureValueSnapshot
            { CalculatedAtUtc = asOfUtc, InvalidReason = "Funding rate calculation overflowed." };
          }
        }
        result[symbol] = new ReadOnlyDictionary<string, FeatureValueSnapshot>(values);
      }
      return new ReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(result);
    }
  }
}
