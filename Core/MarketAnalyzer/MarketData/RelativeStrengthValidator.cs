using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Independently validates relative-strength features directly from normalized
  /// closed candles. This intentionally does not call RelativeStrengthCalculator.
  /// </summary>
  public static class RelativeStrengthValidator
  {
    private const double ComparisonTolerance = 1e-9;

    public static IReadOnlyList<RelativeStrengthValidationSnapshot> Validate(
      AnalyzerUniverseSnapshot universe,
      MarketDataStore store,
      TimeSpan interval,
      MarketAnalysisSnapshot analysis,
      DateTimeOffset asOfUtc)
    {
      if (universe == null)
      {
        throw new ArgumentNullException(nameof(universe));
      }
      if (store == null)
      {
        throw new ArgumentNullException(nameof(store));
      }
      if (analysis == null)
      {
        throw new ArgumentNullException(nameof(analysis));
      }

      Dictionary<string, MarketDataSeriesSnapshot> seriesBySymbol = universe.Symbols.ToDictionary(
        symbol => symbol,
        symbol => store.GetSnapshot(symbol, interval, asOfUtc),
        StringComparer.OrdinalIgnoreCase);
      MarketDataSeriesSnapshot btcSeries = store.GetSnapshot("BTCUSDT", interval, asOfUtc);
      List<RelativeStrengthValidationSnapshot> results = new List<RelativeStrengthValidationSnapshot>();

      foreach (ReturnHorizon horizon in PriceReturnCalculator.Horizons)
      {
        Dictionary<string, double> returns = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (string symbol in universe.Symbols)
        {
          if (TryCalculateReturn(seriesBySymbol[symbol], horizon, out double value))
          {
            returns[symbol] = value;
          }
        }

        bool btcValid = TryCalculateReturn(btcSeries, horizon, out double btcReturn);
        double? marketMedian = returns.Count > 0 ? CalculateMedian(returns.Values.ToArray()) : null;
        int marketMismatchCount = 0;
        int btcMismatchCount = 0;

        foreach (string symbol in universe.Symbols)
        {
          bool assetValid = returns.TryGetValue(symbol, out double assetReturn);
          bool expectedMarketValid = assetValid && marketMedian.HasValue;
          bool expectedBtcValid = assetValid && btcValid;

          FeatureValueSnapshot marketFeature = GetFeature(analysis, symbol, "RelativeStrengthMarket" + horizon.DisplayName);
          FeatureValueSnapshot btcFeature = GetFeature(analysis, symbol, "RelativeStrengthBTC" + horizon.DisplayName);

          if (!Matches(marketFeature, expectedMarketValid, expectedMarketValid ? assetReturn - marketMedian.Value : 0.0))
          {
            marketMismatchCount++;
          }

          if (!Matches(btcFeature, expectedBtcValid, expectedBtcValid ? assetReturn - btcReturn : 0.0))
          {
            btcMismatchCount++;
          }
        }

        string sampleSymbol = SelectSampleSymbol(returns);
        double? sampleReturn = null;
        double? sampleMarketRelativeStrength = null;
        double? sampleBtcRelativeStrength = null;
        double? samplePipelineMarketRelativeStrength = null;
        double? samplePipelineBtcRelativeStrength = null;

        if (!String.IsNullOrWhiteSpace(sampleSymbol) && returns.TryGetValue(sampleSymbol, out double selectedReturn))
        {
          sampleReturn = selectedReturn;
          if (marketMedian.HasValue)
          {
            sampleMarketRelativeStrength = selectedReturn - marketMedian.Value;
          }
          if (btcValid)
          {
            sampleBtcRelativeStrength = selectedReturn - btcReturn;
          }

          samplePipelineMarketRelativeStrength = GetValidValue(analysis, sampleSymbol, "RelativeStrengthMarket" + horizon.DisplayName);
          samplePipelineBtcRelativeStrength = GetValidValue(analysis, sampleSymbol, "RelativeStrengthBTC" + horizon.DisplayName);
        }

        results.Add(new RelativeStrengthValidationSnapshot
        {
          Horizon = horizon.DisplayName,
          SampleCount = returns.Count,
          MarketMedianReturn = marketMedian,
          BtcReturn = btcValid ? btcReturn : null,
          MarketMismatchCount = marketMismatchCount,
          BtcMismatchCount = btcMismatchCount,
          SampleSymbol = sampleSymbol,
          SampleReturn = sampleReturn,
          SampleMarketRelativeStrength = sampleMarketRelativeStrength,
          SampleBtcRelativeStrength = sampleBtcRelativeStrength,
          SamplePipelineMarketRelativeStrength = samplePipelineMarketRelativeStrength,
          SamplePipelineBtcRelativeStrength = samplePipelineBtcRelativeStrength
        });
      }

      return new ReadOnlyCollection<RelativeStrengthValidationSnapshot>(results);
    }

    public static string FormatLogLine(RelativeStrengthValidationSnapshot validation)
    {
      if (validation == null)
      {
        throw new ArgumentNullException(nameof(validation));
      }

      string sampleText = String.IsNullOrWhiteSpace(validation.SampleSymbol)
        ? ""
        : ", sample " + validation.SampleSymbol +
          " return=" + FormatPercent(validation.SampleReturn) +
          " marketRS=" + FormatPercent(validation.SampleMarketRelativeStrength) +
          " marketPipeline=" + FormatPercent(validation.SamplePipelineMarketRelativeStrength) +
          " btcRS=" + FormatPercent(validation.SampleBtcRelativeStrength) +
          " btcPipeline=" + FormatPercent(validation.SamplePipelineBtcRelativeStrength);

      return "Phase 1 relative-strength validation " + validation.Horizon +
        ": N=" + validation.SampleCount +
        ", market median=" + FormatPercent(validation.MarketMedianReturn) +
        ", BTC return=" + FormatPercent(validation.BtcReturn) +
        ", market mismatches=" + validation.MarketMismatchCount +
        ", BTC mismatches=" + validation.BtcMismatchCount +
        sampleText + ".";
    }

    private static bool TryCalculateReturn(MarketDataSeriesSnapshot series, ReturnHorizon horizon, out double value)
    {
      value = 0.0;
      if (series == null || series.IsStale || !series.TryGetLatestClosedCandle(out MarketCandle latest))
      {
        return false;
      }

      DateTimeOffset targetCloseTimeUtc = latest.CloseTimeUtc - horizon.Duration;
      MarketCandle reference = FindByCloseTime(series.ClosedCandles, targetCloseTimeUtc);
      if (reference == null
        || reference.Close <= 0
        || latest.Close <= 0
        || !double.IsFinite(reference.Close)
        || !double.IsFinite(latest.Close))
      {
        return false;
      }

      value = ((latest.Close / reference.Close) - 1.0) * 100.0;
      return true;
    }

    private static MarketCandle FindByCloseTime(IReadOnlyList<MarketCandle> candles, DateTimeOffset targetCloseTimeUtc)
    {
      int low = 0;
      int high = candles.Count - 1;
      long target = targetCloseTimeUtc.ToUnixTimeMilliseconds();

      while (low <= high)
      {
        int middle = low + ((high - low) / 2);
        long candidate = candles[middle].CloseTimeUtc.ToUnixTimeMilliseconds();
        if (candidate == target)
        {
          return candles[middle];
        }

        if (candidate < target)
        {
          low = middle + 1;
        }
        else
        {
          high = middle - 1;
        }
      }

      return null;
    }

    private static double CalculateMedian(double[] values)
    {
      double[] ordered = (double[])values.Clone();
      Array.Sort(ordered);
      int middle = ordered.Length / 2;
      return ordered.Length % 2 == 1
        ? ordered[middle]
        : (ordered[middle - 1] + ordered[middle]) / 2.0;
    }

    private static FeatureValueSnapshot GetFeature(MarketAnalysisSnapshot analysis, string symbol, string featureName)
    {
      if (analysis.Assets.TryGetValue(symbol, out AssetFeatureSnapshot asset)
        && asset?.Features != null
        && asset.Features.TryGetValue(featureName, out FeatureValueSnapshot feature))
      {
        return feature;
      }

      return null;
    }

    private static double? GetValidValue(MarketAnalysisSnapshot analysis, string symbol, string featureName)
    {
      FeatureValueSnapshot feature = GetFeature(analysis, symbol, featureName);
      return feature != null
        && feature.IsValid
        && !feature.IsStale
        && feature.Value.HasValue
        && double.IsFinite(feature.Value.Value)
          ? feature.Value.Value
          : null;
    }

    private static bool Matches(FeatureValueSnapshot feature, bool expectedValid, double expectedValue)
    {
      if (!expectedValid)
      {
        return feature != null && !feature.IsValid && !feature.Value.HasValue;
      }

      return feature != null
        && feature.IsValid
        && !feature.IsStale
        && feature.Value.HasValue
        && double.IsFinite(feature.Value.Value)
        && ApproximatelyEqual(expectedValue, feature.Value.Value);
    }

    private static string SelectSampleSymbol(IReadOnlyDictionary<string, double> returns)
    {
      if (returns.ContainsKey("ETHUSDT"))
      {
        return "ETHUSDT";
      }
      if (returns.ContainsKey("SOLUSDT"))
      {
        return "SOLUSDT";
      }
      return returns.Keys.OrderBy(symbol => symbol, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? "";
    }

    private static bool ApproximatelyEqual(double expected, double actual)
    {
      double scale = Math.Max(1.0, Math.Max(Math.Abs(expected), Math.Abs(actual)));
      return Math.Abs(expected - actual) <= ComparisonTolerance * scale;
    }

    private static string FormatPercent(double? value)
    {
      return value.HasValue
        ? value.Value.ToString("F8", CultureInfo.InvariantCulture) + "%"
        : "unavailable";
    }
  }

  public sealed class RelativeStrengthValidationSnapshot
  {
    public string Horizon { get; init; } = "";
    public int SampleCount { get; init; }
    public double? MarketMedianReturn { get; init; }
    public double? BtcReturn { get; init; }
    public int MarketMismatchCount { get; init; }
    public int BtcMismatchCount { get; init; }
    public string SampleSymbol { get; init; } = "";
    public double? SampleReturn { get; init; }
    public double? SampleMarketRelativeStrength { get; init; }
    public double? SampleBtcRelativeStrength { get; init; }
    public double? SamplePipelineMarketRelativeStrength { get; init; }
    public double? SamplePipelineBtcRelativeStrength { get; init; }
    public bool IsValid => SampleCount > 0 && MarketMedianReturn.HasValue && BtcReturn.HasValue && MarketMismatchCount == 0 && BtcMismatchCount == 0;
  }
}
