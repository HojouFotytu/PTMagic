using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Reporting-only classification of history that cannot predate a verified Binance
  /// Futures onboard date. Never changes metric values, cohorts or trigger decisions.
  /// Establishes the verified new-market notice membership; summary warning exclusion
  /// is market-wide for that raid and does not affect calculation validity.
  /// </summary>
  public sealed class NewMarketHistoryClassification
  {
    private readonly Dictionary<string, HashSet<string>> _unavailable =
      new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _onboard =
      new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _deltaUnavailable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public int MarketCount => _unavailable.Count;

    public bool IsReportedNewMarket(string symbol) =>
      !String.IsNullOrWhiteSpace(symbol) && _unavailable.ContainsKey(symbol.Trim());

    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> Affected =>
      new ReadOnlyDictionary<string, IReadOnlyCollection<string>>(
        _unavailable.ToDictionary(pair => pair.Key,
          pair => (IReadOnlyCollection<string>)Array.AsReadOnly(pair.Value.Select(x => x.EndsWith("|Delta", StringComparison.OrdinalIgnoreCase) ? x.Substring(0, x.Length - 6) : x).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()),
          StringComparer.OrdinalIgnoreCase));

    public bool IsExpected(string symbol, string trend, string metric) =>
      !String.IsNullOrWhiteSpace(symbol) && _unavailable.TryGetValue(symbol, out var keys) &&
      keys.Contains(Key(trend, metric));

    public bool IsExpectedDelta(string symbol, string trend, string metric) =>
      _deltaUnavailable.Contains(symbol + "|" + Key(trend, metric));

    public DateTimeOffset? OnboardDate(string symbol) =>
      _onboard.TryGetValue(symbol, out var time) ? time : (DateTimeOffset?)null;

    internal void Add(string symbol, string trend, string metric, DateTimeOffset onboard)
    {
      if (!_unavailable.TryGetValue(symbol, out var keys))
      {
        keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _unavailable[symbol] = keys;
      }
      keys.Add(Key(trend, metric));
      _onboard[symbol] = onboard;
    }

    private static string Key(string trend, string metric) =>
      (trend ?? "").Trim() + "|" + MarketTrendMetrics.Normalize(metric);

    public static NewMarketHistoryClassification Analyze(
      IReadOnlyDictionary<string, MarketTrendAnalysisSnapshot> analyses,
      MarketDataStore store, Func<string, DateTimeOffset?> onboardDate,
      TimeSpan? sourceInterval = null,
      IReadOnlyDictionary<string, MarketTrendDeltaAnalysisSnapshot> deltas = null)
    {
      var output = new NewMarketHistoryClassification();
      if (analyses == null || store == null || onboardDate == null) return output;
      TimeSpan interval = sourceInterval ?? TimeSpan.FromMinutes(5);
      if (interval <= TimeSpan.Zero) return output;

      // Only markets with a current invalid asset measurement need source
      // inspection. Reuse one immutable store snapshot across trend horizons
      // to avoid sorting and allocating 5m histories thousands of times.
      var snapshots = new Dictionary<string, MarketDataSeriesSnapshot>(StringComparer.OrdinalIgnoreCase);
      foreach (MarketTrendAnalysisSnapshot analysis in analyses.Values.Where(a => a != null))
      {
        if (analysis.TrendMinutes <= 0 || analysis.CalculatedAtUtc == default ||
            analysis.ExpectedClosedCandleTimeUtc == default || analysis.AssetMetrics == null)
          continue;
        foreach (string symbol in analysis.AssetSymbols.Distinct(StringComparer.OrdinalIgnoreCase))
        {
          if (!analysis.AssetMetrics.TryGetValue(symbol, out var values) || values == null)
            continue;
          MarketTrendDeltaAnalysisSnapshot delta = null;
          deltas?.TryGetValue(analysis.MarketTrendName, out delta);
          IReadOnlyDictionary<string, MetricDeltaObservation> comparisons = null;
          delta?.AssetMetrics.TryGetValue(symbol, out comparisons);
          if (!values.Values.Any(v => v != null && !v.IsValid && !v.IsStale && !v.IsNoTrades) &&
              !(comparisons?.Values.Any(v => v?.Delta != null && !v.Delta.IsValid && !v.Delta.IsStale) ?? false))
            continue;
          DateTimeOffset? listed = onboardDate(symbol);
          if (!listed.HasValue || listed.Value <= DateTimeOffset.UnixEpoch ||
              listed.Value >= analysis.CalculatedAtUtc) continue;

          // This must be a continuous, current series whose first traded candle
          // matches the native onboard date. A retained cache starting later
          // than listing is NOT evidence that exchange history did not exist.
          string cacheKey = symbol + "|" + analysis.CalculatedAtUtc.UtcDateTime.Ticks;
          if (!snapshots.TryGetValue(cacheKey, out MarketDataSeriesSnapshot series))
          {
            series = store.GetSnapshot(symbol, interval, analysis.CalculatedAtUtc);
            snapshots[cacheKey] = series;
          }
          if (!IsVerifiedFromListing(series, listed.Value, analysis.ExpectedClosedCandleTimeUtc, interval))
            continue;
          foreach (var pair in values)
          {
            FeatureValueSnapshot feature = pair.Value;
            if (feature == null || feature.IsValid || feature.IsStale || feature.IsNoTrades)
              continue;
            if (IsPrelistingShortage(analysis, values, pair.Key, feature.InvalidReason,
                listed.Value, interval))
              output.Add(symbol, analysis.MarketTrendName, pair.Key, listed.Value);
          }
          // Verify the historical measurement using its own calculation window
          // and benchmark. The CURRENT source series must still be continuous
          // from the exchange listing through the raid's latest closed candle.
          var previous = delta?.PreviousAnalysis;
          if (previous == null || comparisons == null ||
              !previous.AssetMetrics.TryGetValue(symbol, out var priorValues)) continue;
          foreach (var comparison in comparisons)
          {
            var observation = comparison.Value;
            if (observation?.Delta == null || observation.Delta.IsValid || observation.Delta.IsStale ||
                observation.Current == null || observation.Previous == null) continue;
            bool currentExpected = output.IsExpected(symbol, analysis.MarketTrendName, comparison.Key);
            bool priorExpected = !observation.Previous.IsValid && !observation.Previous.IsStale &&
              !observation.Previous.IsNoTrades &&
              IsPrelistingShortage(previous, priorValues, comparison.Key,
                observation.Previous.InvalidReason, listed.Value, interval);
            if ((observation.Current.IsValid || currentExpected) &&
                (observation.Previous.IsValid || priorExpected) && (currentExpected || priorExpected))
            {
              output._deltaUnavailable.Add(symbol + "|" + Key(analysis.MarketTrendName, comparison.Key));
              // Distinct key prevents historical-only absence masking a current failure.
              output.Add(symbol, analysis.MarketTrendName, comparison.Key + "|Delta", listed.Value);
            }
          }
        }
      }
      return output;
    }

    private static bool IsVerifiedFromListing(MarketDataSeriesSnapshot series,
      DateTimeOffset listed, DateTimeOffset expectedLastClose, TimeSpan interval)
    {
      if (series == null || !series.HasClosedData || series.IsStale || series.HasDetectedGaps ||
          !series.FirstOpenTimeUtc.HasValue || !series.LatestClosedCloseTimeUtc.HasValue ||
          series.LatestClosedCloseTimeUtc.Value != expectedLastClose)
        return false;

      // Some listing times are between source-candle boundaries. Only tolerate
      // the single first source period; do not hide a later missing history span.
      return series.FirstOpenTimeUtc.Value >= listed - interval &&
        series.FirstOpenTimeUtc.Value <= listed + interval;
    }

    private static bool IsPrelistingShortage(MarketTrendAnalysisSnapshot analysis,
      IReadOnlyDictionary<string, FeatureValueSnapshot> values,
      string metric, string reason, DateTimeOffset listed, TimeSpan interval)
    {
      string name = MarketTrendMetrics.Normalize(metric);
      if (String.IsNullOrWhiteSpace(reason)) return false;

      bool historyReason;
      if (name == MarketTrendMetrics.ATRP)
        historyReason = reason.StartsWith("Insufficient complete ", StringComparison.OrdinalIgnoreCase)
          && reason.Contains(" for ATRP(", StringComparison.OrdinalIgnoreCase);
      else if (name == MarketTrendMetrics.VolumeExpansion)
        historyReason = reason.StartsWith("Insufficient complete ", StringComparison.OrdinalIgnoreCase)
          && reason.Contains(" for VolumeExpansion(", StringComparison.OrdinalIgnoreCase);
      else if (name == MarketTrendMetrics.PriceTrend)
        historyReason = reason.StartsWith("No exact closed-candle reference exists ", StringComparison.OrdinalIgnoreCase) ||
          (analysis.ExpectedClosedCandleTimeUtc < listed &&
           reason.Equals("No closed candle data is available.", StringComparison.OrdinalIgnoreCase));
      else if (name == MarketTrendMetrics.PricePathEfficiency)
        historyReason = reason.Equals("Missing initial or subsequent closed-candle price-path history.", StringComparison.OrdinalIgnoreCase) ||
          (analysis.ExpectedClosedCandleTimeUtc < listed &&
           reason.Equals("No compatible closed normalized source candles are available.", StringComparison.OrdinalIgnoreCase));
      else if (name == MarketTrendMetrics.CorrelationToBTC)
      {
        Match missing = Regex.Match(reason,
          @"^Asset return vector unavailable: Missing or invalid exact closed source candle for '[^']+' at (?<date>[^\s.]+(?:\.\d+)?(?:\+\d\d:\d\d|Z))\.",
          RegexOptions.IgnoreCase);
        if (!missing.Success || !DateTimeOffset.TryParse(missing.Groups["date"].Value,
              CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset missingClose) ||
            missingClose - interval + TimeSpan.FromMilliseconds(1) >= listed)
          return false;
        historyReason = true;
      }
      else if (name == MarketTrendMetrics.TakerBuyRatio || name == MarketTrendMetrics.TakerImbalance)
        historyReason = reason.Equals(
          "Missing normalized source candle in the latest complete MarketTrend period.",
          StringComparison.OrdinalIgnoreCase);
      else if (name == MarketTrendMetrics.RelativeStrengthMarket ||
               name == MarketTrendMetrics.RelativeStrengthBTC)
      {
        if (!values.TryGetValue(MarketTrendMetrics.PriceTrend, out var ownReturn) ||
            ownReturn == null || ownReturn.IsValid || ownReturn.IsStale ||
            !IsPrelistingShortage(analysis, values, MarketTrendMetrics.PriceTrend,
              ownReturn.InvalidReason, listed, interval)) return false;
        // Do not relabel a benchmark/global outage as a listing shortfall.
        if (name == MarketTrendMetrics.RelativeStrengthMarket)
        {
          if (!analysis.MarketMetrics.TryGetValue(MarketTrendMetrics.MedianPriceTrend,
                out var median) || median == null || !median.IsValid || median.IsStale)
            return false;
        }
        else
        {
          // Suppress dependent RelativeStrengthBTC only when the benchmark
          // return is independently known valid, never when it is absent.
          if (!analysis.AssetMetrics.TryGetValue("BTCUSDT", out var btc) || btc == null ||
              !btc.TryGetValue(MarketTrendMetrics.PriceTrend, out var btcReturn) ||
              btcReturn == null || !btcReturn.IsValid || btcReturn.IsStale)
            return false;
        }
        return reason.StartsWith("Asset return or ", StringComparison.OrdinalIgnoreCase)
          && reason.Contains(" is unavailable for MarketTrend ", StringComparison.OrdinalIgnoreCase);
      }
      else
        return false;

      if (!historyReason) return false;

      MarketTrendHistoryRequirement requirement = analysis.HistoryRequirements?
        .FirstOrDefault(r => String.Equals(r.Metric, name, StringComparison.OrdinalIgnoreCase));
      if (requirement == null || requirement.MinimumClosedSourceCandles <= 0)
        return false;
      bool aligned = name == MarketTrendMetrics.ATRP ||
        name == MarketTrendMetrics.VolumeExpansion ||
        name == MarketTrendMetrics.CorrelationToBTC ||
        name == MarketTrendMetrics.TakerBuyRatio ||
        name == MarketTrendMetrics.TakerImbalance;
      DateTimeOffset endBoundary = aligned
        ? EpochBoundary(analysis.CalculatedAtUtc, analysis.TrendMinutes)
        : analysis.ExpectedClosedCandleTimeUtc.AddMilliseconds(1);
      if (endBoundary == default) return false;
      DateTimeOffset requiredOpen = endBoundary - TimeSpan.FromTicks(
        checked(requirement.MinimumClosedSourceCandles * interval.Ticks));
      return requiredOpen < listed;
    }

    private static DateTimeOffset EpochBoundary(DateTimeOffset at, int minutes)
    {
      long ms = checked((long)minutes * 60000L);
      if (ms <= 0) return default;
      long now = at.ToUniversalTime().ToUnixTimeMilliseconds();
      return DateTimeOffset.FromUnixTimeMilliseconds((now / ms) * ms);
    }
  }
}
