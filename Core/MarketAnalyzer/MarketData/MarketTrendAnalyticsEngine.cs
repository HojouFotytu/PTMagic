using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Calculates the additional analytics requested by one configured MarketTrend.
  /// TrendMinutes supplies the return horizon. The caller supplies the exact global
  /// and asset universes selected by PTMagic's existing MarketTrend pipeline.
  /// </summary>
  public static class MarketTrendAnalyticsEngine
  {
    private static readonly TimeSpan ConcentrationRankingWindow = TimeSpan.FromHours(24);

    public static MarketTrendAnalysisSnapshot Calculate(
      MarketTrend marketTrend,
      IEnumerable<string> globalSymbols,
      IEnumerable<string> assetSymbols,
      MarketDataStore store,
      TimeSpan interval,
      DateTimeOffset calculatedAtUtc)
    {
      if (marketTrend == null)
      {
        throw new ArgumentNullException(nameof(marketTrend));
      }
      if (store == null)
      {
        throw new ArgumentNullException(nameof(store));
      }
      if (interval <= TimeSpan.Zero)
      {
        throw new ArgumentOutOfRangeException(nameof(interval));
      }

      string[] globalUniverse = NormalizeSymbols(globalSymbols);
      string[] assetUniverse = NormalizeSymbols(assetSymbols);
      string[] allSymbols = globalUniverse
        .Concat(assetUniverse)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
      IReadOnlyCollection<string> enabledMetrics = MarketTrendMetrics.ParseEnabled(marketTrend);

      Dictionary<string, FeatureValueSnapshot> returns = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
      foreach (string symbol in allSymbols)
      {
        MarketDataSeriesSnapshot series = store.GetSnapshot(symbol, interval, calculatedAtUtc);
        returns[symbol] = CalculateReturn(series, marketTrend.TrendMinutes, calculatedAtUtc);
      }

      List<FeatureValueSnapshot> globalReturns = globalUniverse
        .Where(returns.ContainsKey)
        .Select(symbol => returns[symbol])
        .Where(IsUsable)
        .ToList();

      FeatureValueSnapshot meanReturn = CalculateMean(globalReturns, calculatedAtUtc, marketTrend.TrendMinutes);
      FeatureValueSnapshot medianReturn = CalculateMedian(globalReturns, calculatedAtUtc, marketTrend.TrendMinutes);
      FeatureValueSnapshot breadth = CalculateBreadth(globalReturns, calculatedAtUtc, marketTrend.TrendMinutes);
      FeatureValueSnapshot dispersion = CalculateDispersion(globalReturns, meanReturn, calculatedAtUtc, marketTrend.TrendMinutes);

      Dictionary<string, FeatureValueSnapshot> marketMetrics = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
      if (enabledMetrics.Contains(MarketTrendMetrics.MeanReturn, StringComparer.OrdinalIgnoreCase))
      {
        marketMetrics[MarketTrendMetrics.MeanReturn] = meanReturn;
      }
      if (enabledMetrics.Contains(MarketTrendMetrics.MedianReturn, StringComparer.OrdinalIgnoreCase))
      {
        marketMetrics[MarketTrendMetrics.MedianReturn] = medianReturn;
      }
      if (enabledMetrics.Contains(MarketTrendMetrics.Breadth, StringComparer.OrdinalIgnoreCase))
      {
        marketMetrics[MarketTrendMetrics.Breadth] = breadth;
      }
      if (enabledMetrics.Contains(MarketTrendMetrics.Dispersion, StringComparer.OrdinalIgnoreCase))
      {
        marketMetrics[MarketTrendMetrics.Dispersion] = dispersion;
      }
      if (enabledMetrics.Contains(MarketTrendMetrics.Concentration, StringComparer.OrdinalIgnoreCase))
      {
        marketMetrics[MarketTrendMetrics.Concentration] = CalculateConcentration(
          globalUniverse,
          returns,
          store,
          interval,
          calculatedAtUtc);
      }

      FeatureValueSnapshot btcReturn = CalculateReturn(
        store.GetSnapshot("BTCUSDT", interval, calculatedAtUtc),
        marketTrend.TrendMinutes,
        calculatedAtUtc);

      Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> assetMetrics =
        new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(StringComparer.OrdinalIgnoreCase);

      foreach (string symbol in assetUniverse)
      {
        Dictionary<string, FeatureValueSnapshot> metrics = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
        FeatureValueSnapshot assetReturn = returns.TryGetValue(symbol, out FeatureValueSnapshot value)
          ? value
          : InvalidFeature(calculatedAtUtc, 0, "No normalized return data is available for '" + symbol + "'.");

        if (enabledMetrics.Contains(MarketTrendMetrics.RelativeStrengthMarket, StringComparer.OrdinalIgnoreCase))
        {
          metrics[MarketTrendMetrics.RelativeStrengthMarket] = CalculateDifference(
            assetReturn,
            medianReturn,
            calculatedAtUtc,
            "Asset return or market median return is unavailable for MarketTrend '" + marketTrend.Name + "'.");
        }

        if (enabledMetrics.Contains(MarketTrendMetrics.RelativeStrengthBTC, StringComparer.OrdinalIgnoreCase))
        {
          metrics[MarketTrendMetrics.RelativeStrengthBTC] = CalculateDifference(
            assetReturn,
            btcReturn,
            calculatedAtUtc,
            "Asset return or BTC return is unavailable for MarketTrend '" + marketTrend.Name + "'.");
        }

        if (metrics.Count > 0)
        {
          assetMetrics[symbol] = new ReadOnlyDictionary<string, FeatureValueSnapshot>(metrics);
        }
      }

      return new MarketTrendAnalysisSnapshot
      {
        MarketTrendName = marketTrend.Name ?? "",
        TrendMinutes = marketTrend.TrendMinutes,
        CalculatedAtUtc = calculatedAtUtc,
        GlobalSymbols = Array.AsReadOnly(globalUniverse),
        AssetSymbols = Array.AsReadOnly(assetUniverse),
        EnabledMetrics = Array.AsReadOnly(enabledMetrics.ToArray()),
        MarketMetrics = new ReadOnlyDictionary<string, FeatureValueSnapshot>(marketMetrics),
        AssetMetrics = new ReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(assetMetrics)
      };
    }

    public static FeatureValueSnapshot CalculateReturn(
      MarketDataSeriesSnapshot series,
      int trendMinutes,
      DateTimeOffset calculatedAtUtc)
    {
      if (trendMinutes <= 0)
      {
        return InvalidFeature(calculatedAtUtc, 0, "TrendMinutes must be greater than zero.");
      }
      if (series == null || series.LatestClosedCandle == null || series.ClosedCandles == null || series.ClosedCandles.Count == 0)
      {
        return InvalidFeature(calculatedAtUtc, 0, "No closed candle data is available.");
      }
      if (series.IsStale)
      {
        return InvalidFeature(calculatedAtUtc, 0, "The normalized candle series is stale.", true, series.LatestClosedCandle.CloseTimeUtc);
      }

      MarketCandle latest = series.LatestClosedCandle;
      DateTimeOffset targetCloseTimeUtc = latest.CloseTimeUtc - TimeSpan.FromMinutes(trendMinutes);
      MarketCandle reference = FindByCloseTime(series.ClosedCandles, targetCloseTimeUtc);
      if (reference == null)
      {
        return InvalidFeature(
          calculatedAtUtc,
          1,
          "No exact closed-candle reference exists " + trendMinutes + " minutes before the latest closed candle. StoreDataMaxHours may be insufficient or TrendMinutes may not align with the normalized interval.",
          false,
          latest.CloseTimeUtc);
      }
      if (!IsValidPrice(latest.Close) || !IsValidPrice(reference.Close))
      {
        return InvalidFeature(calculatedAtUtc, 2, "The latest or reference close is invalid.", false, reference.CloseTimeUtc);
      }

      return ValidFeature(
        ((latest.Close / reference.Close) - 1.0) * 100.0,
        calculatedAtUtc,
        reference.CloseTimeUtc,
        2);
    }

    private static FeatureValueSnapshot CalculateMean(List<FeatureValueSnapshot> features, DateTimeOffset calculatedAtUtc, int trendMinutes)
    {
      if (features.Count == 0)
      {
        return InvalidFeature(calculatedAtUtc, 0, "No valid asset returns are available for the " + trendMinutes + " minute MarketTrend.");
      }

      return ValidFeature(
        features.Average(feature => feature.Value.Value),
        calculatedAtUtc,
        GetOldestSourceTimestamp(features),
        features.Count);
    }

    private static FeatureValueSnapshot CalculateMedian(List<FeatureValueSnapshot> features, DateTimeOffset calculatedAtUtc, int trendMinutes)
    {
      if (features.Count == 0)
      {
        return InvalidFeature(calculatedAtUtc, 0, "No valid asset returns are available for the " + trendMinutes + " minute MarketTrend.");
      }

      double[] values = features.Select(feature => feature.Value.Value).OrderBy(value => value).ToArray();
      int middle = values.Length / 2;
      double median = values.Length % 2 == 1
        ? values[middle]
        : (values[middle - 1] + values[middle]) / 2.0;

      return ValidFeature(median, calculatedAtUtc, GetOldestSourceTimestamp(features), features.Count);
    }

    private static FeatureValueSnapshot CalculateBreadth(List<FeatureValueSnapshot> features, DateTimeOffset calculatedAtUtc, int trendMinutes)
    {
      if (features.Count == 0)
      {
        return InvalidFeature(calculatedAtUtc, 0, "No valid asset returns are available for breadth for the " + trendMinutes + " minute MarketTrend.");
      }

      double breadth = features.Count(feature => feature.Value.Value > 0) * 100.0 / features.Count;
      return ValidFeature(breadth, calculatedAtUtc, GetOldestSourceTimestamp(features), features.Count);
    }

    private static FeatureValueSnapshot CalculateDispersion(
      List<FeatureValueSnapshot> features,
      FeatureValueSnapshot meanReturn,
      DateTimeOffset calculatedAtUtc,
      int trendMinutes)
    {
      if (features.Count < 2 || !IsUsable(meanReturn))
      {
        return InvalidFeature(calculatedAtUtc, features.Count, "At least two valid asset returns are required for dispersion for the " + trendMinutes + " minute MarketTrend.");
      }

      double mean = meanReturn.Value.Value;
      double variance = features.Sum(feature =>
      {
        double difference = feature.Value.Value - mean;
        return difference * difference;
      }) / features.Count;

      return ValidFeature(Math.Sqrt(variance), calculatedAtUtc, GetOldestSourceTimestamp(features), features.Count);
    }

    private static FeatureValueSnapshot CalculateConcentration(
      string[] globalUniverse,
      IReadOnlyDictionary<string, FeatureValueSnapshot> returns,
      MarketDataStore store,
      TimeSpan interval,
      DateTimeOffset calculatedAtUtc)
    {
      List<RankedMarket> rankedMarkets = new List<RankedMarket>();
      foreach (string symbol in globalUniverse)
      {
        MarketDataSeriesSnapshot series = store.GetSnapshot(symbol, interval, calculatedAtUtc);
        if (TryCalculateCompleteTrailingQuoteVolume(series, interval, out double quoteVolume))
        {
          rankedMarkets.Add(new RankedMarket
          {
            Symbol = symbol,
            QuoteVolume = quoteVolume
          });
        }
      }

      if (rankedMarkets.Count < 11)
      {
        return InvalidFeature(
          calculatedAtUtc,
          rankedMarkets.Count,
          "At least 11 markets with a complete trailing 24-hour quote-volume window are required for concentration. StoreDataMaxHours may be insufficient.");
      }

      HashSet<string> top10 = rankedMarkets
        .OrderByDescending(market => market.QuoteVolume)
        .ThenBy(market => market.Symbol, StringComparer.OrdinalIgnoreCase)
        .Take(10)
        .Select(market => market.Symbol)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

      List<FeatureValueSnapshot> topReturns = new List<FeatureValueSnapshot>();
      List<FeatureValueSnapshot> remainingReturns = new List<FeatureValueSnapshot>();
      foreach (string symbol in globalUniverse)
      {
        if (!returns.TryGetValue(symbol, out FeatureValueSnapshot feature) || !IsUsable(feature))
        {
          continue;
        }

        if (top10.Contains(symbol))
        {
          topReturns.Add(feature);
        }
        else
        {
          remainingReturns.Add(feature);
        }
      }

      if (topReturns.Count == 0 || remainingReturns.Count == 0)
      {
        return InvalidFeature(calculatedAtUtc, topReturns.Count + remainingReturns.Count, "Top10 or remaining-universe returns are unavailable for concentration.");
      }

      double topMean = topReturns.Average(feature => feature.Value.Value);
      double remainingMean = remainingReturns.Average(feature => feature.Value.Value);
      return ValidFeature(
        topMean - remainingMean,
        calculatedAtUtc,
        GetOldestSourceTimestamp(topReturns.Concat(remainingReturns)),
        topReturns.Count + remainingReturns.Count);
    }

    private static bool TryCalculateCompleteTrailingQuoteVolume(MarketDataSeriesSnapshot series, TimeSpan interval, out double quoteVolume)
    {
      quoteVolume = 0;
      if (series == null || series.IsStale || series.LatestClosedCandle == null || series.ClosedCandles == null)
      {
        return false;
      }

      int requiredCandles = (int)Math.Ceiling(ConcentrationRankingWindow.TotalMilliseconds / interval.TotalMilliseconds);
      MarketCandle latest = series.LatestClosedCandle;
      DateTimeOffset firstRequiredClose = latest.CloseTimeUtc - ConcentrationRankingWindow + interval;
      MarketCandle[] selected = series.ClosedCandles
        .Where(candle => candle != null && candle.IsClosed && candle.CloseTimeUtc >= firstRequiredClose && candle.CloseTimeUtc <= latest.CloseTimeUtc)
        .OrderBy(candle => candle.CloseTimeUtc)
        .ToArray();

      if (selected.Length != requiredCandles || selected[0].CloseTimeUtc != firstRequiredClose)
      {
        return false;
      }

      for (int index = 0; index < selected.Length; index++)
      {
        DateTimeOffset expectedClose = firstRequiredClose + TimeSpan.FromTicks(interval.Ticks * index);
        if (selected[index].CloseTimeUtc != expectedClose
          || !double.IsFinite(selected[index].QuoteVolume)
          || selected[index].QuoteVolume < 0)
        {
          return false;
        }

        quoteVolume += selected[index].QuoteVolume;
      }

      return double.IsFinite(quoteVolume);
    }

    private static FeatureValueSnapshot CalculateDifference(
      FeatureValueSnapshot left,
      FeatureValueSnapshot right,
      DateTimeOffset calculatedAtUtc,
      string invalidReason)
    {
      bool leftValid = IsUsable(left);
      bool rightValid = IsUsable(right);
      if (!leftValid || !rightValid)
      {
        return InvalidFeature(
          calculatedAtUtc,
          (leftValid ? 1 : 0) + (rightValid ? 1 : 0),
          invalidReason,
          left?.IsStale == true || right?.IsStale == true,
          GetOldestSourceTimestamp(new FeatureValueSnapshot[] { left, right }.Where(feature => feature != null)));
      }

      return ValidFeature(
        left.Value.Value - right.Value.Value,
        calculatedAtUtc,
        GetOldestSourceTimestamp(new FeatureValueSnapshot[] { left, right }),
        2);
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

    private static string[] NormalizeSymbols(IEnumerable<string> symbols)
    {
      return (symbols ?? Array.Empty<string>())
        .Where(symbol => !String.IsNullOrWhiteSpace(symbol))
        .Select(symbol => symbol.Trim().ToUpperInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    }

    private static bool IsValidPrice(double value)
    {
      return double.IsFinite(value) && value > 0;
    }

    private static bool IsUsable(FeatureValueSnapshot feature)
    {
      return feature != null
        && feature.IsValid
        && !feature.IsStale
        && feature.Value.HasValue
        && double.IsFinite(feature.Value.Value);
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

    private static FeatureValueSnapshot InvalidFeature(
      DateTimeOffset calculatedAtUtc,
      int sampleCount,
      string reason,
      bool isStale = false,
      DateTimeOffset? sourceTimestampUtc = null)
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

    private sealed class RankedMarket
    {
      public string Symbol { get; init; } = "";
      public double QuoteVolume { get; init; }
    }
  }

  public sealed class MarketTrendAnalysisSnapshot
  {
    public string MarketTrendName { get; init; } = "";
    public int TrendMinutes { get; init; }
    public DateTimeOffset CalculatedAtUtc { get; init; }
    public IReadOnlyList<string> GlobalSymbols { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AssetSymbols { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> EnabledMetrics { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, FeatureValueSnapshot> MarketMetrics { get; init; } = new Dictionary<string, FeatureValueSnapshot>();
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> AssetMetrics { get; init; } = new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(StringComparer.OrdinalIgnoreCase);
  }
}
