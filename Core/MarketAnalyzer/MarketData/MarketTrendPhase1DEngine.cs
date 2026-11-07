using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer.MarketData
{
  public sealed class Phase1DFeatureResult
  {
    public IReadOnlyDictionary<string, FeatureValueSnapshot> GlobalMetrics { get; init; } =
      new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> AssetMetrics { get; init; } =
      new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(StringComparer.OrdinalIgnoreCase);
  }

  public static class MarketTrendPhase1DEngine
  {
    public static readonly string[] Metrics =
    {
      MarketTrendMetrics.CorrelationToBTC, MarketTrendMetrics.MeanCorrelationToBTC,
      MarketTrendMetrics.MedianCorrelationToBTC
    };

    public static bool HasMetrics(IReadOnlyCollection<string> metrics) => metrics != null &&
      Metrics.Any(metric => metrics.Contains(metric, StringComparer.OrdinalIgnoreCase));

    public static Phase1DFeatureResult Calculate(MarketTrend trend, IReadOnlyCollection<string> enabled,
      IEnumerable<string> globalSymbols, IEnumerable<string> assetSymbols,
      MarketDataStore store, TimeSpan interval, DateTimeOffset asOfUtc, string benchmarkSymbol = "BTCUSDT")
    {
      if (trend == null) throw new ArgumentNullException(nameof(trend));
      if (store == null) throw new ArgumentNullException(nameof(store));
      MarketTrendTimeframe.Validate(trend, interval);

      HashSet<string> active = new HashSet<string>(enabled ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
      string[] global = Normalize(globalSymbols), assets = Normalize(assetSymbols);
      bool needMean = active.Contains(MarketTrendMetrics.MeanCorrelationToBTC);
      bool needMedian = active.Contains(MarketTrendMetrics.MedianCorrelationToBTC);
      bool needAsset = active.Contains(MarketTrendMetrics.CorrelationToBTC);
      HashSet<string> symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      if (needMean || needMedian) symbols.UnionWith(global);
      if (needAsset) symbols.UnionWith(assets);
      if (symbols.Count == 0 && !needMean && !needMedian) return new Phase1DFeatureResult();

      string quote = benchmarkSymbol.StartsWith("BTC", StringComparison.OrdinalIgnoreCase)
        ? benchmarkSymbol.Substring(3) : "USDT";
      string conversionSymbol = MarketTrendFiatConversion.IsFiat(trend.TrendCurrency)
        ? MarketTrendFiatConversion.ConversionSymbol(quote) : null;
      bool conversionRequired = conversionSymbol != null;
      MarketDataSeriesSnapshot conversion = conversionRequired
        ? store.GetSnapshot(conversionSymbol, interval, asOfUtc) : null;
      bool benchmarkIdentity = String.Equals(quote, "BTC", StringComparison.OrdinalIgnoreCase);
      SynchronizedReturnVector benchmark = SynchronizedReturnCorrelationCalculator.BuildVector(
        benchmarkIdentity ? null : store.GetSnapshot(benchmarkSymbol, interval, asOfUtc),
        interval, trend.TrendMinutes, trend.CorrelationLookback, asOfUtc, conversion,
        conversionRequired, benchmarkIdentity);

      Dictionary<string, FeatureValueSnapshot> coefficients = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
      foreach (string symbol in symbols)
      {
        SynchronizedReturnVector vector = String.Equals(symbol, benchmarkSymbol, StringComparison.OrdinalIgnoreCase)
          ? benchmark : SynchronizedReturnCorrelationCalculator.BuildVector(
            store.GetSnapshot(symbol, interval, asOfUtc), interval, trend.TrendMinutes,
            trend.CorrelationLookback, asOfUtc, conversion, conversionRequired);
        coefficients[symbol] = SynchronizedReturnCorrelationCalculator.Calculate(vector, benchmark, asOfUtc);
      }

      Dictionary<string, FeatureValueSnapshot> globalResults = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
      if (needMean || needMedian)
      {
        // BTC remains in the denominator exactly when the configured selected
        // global universe includes it. No benchmark-driven population mutation.
        FeatureValueSnapshot[] valid = global.Where(coefficients.ContainsKey).Select(symbol => coefficients[symbol])
          .Where(value => value.IsValid && !value.IsStale && value.Value.HasValue).ToArray();
        double[] values = valid.Select(value => value.Value.Value).OrderBy(value => value).ToArray();
        FeatureValueSnapshot Aggregate(bool median)
        {
          FeatureValueSnapshot measurement = values.Length == 0
            ? new FeatureValueSnapshot { CalculatedAtUtc = asOfUtc,
                InvalidReason = "No valid synchronized BTC correlations are available." }
            : new FeatureValueSnapshot
              {
                CalculatedAtUtc = asOfUtc, SourceDataTimestampUtc = valid[0].SourceDataTimestampUtc,
                Value = median ? (values.Length % 2 == 1 ? values[values.Length / 2]
                  : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2.0) : values.Average(),
                SampleCount = values.Length, IsValid = true
              };
          return MarketMetricCoverage.Apply(measurement, values.Length, global.Length, asOfUtc);
        }
        if (needMean) globalResults[MarketTrendMetrics.MeanCorrelationToBTC] = Aggregate(false);
        if (needMedian) globalResults[MarketTrendMetrics.MedianCorrelationToBTC] = Aggregate(true);
      }

      Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> assetResults =
        new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(StringComparer.OrdinalIgnoreCase);
      if (needAsset)
      {
        foreach (string symbol in assets)
        {
          assetResults[symbol] = new ReadOnlyDictionary<string, FeatureValueSnapshot>(
            new Dictionary<string, FeatureValueSnapshot> { [MarketTrendMetrics.CorrelationToBTC] = coefficients[symbol] });
        }
      }
      return new Phase1DFeatureResult
      {
        GlobalMetrics = new ReadOnlyDictionary<string, FeatureValueSnapshot>(globalResults),
        AssetMetrics = new ReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(assetResults)
      };
    }

    private static string[] Normalize(IEnumerable<string> symbols) => (symbols ?? Array.Empty<string>())
      .Where(symbol => !String.IsNullOrWhiteSpace(symbol)).Select(symbol => symbol.Trim().ToUpperInvariant())
      .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
  }
}
