using System;
using System.Collections.Generic;
using System.Linq;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer.MarketData
{
  public enum MarketTrendMetricScope
  {
    Global,
    Asset,
    Both
  }

  /// <summary>
  /// Canonical names and scope rules for metrics exposed through MarketTrend.Metrics
  /// and Trigger/OffTrigger.Metric. An empty Metrics value enables the
  /// normalized PriceTrend by default. Non-empty Metrics values are explicit.
  /// </summary>
  public static class MarketTrendMetrics
  {
    public const string PriceTrend = "PriceTrend";
    public const string MedianPriceTrend = "MedianPriceTrend";
    public const string MeanMedianSpread = "MeanMedianSpread";
    public const string Breadth = "Breadth";
    public const string Dispersion = "Dispersion";
    public const string RelativeStrengthMarket = "RelativeStrengthMarket";
    public const string RelativeStrengthBTC = "RelativeStrengthBTC";
    public const string Concentration = "Concentration";
    public const string ATRP = "ATRP";
    public const string MedianATRP = "MedianATRP";
    public const string VolatilityBreadth = "VolatilityBreadth";
    public const string VolumeExpansion = "VolumeExpansion";
    public const string MedianVolumeExpansion = "MedianVolumeExpansion";
    public const string VolumeExpansionBreadth = "VolumeExpansionBreadth";
    public const string StrongVolumeExpansionBreadth = "StrongVolumeExpansionBreadth";
    public const string VolumeConcentration = "VolumeConcentration";
    public const string TopNVolumeShare = "TopNVolumeShare";
    public const string BTCVolumeShare = "BTCVolumeShare";
    public const string BTCETHVolumeShare = "BTCETHVolumeShare";
    public const string CorrelationToBTC = "CorrelationToBTC";
    public const string MeanCorrelationToBTC = "MeanCorrelationToBTC";
    public const string MedianCorrelationToBTC = "MedianCorrelationToBTC";
    // Phase 2 per-asset derivatives: quantity and provider-reported value
    // changes are separate percentage metrics; no arbitrary notional inference.
    public const string OpenInterestChange = "OpenInterestChange";
    public const string OpenInterestValueChange = "OpenInterestValueChange";
    public const string FundingRate = "FundingRate";
    public const string FundingSum = "FundingSum";

    private static readonly Dictionary<string, MarketTrendMetricScope> KnownMetrics =
      new Dictionary<string, MarketTrendMetricScope>(StringComparer.OrdinalIgnoreCase)
      {
        [PriceTrend] = MarketTrendMetricScope.Both,
        [MedianPriceTrend] = MarketTrendMetricScope.Global,
        [MeanMedianSpread] = MarketTrendMetricScope.Global,
        [Breadth] = MarketTrendMetricScope.Global,
        [Dispersion] = MarketTrendMetricScope.Global,
        [RelativeStrengthMarket] = MarketTrendMetricScope.Asset,
        [RelativeStrengthBTC] = MarketTrendMetricScope.Asset,
        [Concentration] = MarketTrendMetricScope.Global,
        [ATRP] = MarketTrendMetricScope.Asset,
        [MedianATRP] = MarketTrendMetricScope.Global,
        [VolatilityBreadth] = MarketTrendMetricScope.Global,
        [VolumeExpansion] = MarketTrendMetricScope.Asset,
        [MedianVolumeExpansion] = MarketTrendMetricScope.Global,
        [VolumeExpansionBreadth] = MarketTrendMetricScope.Global,
        [StrongVolumeExpansionBreadth] = MarketTrendMetricScope.Global,
        [VolumeConcentration] = MarketTrendMetricScope.Global,
        [TopNVolumeShare] = MarketTrendMetricScope.Global,
        [BTCVolumeShare] = MarketTrendMetricScope.Global,
        [BTCETHVolumeShare] = MarketTrendMetricScope.Global,
        [CorrelationToBTC] = MarketTrendMetricScope.Asset,
        [MeanCorrelationToBTC] = MarketTrendMetricScope.Global,
        [MedianCorrelationToBTC] = MarketTrendMetricScope.Global,
        [OpenInterestChange] = MarketTrendMetricScope.Asset,
        [OpenInterestValueChange] = MarketTrendMetricScope.Asset,
        [FundingRate] = MarketTrendMetricScope.Asset,
        [FundingSum] = MarketTrendMetricScope.Asset
      };

    public static IReadOnlyCollection<string> Names => KnownMetrics.Keys.ToArray();

    public static string Normalize(string metric)
    {
      if (String.IsNullOrWhiteSpace(metric))
      {
        return PriceTrend;
      }

      string candidate = metric.Trim();
      foreach (string knownMetric in KnownMetrics.Keys)
      {
        if (knownMetric.Equals(candidate, StringComparison.OrdinalIgnoreCase))
        {
          return knownMetric;
        }
      }

      return candidate;
    }

    public static bool IsKnown(string metric)
    {
      return KnownMetrics.ContainsKey(Normalize(metric));
    }

    public static bool TryGetScope(string metric, out MarketTrendMetricScope scope)
    {
      return KnownMetrics.TryGetValue(Normalize(metric), out scope);
    }

    public static IReadOnlyCollection<string> ParseEnabled(MarketTrend marketTrend)
    {
      HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

      if (marketTrend == null)
      {
        return result.ToArray();
      }
      if (String.IsNullOrWhiteSpace(marketTrend.Metrics))
      {
        result.Add(PriceTrend);
        return result.ToArray();
      }

      foreach (string token in marketTrend.Metrics.Split(',', StringSplitOptions.RemoveEmptyEntries))
      {
        string metric = Normalize(token);
        if (!String.IsNullOrWhiteSpace(metric))
        {
          result.Add(metric);
        }
      }

      return result.ToArray();
    }

    public static bool IsEnabled(MarketTrend marketTrend, string metric)
    {
      string normalized = Normalize(metric);
      return ParseEnabled(marketTrend).Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }
  }
}
