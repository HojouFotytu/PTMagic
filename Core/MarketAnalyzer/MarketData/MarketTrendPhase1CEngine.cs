using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer.MarketData
{
  public sealed class Phase1CFeatureResult
  {
    public IReadOnlyDictionary<string, FeatureValueSnapshot> GlobalMetrics { get; init; } =
      new Dictionary<string, FeatureValueSnapshot>();
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> AssetMetrics { get; init; } =
      new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(StringComparer.OrdinalIgnoreCase);
  }

  /// <summary>
  /// Computes Phase 1C OHLCV metrics over complete MarketTrend.TrendMinutes bars.
  /// Uses the Phase 0 global and per-asset population without changing it.
  /// </summary>
  public static class MarketTrendPhase1CEngine
  {
    public static readonly string[] Metrics =
    {
      MarketTrendMetrics.ATRP, MarketTrendMetrics.MedianATRP, MarketTrendMetrics.VolatilityBreadth,
      MarketTrendMetrics.VolumeExpansion, MarketTrendMetrics.MedianVolumeExpansion,
      MarketTrendMetrics.VolumeExpansionBreadth, MarketTrendMetrics.StrongVolumeExpansionBreadth,
      MarketTrendMetrics.VolumeConcentration, MarketTrendMetrics.TopNVolumeShare,
      MarketTrendMetrics.BTCVolumeShare, MarketTrendMetrics.BTCETHVolumeShare
    };

    public static bool HasMetrics(IReadOnlyCollection<string> enabledMetrics)
    {
      return enabledMetrics != null && Metrics.Any(metric =>
        enabledMetrics.Contains(metric, StringComparer.OrdinalIgnoreCase));
    }

    public static Phase1CFeatureResult Calculate(
      MarketTrend trend,
      IReadOnlyCollection<string> enabled,
      IEnumerable<string> globalSymbols,
      IEnumerable<string> assetSymbols,
      MarketDataStore store,
      TimeSpan interval,
      DateTimeOffset asOfUtc,
      string mainMarket = "USDT")
    {
      HashSet<string> active = new HashSet<string>(enabled ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
      string[] global = Normalize(globalSymbols);
      string[] asset = Normalize(assetSymbols);
      bool medianATR = active.Contains(MarketTrendMetrics.MedianATRP);
      bool volatilityBreadth = active.Contains(MarketTrendMetrics.VolatilityBreadth);
      bool medianVolume = active.Contains(MarketTrendMetrics.MedianVolumeExpansion);
      bool volumeBreadth = active.Contains(MarketTrendMetrics.VolumeExpansionBreadth);
      bool strongVolumeBreadth = active.Contains(MarketTrendMetrics.StrongVolumeExpansionBreadth);
      bool volumeHHI = active.Contains(MarketTrendMetrics.VolumeConcentration);
      bool topShare = active.Contains(MarketTrendMetrics.TopNVolumeShare);
      bool btcShare = active.Contains(MarketTrendMetrics.BTCVolumeShare);
      bool btcEthShare = active.Contains(MarketTrendMetrics.BTCETHVolumeShare);
      bool globalATR = medianATR || volatilityBreadth;
      bool globalExpansion = medianVolume || volumeBreadth || strongVolumeBreadth;
      bool globalVolume = volumeHHI || topShare || btcShare || btcEthShare;
      bool assetATR = active.Contains(MarketTrendMetrics.ATRP);
      bool assetExpansion = active.Contains(MarketTrendMetrics.VolumeExpansion);

      HashSet<string> globalSet = new HashSet<string>(global, StringComparer.OrdinalIgnoreCase);
      HashSet<string> assetSet = new HashSet<string>(asset, StringComparer.OrdinalIgnoreCase);
      HashSet<string> all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      if (globalATR || globalExpansion || globalVolume)
      {
        all.UnionWith(global);
      }
      if (assetATR || assetExpansion)
      {
        all.UnionWith(asset);
      }

      Dictionary<string, TrendPeriodFeatures> features = new Dictionary<string, TrendPeriodFeatures>(StringComparer.OrdinalIgnoreCase);
      foreach (string symbol in all)
      {
        bool isGlobal = globalSet.Contains(symbol);
        bool isAsset = assetSet.Contains(symbol);
        bool needATR = (isGlobal && globalATR) || (isAsset && assetATR);
        bool needExpansion = (isGlobal && globalExpansion) || (isAsset && assetExpansion);
        bool needVolume = isGlobal && globalVolume;
        features[symbol] = TrendPeriodMetricsCalculator.Calculate(
          store.GetSnapshot(symbol, interval, asOfUtc),
          trend.TrendMinutes,
          asOfUtc,
          trend.ATRPeriod,
          trend.VolumeExpansionLookback,
          needATR,
          needExpansion,
          needVolume);
      }

      Dictionary<string, FeatureValueSnapshot> marketResults =
        new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);

      List<FeatureValueSnapshot> atrValues = globalATR
        ? AvailableFeatures(global, features, item => item.ATRP)
        : new List<FeatureValueSnapshot>();
      List<FeatureValueSnapshot> expansionValues = globalExpansion
        ? AvailableFeatures(global, features, item => item.VolumeExpansion)
        : new List<FeatureValueSnapshot>();

      if (medianATR)
      {
        marketResults[MarketTrendMetrics.MedianATRP] = MarketMetricCoverage.Apply(
          Median(atrValues, asOfUtc, "ATRP"), atrValues.Count, global.Length, asOfUtc);
      }
      if (volatilityBreadth)
      {
        marketResults[MarketTrendMetrics.VolatilityBreadth] = MarketMetricCoverage.Apply(
          PercentAbove(atrValues, trend.ATRPBreadthThreshold, asOfUtc, "ATRP"),
          atrValues.Count, global.Length, asOfUtc);
      }
      if (medianVolume)
      {
        marketResults[MarketTrendMetrics.MedianVolumeExpansion] = MarketMetricCoverage.Apply(
          Median(expansionValues, asOfUtc, "VolumeExpansion"), expansionValues.Count, global.Length, asOfUtc);
      }
      if (volumeBreadth)
      {
        marketResults[MarketTrendMetrics.VolumeExpansionBreadth] = MarketMetricCoverage.Apply(
          PercentAbove(expansionValues, trend.VolumeExpansionBreadthThreshold, asOfUtc, "VolumeExpansion"),
          expansionValues.Count, global.Length, asOfUtc);
      }
      if (strongVolumeBreadth)
      {
        marketResults[MarketTrendMetrics.StrongVolumeExpansionBreadth] = MarketMetricCoverage.Apply(
          PercentAbove(expansionValues, trend.StrongVolumeExpansionThreshold, asOfUtc, "VolumeExpansion"),
          expansionValues.Count, global.Length, asOfUtc);
      }

      if (globalVolume)
      {
        var observed = global.ToDictionary(s => s, s => features[s].LastQuoteVolume, StringComparer.OrdinalIgnoreCase);
        var selected = new HashSet<string>(global.Where(s => VolumeDistributionCoverage.Usable(observed[s])), StringComparer.OrdinalIgnoreCase);
        var references = VolumeDistributionCoverage.References(global, observed, selected, trend, store, interval, asOfUtc);
        foreach (string metric in active.Where(VolumeDistributionCoverage.IsMetric))
          marketResults[metric] = VolumeDistributionCoverage.Calculate(metric, trend, global,
            observed, selected, references, asOfUtc, mainMarket);
      }

      Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> assetResults =
        new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(StringComparer.OrdinalIgnoreCase);
      // Reuse feature snapshots already computed as global aggregate inputs,
      // as well as explicitly requested asset features. Other assets are not
      // calculated merely to populate the Monitor.
      foreach (string symbol in asset)
      {
        bool publishATR = assetATR || (globalATR && globalSet.Contains(symbol));
        bool publishExpansion = assetExpansion || (globalExpansion && globalSet.Contains(symbol));
        if ((!publishATR && !publishExpansion) ||
            !features.TryGetValue(symbol, out TrendPeriodFeatures calculated))
          continue;
        Dictionary<string, FeatureValueSnapshot> symbolResults =
          new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
        if (publishATR) symbolResults[MarketTrendMetrics.ATRP] = calculated.ATRP;
        if (publishExpansion) symbolResults[MarketTrendMetrics.VolumeExpansion] = calculated.VolumeExpansion;
        assetResults[symbol] = new ReadOnlyDictionary<string, FeatureValueSnapshot>(symbolResults);
      }

      return new Phase1CFeatureResult
      {
        GlobalMetrics = new ReadOnlyDictionary<string, FeatureValueSnapshot>(marketResults),
        AssetMetrics = new ReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(assetResults)
      };
    }

    private static FeatureValueSnapshot ShareForSymbols(
      List<KeyValuePair<string, FeatureValueSnapshot>> volumes,
      string first, string second, bool totalValid, double total,
      FeatureValueSnapshot failure, DateTimeOffset now, DateTimeOffset? sourceTime)
    {
      if (!totalValid)
      {
        return failure;
      }
      if (!volumes.Any(pair => pair.Key.Equals(first, StringComparison.OrdinalIgnoreCase))
        || (second != null && !volumes.Any(pair => pair.Key.Equals(second, StringComparison.OrdinalIgnoreCase))))
      {
        return Invalid(now, volumes.Count, "Required BTC/ETH symbol is absent from the eligible global MarketTrend universe.");
      }

      double sum = volumes.Where(pair =>
        pair.Key.Equals(first, StringComparison.OrdinalIgnoreCase) ||
        (second != null && pair.Key.Equals(second, StringComparison.OrdinalIgnoreCase)))
        .Sum(pair => pair.Value.Value.Value);
      return Valid(sum / total * 100.0, now, sourceTime, volumes.Count);
    }

    private static FeatureValueSnapshot UsableOrInvalid(
      double value, FeatureValueSnapshot failure, DateTimeOffset now,
      DateTimeOffset? timestamp, int count)
    {
      return double.IsFinite(value) && value >= 0 ? Valid(value, now, timestamp, count) : failure;
    }

    private static List<FeatureValueSnapshot> AvailableFeatures(
      IEnumerable<string> symbols,
      IDictionary<string, TrendPeriodFeatures> features,
      Func<TrendPeriodFeatures, FeatureValueSnapshot> selector)
    {
      return symbols.Select(symbol => selector(features[symbol])).Where(IsUsable).ToList();
    }

    private static FeatureValueSnapshot Median(
      List<FeatureValueSnapshot> values, DateTimeOffset now, string label)
    {
      if (values.Count == 0)
      {
        return Invalid(now, 0, "No valid " + label + " values are available.");
      }
      double[] ordered = values.Select(item => item.Value.Value).OrderBy(value => value).ToArray();
      int middle = ordered.Length / 2;
      double median = ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2.0 : ordered[middle];
      return Valid(median, now, Oldest(values), values.Count);
    }

    private static FeatureValueSnapshot PercentAbove(
      List<FeatureValueSnapshot> values, double threshold, DateTimeOffset now, string label)
    {
      if (!double.IsFinite(threshold) || threshold < 0)
      {
        return Invalid(now, values.Count, label + " breadth threshold must be finite and nonnegative.");
      }
      if (values.Count == 0)
      {
        return Invalid(now, 0, "No valid " + label + " values are available.");
      }
      return Valid(values.Count(feature => feature.Value.Value > threshold) * 100.0 / values.Count,
        now, Oldest(values), values.Count);
    }

    private static FeatureValueSnapshot Valid(double value, DateTimeOffset now, DateTimeOffset? timestamp, int count)
    {
      return new FeatureValueSnapshot
      {
        Value = value, CalculatedAtUtc = now, SourceDataTimestampUtc = timestamp,
        SampleCount = count, IsValid = double.IsFinite(value), IsStale = false
      };
    }

    private static FeatureValueSnapshot Invalid(DateTimeOffset now, int count, string reason)
    {
      return new FeatureValueSnapshot
      {
        CalculatedAtUtc = now, SampleCount = count,
        IsValid = false, InvalidReason = reason
      };
    }

    private static bool IsUsable(FeatureValueSnapshot value)
    {
      return value != null && value.IsValid && !value.IsStale
        && value.Value.HasValue && double.IsFinite(value.Value.Value);
    }

    private static DateTimeOffset? Oldest(IEnumerable<FeatureValueSnapshot> values)
    {
      DateTimeOffset? oldest = null;
      foreach (FeatureValueSnapshot feature in values)
      {
        if (!feature.SourceDataTimestampUtc.HasValue)
        {
          return null;
        }
        if (!oldest.HasValue || feature.SourceDataTimestampUtc.Value < oldest.Value)
        {
          oldest = feature.SourceDataTimestampUtc.Value;
        }
      }
      return oldest;
    }

    private static string[] Normalize(IEnumerable<string> symbols)
    {
      return (symbols ?? Array.Empty<string>())
        .Where(symbol => !String.IsNullOrWhiteSpace(symbol))
        .Select(symbol => symbol.Trim().ToUpperInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
  }
}
