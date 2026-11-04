using System;
using System.Collections.Generic;
using System.Linq;
using Core.Main.DataObjects.PTMagicData;
using Core.MarketAnalyzer.MarketData;

internal static class Program
{
  private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
  // At 10:12 UTC the required aligned endpoint is 10:09:59.999 UTC.
  private static readonly DateTimeOffset AsOf = new DateTimeOffset(2026, 11, 4, 10, 12, 0, TimeSpan.Zero);
  private static readonly DateTimeOffset LatestOpen = new DateTimeOffset(2026, 11, 4, 10, 5, 0, TimeSpan.Zero);
  private static readonly string[] GlobalSymbols = Enumerable.Range(1, 10).Select(i => "TEST" + i + "USDT").ToArray();
  private static int _passed;

  private static int Main()
  {
    try
    {
      TestBoundaryCounts();
      TestIncompleteAndCleanupRaids();
      TestMissingReferenceReason();
      TestConcentrationRequiresCompleteRanking();
      Console.WriteLine("Phase 1A readiness tests passed: " + _passed + ".");
      return 0;
    }
    catch (Exception error)
    {
      Console.Error.WriteLine("Phase 1A readiness regression FAILED: " + error);
      return 1;
    }
  }

  private static void TestBoundaryCounts()
  {
    Expect(MarketMetricCoverage.RequiredValidMarkets(249) == 225, "249 eligible markets require 225 valid.");
    Expect(MarketMetricCoverage.RequiredValidMarkets(717) == 646, "717 eligible markets require 646 valid.");
    Expect(MarketMetricCoverage.IsReady(9, 10), "90% is ready.");
    Expect(!MarketMetricCoverage.IsReady(8, 10), "80% is unavailable.");
    Expect(!MarketMetricCoverage.IsReady(0, 0), "An empty universe cannot pass readiness.");
    Expect(!MarketMetricCoverage.IsReady(10, 9), "Overcounting cannot pass readiness.");
    Expect(!MarketMetricCoverage.IsReady(9, 10, requireComplete: true), "Complete rankings require 100%.");
    _passed++;
  }

  private static void TestIncompleteAndCleanupRaids()
  {
    MarketDataStore store = new MarketDataStore(400);
    for (int index = 0; index < GlobalSymbols.Length; index++)
    {
      AddTwoCandles(store, GlobalSymbols[index], includeLatest: index < 8);
    }
    AddTwoCandles(store, "BTCUSDT", includeLatest: true);

    MarketTrend trend = NewTrend("PriceTrend,MedianPriceTrend,MeanMedianSpread,Breadth,Dispersion,RelativeStrengthMarket,RelativeStrengthBTC");
    string[] assetSymbols = { GlobalSymbols[0], "BTCUSDT" };

    MarketTrendAnalysisSnapshot first = MarketTrendAnalyticsEngine.Calculate(trend, GlobalSymbols, assetSymbols, store, Interval, AsOf);
    Expect(first.ValidGlobalReturnCount == 8, "First raid must find eight aligned returns.");
    Expect(!Get(first, MarketTrendMetrics.PriceTrend).IsValid, "80% PriceTrend must fail closed.");
    Expect(!Get(first, MarketTrendMetrics.MedianPriceTrend).IsValid, "80% median must fail closed.");
    Expect(!Get(first, MarketTrendMetrics.Breadth).IsValid, "80% breadth must fail closed.");
    Expect(!Get(first, MarketTrendMetrics.MeanMedianSpread).IsValid, "80% spread must fail closed.");
    Expect(!Get(first, MarketTrendMetrics.Dispersion).IsValid, "80% dispersion must fail closed.");
    Expect(!first.AssetMetrics[GlobalSymbols[0]][MarketTrendMetrics.RelativeStrengthMarket].IsValid,
      "RelativeStrengthMarket must depend on a ready median.");
    Expect(first.AssetMetrics[GlobalSymbols[0]][MarketTrendMetrics.RelativeStrengthBTC].IsValid,
      "Independent RelativeStrengthBTC must remain available.");
    Expect(first.GlobalInvalidReturnReasons.TryGetValue("MissingEndpoint", out int missing) && missing == 2,
      "Two missing closed endpoints should be reported.");
    Expect(Get(first, MarketTrendMetrics.PriceTrend).EligibleSampleCount == 10, "Coverage denominator must remain ten.");

    // The next scheduled incremental raid supplies only the missing endpoint
    // for one series. Exactly 9/10 now meets the 90% policy.
    store.UpsertCandle(Candle(GlobalSymbols[8], LatestOpen, 1));
    MarketTrendAnalysisSnapshot second = MarketTrendAnalyticsEngine.Calculate(trend, GlobalSymbols, assetSymbols, store, Interval, AsOf);
    Expect(second.ValidGlobalReturnCount == 9, "Second raid should recover one missing endpoint.");
    Expect(Get(second, MarketTrendMetrics.PriceTrend).IsValid, "Exactly 90% must be usable.");
    Expect(Get(second, MarketTrendMetrics.PriceTrend).SampleCount == 9, "Valid numerator must be nine.");
    Expect(Get(second, MarketTrendMetrics.PriceTrend).CoveragePercent == 90.0, "Coverage must report 90%.");
    Expect(Get(second, MarketTrendMetrics.MeanMedianSpread).IsValid, "Ready mean and median should enable spread.");
    Expect(second.AssetMetrics[GlobalSymbols[0]][MarketTrendMetrics.RelativeStrengthMarket].IsValid,
      "Market relative strength should recover when the median is ready.");

    store.UpsertCandle(Candle(GlobalSymbols[9], LatestOpen, 1));
    MarketTrendAnalysisSnapshot third = MarketTrendAnalyticsEngine.Calculate(trend, GlobalSymbols, assetSymbols, store, Interval, AsOf);
    Expect(third.ValidGlobalReturnCount == 10, "Cleanup should restore full coverage.");
    Expect(Get(third, MarketTrendMetrics.PriceTrend).IsValid, "Full coverage must be usable.");
    Expect(third.GlobalInvalidReturnReasons.Count == 0, "No invalid return reasons remain after cleanup.");
    _passed++;
  }

  private static void TestMissingReferenceReason()
  {
    MarketDataStore store = new MarketDataStore(400);
    for (int i = 0; i < GlobalSymbols.Length; i++)
    {
      AddTwoCandles(store, GlobalSymbols[i], includeLatest: true);
    }

    store.RemoveSeries(GlobalSymbols[9], Interval);
    store.UpsertCandle(Candle(GlobalSymbols[9], LatestOpen, 1));

    MarketTrendAnalysisSnapshot result = MarketTrendAnalyticsEngine.Calculate(
      NewTrend("Breadth"), GlobalSymbols, Array.Empty<string>(), store, Interval, AsOf);

    Expect(result.ValidGlobalReturnCount == 9, "The missing reference removes one valid return.");
    Expect(result.GlobalInvalidReturnReasons.TryGetValue("MissingReference", out int count) && count == 1,
      "The reason diagnostic must distinguish missing references from missing endpoints.");
    Expect(Get(result, MarketTrendMetrics.Breadth).IsValid, "90% coverage still permits breadth.");
    _passed++;
  }

  private static void TestConcentrationRequiresCompleteRanking()
  {
    MarketDataStore store = new MarketDataStore(400);
    string[] symbols = Enumerable.Range(1, 12).Select(i => "VOLUME" + i + "USDT").ToArray();
    // Exactly 288 consecutive five-minute closed candles make a 24-hour window.
    DateTimeOffset firstOpen = LatestOpen - TimeSpan.FromMinutes(287 * 5);
    for (int symbolIndex = 0; symbolIndex < symbols.Length; symbolIndex++)
    {
      for (int candleIndex = 0; candleIndex < 288; candleIndex++)
      {
        if (symbolIndex == 11 && candleIndex == 100)
        {
          continue; // Return endpoints exist, but the 24-hour quote-volume window has a gap.
        }

        store.UpsertCandle(Candle(symbols[symbolIndex], firstOpen.AddMinutes(candleIndex * 5),
          candleIndex, quoteVolume: 100.0 + symbolIndex));
      }
    }

    MarketTrend trend = NewTrend("Concentration");
    MarketTrendAnalysisSnapshot incomplete = MarketTrendAnalyticsEngine.Calculate(
      trend, symbols, Array.Empty<string>(), store, Interval, AsOf);
    FeatureValueSnapshot invalidConcentration = Get(incomplete, MarketTrendMetrics.Concentration);
    Expect(!invalidConcentration.IsValid, "11/12 ranked markets must not pass 100% concentration coverage.");
    Expect(invalidConcentration.SampleCount == 11 && invalidConcentration.EligibleSampleCount == 12,
      "Concentration must report ranking coverage, not just valid return coverage.");

    store.UpsertCandle(Candle(symbols[11], firstOpen.AddMinutes(100 * 5), 100, quoteVolume: 111));
    MarketTrendAnalysisSnapshot complete = MarketTrendAnalyticsEngine.Calculate(
      trend, symbols, Array.Empty<string>(), store, Interval, AsOf);
    Expect(Get(complete, MarketTrendMetrics.Concentration).IsValid, "Full ranking must recover when gap is filled.");
    _passed++;
  }

  private static MarketTrend NewTrend(string metrics)
  {
    return new MarketTrend { Name = "TestTrend", Platform = "Exchange", TrendMinutes = 5, Metrics = metrics };
  }

  private static FeatureValueSnapshot Get(MarketTrendAnalysisSnapshot analysis, string metric)
  {
    return analysis.MarketMetrics[metric];
  }

  private static void AddTwoCandles(MarketDataStore store, string symbol, bool includeLatest)
  {
    store.UpsertCandle(Candle(symbol, LatestOpen - Interval, 0));
    if (includeLatest)
    {
      store.UpsertCandle(Candle(symbol, LatestOpen, 1));
    }
  }

  private static MarketCandle Candle(string symbol, DateTimeOffset open, int index, double quoteVolume = 100)
  {
    double close = 100.0 + (index % 10);
    return new MarketCandle
    {
      Symbol = symbol,
      Interval = Interval,
      OpenTimeUtc = open,
      CloseTimeUtc = open + Interval - TimeSpan.FromMilliseconds(1),
      Open = close,
      High = close + 1,
      Low = close - 1,
      Close = close,
      BaseVolume = 1,
      QuoteVolume = quoteVolume,
      TradeCount = 1,
      IsClosed = true
    };
  }

  private static void Expect(bool condition, string explanation)
  {
    if (!condition)
    {
      throw new InvalidOperationException(explanation);
    }
  }
}
