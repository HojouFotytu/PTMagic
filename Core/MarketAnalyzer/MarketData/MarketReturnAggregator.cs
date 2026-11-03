using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Builds Layer-C market-wide aggregates from the already-calculated Layer-B
  /// per-asset returns. Every aggregate for a horizon uses the same valid return set.
  /// Missing, invalid or stale asset returns are excluded rather than treated as zero.
  /// </summary>
  public static class MarketReturnAggregator
  {
    public static IReadOnlyDictionary<string, FeatureValueSnapshot> Calculate(IReadOnlyDictionary<string, AssetFeatureSnapshot> assets, DateTimeOffset calculatedAtUtc)
    {
      if (assets == null)
      {
        throw new ArgumentNullException(nameof(assets));
      }

      Dictionary<string, FeatureValueSnapshot> result = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);

      foreach (ReturnHorizon horizon in PriceReturnCalculator.Horizons)
      {
        List<FeatureValueSnapshot> allHorizonFeatures = new List<FeatureValueSnapshot>();
        List<FeatureValueSnapshot> validHorizonFeatures = new List<FeatureValueSnapshot>();

        foreach (AssetFeatureSnapshot asset in assets.Values)
        {
          if (asset?.Features == null || !asset.Features.TryGetValue(horizon.FeatureName, out FeatureValueSnapshot feature) || feature == null)
          {
            continue;
          }

          allHorizonFeatures.Add(feature);
          if (feature.IsValid && !feature.IsStale && feature.Value.HasValue && double.IsFinite(feature.Value.Value))
          {
            validHorizonFeatures.Add(feature);
          }
        }

        string suffix = horizon.DisplayName;
        if (validHorizonFeatures.Count == 0)
        {
          bool allObservedDataStale = allHorizonFeatures.Count > 0 && allHorizonFeatures.All(feature => feature.IsStale);
          string reason = "No valid asset returns are available for the " + suffix + " horizon.";

          result["MeanReturn" + suffix] = InvalidFeature(calculatedAtUtc, reason, allObservedDataStale, 0);
          result["MedianReturn" + suffix] = InvalidFeature(calculatedAtUtc, reason, allObservedDataStale, 0);
          result["PositiveBreadth" + suffix] = InvalidFeature(calculatedAtUtc, reason, allObservedDataStale, 0);
          result["ReturnStdDev" + suffix] = InvalidFeature(calculatedAtUtc, reason, allObservedDataStale, 0);
          continue;
        }

        double[] values = validHorizonFeatures.Select(feature => feature.Value.Value).ToArray();
        double mean = values.Average();
        double median = CalculateMedian(values);
        double positiveBreadth = values.Count(value => value > 0) * 100.0 / values.Length;
        DateTimeOffset? sourceTimestampUtc = GetOldestSourceTimestamp(validHorizonFeatures);

        result["MeanReturn" + suffix] = ValidFeature(mean, calculatedAtUtc, sourceTimestampUtc, values.Length);
        result["MedianReturn" + suffix] = ValidFeature(median, calculatedAtUtc, sourceTimestampUtc, values.Length);
        result["PositiveBreadth" + suffix] = ValidFeature(positiveBreadth, calculatedAtUtc, sourceTimestampUtc, values.Length);

        if (values.Length < 2)
        {
          result["ReturnStdDev" + suffix] = InvalidFeature(
            calculatedAtUtc,
            "At least two valid asset returns are required to calculate dispersion for the " + suffix + " horizon.",
            false,
            values.Length,
            sourceTimestampUtc);
        }
        else
        {
          double variance = values.Sum(value =>
          {
            double difference = value - mean;
            return difference * difference;
          }) / values.Length;

          result["ReturnStdDev" + suffix] = ValidFeature(Math.Sqrt(variance), calculatedAtUtc, sourceTimestampUtc, values.Length);
        }
      }

      return new ReadOnlyDictionary<string, FeatureValueSnapshot>(result);
    }

    private static double CalculateMedian(double[] values)
    {
      double[] ordered = values.OrderBy(value => value).ToArray();
      int middle = ordered.Length / 2;

      if (ordered.Length % 2 == 1)
      {
        return ordered[middle];
      }

      return (ordered[middle - 1] + ordered[middle]) / 2.0;
    }

    private static DateTimeOffset? GetOldestSourceTimestamp(IEnumerable<FeatureValueSnapshot> features)
    {
      DateTimeOffset? oldest = null;

      foreach (FeatureValueSnapshot feature in features)
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

    private static FeatureValueSnapshot ValidFeature(double value, DateTimeOffset calculatedAtUtc, DateTimeOffset? sourceTimestampUtc, int sampleCount)
    {
      return new FeatureValueSnapshot
      {
        Value = value,
        CalculatedAtUtc = calculatedAtUtc,
        SourceDataTimestampUtc = sourceTimestampUtc,
        SampleCount = sampleCount,
        IsValid = true,
        IsStale = false,
        InvalidReason = ""
      };
    }

    private static FeatureValueSnapshot InvalidFeature(DateTimeOffset calculatedAtUtc, string reason, bool isStale, int sampleCount, DateTimeOffset? sourceTimestampUtc = null)
    {
      return new FeatureValueSnapshot
      {
        Value = null,
        CalculatedAtUtc = calculatedAtUtc,
        SourceDataTimestampUtc = sourceTimestampUtc,
        SampleCount = sampleCount,
        IsValid = false,
        IsStale = isStale,
        InvalidReason = reason
      };
    }
  }
}
