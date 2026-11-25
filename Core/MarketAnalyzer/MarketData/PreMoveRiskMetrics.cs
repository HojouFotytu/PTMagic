using System;
using System.Collections.Generic;
using System.Linq;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer.MarketData
{
  public static partial class MarketTrendAnalyticsEngine
  {
    internal static bool IsPreMoveRiskMetric(string metric) =>
      metric == MarketTrendMetrics.MedianReturnInPriorATR || metric == MarketTrendMetrics.PreviousMedianReturnInPriorATR
      || metric == MarketTrendMetrics.MarketReversalStrength || metric == MarketTrendMetrics.MedianATRPRatioToPrior
      || metric == MarketTrendMetrics.ReturnInPriorATR || metric == MarketTrendMetrics.RelativeStrengthInPriorATR;

    private static bool NeedsPreMoveRisk(IReadOnlyCollection<string> enabled) =>
      enabled.Any(metric => IsPreMoveRiskMetric(MarketTrendMetrics.Normalize(metric)));

    private static Phase1DFeatureResult CalculatePreMoveRisk(MarketTrend trend, IReadOnlyCollection<string> requested,
      string[] global, string[] assets, MarketDataStore store, TimeSpan interval, DateTimeOffset asOf,
      string quote, bool needsConversion, FeatureValueSnapshot medianNow, FeatureValueSnapshot currentAtr,
      IReadOnlyDictionary<string, FeatureValueSnapshot> returns)
    {
      bool Has(string name) => requested.Contains(name, StringComparer.OrdinalIgnoreCase);
      var market = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
      var symbols = new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(StringComparer.OrdinalIgnoreCase);
      bool assetRisk = Has(MarketTrendMetrics.ReturnInPriorATR) || Has(MarketTrendMetrics.RelativeStrengthInPriorATR);
      bool medianPrior = Has(MarketTrendMetrics.MedianReturnInPriorATR) || Has(MarketTrendMetrics.MarketReversalStrength)
        || Has(MarketTrendMetrics.MedianATRPRatioToPrior);
      var reference = asOf.AddMinutes(-trend.TrendMinutes);
      var features = new List<string>();
      if (assetRisk) features.Add(MarketTrendMetrics.ATRP);
      if (medianPrior) features.Add(MarketTrendMetrics.MedianATRP);
      var prior = MarketTrendPhase1CEngine.Calculate(trend, features, medianPrior ? global : Array.Empty<string>(),
        assetRisk ? assets : Array.Empty<string>(), store, interval, reference, quote);
      prior.GlobalMetrics.TryGetValue(MarketTrendMetrics.MedianATRP, out var priorAtr);
      if (Has(MarketTrendMetrics.MedianReturnInPriorATR))
        market[MarketTrendMetrics.MedianReturnInPriorATR] = InAtrUnits(medianNow, priorAtr, asOf, needsConversion);
      if (Has(MarketTrendMetrics.MedianATRPRatioToPrior))
        market[MarketTrendMetrics.MedianATRPRatioToPrior] = InAtrUnits(currentAtr, priorAtr, asOf, needsConversion);

      if (Has(MarketTrendMetrics.PreviousMedianReturnInPriorATR) || Has(MarketTrendMetrics.MarketReversalStrength))
      {
        var values = global.Select(symbol => CalculateReturn(store.GetSnapshot(symbol, interval, reference),
          trend.TrendMinutes, reference)).Where(IsUsable).ToList();
        var previous = MarketMetricCoverage.Apply(CalculateMedian(values, reference, trend.TrendMinutes),
          values.Count, global.Length, reference);
        if (Has(MarketTrendMetrics.PreviousMedianReturnInPriorATR))
        {
          var before = MarketTrendPhase1CEngine.Calculate(trend, new[] { MarketTrendMetrics.MedianATRP }, global,
            Array.Empty<string>(), store, interval, reference.AddMinutes(-trend.TrendMinutes), quote);
          market[MarketTrendMetrics.PreviousMedianReturnInPriorATR] = InAtrUnits(previous,
            before.GlobalMetrics[MarketTrendMetrics.MedianATRP], asOf, needsConversion);
        }
        if (Has(MarketTrendMetrics.MarketReversalStrength))
        {
          var score = InAtrUnits(medianNow, priorAtr, asOf, needsConversion);
          if (IsUsable(score) && IsUsable(previous))
          {
            double now = medianNow.Value.Value, then = previous.Value.Value;
            double value = Math.Sign(now) * Math.Sign(then) < 0
              ? now / Math.Max(priorAtr.Value.Value, Math.Abs(then)) : 0;
            var inputs = new[] { score, previous };
            market[MarketTrendMetrics.MarketReversalStrength] = new FeatureValueSnapshot
            {
              Value = value, IsValid = true, CalculatedAtUtc = asOf,
              SourceDataTimestampUtc = GetOldestSourceTimestamp(inputs), SampleCount = inputs.Min(v => v.SampleCount),
              EligibleSampleCount = global.Length, CoveragePercent = inputs.Min(v => v.CoveragePercent)
            };
          }
          else market[MarketTrendMetrics.MarketReversalStrength] = InvalidFeature(asOf, 0,
            "Both rolling return legs and valid pre-move ATRP in the same quote currency are required.");
        }
      }

      if (assetRisk)
      {
        foreach (string symbol in assets)
        {
          FeatureValueSnapshot atr = null;
          if (prior.AssetMetrics.TryGetValue(symbol, out var history)) history.TryGetValue(MarketTrendMetrics.ATRP, out atr);
          returns.TryGetValue(symbol, out var price);
          var metrics = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
          if (Has(MarketTrendMetrics.ReturnInPriorATR))
            metrics[MarketTrendMetrics.ReturnInPriorATR] = InAtrUnits(price, atr, asOf, needsConversion);
          if (Has(MarketTrendMetrics.RelativeStrengthInPriorATR))
          {
            var excess = CalculateDifference(price, medianNow, asOf, "Missing asset or broad-market median return.");
            metrics[MarketTrendMetrics.RelativeStrengthInPriorATR] = InAtrUnits(excess, atr, asOf, needsConversion);
          }
          symbols[symbol] = metrics;
        }
      }
      return new Phase1DFeatureResult { GlobalMetrics = market, AssetMetrics = symbols };
    }
  }
}
