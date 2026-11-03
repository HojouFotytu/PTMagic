using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Independently validates return concentration directly from closed candles.
  /// It recomputes both the trailing-24h quote-volume Top10 ranking and return statistics
  /// without calling ReturnConcentrationCalculator.Calculate.
  /// </summary>
  public static class ReturnConcentrationValidator
  {
    private const double ComparisonTolerance = 1e-9;
    private static readonly TimeSpan RankingWindow = TimeSpan.FromHours(24);

    public static IReadOnlyList<ReturnConcentrationValidationSnapshot> Validate(
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

      List<RankedMarket> rankedMarkets = new List<RankedMarket>();
      Dictionary<string, MarketDataSeriesSnapshot> seriesBySymbol = new Dictionary<string, MarketDataSeriesSnapshot>(StringComparer.OrdinalIgnoreCase);

      foreach (AnalyzerUniverseMarket market in universe.Markets)
      {
        MarketDataSeriesSnapshot series = store.GetSnapshot(market.Symbol, interval, asOfUtc);
        seriesBySymbol[market.Symbol] = series;
        if (TryCalculateTrailingQuoteVolume(series, out double quoteVolume))
        {
          rankedMarkets.Add(new RankedMarket
          {
            Symbol = market.Symbol,
            QuoteVolume = quoteVolume
          });
        }
      }

      string[] top10Symbols = rankedMarkets
        .OrderByDescending(market => market.QuoteVolume)
        .ThenBy(market => market.Symbol, StringComparer.OrdinalIgnoreCase)
        .Take(10)
        .Select(market => market.Symbol)
        .ToArray();
      HashSet<string> top10Set = top10Symbols.ToHashSet(StringComparer.OrdinalIgnoreCase);

      List<ReturnConcentrationValidationSnapshot> results = new List<ReturnConcentrationValidationSnapshot>();
      foreach (ReturnHorizon horizon in PriceReturnCalculator.Horizons)
      {
        List<IndependentReturn> top = new List<IndependentReturn>();
        List<IndependentReturn> remaining = new List<IndependentReturn>();

        foreach (AnalyzerUniverseMarket market in universe.Markets)
        {
          if (!seriesBySymbol.TryGetValue(market.Symbol, out MarketDataSeriesSnapshot series)
            || !TryCalculateIndependentReturn(series, horizon, out IndependentReturn independentReturn))
          {
            continue;
          }

          if (top10Set.Contains(market.Symbol))
          {
            top.Add(independentReturn);
          }
          else
          {
            remaining.Add(independentReturn);
          }
        }

        results.Add(BuildValidation(horizon, top10Symbols, top, remaining, analysis));
      }

      return new ReadOnlyCollection<ReturnConcentrationValidationSnapshot>(results);
    }

    public static string FormatLogLine(ReturnConcentrationValidationSnapshot validation)
    {
      if (validation == null)
      {
        throw new ArgumentNullException(nameof(validation));
      }

      return "Phase 1 return-concentration validation " + validation.Horizon +
        ": Top10=" + String.Join(",", validation.Top10Symbols) +
        ", topN=" + validation.TopSampleCount +
        ", remainingN=" + validation.RemainingUniverseSampleCount +
        ", topMean=" + FormatPercent(validation.Top10MeanReturn) +
        ", remainingMean=" + FormatPercent(validation.RemainingUniverseMeanReturn) +
        ", advantage=" + FormatPercent(validation.Top10ReturnAdvantage) +
        ", topMedian=" + FormatPercent(validation.Top10MedianReturn) +
        ", remainingMedian=" + FormatPercent(validation.RemainingUniverseMedianReturn) +
        ", mismatches=" + validation.MismatchCount + ".";
    }

    private static ReturnConcentrationValidationSnapshot BuildValidation(
      ReturnHorizon horizon,
      string[] top10Symbols,
      List<IndependentReturn> top,
      List<IndependentReturn> remaining,
      MarketAnalysisSnapshot analysis)
    {
      if (top.Count == 0 || remaining.Count == 0)
      {
        return new ReturnConcentrationValidationSnapshot
        {
          Horizon = horizon.DisplayName,
          Top10Symbols = new ReadOnlyCollection<string>((string[])top10Symbols.Clone()),
          TopSampleCount = top.Count,
          RemainingUniverseSampleCount = remaining.Count,
          MismatchCount = 5
        };
      }

      double topMean = top.Average(item => item.Value);
      double remainingMean = remaining.Average(item => item.Value);
      double topMedian = CalculateMedian(top.Select(item => item.Value).ToArray());
      double remainingMedian = CalculateMedian(remaining.Select(item => item.Value).ToArray());
      double advantage = topMean - remainingMean;

      int mismatches = 0;
      mismatches += FeatureMatches(analysis, "Top10MeanReturn" + horizon.DisplayName, topMean, top.Count) ? 0 : 1;
      mismatches += FeatureMatches(analysis, "RemainingUniverseMeanReturn" + horizon.DisplayName, remainingMean, remaining.Count) ? 0 : 1;
      mismatches += FeatureMatches(analysis, "Top10MedianReturn" + horizon.DisplayName, topMedian, top.Count) ? 0 : 1;
      mismatches += FeatureMatches(analysis, "RemainingUniverseMedianReturn" + horizon.DisplayName, remainingMedian, remaining.Count) ? 0 : 1;
      mismatches += FeatureMatches(analysis, "Top10ReturnAdvantage" + horizon.DisplayName, advantage, top.Count + remaining.Count) ? 0 : 1;

      return new ReturnConcentrationValidationSnapshot
      {
        Horizon = horizon.DisplayName,
        Top10Symbols = new ReadOnlyCollection<string>((string[])top10Symbols.Clone()),
        TopSampleCount = top.Count,
        RemainingUniverseSampleCount = remaining.Count,
        Top10MeanReturn = topMean,
        RemainingUniverseMeanReturn = remainingMean,
        Top10MedianReturn = topMedian,
        RemainingUniverseMedianReturn = remainingMedian,
        Top10ReturnAdvantage = advantage,
        MismatchCount = mismatches
      };
    }

    private static bool TryCalculateTrailingQuoteVolume(MarketDataSeriesSnapshot series, out double quoteVolume)
    {
      quoteVolume = 0;
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
      bool found = false;
      foreach (MarketCandle candle in series.ClosedCandles)
      {
        if (candle == null || !candle.IsClosed || candle.CloseTimeUtc <= windowStart || candle.CloseTimeUtc > latest.CloseTimeUtc)
        {
          continue;
        }

        if (!double.IsFinite(candle.QuoteVolume) || candle.QuoteVolume < 0)
        {
          return false;
        }

        quoteVolume += candle.QuoteVolume;
        found = true;
      }

      return found && double.IsFinite(quoteVolume);
    }

    private static bool TryCalculateIndependentReturn(
      MarketDataSeriesSnapshot series,
      ReturnHorizon horizon,
      out IndependentReturn result)
    {
      result = null;
      if (series == null || series.IsStale || series.LatestClosedCandle == null)
      {
        return false;
      }

      MarketCandle latest = series.LatestClosedCandle;
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
        Value = ((latest.Close / reference.Close) - 1.0) * 100.0
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

    private static bool FeatureMatches(MarketAnalysisSnapshot analysis, string featureName, double expectedValue, int expectedSampleCount)
    {
      return analysis.MarketFeatures.TryGetValue(featureName, out FeatureValueSnapshot feature)
        && feature != null
        && feature.IsValid
        && !feature.IsStale
        && feature.Value.HasValue
        && double.IsFinite(feature.Value.Value)
        && ApproximatelyEqual(expectedValue, feature.Value.Value)
        && feature.SampleCount == expectedSampleCount;
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

    private static bool ApproximatelyEqual(double expected, double actual)
    {
      double scale = Math.Max(1.0, Math.Max(Math.Abs(expected), Math.Abs(actual)));
      return Math.Abs(expected - actual) <= ComparisonTolerance * scale;
    }

    private static string FormatPercent(double value)
    {
      return value.ToString("F8", CultureInfo.InvariantCulture) + "%";
    }

    private sealed class RankedMarket
    {
      public string Symbol { get; init; } = "";
      public double QuoteVolume { get; init; }
    }

    private sealed class IndependentReturn
    {
      public double Value { get; init; }
    }
  }

  public sealed class ReturnConcentrationValidationSnapshot
  {
    public string Horizon { get; init; } = "";
    public IReadOnlyList<string> Top10Symbols { get; init; } = Array.Empty<string>();
    public int TopSampleCount { get; init; }
    public int RemainingUniverseSampleCount { get; init; }
    public double Top10MeanReturn { get; init; }
    public double RemainingUniverseMeanReturn { get; init; }
    public double Top10MedianReturn { get; init; }
    public double RemainingUniverseMedianReturn { get; init; }
    public double Top10ReturnAdvantage { get; init; }
    public int MismatchCount { get; init; }
    public bool IsValid => TopSampleCount > 0 && RemainingUniverseSampleCount > 0 && MismatchCount == 0;
  }
}
