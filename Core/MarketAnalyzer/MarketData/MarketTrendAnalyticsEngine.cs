using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Calculates the normalized analytics explicitly requested by one configured MarketTrend.
  /// TrendMinutes supplies the return horizon. The caller supplies the exact global
  /// and asset universes selected by PTMagic's existing MarketTrend pipeline.
  /// </summary>
  public static class MarketTrendAnalyticsEngine
  {
    public static MarketTrendAnalysisSnapshot Calculate(
      MarketTrend marketTrend,
      IEnumerable<string> globalSymbols,
      IEnumerable<string> assetSymbols,
      MarketDataStore store,
      TimeSpan interval,
      DateTimeOffset calculatedAtUtc,
      IReadOnlyCollection<string> selectedMetrics = null,
      string benchmarkSymbol = "BTCUSDT",
      int? configuredStoreDataMaxHours = null,
      FuturesOpenInterestDataService openInterestData = null, string exchangeName = "BinanceFutures",
      FuturesFundingDataService fundingData = null)
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
      MarketTrendTimeframe.Validate(marketTrend, interval);

      string[] globalUniverse = NormalizeSymbols(globalSymbols);
      string[] assetUniverse = NormalizeSymbols(assetSymbols);
      IReadOnlyCollection<string> enabledMetrics = selectedMetrics ?? MarketTrendMetrics.ParseEnabled(marketTrend);

      bool needsMeanPriceTrend = enabledMetrics.Contains(MarketTrendMetrics.PriceTrend, StringComparer.OrdinalIgnoreCase)
        || enabledMetrics.Contains(MarketTrendMetrics.MeanMedianSpread, StringComparer.OrdinalIgnoreCase)
        || enabledMetrics.Contains(MarketTrendMetrics.Dispersion, StringComparer.OrdinalIgnoreCase);
      bool needsMedianPriceTrend = enabledMetrics.Contains(MarketTrendMetrics.MedianPriceTrend, StringComparer.OrdinalIgnoreCase)
        || enabledMetrics.Contains(MarketTrendMetrics.MeanMedianSpread, StringComparer.OrdinalIgnoreCase)
        || enabledMetrics.Contains(MarketTrendMetrics.RelativeStrengthMarket, StringComparer.OrdinalIgnoreCase);
      bool needsBreadth = enabledMetrics.Contains(MarketTrendMetrics.Breadth, StringComparer.OrdinalIgnoreCase);
      bool needsDispersion = enabledMetrics.Contains(MarketTrendMetrics.Dispersion, StringComparer.OrdinalIgnoreCase);
      bool needsConcentration = enabledMetrics.Contains(MarketTrendMetrics.Concentration, StringComparer.OrdinalIgnoreCase);
      bool needsRelativeStrengthMarket = enabledMetrics.Contains(MarketTrendMetrics.RelativeStrengthMarket, StringComparer.OrdinalIgnoreCase);
      bool needsRelativeStrengthBTC = enabledMetrics.Contains(MarketTrendMetrics.RelativeStrengthBTC, StringComparer.OrdinalIgnoreCase);
      bool needsGlobalReturns = needsMeanPriceTrend || needsMedianPriceTrend || needsBreadth || needsConcentration;
      bool needsAssetReturns = needsRelativeStrengthMarket || needsRelativeStrengthBTC
        || enabledMetrics.Contains(MarketTrendMetrics.PriceTrend, StringComparer.OrdinalIgnoreCase);

      HashSet<string> symbolsToCalculate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      if (needsGlobalReturns)
      {
        symbolsToCalculate.UnionWith(globalUniverse);
      }
      if (needsAssetReturns)
      {
        symbolsToCalculate.UnionWith(assetUniverse);
      }

      // Fiat returns are calculated from the asset/quote and quote/USDT
      // prices at the same closed endpoints. The conversion pair is a data
      // dependency, not an additional market in the global/asset population.
      bool fiat = MarketTrendFiatConversion.IsFiat(marketTrend.TrendCurrency);
      string quote = benchmarkSymbol.StartsWith("BTC", StringComparison.OrdinalIgnoreCase)
        ? benchmarkSymbol.Substring(3) : "USDT";
      string fiatConversionSymbol = fiat ? MarketTrendFiatConversion.ConversionSymbol(quote) : null;
      MarketDataSeriesSnapshot fiatConversion = fiatConversionSymbol == null
        ? null : store.GetSnapshot(fiatConversionSymbol, interval, calculatedAtUtc);

      FeatureValueSnapshot GetReturn(string symbol)
      {
        MarketDataSeriesSnapshot series = store.GetSnapshot(symbol, interval, calculatedAtUtc);
        return fiat
          ? MarketTrendFiatConversion.CalculateReturn(series, fiatConversion, quote, marketTrend.TrendMinutes, calculatedAtUtc)
          : CalculateReturn(series, marketTrend.TrendMinutes, calculatedAtUtc);
      }

      Dictionary<string, FeatureValueSnapshot> returns = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
      foreach (string symbol in symbolsToCalculate)
      {
        returns[symbol] = GetReturn(symbol);
      }

      List<FeatureValueSnapshot> globalReturns = needsGlobalReturns
        ? globalUniverse
          .Where(returns.ContainsKey)
          .Select(symbol => returns[symbol])
          .Where(IsUsable)
          .ToList()
        : new List<FeatureValueSnapshot>();

      // The legacy outlier gate applies ONLY to the arithmetic PriceTrend
      // average, not to the shared population or other global metrics.
      // Preserve the full mean for dispersion and mean/median spread.
      FeatureValueSnapshot fullMean = needsMeanPriceTrend
        ? MarketMetricCoverage.Apply(CalculateMean(globalReturns, calculatedAtUtc, marketTrend.TrendMinutes),
            globalReturns.Count, globalUniverse.Length, calculatedAtUtc)
        : null;
      List<FeatureValueSnapshot> priceTrendReturns = FilterPriceTrendOutliers(globalReturns, marketTrend.TrendThreshold);
      FeatureValueSnapshot meanPriceTrend = needsMeanPriceTrend
        ? MarketMetricCoverage.Apply(CalculateMean(priceTrendReturns, calculatedAtUtc, marketTrend.TrendMinutes),
            globalReturns.Count, globalUniverse.Length, calculatedAtUtc)
        : null;
      FeatureValueSnapshot medianPriceTrend = needsMedianPriceTrend
        ? MarketMetricCoverage.Apply(CalculateMedian(globalReturns, calculatedAtUtc, marketTrend.TrendMinutes),
            globalReturns.Count, globalUniverse.Length, calculatedAtUtc)
        : null;

      Dictionary<string, FeatureValueSnapshot> marketMetrics = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
      if (enabledMetrics.Contains(MarketTrendMetrics.PriceTrend, StringComparer.OrdinalIgnoreCase))
      {
        marketMetrics[MarketTrendMetrics.PriceTrend] = meanPriceTrend;
      }
      if (enabledMetrics.Contains(MarketTrendMetrics.MedianPriceTrend, StringComparer.OrdinalIgnoreCase))
      {
        marketMetrics[MarketTrendMetrics.MedianPriceTrend] = medianPriceTrend;
      }
      if (enabledMetrics.Contains(MarketTrendMetrics.MeanMedianSpread, StringComparer.OrdinalIgnoreCase))
      {
        marketMetrics[MarketTrendMetrics.MeanMedianSpread] = MarketMetricCoverage.Apply(CalculateMeanMedianSpread(
          fullMean,
          medianPriceTrend,
          globalReturns.Count,
          calculatedAtUtc,
          marketTrend.TrendMinutes), globalReturns.Count, globalUniverse.Length, calculatedAtUtc);
      }
      if (needsBreadth)
      {
        marketMetrics[MarketTrendMetrics.Breadth] = MarketMetricCoverage.Apply(
          CalculateBreadth(globalReturns, calculatedAtUtc, marketTrend.TrendMinutes),
          globalReturns.Count, globalUniverse.Length, calculatedAtUtc);
      }
      if (needsDispersion)
      {
        marketMetrics[MarketTrendMetrics.Dispersion] = MarketMetricCoverage.Apply(
          CalculateDispersion(globalReturns, fullMean, calculatedAtUtc, marketTrend.TrendMinutes),
          globalReturns.Count, globalUniverse.Length, calculatedAtUtc);
      }
      if (needsConcentration)
      {
        FeatureValueSnapshot concentration = CalculateConcentration(
          globalUniverse,
          returns,
          store,
          interval,
          calculatedAtUtc,
          TimeSpan.FromMinutes(marketTrend.TrendMinutes));
        // Top10 ranking and remaining-population comparison require a complete
        // trailing-volume sample, unlike the 90% mean/median/breadth policy.
        marketMetrics[MarketTrendMetrics.Concentration] = MarketMetricCoverage.Apply(
          concentration, concentration.SampleCount, globalUniverse.Length, calculatedAtUtc, requireComplete: true);
      }

      Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> assetMetrics =
        new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(StringComparer.OrdinalIgnoreCase);

      FeatureValueSnapshot btcReturn = null;
      if (needsRelativeStrengthBTC)
      {
        if (!returns.TryGetValue(benchmarkSymbol, out btcReturn))
        {
          btcReturn = GetReturn(benchmarkSymbol);
        }
      }

      if (needsAssetReturns)
      {
        foreach (string symbol in assetUniverse)
        {
          Dictionary<string, FeatureValueSnapshot> metrics = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
          FeatureValueSnapshot assetReturn = returns.TryGetValue(symbol, out FeatureValueSnapshot value)
            ? value
            : InvalidFeature(calculatedAtUtc, 0, "No normalized return data is available for '" + symbol + "'.");

          if (enabledMetrics.Contains(MarketTrendMetrics.PriceTrend, StringComparer.OrdinalIgnoreCase))
          {
            metrics[MarketTrendMetrics.PriceTrend] = assetReturn;
          }

          if (needsRelativeStrengthMarket)
          {
            metrics[MarketTrendMetrics.RelativeStrengthMarket] = CalculateDifference(
              assetReturn,
              medianPriceTrend,
              calculatedAtUtc,
              "Asset return or MedianPriceTrend is unavailable for MarketTrend '" + marketTrend.Name + "'.");
          }

          if (needsRelativeStrengthBTC)
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
      }

      FeatureValueSnapshot smsRelativePriceTrend = enabledMetrics.Contains(
          MarketTrendMetrics.PriceTrend, StringComparer.OrdinalIgnoreCase)
        ? MarketMetricCoverage.Apply(
            CalculateMean(FilterPriceTrendOutliers(
              assetUniverse.Where(returns.ContainsKey).Select(symbol => returns[symbol])
                .Where(IsUsable).ToList(), marketTrend.TrendThreshold),
              calculatedAtUtc, marketTrend.TrendMinutes),
            assetUniverse.Count(symbol => returns.TryGetValue(symbol, out FeatureValueSnapshot item) && IsUsable(item)),
            assetUniverse.Length, calculatedAtUtc)
        : null;

      // Phase 1C runs only for explicitly enabled metrics, sharing the exact
      // frozen MarketTrend populations and the existing normalized OHLCV store.
      if (MarketTrendPhase1CEngine.HasMetrics(enabledMetrics))
      {
        Phase1CFeatureResult phase1c = MarketTrendPhase1CEngine.Calculate(
          marketTrend, enabledMetrics, globalUniverse, assetUniverse, store, interval, calculatedAtUtc,
          benchmarkSymbol.StartsWith("BTC", StringComparison.OrdinalIgnoreCase)
            ? benchmarkSymbol.Substring(3) : "USDT");
        foreach (KeyValuePair<string, FeatureValueSnapshot> feature in phase1c.GlobalMetrics)
        {
          marketMetrics[feature.Key] = feature.Value;
        }
        foreach (KeyValuePair<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> asset in phase1c.AssetMetrics)
        {
          if (assetMetrics.TryGetValue(asset.Key, out IReadOnlyDictionary<string, FeatureValueSnapshot> existing))
          {
            Dictionary<string, FeatureValueSnapshot> merged =
              new Dictionary<string, FeatureValueSnapshot>(existing, StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, FeatureValueSnapshot> metric in asset.Value)
            {
              merged[metric.Key] = metric.Value;
            }
            assetMetrics[asset.Key] = new ReadOnlyDictionary<string, FeatureValueSnapshot>(merged);
          }
          else
          {
            assetMetrics[asset.Key] = asset.Value;
          }
        }
      }

      if (MarketTrendPhase1DEngine.HasMetrics(enabledMetrics))
      {
        Phase1DFeatureResult phase1d = MarketTrendPhase1DEngine.Calculate(
          marketTrend, enabledMetrics, globalUniverse, assetUniverse, store, interval,
          calculatedAtUtc, benchmarkSymbol);
        foreach (KeyValuePair<string, FeatureValueSnapshot> feature in phase1d.GlobalMetrics)
        {
          marketMetrics[feature.Key] = feature.Value;
        }
        foreach (KeyValuePair<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> asset in phase1d.AssetMetrics)
        {
          Dictionary<string, FeatureValueSnapshot> merged = assetMetrics.TryGetValue(asset.Key,
              out IReadOnlyDictionary<string, FeatureValueSnapshot> existing)
            ? new Dictionary<string, FeatureValueSnapshot>(existing, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
          foreach (KeyValuePair<string, FeatureValueSnapshot> metric in asset.Value)
          {
            merged[metric.Key] = metric.Value;
          }
          assetMetrics[asset.Key] = new ReadOnlyDictionary<string, FeatureValueSnapshot>(merged);
        }
      }

      if (FuturesOpenInterestFeatureEngine.HasMetrics(enabledMetrics))
      {
        var phase2 = FuturesOpenInterestFeatureEngine.Calculate(assetUniverse, enabledMetrics,
          marketTrend.TrendMinutes, calculatedAtUtc, exchangeName, openInterestData);
        foreach (var asset in phase2)
        {
          Dictionary<string, FeatureValueSnapshot> merged = assetMetrics.TryGetValue(asset.Key,
              out IReadOnlyDictionary<string, FeatureValueSnapshot> existing)
            ? new Dictionary<string, FeatureValueSnapshot>(existing, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
          foreach (var feature in asset.Value) merged[feature.Key] = feature.Value;
          assetMetrics[asset.Key] = new ReadOnlyDictionary<string, FeatureValueSnapshot>(merged);
        }
      }

      if (FuturesFundingFeatureEngine.HasMetrics(enabledMetrics))
      {
        var funding = FuturesFundingFeatureEngine.Calculate(assetUniverse, enabledMetrics,
          marketTrend.TrendMinutes, calculatedAtUtc, exchangeName, fundingData);
        foreach (var asset in funding)
        {
          Dictionary<string, FeatureValueSnapshot> merged = assetMetrics.TryGetValue(asset.Key,
              out IReadOnlyDictionary<string, FeatureValueSnapshot> existing)
            ? new Dictionary<string, FeatureValueSnapshot>(existing, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
          foreach (var feature in asset.Value) merged[feature.Key] = feature.Value;
          assetMetrics[asset.Key] = new ReadOnlyDictionary<string, FeatureValueSnapshot>(merged);
        }
      }

      Dictionary<string, int> globalIssues = needsGlobalReturns
        ? CountInvalidReturnReasons(globalUniverse, returns)
        : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      Dictionary<string, int> assetIssues = needsAssetReturns
        ? CountInvalidReturnReasons(assetUniverse, returns)
        : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

      return new MarketTrendAnalysisSnapshot
      {
        ExpectedClosedCandleTimeUtc = GetExpectedClosedCandleCloseTime(calculatedAtUtc, interval),
        GlobalReturnsEvaluated = needsGlobalReturns,
        AssetReturnsEvaluated = needsAssetReturns,
        ValidGlobalReturnCount = globalReturns.Count,
        ValidAssetReturnCount = needsAssetReturns
          ? assetUniverse.Count(symbol => returns.TryGetValue(symbol, out FeatureValueSnapshot value) && IsUsable(value))
          : 0,
        GlobalInvalidReturnReasons = new ReadOnlyDictionary<string, int>(globalIssues),
        AssetInvalidReturnReasons = new ReadOnlyDictionary<string, int>(assetIssues),
        MarketTrendName = marketTrend.Name ?? "",
        SmsRelativePriceTrend = smsRelativePriceTrend,
        TrendMinutes = marketTrend.TrendMinutes,
        CalculatedAtUtc = calculatedAtUtc,
        GlobalSymbols = Array.AsReadOnly(globalUniverse),
        AssetSymbols = Array.AsReadOnly(assetUniverse),
        EnabledMetrics = Array.AsReadOnly(enabledMetrics.ToArray()),
        HistoryRequirements = MarketTrendHistoryRequirements.Describe(marketTrend, enabledMetrics,
          interval, calculatedAtUtc, configuredStoreDataMaxHours),
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
      if (series == null || series.ClosedCandles == null || series.ClosedCandles.Count == 0)
      {
        return InvalidFeature(calculatedAtUtc, 0, "No closed candle data is available.");
      }
      if (series.Interval <= TimeSpan.Zero)
      {
        return InvalidFeature(calculatedAtUtc, 0, "The normalized candle interval is invalid.");
      }

      DateTimeOffset expectedCloseTimeUtc = GetExpectedClosedCandleCloseTime(calculatedAtUtc, series.Interval);
      MarketCandle latest = FindByCloseTime(series.ClosedCandles, expectedCloseTimeUtc);
      if (latest == null)
      {
        return InvalidFeature(
          calculatedAtUtc,
          0,
          "No exact closed-candle endpoint exists for the current normalized interval.",
          series.IsStale,
          series.LatestClosedCloseTimeUtc);
      }

      DateTimeOffset targetCloseTimeUtc = expectedCloseTimeUtc - TimeSpan.FromMinutes(trendMinutes);
      MarketCandle reference = FindByCloseTime(series.ClosedCandles, targetCloseTimeUtc);
      if (reference == null)
      {
        return InvalidFeature(
          calculatedAtUtc,
          1,
          "No exact closed-candle reference exists " + trendMinutes + " minutes before the current normalized endpoint. StoreDataMaxHours may be insufficient or TrendMinutes may not align with the normalized interval.",
          false,
          expectedCloseTimeUtc);
      }
      if (!IsValidPrice(latest.Close) || !IsValidPrice(reference.Close))
      {
        return InvalidFeature(calculatedAtUtc, 2, "The endpoint or reference close is invalid.", false, expectedCloseTimeUtc);
      }

      return ValidFeature(
        ((latest.Close / reference.Close) - 1.0) * 100.0,
        calculatedAtUtc,
        expectedCloseTimeUtc,
        2);
    }

    private static List<FeatureValueSnapshot> FilterPriceTrendOutliers(
      List<FeatureValueSnapshot> features, int trendThreshold)
    {
      if (trendThreshold == 0) return features;
      double limit = Math.Abs((double)trendThreshold);
      return features.Where(feature => Math.Abs(feature.Value.Value) <= limit).ToList();
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

    private static FeatureValueSnapshot CalculateMeanMedianSpread(
      FeatureValueSnapshot meanPriceTrend,
      FeatureValueSnapshot medianPriceTrend,
      int sampleCount,
      DateTimeOffset calculatedAtUtc,
      int trendMinutes)
    {
      if (!IsUsable(meanPriceTrend) || !IsUsable(medianPriceTrend))
      {
        return InvalidFeature(
          calculatedAtUtc,
          sampleCount,
          "Mean or median PriceTrend is unavailable for MeanMedianSpread for the " + trendMinutes + " minute MarketTrend.",
          meanPriceTrend?.IsStale == true || medianPriceTrend?.IsStale == true,
          GetOldestSourceTimestamp(new FeatureValueSnapshot[] { meanPriceTrend, medianPriceTrend }.Where(feature => feature != null)));
      }

      return ValidFeature(
        meanPriceTrend.Value.Value - medianPriceTrend.Value.Value,
        calculatedAtUtc,
        GetOldestSourceTimestamp(new FeatureValueSnapshot[] { meanPriceTrend, medianPriceTrend }),
        sampleCount);
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
      FeatureValueSnapshot meanPriceTrend,
      DateTimeOffset calculatedAtUtc,
      int trendMinutes)
    {
      if (features.Count < 2 || !IsUsable(meanPriceTrend))
      {
        return InvalidFeature(calculatedAtUtc, features.Count, "At least two valid asset returns are required for dispersion for the " + trendMinutes + " minute MarketTrend.");
      }

      double mean = meanPriceTrend.Value.Value;
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
      DateTimeOffset calculatedAtUtc,
      TimeSpan rankingWindow)
    {
      DateTimeOffset expectedCloseTimeUtc = GetExpectedClosedCandleCloseTime(calculatedAtUtc, interval);
      List<RankedMarket> rankedMarkets = new List<RankedMarket>();
      foreach (string symbol in globalUniverse)
      {
        MarketDataSeriesSnapshot series = store.GetSnapshot(symbol, interval, calculatedAtUtc);
        if (TryCalculateCompleteTrailingQuoteVolume(series, interval, rankingWindow, expectedCloseTimeUtc, out double quoteVolume))
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
          "At least 11 markets with a complete trailing " + rankingWindow.TotalMinutes +
          "-minute quote-volume window ending at the current normalized endpoint are required for concentration. StoreDataMaxHours may be insufficient.");
      }

      RankedMarket[] orderedMarkets = rankedMarkets
        .OrderByDescending(market => market.QuoteVolume)
        .ThenBy(market => market.Symbol, StringComparer.OrdinalIgnoreCase)
        .ToArray();
      HashSet<string> top10 = orderedMarkets
        .Take(10)
        .Select(market => market.Symbol)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

      List<FeatureValueSnapshot> topReturns = new List<FeatureValueSnapshot>(10);
      List<FeatureValueSnapshot> remainingReturns = new List<FeatureValueSnapshot>(orderedMarkets.Length - 10);
      foreach (RankedMarket rankedMarket in orderedMarkets)
      {
        if (!returns.TryGetValue(rankedMarket.Symbol, out FeatureValueSnapshot feature) || !IsUsable(feature))
        {
          return InvalidFeature(
            calculatedAtUtc,
            rankedMarkets.Count,
            "A return is unavailable for volume-ranked market '" + rankedMarket.Symbol + "'; concentration requires aligned returns for the complete ranked population.",
            feature?.IsStale == true,
            feature?.SourceDataTimestampUtc);
        }

        if (top10.Contains(rankedMarket.Symbol))
        {
          topReturns.Add(feature);
        }
        else
        {
          remainingReturns.Add(feature);
        }
      }

      if (topReturns.Count != 10 || remainingReturns.Count == 0)
      {
        return InvalidFeature(calculatedAtUtc, topReturns.Count + remainingReturns.Count, "A complete Top10 and remaining-universe return population is required for concentration.");
      }

      double topMean = topReturns.Average(feature => feature.Value.Value);
      double remainingMean = remainingReturns.Average(feature => feature.Value.Value);
      return ValidFeature(
        topMean - remainingMean,
        calculatedAtUtc,
        GetOldestSourceTimestamp(topReturns.Concat(remainingReturns)),
        topReturns.Count + remainingReturns.Count);
    }

    private static bool TryCalculateCompleteTrailingQuoteVolume(
      MarketDataSeriesSnapshot series,
      TimeSpan interval,
      TimeSpan rankingWindow,
      DateTimeOffset expectedCloseTimeUtc,
      out double quoteVolume)
    {
      quoteVolume = 0;
      if (series == null || series.ClosedCandles == null || series.ClosedCandles.Count == 0)
      {
        return false;
      }
      if (FindByCloseTime(series.ClosedCandles, expectedCloseTimeUtc) == null)
      {
        return false;
      }

      int requiredCandles = checked((int)(rankingWindow.Ticks / interval.Ticks));
      DateTimeOffset firstRequiredClose = expectedCloseTimeUtc - rankingWindow + interval;
      MarketCandle[] selected = series.ClosedCandles
        .Where(candle => candle != null && candle.IsClosed && candle.CloseTimeUtc >= firstRequiredClose && candle.CloseTimeUtc <= expectedCloseTimeUtc)
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
          || selected[index].Interval != interval
          || selected[index].OpenTimeUtc != expectedClose - interval + TimeSpan.FromMilliseconds(1)
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

    private static DateTimeOffset GetExpectedClosedCandleCloseTime(DateTimeOffset calculatedAtUtc, TimeSpan interval)
    {
      long intervalMilliseconds = checked((long)interval.TotalMilliseconds);
      if (intervalMilliseconds <= 0)
      {
        throw new ArgumentOutOfRangeException(nameof(interval));
      }

      long asOfMilliseconds = calculatedAtUtc.ToUniversalTime().ToUnixTimeMilliseconds();
      long currentIntervalOpenMilliseconds = asOfMilliseconds - (asOfMilliseconds % intervalMilliseconds);
      return DateTimeOffset.FromUnixTimeMilliseconds(currentIntervalOpenMilliseconds - 1);
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

    private static Dictionary<string, int> CountInvalidReturnReasons(
      IEnumerable<string> symbols,
      IReadOnlyDictionary<string, FeatureValueSnapshot> returns)
    {
      Dictionary<string, int> reasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      foreach (string symbol in symbols)
      {
        if (returns.TryGetValue(symbol, out FeatureValueSnapshot feature) && IsUsable(feature))
        {
          continue;
        }

        string reason;
        if (feature == null)
        {
          reason = "MissingObservation";
        }
        else if (feature.InvalidReason.Contains("closed-candle endpoint", StringComparison.OrdinalIgnoreCase))
        {
          reason = "MissingEndpoint";
        }
        else if (feature.InvalidReason.Contains("closed-candle reference", StringComparison.OrdinalIgnoreCase))
        {
          reason = "MissingReference";
        }
        else if (feature.InvalidReason.Contains("No closed candle", StringComparison.OrdinalIgnoreCase))
        {
          reason = "NoClosedCandles";
        }
        else if (feature.IsStale)
        {
          reason = "Stale";
        }
        else
        {
          reason = "InvalidReturn";
        }

        reasons[reason] = reasons.TryGetValue(reason, out int count) ? count + 1 : 1;
      }

      return reasons;
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
    // Uncapped asset-population reference mean for legacy SMS Relative PriceTrend.
    public FeatureValueSnapshot SmsRelativePriceTrend { get; init; }
    public DateTimeOffset ExpectedClosedCandleTimeUtc { get; init; }
    public bool GlobalReturnsEvaluated { get; init; }
    public bool AssetReturnsEvaluated { get; init; }
    public int ValidGlobalReturnCount { get; init; }
    public int ValidAssetReturnCount { get; init; }
    public IReadOnlyDictionary<string, int> GlobalInvalidReturnReasons { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> AssetInvalidReturnReasons { get; init; } = new Dictionary<string, int>();
    public int TrendMinutes { get; init; }
    public DateTimeOffset CalculatedAtUtc { get; init; }
    public IReadOnlyList<string> GlobalSymbols { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AssetSymbols { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> EnabledMetrics { get; init; } = Array.Empty<string>();
    public IReadOnlyList<MarketTrendHistoryRequirement> HistoryRequirements { get; init; } = Array.Empty<MarketTrendHistoryRequirement>();
    public IReadOnlyDictionary<string, FeatureValueSnapshot> MarketMetrics { get; init; } = new Dictionary<string, FeatureValueSnapshot>();
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> AssetMetrics { get; init; } = new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(StringComparer.OrdinalIgnoreCase);
  }
}
