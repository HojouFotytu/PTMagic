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
    public const string Top50Breadth = "Top50Breadth";
    public const string Top75Breadth = "Top75Breadth";
    public const string Top100Breadth = "Top100Breadth";
    public const string MedianReturnInPriorATR = "MedianReturnInPriorATR";
    public const string MedianReturnInATR = "MedianReturnInATR";
    public const string DrawdownSizeInATR = "DrawdownSizeInATR";
    public const string DrawdownRecoveryPercent = "DrawdownRecoveryPercent";
    public const string DrawdownParticipationRecoveryPercent = "DrawdownParticipationRecoveryPercent";
    public const string DrawdownLeaderOverlapPercent = "DrawdownLeaderOverlapPercent";
    public const string RallySizeInATR = "RallySizeInATR";
    public const string RallyRetracementPercent = "RallyRetracementPercent";
    public const string RallyParticipationReversalPercent = "RallyParticipationReversalPercent";
    public const string RallyLeaderOverlapPercent = "RallyLeaderOverlapPercent";
    public const string ReturnInATR = "ReturnInATR";
    public const string RelativeStrengthInATR = "RelativeStrengthInATR";
    public const string PriceTrend = "PriceTrend";
    public const string MedianPriceTrend = "MedianPriceTrend";
    public const string PricePathEfficiency = "PricePathEfficiency";
    public const string MedianPricePathEfficiency = "MedianPricePathEfficiency";
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
    // Retained Futures derivatives are settled funding rates.
    public const string FundingRate = "FundingRate";
    public const string FundingSum = "FundingSum";
    // Market-wide derivatives metrics use the existing settled funding source.
    public const string MedianFundingRate = "MedianFundingRate";
    public const string PositiveFundingBreadth = "PositiveFundingBreadth";
    public const string NegativeFundingBreadth = "NegativeFundingBreadth";
    public const string ExtremePositiveFundingBreadth = "ExtremePositiveFundingBreadth";
    public const string ExtremeNegativeFundingBreadth = "ExtremeNegativeFundingBreadth";
    public const string TakerBuyRatio = "TakerBuyRatio";
    public const string TakerImbalance = "TakerImbalance";
    public const string MedianTakerBuyRatio = "MedianTakerBuyRatio";
    public const string BuyerDominanceBreadth = "BuyerDominanceBreadth";
    public const string StrongBuyerDominanceBreadth = "StrongBuyerDominanceBreadth";
    public const string TakerImbalanceDispersion = "TakerImbalanceDispersion";
    public const string BuyerFlowPriceConfirmation = "BuyerFlowPriceConfirmation";
    public const string SellerFlowPriceConfirmation = "SellerFlowPriceConfirmation";
    public const string BuyerFlowPriceDivergence = "BuyerFlowPriceDivergence";
    public const string SellerFlowPriceDivergence = "SellerFlowPriceDivergence";
    public const string BuyerFlowPriceConfirmationVolumeShare = "BuyerFlowPriceConfirmationVolumeShare";
    public const string SellerFlowPriceConfirmationVolumeShare = "SellerFlowPriceConfirmationVolumeShare";
    public const string BuyerFlowPriceDivergenceVolumeShare = "BuyerFlowPriceDivergenceVolumeShare";
    public const string SellerFlowPriceDivergenceVolumeShare = "SellerFlowPriceDivergenceVolumeShare";

    private static readonly Dictionary<string, MarketTrendMetricScope> KnownMetrics =
      new Dictionary<string, MarketTrendMetricScope>(StringComparer.OrdinalIgnoreCase)
      {
        [Top50Breadth] = MarketTrendMetricScope.Global,
        [Top75Breadth] = MarketTrendMetricScope.Global,
        [Top100Breadth] = MarketTrendMetricScope.Global,
        [MedianReturnInPriorATR] = MarketTrendMetricScope.Global,
        [MedianReturnInATR] = MarketTrendMetricScope.Global,
        [DrawdownSizeInATR] = MarketTrendMetricScope.Global,
        [DrawdownRecoveryPercent] = MarketTrendMetricScope.Global,
        [DrawdownParticipationRecoveryPercent] = MarketTrendMetricScope.Global,
        [DrawdownLeaderOverlapPercent] = MarketTrendMetricScope.Global,
        [RallySizeInATR] = MarketTrendMetricScope.Global,
        [RallyRetracementPercent] = MarketTrendMetricScope.Global,
        [RallyParticipationReversalPercent] = MarketTrendMetricScope.Global,
        [RallyLeaderOverlapPercent] = MarketTrendMetricScope.Global,
        [ReturnInATR] = MarketTrendMetricScope.Asset,
        [RelativeStrengthInATR] = MarketTrendMetricScope.Asset,
        [PriceTrend] = MarketTrendMetricScope.Both,
        [MedianPriceTrend] = MarketTrendMetricScope.Global,
        [PricePathEfficiency] = MarketTrendMetricScope.Asset,
        [MedianPricePathEfficiency] = MarketTrendMetricScope.Global,
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
        [FundingRate] = MarketTrendMetricScope.Asset,
        [FundingSum] = MarketTrendMetricScope.Asset,
        [MedianFundingRate] = MarketTrendMetricScope.Global,
        [PositiveFundingBreadth] = MarketTrendMetricScope.Global,
        [NegativeFundingBreadth] = MarketTrendMetricScope.Global,
        [ExtremePositiveFundingBreadth] = MarketTrendMetricScope.Global,
        [ExtremeNegativeFundingBreadth] = MarketTrendMetricScope.Global,
        [TakerBuyRatio] = MarketTrendMetricScope.Asset,
        [TakerImbalance] = MarketTrendMetricScope.Asset,
        [MedianTakerBuyRatio] = MarketTrendMetricScope.Global,
        [BuyerDominanceBreadth] = MarketTrendMetricScope.Global,
        [StrongBuyerDominanceBreadth] = MarketTrendMetricScope.Global,
        [TakerImbalanceDispersion] = MarketTrendMetricScope.Global,
        [BuyerFlowPriceConfirmation] = MarketTrendMetricScope.Asset,
        [SellerFlowPriceConfirmation] = MarketTrendMetricScope.Asset,
        [BuyerFlowPriceDivergence] = MarketTrendMetricScope.Asset,
        [SellerFlowPriceDivergence] = MarketTrendMetricScope.Asset,
        [BuyerFlowPriceConfirmationVolumeShare] = MarketTrendMetricScope.Global,
        [SellerFlowPriceConfirmationVolumeShare] = MarketTrendMetricScope.Global,
        [BuyerFlowPriceDivergenceVolumeShare] = MarketTrendMetricScope.Global,
        [SellerFlowPriceDivergenceVolumeShare] = MarketTrendMetricScope.Global,
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
