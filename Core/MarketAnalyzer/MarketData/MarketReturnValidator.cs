using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Independently validates Phase 1 return and market aggregate snapshots directly
  /// from normalized closed candles. This intentionally does not call
  /// PriceReturnCalculator.Calculate or MarketReturnAggregator.Calculate.
  /// </summary>
  public static class MarketReturnValidator
  {
    private const double ComparisonTolerance = 1e-9;

    public static IReadOnlyList<MarketReturnValidationSnapshot> Validate(
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

      Dictionary<string, List<IndependentReturn>> returnsByHorizon = PriceReturnCalculator.Horizons
        .ToDictionary(
          horizon => horizon.DisplayName,
          _ => new List<IndependentReturn>(),
          StringComparer.OrdinalIgnoreCase);
      Dictionary<string, int> assetMismatchesByHorizon = PriceReturnCalculator.Horizons
        .ToDictionary(
          horizon => horizon.DisplayName,
          _ => 0,
          StringComparer.OrdinalIgnoreCase);

      foreach (AnalyzerUniverseMarket market in universe.Markets)
      {
        MarketDataSeriesSnapshot series = store.GetSnapshot(market.Symbol, interval, asOfUtc);
        MarketCandle latest = series?.LatestClosedCandle;

        foreach (ReturnHorizon horizon in PriceReturnCalculator.Horizons)
        {
          bool expectedValid = TryCalculateIndependentReturn(series, latest, horizon, out IndependentReturn independentReturn);
          bool pipelineValid = TryGetPipelineReturn(analysis, market.Symbol, horizon.FeatureName, out FeatureValueSnapshot pipelineFeature);

          if (expectedValid)
          {
            returnsByHorizon[horizon.DisplayName].Add(independentReturn);
          }

          if (expectedValid != pipelineValid)
          {
            assetMismatchesByHorizon[horizon.DisplayName]++;
            continue;
          }

          if (!expectedValid)
          {
            continue;
          }

          if (!ApproximatelyEqual(independentReturn.Value, pipelineFeature.Value.Value)
            || pipelineFeature.SampleCount != 2
            || pipelineFeature.IsStale
            || pipelineFeature.SourceDataTimestampUtc != independentReturn.SourceTimestampUtc)
          {
            assetMismatchesByHorizon[horizon.DisplayName]++;
          }
        }
      }

      List<MarketReturnValidationSnapshot> results = new List<MarketReturnValidationSnapshot>();
      foreach (ReturnHorizon horizon in PriceReturnCalculator.Horizons)
      {
        List<IndependentReturn> independentReturns = returnsByHorizon[horizon.DisplayName];
        results.Add(BuildHorizonValidation(
          horizon,
          independentReturns,
          assetMismatchesByHorizon[horizon.DisplayName],
          analysis));
      }

      return new ReadOnlyCollection<MarketReturnValidationSnapshot>(results);
    }

    public static string FormatLogLine(MarketReturnValidationSnapshot validation)
    {
      if (validation == null)
      {
        throw new ArgumentNullException(nameof(validation));
      }

      if (!validation.HasIndependentData)
      {
        return "Phase 1 validation " + validation.Horizon + ": no independently valid returns; asset mismatches=" +
          validation.AssetMismatchCount + ", aggregate mismatches=" + validation.AggregateMismatchCount + ".";
      }

      string sampleText = validation.SampleSymbol.Length == 0
        ? ""
        : ", sample " + validation.SampleSymbol +
          " close=" + validation.SampleLatestClose.ToString("G17", CultureInfo.InvariantCulture) +
          " ref=" + validation.SampleReferenceClose.ToString("G17", CultureInfo.InvariantCulture) +
          " return=" + validation.SampleIndependentReturn.ToString("F8", CultureInfo.InvariantCulture) + "%" +
          " pipeline=" + (validation.SamplePipelineReturn.HasValue
            ? validation.SamplePipelineReturn.Value.ToString("F8", CultureInfo.InvariantCulture) + "%"
            : "unavailable");

      return "Phase 1 validation " + validation.Horizon +
        ": N=" + validation.SampleCount +
        ", mean=" + validation.MeanReturn.ToString("F8", CultureInfo.InvariantCulture) + "%" +
        ", median=" + validation.MedianReturn.ToString("F8", CultureInfo.InvariantCulture) + "%" +
        ", breadth=" + validation.PositiveBreadth.ToString("F8", CultureInfo.InvariantCulture) + "%" +
        ", stddev=" + validation.ReturnStdDev.ToString("F8", CultureInfo.InvariantCulture) + "%" +
        ", source=" + (validation.SourceTimestampUtc.HasValue
          ? validation.SourceTimestampUtc.Value.ToString("O", CultureInfo.InvariantCulture)
          : "unavailable") +
        ", asset mismatches=" + validation.AssetMismatchCount +
        ", aggregate mismatches=" + validation.AggregateMismatchCount +
        sampleText + ".";
    }

    private static MarketReturnValidationSnapshot BuildHorizonValidation(
      ReturnHorizon horizon,
      List<IndependentReturn> independentReturns,
      int assetMismatchCount,
      MarketAnalysisSnapshot analysis)
    {
      if (independentReturns.Count == 0)
      {
        int aggregateMismatchCount = CountExpectedInvalidAggregateMismatches(horizon, analysis);
        return new MarketReturnValidationSnapshot
        {
          Horizon = horizon.DisplayName,
          HasIndependentData = false,
          SampleCount = 0,
          AssetMismatchCount = assetMismatchCount,
          AggregateMismatchCount = aggregateMismatchCount
        };
      }

      double[] values = independentReturns.Select(item => item.Value).ToArray();
      double mean = values.Sum() / values.Length;
      double median = CalculateMedian(values);
      double positiveBreadth = values.Count(value => value > 0) * 100.0 / values.Length;
      double variance = values.Length > 1
        ? values.Sum(value =>
        {
          double difference = value - mean;
          return difference * difference;
        }) / values.Length
        : double.NaN;
      double standardDeviation = values.Length > 1 ? Math.Sqrt(variance) : double.NaN;
      DateTimeOffset? sourceTimestampUtc = independentReturns.Min(item => item.SourceTimestampUtc);

      int aggregateMismatchCount = 0;
      aggregateMismatchCount += AggregateMatches(analysis, "MeanReturn" + horizon.DisplayName, mean, values.Length, sourceTimestampUtc, true) ? 0 : 1;
      aggregateMismatchCount += AggregateMatches(analysis, "MedianReturn" + horizon.DisplayName, median, values.Length, sourceTimestampUtc, true) ? 0 : 1;
      aggregateMismatchCount += AggregateMatches(analysis, "PositiveBreadth" + horizon.DisplayName, positiveBreadth, values.Length, sourceTimestampUtc, true) ? 0 : 1;
      aggregateMismatchCount += AggregateMatches(analysis, "ReturnStdDev" + horizon.DisplayName, standardDeviation, values.Length, sourceTimestampUtc, values.Length > 1) ? 0 : 1;

      IndependentReturn sample = SelectSample(independentReturns);
      double? samplePipelineReturn = null;
      if (analysis.Assets.TryGetValue(sample.Symbol, out AssetFeatureSnapshot sampleAsset)
        && sampleAsset.Features.TryGetValue(horizon.FeatureName, out FeatureValueSnapshot sampleFeature)
        && sampleFeature.IsValid
        && sampleFeature.Value.HasValue)
      {
        samplePipelineReturn = sampleFeature.Value.Value;
      }

      return new MarketReturnValidationSnapshot
      {
        Horizon = horizon.DisplayName,
        HasIndependentData = true,
        SampleCount = values.Length,
        MeanReturn = mean,
        MedianReturn = median,
        PositiveBreadth = positiveBreadth,
        ReturnStdDev = standardDeviation,
        SourceTimestampUtc = sourceTimestampUtc,
        AssetMismatchCount = assetMismatchCount,
        AggregateMismatchCount = aggregateMismatchCount,
        SampleSymbol = sample.Symbol,
        SampleLatestClose = sample.LatestClose,
        SampleReferenceClose = sample.ReferenceClose,
        SampleIndependentReturn = sample.Value,
        SamplePipelineReturn = samplePipelineReturn
      };
    }

    private static bool TryCalculateIndependentReturn(
      MarketDataSeriesSnapshot series,
      MarketCandle latest,
      ReturnHorizon horizon,
      out IndependentReturn result)
    {
      result = null;
      if (series == null || latest == null || series.IsStale)
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

      result = new IndependentReturn
      {
        Symbol = series.Symbol,
        Value = ((latest.Close / reference.Close) - 1.0) * 100.0,
        LatestClose = latest.Close,
        ReferenceClose = reference.Close,
        SourceTimestampUtc = latest.CloseTimeUtc
      };
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

    private static bool TryGetPipelineReturn(
      MarketAnalysisSnapshot analysis,
      string symbol,
      string featureName,
      out FeatureValueSnapshot feature)
    {
      feature = null;
      return analysis.Assets.TryGetValue(symbol, out AssetFeatureSnapshot asset)
        && asset?.Features != null
        && asset.Features.TryGetValue(featureName, out feature)
        && feature != null
        && feature.IsValid
        && !feature.IsStale
        && feature.Value.HasValue
        && double.IsFinite(feature.Value.Value);
    }

    private static int CountExpectedInvalidAggregateMismatches(ReturnHorizon horizon, MarketAnalysisSnapshot analysis)
    {
      string[] featureNames =
      {
        "MeanReturn" + horizon.DisplayName,
        "MedianReturn" + horizon.DisplayName,
        "PositiveBreadth" + horizon.DisplayName,
        "ReturnStdDev" + horizon.DisplayName
      };

      return featureNames.Count(featureName =>
        !analysis.MarketFeatures.TryGetValue(featureName, out FeatureValueSnapshot feature)
        || feature == null
        || feature.IsValid
        || feature.Value.HasValue
        || feature.SampleCount != 0);
    }

    private static bool AggregateMatches(
      MarketAnalysisSnapshot analysis,
      string featureName,
      double expectedValue,
      int expectedSampleCount,
      DateTimeOffset? expectedSourceTimestampUtc,
      bool expectedValid)
    {
      if (!analysis.MarketFeatures.TryGetValue(featureName, out FeatureValueSnapshot feature) || feature == null)
      {
        return false;
      }

      if (!expectedValid)
      {
        return !feature.IsValid && !feature.Value.HasValue && feature.SampleCount == expectedSampleCount;
      }

      return feature.IsValid
        && !feature.IsStale
        && feature.Value.HasValue
        && double.IsFinite(feature.Value.Value)
        && ApproximatelyEqual(expectedValue, feature.Value.Value)
        && feature.SampleCount == expectedSampleCount
        && feature.SourceDataTimestampUtc == expectedSourceTimestampUtc;
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

    private static IndependentReturn SelectSample(List<IndependentReturn> returns)
    {
      IndependentReturn preferred = returns.FirstOrDefault(item => item.Symbol.Equals("BTCUSDT", StringComparison.OrdinalIgnoreCase));
      if (preferred != null)
      {
        return preferred;
      }

      preferred = returns.FirstOrDefault(item => item.Symbol.Equals("ETHUSDT", StringComparison.OrdinalIgnoreCase));
      return preferred ?? returns.OrderBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase).First();
    }

    private static bool ApproximatelyEqual(double expected, double actual)
    {
      double scale = Math.Max(1.0, Math.Max(Math.Abs(expected), Math.Abs(actual)));
      return Math.Abs(expected - actual) <= ComparisonTolerance * scale;
    }

    private sealed class IndependentReturn
    {
      public string Symbol { get; init; } = "";
      public double Value { get; init; }
      public double LatestClose { get; init; }
      public double ReferenceClose { get; init; }
      public DateTimeOffset SourceTimestampUtc { get; init; }
    }
  }

  public sealed class MarketReturnValidationSnapshot
  {
    public string Horizon { get; init; } = "";
    public bool HasIndependentData { get; init; }
    public int SampleCount { get; init; }
    public double MeanReturn { get; init; }
    public double MedianReturn { get; init; }
    public double PositiveBreadth { get; init; }
    public double ReturnStdDev { get; init; }
    public DateTimeOffset? SourceTimestampUtc { get; init; }
    public int AssetMismatchCount { get; init; }
    public int AggregateMismatchCount { get; init; }
    public string SampleSymbol { get; init; } = "";
    public double SampleLatestClose { get; init; }
    public double SampleReferenceClose { get; init; }
    public double SampleIndependentReturn { get; init; }
    public double? SamplePipelineReturn { get; init; }
    public bool IsValid => HasIndependentData && AssetMismatchCount == 0 && AggregateMismatchCount == 0;
  }
}
