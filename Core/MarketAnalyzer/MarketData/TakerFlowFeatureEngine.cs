using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Measures executed taker-buy quote volume within the latest fully closed,
  /// UTC-aligned MarketTrend duration using the shared normalized kline store.
  /// Ratios are dimensionless; no independent exchange acquisition occurs.
  /// </summary>
  /// <summary>
  /// Optional phase-3 outputs captured while computing the existing taker
  /// ratio, not by traversing the same source candles a second time.
  /// Price and quote volume are native to that exact complete UTC period.
  /// </summary>
  public sealed class AlignedTakerFlowPeriod
  {
    public FeatureValueSnapshot TakerBuyRatio { get; init; }
    public double? PriceReturnPercent { get; init; }
    public double? QuoteVolume { get; init; }
  }

  public static class TakerFlowFeatureEngine
  {
    public static bool IsMetric(string metric)
    {
      string name = MarketTrendMetrics.Normalize(metric);
      return name == MarketTrendMetrics.TakerBuyRatio ||
        name == MarketTrendMetrics.TakerImbalance;
    }

    public static bool HasMetrics(IReadOnlyCollection<string> metrics) =>
      metrics != null && metrics.Any(IsMetric);

    public static bool IsGlobalMetric(string metric)
    {
      string name = MarketTrendMetrics.Normalize(metric);
      return name == MarketTrendMetrics.MedianTakerBuyRatio ||
        name == MarketTrendMetrics.BuyerDominanceBreadth ||
        name == MarketTrendMetrics.StrongBuyerDominanceBreadth ||
        name == MarketTrendMetrics.TakerImbalanceDispersion;
    }

    public static bool HasGlobalMetrics(IReadOnlyCollection<string> metrics) =>
      metrics != null && metrics.Any(IsGlobalMetric);

    public static bool HasAnyMetrics(IReadOnlyCollection<string> metrics) =>
      HasMetrics(metrics) || HasGlobalMetrics(metrics);

    /// <summary>
    /// Equal-market-weighted aggregation of already calculated per-asset
    /// taker ratios and imbalances, using the fixed global MarketTrend cohort.
    /// </summary>
    public static IReadOnlyDictionary<string, FeatureValueSnapshot> CalculateGlobal(
      IEnumerable<string> globalSymbols, IReadOnlyCollection<string> enabled,
      IReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> assetMetrics,
      DateTimeOffset asOfUtc, MarketTrend trend = null)
    {
      var results = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
      if (!HasGlobalMetrics(enabled))
        return new ReadOnlyDictionary<string, FeatureValueSnapshot>(results);

      string[] symbols = (globalSymbols ?? Array.Empty<string>())
        .Where(symbol => !String.IsNullOrWhiteSpace(symbol))
        .Select(symbol => symbol.Trim().ToUpperInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
      var ratios = new List<FeatureValueSnapshot>();
      var imbalances = new List<FeatureValueSnapshot>();
      foreach (string symbol in symbols)
      {
        if (assetMetrics == null || !assetMetrics.TryGetValue(symbol, out var values) ||
            values == null)
          continue;
        if (values.TryGetValue(MarketTrendMetrics.TakerBuyRatio, out var ratio) &&
            Usable(ratio) && ratio.Value.Value >= 0 && ratio.Value.Value <= 1)
          ratios.Add(ratio);
        if (values.TryGetValue(MarketTrendMetrics.TakerImbalance, out var imbalance) &&
            Usable(imbalance) && imbalance.Value.Value >= -1 && imbalance.Value.Value <= 1)
          imbalances.Add(imbalance);
      }
      double strong = trend?.StrongBuyerDominanceThreshold ?? 0.60;
      foreach (string metric in enabled.Where(IsGlobalMetric)
        .Select(MarketTrendMetrics.Normalize).Distinct(StringComparer.OrdinalIgnoreCase))
      {
        List<FeatureValueSnapshot> inputs = metric == MarketTrendMetrics.TakerImbalanceDispersion
          ? imbalances : ratios;
        double[] values = inputs.Select(feature => feature.Value.Value)
          .OrderBy(value => value).ToArray();
        string thresholdError = metric == MarketTrendMetrics.StrongBuyerDominanceBreadth &&
          (!Double.IsFinite(strong) || strong <= 0.5 || strong > 1)
          ? "StrongBuyerDominanceThreshold must be finite, greater than 0.5 and at most 1 (ratio)."
          : "";
        double? measurement = null;
        if (thresholdError.Length == 0 && values.Length > 0)
        {
          switch (metric)
          {
            case MarketTrendMetrics.MedianTakerBuyRatio:
              int middle = values.Length / 2;
              measurement = values.Length % 2 == 1 ? values[middle]
                : values[middle - 1] + (values[middle] - values[middle - 1]) / 2.0;
              break;
            case MarketTrendMetrics.BuyerDominanceBreadth:
              measurement = values.Count(value => value > 0.5) * 100.0 / values.Length;
              break;
            case MarketTrendMetrics.StrongBuyerDominanceBreadth:
              measurement = values.Count(value => value >= strong) * 100.0 / values.Length;
              break;
            case MarketTrendMetrics.TakerImbalanceDispersion:
              double mean = values.Average();
              measurement = Math.Sqrt(values.Average(value => (value - mean) * (value - mean)));
              break;
          }
        }
        var raw = new FeatureValueSnapshot
        {
          CalculatedAtUtc = asOfUtc,
          SourceDataTimestampUtc = inputs.Count > 0 &&
            inputs.All(value => value.SourceDataTimestampUtc.HasValue)
              ? inputs.Min(value => value.SourceDataTimestampUtc.Value) : (DateTimeOffset?)null,
          Value = measurement,
          SampleCount = inputs.Count,
          IsValid = measurement.HasValue && Double.IsFinite(measurement.Value),
          InvalidReason = thresholdError.Length > 0 ? thresholdError :
            measurement.HasValue ? "" : "No complete taker-flow observations are available."
        };
        results[metric] = MarketMetricCoverage.Apply(raw, inputs.Count, symbols.Length, asOfUtc);
      }
      return new ReadOnlyDictionary<string, FeatureValueSnapshot>(results);
    }

    private static bool Usable(FeatureValueSnapshot feature) =>
      feature != null && feature.IsValid && !feature.IsStale &&
      feature.Value.HasValue && Double.IsFinite(feature.Value.Value);

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> Calculate(
      IEnumerable<string> assetSymbols, IReadOnlyCollection<string> enabled,
      MarketDataStore store, TimeSpan interval, int trendMinutes, DateTimeOffset asOfUtc,
      IReadOnlyCollection<string> aggregateRatioSymbols = null,
      IDictionary<string, AlignedTakerFlowPeriod> alignedPeriods = null)
    {
      var result = new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(
        StringComparer.OrdinalIgnoreCase);
      if (!HasAnyMetrics(enabled))
        return new ReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(result);

      string[] selected = (enabled ?? Array.Empty<string>()).Where(IsMetric)
        .Select(MarketTrendMetrics.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
      bool assetRatio = selected.Contains(MarketTrendMetrics.TakerBuyRatio, StringComparer.OrdinalIgnoreCase);
      bool assetImbalance = selected.Contains(MarketTrendMetrics.TakerImbalance, StringComparer.OrdinalIgnoreCase);
      bool globalDispersion = (enabled ?? Array.Empty<string>())
        .Contains(MarketTrendMetrics.TakerImbalanceDispersion, StringComparer.OrdinalIgnoreCase);
      var assets = new HashSet<string>((assetSymbols ?? Array.Empty<string>())
        .Where(symbol => !String.IsNullOrWhiteSpace(symbol))
        .Select(symbol => symbol.Trim().ToUpperInvariant()), StringComparer.OrdinalIgnoreCase);
      var globals = new HashSet<string>((aggregateRatioSymbols ?? Array.Empty<string>())
        .Where(symbol => !String.IsNullOrWhiteSpace(symbol))
        .Select(symbol => symbol.Trim().ToUpperInvariant()), StringComparer.OrdinalIgnoreCase);
      var required = new HashSet<string>(globals, StringComparer.OrdinalIgnoreCase);
      if (assetRatio || assetImbalance) required.UnionWith(assets);
      foreach (string symbol in required)
      {
        bool needRatio = globals.Contains(symbol) ||
          (assets.Contains(symbol) && (assetRatio || assetImbalance));
        bool needImbalance = (assetImbalance && assets.Contains(symbol)) ||
          (globalDispersion && globals.Contains(symbol));
        if (!needRatio && !needImbalance) continue;

        FeatureValueSnapshot buy = CalculateRatio(store?.GetSnapshot(symbol, interval, asOfUtc),
          trendMinutes, asOfUtc, alignedPeriods != null, out double? alignedPriceReturn,
          out double? alignedQuoteVolume);
        if (alignedPeriods != null)
        {
          alignedPeriods[symbol] = new AlignedTakerFlowPeriod
          {
            TakerBuyRatio = buy,
            PriceReturnPercent = alignedPriceReturn,
            QuoteVolume = alignedQuoteVolume
          };
        }
        var values = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
        // The ratio is already needed internally when imbalance is selected,
        // so publish it for Monitor display without additional calculation.
        if (needRatio) values[MarketTrendMetrics.TakerBuyRatio] = buy;
        if (needImbalance)
        {
          values[MarketTrendMetrics.TakerImbalance] = !buy.IsValid || !buy.Value.HasValue
            ? new FeatureValueSnapshot
            {
              CalculatedAtUtc = buy.CalculatedAtUtc,
              SourceDataTimestampUtc = buy.SourceDataTimestampUtc,
              SampleCount = buy.SampleCount,
              IsStale = buy.IsStale,
              InvalidReason = buy.InvalidReason
            }
            : new FeatureValueSnapshot
            {
              CalculatedAtUtc = buy.CalculatedAtUtc,
              SourceDataTimestampUtc = buy.SourceDataTimestampUtc,
              SampleCount = buy.SampleCount,
              IsValid = true,
              Value = 2.0 * buy.Value.Value - 1.0
            };
        }
        result[symbol] = new ReadOnlyDictionary<string, FeatureValueSnapshot>(values);
      }
      return new ReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(result);
    }

    private static bool TryFindClosedCandle(IReadOnlyList<MarketCandle> candles,
      long expectedOpenMilliseconds, out MarketCandle candle)
    {
      int left = 0, right = candles.Count - 1;
      while (left <= right)
      {
        int middle = left + (right - left) / 2;
        MarketCandle current = candles[middle];
        long open = current.OpenTimeUtc.ToUnixTimeMilliseconds();
        if (open == expectedOpenMilliseconds)
        {
          candle = current;
          return true;
        }
        if (open < expectedOpenMilliseconds) left = middle + 1;
        else right = middle - 1;
      }
      candle = null;
      return false;
    }

    private static FeatureValueSnapshot CalculateRatio(
      MarketDataSeriesSnapshot snapshot, int minutes, DateTimeOffset asOfUtc,
      bool includeAlignedPrice, out double? alignedReturnPercent,
      out double? alignedQuoteVolume)
    {
      alignedReturnPercent = null;
      alignedQuoteVolume = null;
      FeatureValueSnapshot Invalid(string reason, int samples = 0, bool stale = false) =>
        new FeatureValueSnapshot
        {
          CalculatedAtUtc = asOfUtc, SampleCount = samples, IsStale = stale,
          InvalidReason = reason
        };

      if (snapshot == null || snapshot.Interval <= TimeSpan.Zero)
        return Invalid("No normalized source candles are available for taker flow.");
      if (minutes <= 0 || TimeSpan.FromMinutes(minutes).Ticks % snapshot.Interval.Ticks != 0)
        return Invalid("Taker flow requires TrendMinutes to be a multiple of the normalized candle interval.");
      if (snapshot.IsStale)
        return Invalid("Normalized source candles for taker flow are stale.", stale: true);

      long intervalMs = checked((long)snapshot.Interval.TotalMilliseconds);
      long trendMs = checked((long)TimeSpan.FromMinutes(minutes).TotalMilliseconds);
      long asOfMs = asOfUtc.ToUniversalTime().ToUnixTimeMilliseconds();
      long firstOpen = asOfMs / trendMs * trendMs - trendMs;
      DateTimeOffset expectedCloseUtc = DateTimeOffset.FromUnixTimeMilliseconds(firstOpen + trendMs - 1);
      IReadOnlyList<MarketCandle> candles = snapshot.ClosedCandles;

      double total = 0;
      double bought = 0;
      double firstPrice = 0;
      double lastPrice = 0;
      bool validPrice = true;
      int count = 0;
      for (long open = firstOpen; open < firstOpen + trendMs; open += intervalMs)
      {
        if (!TryFindClosedCandle(candles, open, out MarketCandle candle))
          return Invalid("Missing normalized source candle in the latest complete MarketTrend period.", count);
        if (candle.CloseTimeUtc.ToUnixTimeMilliseconds() != open + intervalMs - 1 ||
            !candle.IsClosed)
          return Invalid("Incomplete or misaligned normalized source candle in the latest complete MarketTrend period.", count);
        if (!candle.TakerBuyQuoteVolume.HasValue)
          return Invalid("Taker-buy quote-volume field is missing or unverified (including rejected raw values).", count);

        double q = candle.QuoteVolume;
        double b = candle.TakerBuyQuoteVolume.Value;
        if (!double.IsFinite(q) || q < 0)
          return Invalid("Invalid total quote-volume value in a source candle (negative or nonfinite).", count);
        if (!double.IsFinite(b) || b < 0 || b > q)
          return Invalid("Invalid taker-buy quote-volume value in a source candle (negative, nonfinite or greater than total).", count);
        total += q;
        bought += b;
        if (includeAlignedPrice)
        {
          if (count == 0) firstPrice = candle.Open;
          lastPrice = candle.Close;
          validPrice &= Double.IsFinite(candle.Open) && Double.IsFinite(candle.Close)
            && Double.IsFinite(candle.High) && Double.IsFinite(candle.Low)
            && candle.Open > 0 && candle.Close > 0 && candle.Low > 0
            && candle.High >= Math.Max(candle.Open, candle.Close)
            && candle.Low <= Math.Min(candle.Open, candle.Close)
            && candle.High >= candle.Low;
        }
        count++;
      }
      if (!double.IsFinite(total) || total < 0)
        return Invalid("Invalid accumulated total quote volume in the latest complete MarketTrend period.", count);
      if (total == 0)
        return Invalid("Zero total quote volume in the latest complete MarketTrend period; taker-buy ratio is undefined (0/0).", count);
      if (!double.IsFinite(bought) || bought < 0 || bought > total)
        return Invalid("Invalid accumulated taker-buy quote volume in the latest complete MarketTrend period.", count);

      double ratio = bought / total;
      if (!double.IsFinite(ratio) || ratio < 0 || ratio > 1)
        return Invalid("Taker-buy ratio is outside the valid [0,1] interval.", count);

      if (includeAlignedPrice)
      {
        // The same verified taker-volume pass already visited every candle.
        // Existing ratio validity is independent of price validity.
        alignedQuoteVolume = total;
        double priceChange = (lastPrice / firstPrice - 1.0) * 100.0;
        if (validPrice && Double.IsFinite(priceChange))
          alignedReturnPercent = priceChange;
      }

      return new FeatureValueSnapshot
      {
        CalculatedAtUtc = asOfUtc,
        SourceDataTimestampUtc = expectedCloseUtc,
        SampleCount = count,
        Value = ratio,
        IsValid = true
      };
    }
  }
}
