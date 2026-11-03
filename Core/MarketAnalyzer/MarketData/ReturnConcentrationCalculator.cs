using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Calculates return concentration using a stable Top10 selected by trailing
  /// 24-hour closed-candle quote volume from the frozen eligible universe.
  /// The same Top10 membership is reused across all return horizons in a cycle.
  /// </summary>
  public static class ReturnConcentrationCalculator
  {
    private static readonly TimeSpan RankingWindow = TimeSpan.FromHours(24);

    public static ReturnConcentrationResult Calculate(
      AnalyzerUniverseSnapshot universe,
      MarketDataStore store,
      TimeSpan interval,
      IReadOnlyDictionary<string, AssetFeatureSnapshot> assets,
      DateTimeOffset calculatedAtUtc)
    {
      if (universe == null)
      {
        throw new ArgumentNullException(nameof(universe));
      }
      if (store == null)
      {
        throw new ArgumentNullException(nameof(store));
      }
      if (assets == null)
      {
        throw new ArgumentNullException(nameof(assets));
      }

      List<RankedMarket> rankedMarkets = new List<RankedMarket>();
      foreach (AnalyzerUniverseMarket market in universe.Markets)
      {
        MarketDataSeriesSnapshot series = store.GetSnapshot(market.Symbol, interval, calculatedAtUtc);
        if (!TryCalculateTrailingQuoteVolume(series, calculatedAtUtc, out double quoteVolume, out DateTimeOffset? sourceTimestampUtc))
        {
          continue;
        }

        rankedMarkets.Add(new RankedMarket
        {
          Symbol = market.Symbol,
          QuoteVolume = quoteVolume,
          SourceTimestampUtc = sourceTimestampUtc
        });
      }

      string[] top10Symbols = rankedMarkets
        .OrderByDescending(market => market.QuoteVolume)
        .ThenBy(market => market.Symbol, StringComparer.OrdinalIgnoreCase)
        .Take(10)
        .Select(market => market.Symbol)
        .ToArray();

      HashSet<string> top10Set = top10Symbols.ToHashSet(StringComparer.OrdinalIgnoreCase);
      Dictionary<string, FeatureValueSnapshot> features = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);

      foreach (ReturnHorizon horizon in PriceReturnCalculator.Horizons)
      {
        List<FeatureValueSnapshot> top = new List<FeatureValueSnapshot>();
        List<FeatureValueSnapshot> rest = new List<FeatureValueSnapshot>();

        foreach (KeyValuePair<string, AssetFeatureSnapshot> pair in assets)
        {
          if (pair.Value?.Features == null
            || !pair.Value.Features.TryGetValue(horizon.FeatureName, out FeatureValueSnapshot feature)
            || !IsUsable(feature))
          {
            continue;
          }

          if (top10Set.Contains(pair.Key))
          {
            top.Add(feature);
          }
          else
          {
            rest.Add(feature);
          }
        }

        AddHorizonFeatures(features, horizon, top, rest, calculatedAtUtc);
      }

      return new ReturnConcentrationResult
      {
        RankingWindow = RankingWindow,
        Top10Symbols = new ReadOnlyCollection<string>(top10Symbols),
        Features = new ReadOnlyDictionary<string, FeatureValueSnapshot>(features)
      };
    }

    private static void AddHorizonFeatures(
      Dictionary<string, FeatureValueSnapshot> output,
      ReturnHorizon horizon,
      List<FeatureValueSnapshot> top,
      List<FeatureValueSnapshot> rest,
      DateTimeOffset calculatedAtUtc)
    {
      string suffix = horizon.DisplayName;
      if (top.Count == 0 || rest.Count == 0)
      {
        output["Top10MeanReturn" + suffix] = InvalidFeature(calculatedAtUtc, top.Count, "Top10 or remaining-universe returns are unavailable.");
        output["RestMeanReturn" + suffix] = InvalidFeature(calculatedAtUtc, rest.Count, "Top10 or remaining-universe returns are unavailable.");
        output["Top10MedianReturn" + suffix] = InvalidFeature(calculatedAtUtc, top.Count, "Top10 or remaining-universe returns are unavailable.");
        output["RestMedianReturn" + suffix] = InvalidFeature(calculatedAtUtc, rest.Count, "Top10 or remaining-universe returns are unavailable.");
        output["Top10ReturnAdvantage" + suffix] = InvalidFeature(calculatedAtUtc, top.Count + rest.Count, "Top10 or remaining-universe returns are unavailable.");
        return;
      }

      double topMean = top.Average(feature => feature.Value.Value);
      double restMean = rest.Average(feature => feature.Value.Value);
      double topMedian = CalculateMedian(top.Select(feature => feature.Value.Value).ToArray());
      double restMedian = CalculateMedian(rest.Select(feature => feature.Value.Value).ToArray());
      DateTimeOffset? topSource = GetOldestSourceTimestamp(top);
      DateTimeOffset? restSource = GetOldestSourceTimestamp(rest);
      DateTimeOffset? combinedSource = GetOldestSourceTimestamp(top.Concat(rest));

      output["Top10MeanReturn" + suffix] = ValidFeature(topMean, calculatedAtUtc, topSource, top.Count);
      output["RestMeanReturn" + suffix] = ValidFeature(restMean, calculatedAtUtc, restSource, rest.Count);
      output["Top10MedianReturn" + suffix] = ValidFeature(topMedian, calculatedAtUtc, topSource, top.Count);
      output["RestMedianReturn" + suffix] = ValidFeature(restMedian, calculatedAtUtc, restSource, rest.Count);
      output["Top10ReturnAdvantage" + suffix] = ValidFeature(topMean - restMean, calculatedAtUtc, combinedSource, top.Count + rest.Count);
    }

    private static bool TryCalculateTrailingQuoteVolume(
      MarketDataSeriesSnapshot series,
      DateTimeOffset calculatedAtUtc,
      out double quoteVolume,
      out DateTimeOffset? sourceTimestampUtc)
    {
      quoteVolume = 0;
      sourceTimestampUtc = null;
      if (series == null || series.IsStale || series.ClosedCandles == null || series.ClosedCandles.Count == 0)
      {
        return false;
      }

      MarketCandle latest = series.LatestClosedCandle;
      if (latest == null)
      {
        return false;
      }

      DateTimeOffset windowStart = latest.CloseTimeUtc - RankingWindow;
      List<MarketCandle> selected = series.ClosedCandles
        .Where(candle => candle != null && candle.IsClosed && candle.CloseTimeUtc > windowStart && candle.CloseTimeUtc <= latest.CloseTimeUtc)
        .ToList();

      if (selected.Count == 0 || selected.Any(candle => !double.IsFinite(candle.QuoteVolume) || candle.QuoteVolume < 0))
      {
        return false;
      }

      quoteVolume = selected.Sum(candle => candle.QuoteVolume);
      sourceTimestampUtc = latest.CloseTimeUtc;
      return double.IsFinite(quoteVolume);
    }

    private static FeatureValueSnapshot ValidFeature(
      double value,
      DateTimeOffset calculatedAtUtc,
      DateTimeOffset? sourceTimestampUtc,
      int sampleCount)
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

    private static FeatureValueSnapshot InvalidFeature(DateTimeOffset calculatedAtUtc, int sampleCount, string reason)
    {
      return new FeatureValueSnapshot
      {
        Value = null,
        CalculatedAtUtc = calculatedAtUtc,
        SourceDataTimestampUtc = null,
        SampleCount = sampleCount,
        IsValid = false,
        IsStale = false,
        InvalidReason = reason
      };
    }

    private static bool IsUsable(FeatureValueSnapshot feature)
    {
      return feature != null
        && feature.IsValid
        && !feature.IsStale
        && feature.Value.HasValue
        && double.IsFinite(feature.Value.Value);
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

    private static DateTimeOffset? GetOldestSourceTimestamp(IEnumerable<FeatureValueSnapshot> features)
    {
      DateTimeOffset? oldest = null;
      foreach (FeatureValueSnapshot feature in features)
      {
        if (feature?.SourceDataTimestampUtc == null)
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

    private sealed class RankedMarket
    {
      public string Symbol { get; init; } = "";
      public double QuoteVolume { get; init; }
      public DateTimeOffset? SourceTimestampUtc { get; init; }
    }
  }

  public sealed class ReturnConcentrationResult
  {
    public TimeSpan RankingWindow { get; init; }
    public IReadOnlyList<string> Top10Symbols { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, FeatureValueSnapshot> Features { get; init; } = new Dictionary<string, FeatureValueSnapshot>();
  }
}
