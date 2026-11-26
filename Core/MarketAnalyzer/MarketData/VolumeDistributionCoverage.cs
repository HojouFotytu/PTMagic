using System;
using System.Collections.Generic;
using System.Linq;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer.MarketData
{
  public sealed class VolumeCoverageDiagnostics
  {
    public string[] EligibleAssets { get; init; } = Array.Empty<string>();
    public string[] ValidAssets { get; init; } = Array.Empty<string>();
    public string[] ExcludedAssets { get; init; } = Array.Empty<string>();
    public string[] PrelistingAssets { get; init; } = Array.Empty<string>();
    public string[] PartialListingAssets { get; init; } = Array.Empty<string>();
    public string[] ProxyEstimatedAssets { get; init; } = Array.Empty<string>();
    public double? UnknownVolumeProxyPerAsset { get; init; }
    public string[] MissingBenchmarks { get; init; } = Array.Empty<string>();
    public double? EstimatedVolumeWeightedCoveragePercent { get; init; }
    public double? EstimatedMissingVolumeSharePercent => EstimatedVolumeWeightedCoveragePercent.HasValue
      ? 100 - EstimatedVolumeWeightedCoveragePercent.Value : null;
    public double? ActualQuoteVolumeDenominator { get; init; }
    public int? EstimateAgeMinutes { get; init; }
    public bool IsPartial => ValidAssets.Length != EligibleAssets.Length;
  }

  public static class VolumeDistributionCoverage
  {
    // One immediately preceding equal-duration completed period is the
    // freshness limit; no forward observations or extrapolation.
    public static bool IsMetric(string metric) =>
      metric == MarketTrendMetrics.VolumeConcentration || metric == MarketTrendMetrics.TopNVolumeShare ||
      metric == MarketTrendMetrics.BTCVolumeShare || metric == MarketTrendMetrics.BTCETHVolumeShare;

    internal static bool Usable(FeatureValueSnapshot v) => v != null && v.IsValid && !v.IsStale &&
      v.Value.HasValue && double.IsFinite(v.Value.Value) && v.Value.Value >= 0;

    internal static Dictionary<string, FeatureValueSnapshot> Read(
      IEnumerable<string> symbols, MarketTrend trend, MarketDataStore store, TimeSpan interval, DateTimeOffset now)
    {
      return symbols.ToDictionary(s => s, s => TrendPeriodMetricsCalculator.Calculate(
        store.GetSnapshot(s, interval, now), trend.TrendMinutes, now,
        trend.ATRPeriod, trend.VolumeExpansionLookback, false, false, true).LastQuoteVolume,
        StringComparer.OrdinalIgnoreCase);
    }

    internal static Dictionary<string, FeatureValueSnapshot> References(
      string[] eligible, IReadOnlyDictionary<string, FeatureValueSnapshot> observed,
      HashSet<string> selected, MarketTrend trend, MarketDataStore store, TimeSpan interval, DateTimeOffset now)
    {
      // For an excluded but observed market, its actual period volume is
      // known (e.g. Delta exclusion); only missing observations need estimates.
      return Read(eligible.Where(s => !selected.Contains(s) &&
        (!observed.TryGetValue(s, out var v) || !Usable(v))), trend, store, interval,
        now.AddMinutes(-trend.TrendMinutes));
    }

    internal sealed class MissingEvidence
    {
      public Dictionary<string, double> Known = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
      public HashSet<string> Prelisting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      public HashSet<string> PartialListing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    internal static MissingEvidence ListingEvidence(string[] eligible,
      IReadOnlyDictionary<string, FeatureValueSnapshot> observed, HashSet<string> selected,
      MarketTrend trend, MarketDataStore store, TimeSpan interval, DateTimeOffset now,
      Func<string, DateTimeOffset?> onboardDate)
    {
      var result = new MissingEvidence();
      if (onboardDate == null) return result;
      long periodMs = checked((long)trend.TrendMinutes * 60000L);
      long at = now.ToUnixTimeMilliseconds();
      var end = DateTimeOffset.FromUnixTimeMilliseconds(at / periodMs * periodMs);
      var start = end.AddMinutes(-trend.TrendMinutes);
      foreach (string symbol in eligible)
      {
        if (selected.Contains(symbol) || (observed.TryGetValue(symbol, out var actual) && Usable(actual))) continue;
        var listed = onboardDate(symbol);
        if (!listed.HasValue || listed.Value <= DateTimeOffset.UnixEpoch) continue;
        if (listed.Value >= end)
        {
          result.Known[symbol] = 0;
          result.Prelisting.Add(symbol);
          continue;
        }
        if (listed.Value <= start) continue;
        // A listing-crossing interval is not a zero-volume interval.
        var series = store.GetSnapshot(symbol, interval, now);
        long sourceMs = checked((long)interval.TotalMilliseconds);
        long listingMs = listed.Value.ToUnixTimeMilliseconds();
        var first = DateTimeOffset.FromUnixTimeMilliseconds(listingMs / sourceMs * sourceMs);
        var candles = series.ClosedCandles.Where(c => c.OpenTimeUtc >= first && c.OpenTimeUtc < end).ToArray();
        if (candles.Length != (end - first).Ticks / interval.Ticks || candles.Length == 0) continue;
        bool complete = true;
        double total = 0;
        for (int i = 0; i < candles.Length; i++)
        {
          var candle = candles[i];
          var open = first.AddTicks(interval.Ticks * i);
          if (candle.OpenTimeUtc != open || candle.CloseTimeUtc != open.Add(interval).AddMilliseconds(-1) ||
              !double.IsFinite(candle.QuoteVolume) || candle.QuoteVolume < 0)
          { complete = false; break; }
          total += candle.QuoteVolume;
        }
        if (complete && double.IsFinite(total))
        {
          result.Known[symbol] = total;
          result.PartialListing.Add(symbol);
        }
      }
      return result;
    }

    internal static FeatureValueSnapshot Calculate(
      string metric, MarketTrend trend, string[] eligible,
      IReadOnlyDictionary<string, FeatureValueSnapshot> observed, HashSet<string> selected,
      IReadOnlyDictionary<string, FeatureValueSnapshot> references, DateTimeOffset now, string quote, MissingEvidence evidence = null)
    {
      var valid = eligible.Where(s => selected.Contains(s) && observed.TryGetValue(s, out var v) && Usable(v)).ToArray();
      var validSet = new HashSet<string>(valid, StringComparer.OrdinalIgnoreCase);
      var excluded = eligible.Where(s => !validSet.Contains(s)).ToArray();
      double total = valid.Sum(s => observed[s].Value.Value);
      double missing = 0;
      bool usedReference = false;
      evidence ??= new MissingEvidence();
      var unknown = new List<string>();
      // Charge unresolved assets the largest verified cohort volume. This
      // conservative proxy is not a proven upper bound and never enters the
      // actual metric denominator.
      double proxy = observed.Values.Where(Usable).Select(x => x.Value.Value)
        .Concat(references.Values.Where(Usable).Select(x => x.Value.Value))
        .Concat(evidence.Known.Values).DefaultIfEmpty(0).Max();
      foreach (string s in excluded)
      {
        if (observed.TryGetValue(s, out var actual) && Usable(actual)) missing += actual.Value.Value;
        else if (evidence.Known.TryGetValue(s, out double known)) missing += known;
        else if (references.TryGetValue(s, out var reference) && Usable(reference))
        { missing += reference.Value.Value; usedReference = true; }
        else { unknown.Add(s); missing += proxy; }
      }
      double combined = total + missing;
      double? economic = double.IsFinite(combined) && combined > 0
        ? total / combined * 100 : null;
      string[] required = metric == MarketTrendMetrics.BTCVolumeShare ? new[] { "BTC" + quote } :
        metric == MarketTrendMetrics.BTCETHVolumeShare ? new[] { "BTC" + quote, "ETH" + quote } : Array.Empty<string>();
      string[] missingBenchmarks = required.Where(s => !validSet.Contains(s)).ToArray();
      string reason = "";
      double? value = null;
      if (missingBenchmarks.Length > 0) reason = "Missing required volume benchmark: " + String.Join(", ", missingBenchmarks);
      else if (!MarketMetricCoverage.IsReady(valid.Length, eligible.Length)) reason = "Insufficient numerical volume coverage.";
      else if (!(total > 0) || !double.IsFinite(total)) reason = "Total complete-period quote volume is zero or invalid.";
      else if (!economic.HasValue) reason = "Missing comparable verified volume history for coverage estimation.";
      else if (economic.Value < MarketMetricCoverage.MinimumCoverageRatio * 100) reason = "Insufficient estimated economic volume coverage.";
      else if (metric == MarketTrendMetrics.TopNVolumeShare &&
        (trend.TopNVolumeCount < 1 || trend.TopNVolumeCount > 1000 || trend.TopNVolumeCount > valid.Length))
        reason = "Invalid TopNVolumeCount or fewer valid markets than TopNVolumeCount.";
      else if (metric == MarketTrendMetrics.VolumeConcentration)
        value = valid.Sum(s => Math.Pow(observed[s].Value.Value / total, 2.0)) * 100;
      else if (metric == MarketTrendMetrics.TopNVolumeShare)
        value = valid.OrderByDescending(s => observed[s].Value.Value).ThenBy(s => s, StringComparer.OrdinalIgnoreCase)
          .Take(trend.TopNVolumeCount).Sum(s => observed[s].Value.Value) / total * 100;
      else value = required.Sum(s => observed[s].Value.Value) / total * 100;
      return new FeatureValueSnapshot
      {
        Value = value, IsValid = value.HasValue, InvalidReason = reason, CalculatedAtUtc = now,
        SourceDataTimestampUtc = valid.Length == 0 ? null : valid.Select(s => observed[s].SourceDataTimestampUtc).Min(),
        SampleCount = valid.Length, EligibleSampleCount = eligible.Length,
        CoveragePercent = eligible.Length > 0 ? valid.Length * 100.0 / eligible.Length : null,
        VolumeCoverage = new VolumeCoverageDiagnostics
        {
          EligibleAssets = eligible, ValidAssets = valid, ExcludedAssets = excluded,
          MissingBenchmarks = missingBenchmarks, EstimatedVolumeWeightedCoveragePercent = economic,
          ActualQuoteVolumeDenominator = double.IsFinite(total) ? total : null,
          EstimateAgeMinutes = usedReference ? trend.TrendMinutes : null,
          PrelistingAssets = evidence.Prelisting.ToArray(),
          PartialListingAssets = evidence.PartialListing.ToArray(),
          ProxyEstimatedAssets = unknown.ToArray(),
          UnknownVolumeProxyPerAsset = unknown.Count > 0 ? proxy : null
        }
      };
    }
  }
}
