using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  public sealed class FlowPriceConfirmationResult
  {
    public IReadOnlyDictionary<string, FeatureValueSnapshot> GlobalMetrics { get; init; } =
      new Dictionary<string, FeatureValueSnapshot>();
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> AssetMetrics { get; init; } =
      new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(StringComparer.OrdinalIgnoreCase);
  }

  /// <summary>
  /// Phase 3 directional price / aggressive quote-flow agreement and divergence.
  /// Takes aligned observations already captured inside TakerFlowFeatureEngine's
  /// existing candle pass; never traverses OHLCV a second time.
  /// </summary>
  public static class FlowPriceConfirmationEngine
  {
    private static readonly (string Asset, string Global)[] Conditions =
    {
      (MarketTrendMetrics.BuyerFlowPriceConfirmation, MarketTrendMetrics.BuyerFlowPriceConfirmationVolumeShare),
      (MarketTrendMetrics.SellerFlowPriceConfirmation, MarketTrendMetrics.SellerFlowPriceConfirmationVolumeShare),
      (MarketTrendMetrics.BuyerFlowPriceDivergence, MarketTrendMetrics.BuyerFlowPriceDivergenceVolumeShare),
      (MarketTrendMetrics.SellerFlowPriceDivergence, MarketTrendMetrics.SellerFlowPriceDivergenceVolumeShare)
    };

    public static bool IsAssetMetric(string metric) =>
      Conditions.Any(c => c.Asset.Equals(MarketTrendMetrics.Normalize(metric), StringComparison.OrdinalIgnoreCase));
    public static bool IsGlobalMetric(string metric) =>
      Conditions.Any(c => c.Global.Equals(MarketTrendMetrics.Normalize(metric), StringComparison.OrdinalIgnoreCase));
    public static bool HasAssetMetrics(IReadOnlyCollection<string> metrics) =>
      metrics != null && metrics.Any(IsAssetMetric);
    public static bool HasGlobalMetrics(IReadOnlyCollection<string> metrics) =>
      metrics != null && metrics.Any(IsGlobalMetric);
    public static bool HasAnyMetrics(IReadOnlyCollection<string> metrics) =>
      HasAssetMetrics(metrics) || HasGlobalMetrics(metrics);

    public static FlowPriceConfirmationResult Calculate(
      IEnumerable<string> globalSymbols, IEnumerable<string> assetSymbols,
      IReadOnlyCollection<string> enabled,
      IReadOnlyDictionary<string, AlignedTakerFlowPeriod> alignedPeriods,
      DateTimeOffset asOfUtc)
    {
      var globals = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
      var assets = new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(
        StringComparer.OrdinalIgnoreCase);
      if (!HasAnyMetrics(enabled))
        return new FlowPriceConfirmationResult { GlobalMetrics = globals, AssetMetrics = assets };

      var selected = new HashSet<string>(enabled.Select(MarketTrendMetrics.Normalize), StringComparer.OrdinalIgnoreCase);
      string[] globalSymbolsArray = Normalize(globalSymbols);
      string[] assetSymbolsArray = Normalize(assetSymbols);
      var globalSet = new HashSet<string>(globalSymbolsArray, StringComparer.OrdinalIgnoreCase);
      var assetSet = new HashSet<string>(assetSymbolsArray, StringComparer.OrdinalIgnoreCase);
      bool globalActive = HasGlobalMetrics(enabled), assetActive = HasAssetMetrics(enabled);
      var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      if (globalActive) required.UnionWith(globalSymbolsArray);
      if (assetActive) required.UnionWith(assetSymbolsArray);

      foreach (string symbol in required)
      {
        var flags = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
        AlignedTakerFlowPeriod input = null;
        if (alignedPeriods != null)
          alignedPeriods.TryGetValue(symbol, out input);
        bool usable = IsUsable(input);
        foreach (var c in Conditions)
        {
          if (!(assetActive && assetSet.Contains(symbol) && selected.Contains(c.Asset)) &&
              !(globalActive && globalSet.Contains(symbol) && selected.Contains(c.Global)))
            continue;

          flags[c.Asset] = usable
            ? new FeatureValueSnapshot
            {
              IsValid = true, Value = Matches(c.Asset, input) ? 1.0 : 0.0,
              CalculatedAtUtc = asOfUtc,
              SourceDataTimestampUtc = input.TakerBuyRatio.SourceDataTimestampUtc,
              SampleCount = input.TakerBuyRatio.SampleCount
            }
            : new FeatureValueSnapshot
            {
              CalculatedAtUtc = asOfUtc,
              SourceDataTimestampUtc = input?.TakerBuyRatio?.SourceDataTimestampUtc,
              SampleCount = input?.TakerBuyRatio?.SampleCount ?? 0,
              IsStale = input?.TakerBuyRatio?.IsStale == true,
              IsNoTrades = input?.TakerBuyRatio?.IsNoTrades == true,
              InvalidReason = !String.IsNullOrWhiteSpace(input?.TakerBuyRatio?.InvalidReason)
                ? input.TakerBuyRatio.InvalidReason
                : "Aligned price, quote volume or source-verified taker ratio is unavailable."
            };
        }
        if (flags.Count > 0)
          assets[symbol] = new ReadOnlyDictionary<string, FeatureValueSnapshot>(flags);
      }

      if (globalActive)
      {
        // Coverage is measured against the unchanged frozen population.
        // Verified no-trade assets are complete zero-volume observations;
        // otherwise missing/invalid markets contribute neither data nor a
        // fabricated zero. Require the standard >=90% observation coverage.
        var observed = globalSymbolsArray.Where(s => alignedPeriods != null &&
          alignedPeriods.TryGetValue(s, out var v) && (IsUsable(v) || IsVerifiedNoTrades(v)))
          .Select(s => alignedPeriods[s]).ToArray();
        var active = observed.Where(IsUsable).ToArray();
        double totalQuote = active.Sum(v => v.QuoteVolume.Value);
        bool goodDenominator = observed.Length > 0 && Double.IsFinite(totalQuote) && totalQuote > 0;
        foreach (var c in Conditions.Where(c => selected.Contains(c.Global)))
        {
          double numerator = goodDenominator
            ? active.Where(v => Matches(c.Asset, v)).Sum(v =>
                Math.Abs(v.QuoteVolume.Value * (2.0 * v.TakerBuyRatio.Value.Value - 1.0)))
            : Double.NaN;
          double share = goodDenominator ? numerator / totalQuote * 100.0 : Double.NaN;
          bool validShare = goodDenominator && Double.IsFinite(share) &&
            share >= 0 && share <= 100.00000001;
          var raw = new FeatureValueSnapshot
          {
            CalculatedAtUtc = asOfUtc,
            SourceDataTimestampUtc = observed.Length == 0 ? null
              : observed.Min(v => v.TakerBuyRatio.SourceDataTimestampUtc),
            Value = validShare ? Math.Clamp(share, 0.0, 100.0) : null,
            IsValid = validShare, SampleCount = observed.Length,
            InvalidReason = validShare ? "" : "Invalid complete-period quote-volume denominator or net taker flow."
          };
          // Apply the normal 90% minimum; the denominator still uses only
          // source-verified positive trading volume, never missing assets.
          globals[c.Global] = MarketMetricCoverage.Apply(
            raw, observed.Length, globalSymbolsArray.Length, asOfUtc);
        }
      }
      return new FlowPriceConfirmationResult
      {
        GlobalMetrics = new ReadOnlyDictionary<string, FeatureValueSnapshot>(globals),
        AssetMetrics = new ReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(assets)
      };
    }

    private static string[] Normalize(IEnumerable<string> names) =>
      (names ?? Array.Empty<string>())
        .Where(s => !String.IsNullOrWhiteSpace(s))
        .Select(s => s.Trim().ToUpperInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool IsUsable(AlignedTakerFlowPeriod v)
    {
      if (v?.TakerBuyRatio == null || !v.TakerBuyRatio.IsValid ||
          v.TakerBuyRatio.IsStale || !v.TakerBuyRatio.Value.HasValue ||
          !v.TakerBuyRatio.SourceDataTimestampUtc.HasValue ||
          !v.PriceReturnPercent.HasValue || !v.QuoteVolume.HasValue)
        return false;
      double ratio = v.TakerBuyRatio.Value.Value;
      return Double.IsFinite(ratio) && ratio >= 0 && ratio <= 1 &&
        Double.IsFinite(v.PriceReturnPercent.Value) &&
        Double.IsFinite(v.QuoteVolume.Value) && v.QuoteVolume.Value > 0;
    }

    private static bool IsVerifiedNoTrades(AlignedTakerFlowPeriod v)
    {
      FeatureValueSnapshot ratio = v?.TakerBuyRatio;
      return ratio != null && ratio.IsNoTrades && !ratio.IsValid && !ratio.IsStale &&
        !ratio.Value.HasValue && ratio.SourceDataTimestampUtc.HasValue &&
        ratio.SampleCount > 0 && v.QuoteVolume.HasValue && v.QuoteVolume.Value == 0;
    }

    private static bool Matches(string metric, AlignedTakerFlowPeriod v)
    {
      double price = v.PriceReturnPercent.Value;
      double imbalance = 2.0 * v.TakerBuyRatio.Value.Value - 1.0;
      return metric == MarketTrendMetrics.BuyerFlowPriceConfirmation ? price > 0 && imbalance > 0 :
        metric == MarketTrendMetrics.SellerFlowPriceConfirmation ? price < 0 && imbalance < 0 :
        metric == MarketTrendMetrics.BuyerFlowPriceDivergence ? price < 0 && imbalance > 0 :
        metric == MarketTrendMetrics.SellerFlowPriceDivergence && price > 0 && imbalance < 0;
    }
  }
}
