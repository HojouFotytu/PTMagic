using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Adds per-asset relative-strength features using existing return snapshots.
  /// Market relative strength is asset return minus the market median return.
  /// BTC relative strength is asset return minus the BTC return for the same horizon.
  /// </summary>
  public static class RelativeStrengthCalculator
  {
    public static IReadOnlyDictionary<string, AssetFeatureSnapshot> Calculate(
      IReadOnlyDictionary<string, AssetFeatureSnapshot> assets,
      IReadOnlyDictionary<string, FeatureValueSnapshot> marketFeatures,
      AssetFeatureSnapshot btcBenchmark,
      DateTimeOffset calculatedAtUtc)
    {
      if (assets == null)
      {
        throw new ArgumentNullException(nameof(assets));
      }
      if (marketFeatures == null)
      {
        throw new ArgumentNullException(nameof(marketFeatures));
      }

      Dictionary<string, AssetFeatureSnapshot> result = new Dictionary<string, AssetFeatureSnapshot>(StringComparer.OrdinalIgnoreCase);

      foreach (KeyValuePair<string, AssetFeatureSnapshot> pair in assets)
      {
        AssetFeatureSnapshot asset = pair.Value;
        Dictionary<string, FeatureValueSnapshot> features = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);

        if (asset?.Features != null)
        {
          foreach (KeyValuePair<string, FeatureValueSnapshot> featurePair in asset.Features)
          {
            features[featurePair.Key] = featurePair.Value;
          }
        }

        foreach (ReturnHorizon horizon in PriceReturnCalculator.Horizons)
        {
          FeatureValueSnapshot assetReturn = GetFeature(asset?.Features, horizon.FeatureName);
          FeatureValueSnapshot marketMedian = GetFeature(marketFeatures, "MedianReturn" + horizon.DisplayName);
          FeatureValueSnapshot btcReturn = GetFeature(btcBenchmark?.Features, horizon.FeatureName);

          features["RelativeStrengthMarket" + horizon.DisplayName] = CalculateDifference(
            assetReturn,
            marketMedian,
            calculatedAtUtc,
            "Asset return or market median return is unavailable for the " + horizon.DisplayName + " horizon.");

          features["RelativeStrengthBTC" + horizon.DisplayName] = CalculateDifference(
            assetReturn,
            btcReturn,
            calculatedAtUtc,
            "Asset return or BTC return is unavailable for the " + horizon.DisplayName + " horizon.");
        }

        result[pair.Key] = new AssetFeatureSnapshot
        {
          Symbol = asset?.Symbol ?? pair.Key,
          CalculatedAtUtc = asset?.CalculatedAtUtc ?? calculatedAtUtc,
          SourceDataTimestampUtc = asset?.SourceDataTimestampUtc,
          IsValid = asset?.IsValid == true,
          IsStale = asset?.IsStale == true,
          SourceSampleCount = asset?.SourceSampleCount ?? 0,
          Features = new ReadOnlyDictionary<string, FeatureValueSnapshot>(features)
        };
      }

      return new ReadOnlyDictionary<string, AssetFeatureSnapshot>(result);
    }

    private static FeatureValueSnapshot CalculateDifference(
      FeatureValueSnapshot left,
      FeatureValueSnapshot right,
      DateTimeOffset calculatedAtUtc,
      string invalidReason)
    {
      bool leftValid = IsUsable(left);
      bool rightValid = IsUsable(right);
      int sampleCount = (leftValid ? 1 : 0) + (rightValid ? 1 : 0);
      bool isStale = (left?.IsStale == true) || (right?.IsStale == true);
      DateTimeOffset? sourceTimestampUtc = GetOldestSourceTimestamp(left, right);

      if (!leftValid || !rightValid)
      {
        return new FeatureValueSnapshot
        {
          Value = null,
          CalculatedAtUtc = calculatedAtUtc,
          SourceDataTimestampUtc = sourceTimestampUtc,
          SampleCount = sampleCount,
          IsValid = false,
          IsStale = isStale,
          InvalidReason = invalidReason
        };
      }

      return new FeatureValueSnapshot
      {
        Value = left.Value.Value - right.Value.Value,
        CalculatedAtUtc = calculatedAtUtc,
        SourceDataTimestampUtc = sourceTimestampUtc,
        SampleCount = 2,
        IsValid = true,
        IsStale = false,
        InvalidReason = ""
      };
    }

    private static FeatureValueSnapshot GetFeature(IReadOnlyDictionary<string, FeatureValueSnapshot> features, string featureName)
    {
      if (features != null && features.TryGetValue(featureName, out FeatureValueSnapshot feature))
      {
        return feature;
      }

      return null;
    }

    private static bool IsUsable(FeatureValueSnapshot feature)
    {
      return feature != null
        && feature.IsValid
        && !feature.IsStale
        && feature.Value.HasValue
        && double.IsFinite(feature.Value.Value);
    }

    private static DateTimeOffset? GetOldestSourceTimestamp(FeatureValueSnapshot left, FeatureValueSnapshot right)
    {
      if (left?.SourceDataTimestampUtc == null || right?.SourceDataTimestampUtc == null)
      {
        return null;
      }

      return left.SourceDataTimestampUtc.Value <= right.SourceDataTimestampUtc.Value
        ? left.SourceDataTimestampUtc.Value
        : right.SourceDataTimestampUtc.Value;
    }
  }
}
