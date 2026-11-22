using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer.MarketData
{
  public sealed class PricePathEfficiencyResult
  {
    public IReadOnlyDictionary<string, FeatureValueSnapshot> GlobalMetrics { get; init; } =
      new Dictionary<string, FeatureValueSnapshot>();
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> AssetMetrics { get; init; } =
      new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(StringComparer.OrdinalIgnoreCase);
  }

  /// <summary>
  /// Closed-candle directional path directness for an arbitrary configured
  /// MarketTrend duration. Reuses the normalized store; no new source endpoint.
  /// Direction remains the responsibility of the existing PriceTrend metric.
  /// </summary>
  public static class PricePathEfficiencyEngine
  {
    // Exact, source-proven mathematical absence: distinct from missing,
    // invalid, incomplete or stale price candles.
    public const string FlatPathUnavailableReason = "A completely flat price path has an undefined efficiency denominator.";

    public static bool HasMetrics(IReadOnlyCollection<string> enabled) =>
      enabled != null && (enabled.Contains(MarketTrendMetrics.PricePathEfficiency, StringComparer.OrdinalIgnoreCase) ||
        enabled.Contains(MarketTrendMetrics.MedianPricePathEfficiency, StringComparer.OrdinalIgnoreCase));

    public static PricePathEfficiencyResult Calculate(
      MarketTrend trend, IReadOnlyCollection<string> enabled,
      IEnumerable<string> globalSymbols, IEnumerable<string> assetSymbols,
      MarketDataStore store, TimeSpan interval, DateTimeOffset asOfUtc,
      string quoteCurrency = "USDT")
    {
      if (trend == null) throw new ArgumentNullException(nameof(trend));
      if (store == null) throw new ArgumentNullException(nameof(store));
      MarketTrendTimeframe.Validate(trend, interval);

      IReadOnlyCollection<string> requested = enabled ?? MarketTrendMetrics.ParseEnabled(trend);
      bool needAsset = requested.Contains(MarketTrendMetrics.PricePathEfficiency, StringComparer.OrdinalIgnoreCase);
      bool needGlobal = requested.Contains(MarketTrendMetrics.MedianPricePathEfficiency, StringComparer.OrdinalIgnoreCase);
      var globalResults = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
      var assetResults = new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(StringComparer.OrdinalIgnoreCase);
      if (!needAsset && !needGlobal)
        return new PricePathEfficiencyResult { GlobalMetrics = globalResults, AssetMetrics = assetResults };

      string[] global = Normalize(globalSymbols);
      string[] assets = Normalize(assetSymbols);
      var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      if (needGlobal) required.UnionWith(global);
      if (needAsset) required.UnionWith(assets);

      bool fiat = MarketTrendFiatConversion.IsFiat(trend.TrendCurrency);
      string conversionName = fiat ? MarketTrendFiatConversion.ConversionSymbol(quoteCurrency) : null;
      bool needConversion = fiat && conversionName != null;
      MarketDataSeriesSnapshot conversion = needConversion && !String.IsNullOrWhiteSpace(conversionName)
        ? store.GetSnapshot(conversionName, interval, asOfUtc) : null;

      var calculated = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
      foreach (string symbol in required)
      {
        FeatureValueSnapshot feature = CalculateAsset(
          store.GetSnapshot(symbol, interval, asOfUtc), conversion, needConversion,
          interval, trend.TrendMinutes, asOfUtc);
        calculated[symbol] = feature;
        // A global calculation has already evaluated each per-asset prerequisite.
        // Publish it for inspection without implicitly enabling an SMS trigger.
        assetResults[symbol] = new ReadOnlyDictionary<string, FeatureValueSnapshot>(
          new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase)
          {
            [MarketTrendMetrics.PricePathEfficiency] = feature
          });
      }

      if (needGlobal)
      {
        FeatureValueSnapshot[] valid = global.Where(calculated.ContainsKey)
          .Select(symbol => calculated[symbol]).Where(Usable).ToArray();
        double[] values = valid.Select(value => value.Value.Value).OrderBy(value => value).ToArray();
        double? median = values.Length == 0 ? null
          : values.Length % 2 == 1 ? values[values.Length / 2]
          : values[values.Length / 2 - 1] +
            (values[values.Length / 2] - values[values.Length / 2 - 1]) / 2.0;
        FeatureValueSnapshot raw = new FeatureValueSnapshot
        {
          CalculatedAtUtc = asOfUtc,
          SourceDataTimestampUtc = valid.Length > 0 && valid.All(v => v.SourceDataTimestampUtc.HasValue)
            ? valid.Min(v => v.SourceDataTimestampUtc.Value) : (DateTimeOffset?)null,
          Value = median, IsValid = median.HasValue && Double.IsFinite(median.Value),
          SampleCount = valid.Length,
          InvalidReason = median.HasValue ? "" : "No valid asset price-path efficiencies are available."
        };
        globalResults[MarketTrendMetrics.MedianPricePathEfficiency] =
          MarketMetricCoverage.Apply(raw, valid.Length, global.Length, asOfUtc);
      }

      return new PricePathEfficiencyResult
      {
        GlobalMetrics = new ReadOnlyDictionary<string, FeatureValueSnapshot>(globalResults),
        AssetMetrics = new ReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(assetResults)
      };
    }

    private static FeatureValueSnapshot CalculateAsset(
      MarketDataSeriesSnapshot series, MarketDataSeriesSnapshot conversion, bool needConversion,
      TimeSpan interval, int trendMinutes, DateTimeOffset asOfUtc)
    {
      if (series == null || !series.HasClosedData || series.Interval != interval)
        return Invalid(asOfUtc, "No compatible closed normalized source candles are available.");
      if (series.IsStale)
        return Invalid(asOfUtc, "Normalized price-path source candles are stale.", stale: true);
      if (needConversion && (conversion == null || !conversion.HasClosedData || conversion.Interval != interval))
        return Invalid(asOfUtc, "Fiat price-path efficiency requires a complete quote-to-USDT conversion series.");
      if (needConversion && conversion.IsStale)
        return Invalid(asOfUtc, "Fiat price-path conversion candles are stale.", stale: true);

      long steps = TimeSpan.FromMinutes(trendMinutes).Ticks / interval.Ticks;
      if (steps < 2)
        return Invalid(asOfUtc, "Price-path efficiency requires at least two closed-candle price changes.");

      long intervalMs = checked((long)interval.TotalMilliseconds);
      long nowMs = asOfUtc.ToUniversalTime().ToUnixTimeMilliseconds();
      long latestCloseMs = (nowMs / intervalMs) * intervalMs - 1;
      long earliestCloseMs = latestCloseMs - checked(steps * intervalMs);
      IReadOnlyList<MarketCandle> source = series.ClosedCandles;
      int startIndex = FindClose(source, earliestCloseMs);
      if (startIndex < 0 || steps >= source.Count - startIndex)
        return Invalid(asOfUtc, "Missing initial or subsequent closed-candle price-path history.");

      IReadOnlyList<MarketCandle> conversionCandles = conversion?.ClosedCandles;
      int conversionStart = needConversion ? FindClose(conversionCandles, earliestCloseMs) : -1;
      if (needConversion && (conversionStart < 0 || steps >= conversionCandles.Count - conversionStart))
        return Invalid(asOfUtc, "Fiat conversion is missing required aligned closed-candle history.");

      double net = 0, travel = 0;
      double previousPrice = 0, previousConversion = 0;
      for (long step = 0; step <= steps; step++)
      {
        long expectedCloseMs = earliestCloseMs + step * intervalMs;
        MarketCandle candle = source[startIndex + (int)step];
        if (!ValidClose(candle, expectedCloseMs))
          return Invalid(asOfUtc, "Price path contains a missing, invalid or misaligned closed candle.", (int)step);

        double converted = 0;
        if (needConversion)
        {
          MarketCandle quoteCandle = conversionCandles[conversionStart + (int)step];
          if (!ValidClose(quoteCandle, expectedCloseMs))
            return Invalid(asOfUtc, "Fiat price path contains a missing, invalid or misaligned conversion candle.", (int)step);
          converted = quoteCandle.Close;
        }
        if (step > 0)
        {
          // Log differences avoid an overflow from multiplying native and Fiat
          // prices, and keep every intervening source-candle reversal visible.
          double move = Math.Log(candle.Close) - Math.Log(previousPrice);
          if (needConversion)
            move += Math.Log(converted) - Math.Log(previousConversion);
          if (!Double.IsFinite(move))
            return Invalid(asOfUtc, "Price-path logarithmic movement is invalid.", (int)step);
          net += move;
          travel += Math.Abs(move);
        }
        previousPrice = candle.Close;
        previousConversion = converted;
      }

      if (!Double.IsFinite(travel) || !Double.IsFinite(net))
        return Invalid(asOfUtc, "Price-path movement accumulated to a nonfinite value.", (int)(steps + 1));
      if (travel == 0)
        return Invalid(asOfUtc, FlatPathUnavailableReason, (int)(steps + 1));

      double efficiency = Math.Abs(net) / travel;
      if (!Double.IsFinite(efficiency))
        return Invalid(asOfUtc, "Price-path efficiency is nonfinite.", (int)(steps + 1));
      return new FeatureValueSnapshot
      {
        Value = Math.Clamp(efficiency, 0.0, 1.0),
        CalculatedAtUtc = asOfUtc,
        SourceDataTimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(latestCloseMs),
        SampleCount = (int)(steps + 1),
        IsValid = true
      };
    }

    private static int FindClose(IReadOnlyList<MarketCandle> candles, long timestampMs)
    {
      if (candles == null) return -1;
      int left = 0, right = candles.Count - 1;
      while (left <= right)
      {
        int mid = left + (right - left) / 2;
        long actual = candles[mid].CloseTimeUtc.ToUnixTimeMilliseconds();
        if (actual == timestampMs) return mid;
        if (actual < timestampMs) left = mid + 1;
        else right = mid - 1;
      }
      return -1;
    }

    private static bool ValidClose(MarketCandle candle, long closeMs) =>
      candle != null && candle.IsClosed &&
      candle.CloseTimeUtc.ToUnixTimeMilliseconds() == closeMs &&
      Double.IsFinite(candle.Close) && candle.Close > 0;

    private static bool Usable(FeatureValueSnapshot value) =>
      value != null && value.IsValid && !value.IsStale &&
      value.Value.HasValue && Double.IsFinite(value.Value.Value);

    private static FeatureValueSnapshot Invalid(DateTimeOffset at, string reason,
      int sampleCount = 0, bool stale = false) =>
      new FeatureValueSnapshot
      {
        CalculatedAtUtc = at, SampleCount = sampleCount,
        IsStale = stale, IsValid = false, InvalidReason = reason
      };

    private static string[] Normalize(IEnumerable<string> names) =>
      (names ?? Array.Empty<string>()).Where(name => !String.IsNullOrWhiteSpace(name))
        .Select(name => name.Trim().ToUpperInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
  }
}
