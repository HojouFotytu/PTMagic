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
        .Select(FuturesOpenInterest.NormalizeSymbol).Distinct(StringComparer.OrdinalIgnoreCase))
      {
        var values = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
        bool valid = false;
        IReadOnlyList<FuturesFundingObservation> history = Array.Empty<FuturesFundingObservation>();
        if (futures && data != null)
          valid = data.TrySnapshot(symbol, trendMinutes, asOfUtc, out history);
        var regular = history.Where(row => row.IsRegular).OrderBy(row => row.FundingTimeUtc).ToArray();

        foreach (string metric in metrics)
        {
          string error = !futures ? "Funding metrics require BinanceFutures, not Spot/BinanceUS."
            : !valid ? "Funding history is not completely verified over this MarketTrend window."
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
