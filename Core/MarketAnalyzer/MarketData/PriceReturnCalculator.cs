using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Calculates timestamp-aligned close-to-close percentage returns from normalized,
  /// closed candles. Missing or stale observations remain unavailable rather than zero.
  /// </summary>
  public static class PriceReturnCalculator
  {
    private static readonly IReadOnlyList<ReturnHorizon> HorizonDefinitions = Array.AsReadOnly(new[]
    {
      new ReturnHorizon("Return5m", TimeSpan.FromMinutes(5)),
      new ReturnHorizon("Return15m", TimeSpan.FromMinutes(15)),
      new ReturnHorizon("Return1h", TimeSpan.FromHours(1)),
      new ReturnHorizon("Return2h", TimeSpan.FromHours(2)),
      new ReturnHorizon("Return4h", TimeSpan.FromHours(4)),
      new ReturnHorizon("Return12h", TimeSpan.FromHours(12)),
      new ReturnHorizon("Return24h", TimeSpan.FromHours(24)),
      new ReturnHorizon("Return72h", TimeSpan.FromHours(72))
    });

    public static IReadOnlyList<ReturnHorizon> Horizons => HorizonDefinitions;

    public static AssetFeatureSnapshot Calculate(MarketDataSeriesSnapshot series)
    {
      if (series == null)
      {
        throw new ArgumentNullException(nameof(series));
      }

      Dictionary<string, FeatureValueSnapshot> features = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);

      if (!series.TryGetLatestClosedCandle(out MarketCandle latest))
      {
        foreach (ReturnHorizon horizon in HorizonDefinitions)
        {
          features[horizon.FeatureName] = InvalidFeature(series.AsOfUtc, "No closed candle is available.", series.IsStale, series.ClosedSampleCount, null);
        }

        return CreateAssetSnapshot(series, features, false, null);
      }

      if (series.IsStale)
      {
        foreach (ReturnHorizon horizon in HorizonDefinitions)
        {
          features[horizon.FeatureName] = InvalidFeature(series.AsOfUtc, "Latest closed candle is stale.", true, series.ClosedSampleCount, latest.CloseTimeUtc);
        }

        return CreateAssetSnapshot(series, features, false, latest.CloseTimeUtc);
      }

      Dictionary<long, MarketCandle> byCloseTime = series.ClosedCandles.ToDictionary(
        candle => candle.CloseTimeUtc.ToUnixTimeMilliseconds(),
        candle => candle);

      foreach (ReturnHorizon horizon in HorizonDefinitions)
      {
        DateTimeOffset targetCloseTimeUtc = latest.CloseTimeUtc - horizon.Duration;
        long targetCloseMilliseconds = targetCloseTimeUtc.ToUnixTimeMilliseconds();

        if (!byCloseTime.TryGetValue(targetCloseMilliseconds, out MarketCandle reference))
        {
          features[horizon.FeatureName] = InvalidFeature(
            series.AsOfUtc,
            "Exact reference candle is unavailable for the " + horizon.DisplayName + " horizon.",
            false,
            series.ClosedSampleCount,
            latest.CloseTimeUtc);
          continue;
        }

        if (reference.Close <= 0 || !double.IsFinite(reference.Close) || latest.Close <= 0 || !double.IsFinite(latest.Close))
        {
          features[horizon.FeatureName] = InvalidFeature(
            series.AsOfUtc,
            "Reference or latest close price is invalid.",
            false,
            series.ClosedSampleCount,
            latest.CloseTimeUtc);
          continue;
        }

        double value = ((latest.Close / reference.Close) - 1.0) * 100.0;
        features[horizon.FeatureName] = new FeatureValueSnapshot
        {
          Value = value,
          CalculatedAtUtc = series.AsOfUtc,
          SourceDataTimestampUtc = latest.CloseTimeUtc,
          SampleCount = 2,
          IsValid = true,
          IsStale = false,
          InvalidReason = ""
        };
      }

      bool hasAnyValidFeature = features.Values.Any(feature => feature.IsValid);
      return CreateAssetSnapshot(series, features, hasAnyValidFeature, latest.CloseTimeUtc);
    }

    private static AssetFeatureSnapshot CreateAssetSnapshot(MarketDataSeriesSnapshot series, Dictionary<string, FeatureValueSnapshot> features, bool isValid, DateTimeOffset? sourceTimestampUtc)
    {
      return new AssetFeatureSnapshot
      {
        Symbol = series.Symbol,
        CalculatedAtUtc = series.AsOfUtc,
        SourceDataTimestampUtc = sourceTimestampUtc,
        IsValid = isValid,
        IsStale = series.IsStale,
        SourceSampleCount = series.ClosedSampleCount,
        Features = new ReadOnlyDictionary<string, FeatureValueSnapshot>(features)
      };
    }

    private static FeatureValueSnapshot InvalidFeature(DateTimeOffset calculatedAtUtc, string reason, bool isStale, int sampleCount, DateTimeOffset? sourceTimestampUtc)
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

  public sealed class ReturnHorizon
  {
    internal ReturnHorizon(string featureName, TimeSpan duration)
    {
      FeatureName = featureName;
      Duration = duration;
    }

    public string FeatureName { get; }
    public TimeSpan Duration { get; }
    public string DisplayName => FeatureName.Substring("Return".Length);
  }
}
