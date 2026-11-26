using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  public sealed class MetricDeltaObservation
  {
    public FeatureValueSnapshot Current { get; init; }
    public FeatureValueSnapshot Previous { get; init; }
    public FeatureValueSnapshot Delta { get; init; }
    public DateTimeOffset? PreviousSourceDataTimestampUtc => Previous?.SourceDataTimestampUtc;
  }

  public sealed class MarketTrendDeltaAnalysisSnapshot
  {
    public string MarketTrendName { get; init; } = "";
    public DateTimeOffset PreviousAsOfUtc { get; init; }
    internal MarketTrendAnalysisSnapshot PreviousAnalysis { get; init; }
    public IReadOnlyDictionary<string, MetricDeltaObservation> MarketMetrics { get; init; } =
      new Dictionary<string, MetricDeltaObservation>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, MetricDeltaObservation>> AssetMetrics { get; init; } =
      new Dictionary<string, IReadOnlyDictionary<string, MetricDeltaObservation>>(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string metric, string marketSymbol, out MetricDeltaObservation observation)
    {
      observation = null;
      if (!MarketTrendMetrics.TryGetScope(metric, out MarketTrendMetricScope scope))
        return false;
      if (!String.IsNullOrWhiteSpace(marketSymbol) &&
        (scope == MarketTrendMetricScope.Asset || scope == MarketTrendMetricScope.Both))
        return AssetMetrics.TryGetValue(marketSymbol,
          out IReadOnlyDictionary<string, MetricDeltaObservation> asset) &&
          asset.TryGetValue(metric, out observation);
      if (scope == MarketTrendMetricScope.Global || scope == MarketTrendMetricScope.Both)
        return MarketMetrics.TryGetValue(metric, out observation);
      return false;
    }
  }

  /// <summary>
  /// One configured MarketTrend duration separates two source-valid observations.
  /// Rolling metrics use separated rolling endpoints; completed-period metrics
  /// resolve to successive completed periods through their existing calculators.
  /// The historical calculation reuses the CURRENT frozen eligible population,
  /// preventing membership changes from masquerading as metric changes.
  /// </summary>
  public static class MarketTrendDeltaEngine
  {
    public static MarketTrendDeltaAnalysisSnapshot Calculate(
      Core.Main.DataObjects.PTMagicData.MarketTrend trend,
      IReadOnlyCollection<string> requestedMetrics,
      MarketTrendAnalysisSnapshot current,
      MarketDataStore store,
      TimeSpan interval,
      string benchmarkSymbol = "BTCUSDT",
      int? configuredStoreDataMaxHours = null,
      string exchangeName = "BinanceFutures",
      FuturesFundingDataService fundingData = null)
    {
      if (trend == null) throw new ArgumentNullException(nameof(trend));
      if (current == null) throw new ArgumentNullException(nameof(current));
      if (store == null) throw new ArgumentNullException(nameof(store));
      MarketTrendTimeframe.Validate(trend, interval);
      var requested = (requestedMetrics ?? Array.Empty<string>())
        .Select(MarketTrendMetrics.Normalize).Distinct(StringComparer.OrdinalIgnoreCase)
        .Where(MarketTrendMetrics.IsKnown).ToArray();
      DateTimeOffset previousAsOfUtc = current.CalculatedAtUtc.AddMinutes(-trend.TrendMinutes);

      MarketTrendAnalysisSnapshot previous = requested.Length == 0 ? null
        : MarketTrendAnalyticsEngine.Calculate(
          trend, current.GlobalSymbols, current.AssetSymbols, store, interval,
          previousAsOfUtc, requested, benchmarkSymbol, configuredStoreDataMaxHours,
          exchangeName, fundingData);

      var global = new Dictionary<string, MetricDeltaObservation>(StringComparer.OrdinalIgnoreCase);
      var assets = new Dictionary<string, IReadOnlyDictionary<string, MetricDeltaObservation>>(StringComparer.OrdinalIgnoreCase);

      string[] eligible = current.GlobalSymbols.ToArray();
      var volumeMetrics = requested.Where(VolumeDistributionCoverage.IsMetric).ToArray();
      if (volumeMetrics.Length > 0)
      {
        var now = VolumeDistributionCoverage.Read(eligible, trend, store, interval, current.CalculatedAtUtc);
        var prior = VolumeDistributionCoverage.Read(eligible, trend, store, interval, previousAsOfUtc);
        var matched = new HashSet<string>(eligible.Where(s => VolumeDistributionCoverage.Usable(now[s]) &&
          VolumeDistributionCoverage.Usable(prior[s])), StringComparer.OrdinalIgnoreCase);
        var nowReferences = VolumeDistributionCoverage.References(eligible, now, matched, trend, store, interval, current.CalculatedAtUtc);
        var priorReferences = VolumeDistributionCoverage.References(eligible, prior, matched, trend, store, interval, previousAsOfUtc);
        string quote = benchmarkSymbol.StartsWith("BTC", StringComparison.OrdinalIgnoreCase) ? benchmarkSymbol.Substring(3) : "USDT";
        foreach (string metric in volumeMetrics)
          global[metric] = Compare(
            VolumeDistributionCoverage.Calculate(metric, trend, eligible, now, matched, nowReferences, current.CalculatedAtUtc, quote),
            VolumeDistributionCoverage.Calculate(metric, trend, eligible, prior, matched, priorReferences, previousAsOfUtc, quote),
            current.CalculatedAtUtc);
      }

      foreach (string metric in requested)
      {
        if (!MarketTrendMetrics.TryGetScope(metric, out MarketTrendMetricScope scope))
          continue;
        if (scope == MarketTrendMetricScope.Global || scope == MarketTrendMetricScope.Both)
        {
          if (!VolumeDistributionCoverage.IsMetric(metric)) global[metric] = Compare(
            GetFeature(current, metric, null), GetFeature(previous, metric, null),
            current.CalculatedAtUtc);
        }
        if (scope == MarketTrendMetricScope.Asset || scope == MarketTrendMetricScope.Both)
        {
          foreach (string symbol in current.AssetSymbols)
          {
            if (!assets.TryGetValue(symbol, out IReadOnlyDictionary<string, MetricDeltaObservation> existing))
              existing = new ReadOnlyDictionary<string, MetricDeltaObservation>(
                new Dictionary<string, MetricDeltaObservation>(StringComparer.OrdinalIgnoreCase));
            var values = new Dictionary<string, MetricDeltaObservation>(
              existing, StringComparer.OrdinalIgnoreCase);
            values[metric] = Compare(
              GetFeature(current, metric, symbol), GetFeature(previous, metric, symbol),
              current.CalculatedAtUtc);
            assets[symbol] = new ReadOnlyDictionary<string, MetricDeltaObservation>(values);
          }
        }
      }

      return new MarketTrendDeltaAnalysisSnapshot
      {
        MarketTrendName = trend.Name,
        PreviousAsOfUtc = previousAsOfUtc,
        PreviousAnalysis = previous,
        MarketMetrics = new ReadOnlyDictionary<string, MetricDeltaObservation>(global),
        AssetMetrics = new ReadOnlyDictionary<string, IReadOnlyDictionary<string, MetricDeltaObservation>>(assets)
      };
    }

    public static MetricDeltaObservation Compare(
      FeatureValueSnapshot current, FeatureValueSnapshot previous, DateTimeOffset asOfUtc)
    {
      string invalid = "";
      bool IsUsable(FeatureValueSnapshot item) =>
        item != null && item.IsValid && !item.IsStale &&
        item.Value.HasValue && double.IsFinite(item.Value.Value);

      if (!IsUsable(current))
        invalid = "Current measurement is unavailable: " +
          (current?.InvalidReason ?? "missing source value.");
      else if (!IsUsable(previous))
        invalid = "Previous MarketTrend-duration measurement is unavailable: " +
          (previous?.InvalidReason ?? "missing historical source value.");
      else if (!current.SourceDataTimestampUtc.HasValue ||
        !previous.SourceDataTimestampUtc.HasValue)
        invalid = "Delta requires exact source timestamps for both observations.";
      else if (previous.SourceDataTimestampUtc.Value >= current.SourceDataTimestampUtc.Value)
        invalid = "Delta cannot compare repeated or out-of-order source observations.";

      double? difference = null;
      if (invalid.Length == 0)
      {
        double value = current.Value.Value - previous.Value.Value;
        if (!double.IsFinite(value))
          invalid = "Delta subtraction produced a nonfinite value.";
        else
          difference = value;
      }
      return new MetricDeltaObservation
      {
        Current = current,
        Previous = previous,
        Delta = new FeatureValueSnapshot
        {
          Value = difference,
          IsValid = difference.HasValue,
          IsStale = current?.IsStale == true || previous?.IsStale == true,
          CalculatedAtUtc = asOfUtc,
          SourceDataTimestampUtc = current?.SourceDataTimestampUtc,
          SampleCount = current?.SampleCount ?? 0,
          EligibleSampleCount = current?.EligibleSampleCount,
          CoveragePercent = current?.CoveragePercent,
          VolumeCoverage = current?.VolumeCoverage,
          PreviousVolumeCoverage = previous?.VolumeCoverage,
          InvalidReason = invalid
        }
      };
    }

    private static FeatureValueSnapshot GetFeature(
      MarketTrendAnalysisSnapshot analysis, string metric, string symbol)
    {
      if (analysis == null) return null;
      if (symbol != null)
        return analysis.AssetMetrics.TryGetValue(symbol,
          out IReadOnlyDictionary<string, FeatureValueSnapshot> values) &&
          values.TryGetValue(metric, out FeatureValueSnapshot asset) ? asset : null;
      return analysis.MarketMetrics.TryGetValue(metric, out FeatureValueSnapshot global) ? global : null;
    }
  }
}
