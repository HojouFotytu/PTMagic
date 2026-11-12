using System;
using System.Collections.Generic;
using System.Linq;
using Core.Main.DataObjects.PTMagicData;
using Core.MarketAnalyzer.MarketData;

internal static class Program
{
  private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
  private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
  private static int _passed;

  private static int Main()
  {
    try
    {
      TestATRSeedAndWilder();
      TestAllTrendDurationsAndZeroRange();
      TestVolumeExpansion();
      TestPartialPeriodExcluded();
      TestMissingSourceCandleFailsClosed();
      TestInsufficientHistoryAndMisalignment();
      TestZeroVolumeBaseline();
      TestStaleSeries();
      TestMedianAndBreadthCoverage();
      TestVolumeExpansionBreadthAndCoverage();
      TestVolumeConcentration();
      TestIncompleteVolumePopulation();
      TestRegistryScopesAndSharedIntegration();
      TestTakerFlowAndMissingHistory();
      Console.WriteLine("Phase 1C OHLCV metric tests passed: " + _passed + ".");
      return 0;
    }
    catch (Exception error)
    {
      Console.Error.WriteLine("Phase 1C tests FAILED: " + error);
      return 1;
    }
  }

  private static void TestATRSeedAndWilder()
  {
    MarketDataStore store = NewStore();
    AddBars(store, "AAAUSDT", 15);
    DateTimeOffset asOf = Start.AddMinutes(150).AddSeconds(1);
    TrendPeriodFeatures seed = Derive(store, "AAAUSDT", asOf, needATR: true, needExpansion: false);
    Near(seed.ATRP, 20.0, "ATR(14) seed from 15 complete 10m bars");

    store = NewStore();
    AddBars(store, "AAAUSDT", 16, finalRange: 40);
    asOf = Start.AddMinutes(160).AddSeconds(1);
    TrendPeriodFeatures smoothed = Derive(store, "AAAUSDT", asOf, needATR: true, needExpansion: false);
    Near(smoothed.ATRP, (13 * 20.0 + 40.0) / 14.0, "Wilder ATR(14) recurrence");
    _passed++;
  }

  private static void TestAllTrendDurationsAndZeroRange()
  {
    foreach (int duration in new[] { 5, 60, 120, 240, 480, 720, 1440 })
    {
      MarketDataStore store = NewStore();
      int samples = 15 * (duration / 5);
      AddSourceSequence(store, "AAAUSDT", samples);
      DateTimeOffset asOf = Start.AddMinutes(15 * duration).AddSeconds(1);
      FeatureValueSnapshot atrp = TrendPeriodMetricsCalculator.Calculate(
        store.GetSnapshot("AAAUSDT", Interval, asOf), duration, asOf,
        needATR: true, needVolumeExpansion: false, needLastVolume: false).ATRP;
      Near(atrp, 20.0, "ATRP must follow MarketTrend duration " + duration + "m");
      Assert(atrp.SourceDataTimestampUtc == Start.AddMinutes(15 * duration).AddMilliseconds(-1),
        "ATRP source timestamp must be the latest complete trend-sized endpoint.");
    }

    MarketDataStore flat = NewStore();
    AddBars(flat, "AAAUSDT", 15, baseRange: 0);
    FeatureValueSnapshot zeroATR = Derive(flat, "AAAUSDT",
      Start.AddMinutes(150).AddSeconds(1), needATR: true, needExpansion: false).ATRP;
    Near(zeroATR, 0.0, "Zero true range is a valid ATRP value");
    _passed++;
  }

  private static void TestVolumeExpansion()
  {
    MarketDataStore store = NewStore();
    AddBars(store, "AAAUSDT", 21, finalSourceQuote: 20);
    TrendPeriodFeatures features = Derive(store, "AAAUSDT", Start.AddMinutes(210).AddSeconds(1),
      needATR: true, needExpansion: true);
    Near(features.VolumeExpansion, 2.0, "Volume expansion across 21 complete trend periods");
    Near(features.LastQuoteVolume, 40.0, "Last trend-sized quote volume");
    _passed++;
  }

  private static void TestPartialPeriodExcluded()
  {
    MarketDataStore store = NewStore();
    AddBars(store, "AAAUSDT", 15);
    // The first 5m source candle of the current 10m trend bar is closed,
    // but the current trend bar remains incomplete and cannot affect ATRP.
    AddSource(store, "AAAUSDT", Start.AddMinutes(150), 50, 150, 100);
    TrendPeriodFeatures result = Derive(store, "AAAUSDT",
      Start.AddMinutes(155).AddSeconds(1), needATR: true, needExpansion: false);
    Near(result.ATRP, 20.0, "A partially completed trend bar must be excluded");
    _passed++;
  }

  private static void TestMissingSourceCandleFailsClosed()
  {
    MarketDataStore store = NewStore();
    AddBars(store, "AAAUSDT", 15, skipSourceIndex: 29);
    TrendPeriodFeatures result = Derive(store, "AAAUSDT",
      Start.AddMinutes(150).AddSeconds(1), needATR: true, needExpansion: false);
    Invalid(result.ATRP, "Missing 5m candle in a required trend bar");
    _passed++;
  }

  private static void TestInsufficientHistoryAndMisalignment()
  {
    MarketDataStore store = NewStore();
    AddBars(store, "AAAUSDT", 14);
    FeatureValueSnapshot feature = Derive(store, "AAAUSDT",
      Start.AddMinutes(140).AddSeconds(1), needATR: true, needExpansion: false).ATRP;
    Invalid(feature, "Only 14 trend bars cannot seed ATR(14)");
    Assert(feature.InvalidReason.Contains("15"), "Missing required history count in diagnostic");

    MarketDataSeriesSnapshot snapshot = store.GetSnapshot("AAAUSDT", Interval,
      Start.AddMinutes(140).AddSeconds(1));
    feature = TrendPeriodMetricsCalculator.Calculate(snapshot, 7,
      Start.AddMinutes(140).AddSeconds(1), needATR: true,
      needVolumeExpansion: false, needLastVolume: false).ATRP;
    Invalid(feature, "Unaligned TrendMinutes must fail closed");

    store = NewStore();
    AddSourceSequence(store, "AAAUSDT", 3 * 288);
    feature = TrendPeriodMetricsCalculator.Calculate(
      store.GetSnapshot("AAAUSDT", Interval, Start.AddHours(72).AddSeconds(1)),
      1440, Start.AddHours(72).AddSeconds(1),
      needATR: true, needVolumeExpansion: false, needLastVolume: false).ATRP;
    Invalid(feature, "72h retained history is insufficient for 24h ATR(14)");
    _passed++;
  }

  private static void TestZeroVolumeBaseline()
  {
    MarketDataStore store = NewStore();
    AddBars(store, "AAAUSDT", 21, baseSourceQuote: 0, finalSourceQuote: 20);
    TrendPeriodFeatures result = Derive(store, "AAAUSDT",
      Start.AddMinutes(210).AddSeconds(1), needATR: false, needExpansion: true);
    Invalid(result.VolumeExpansion, "Zero volume baseline must fail closed");
    _passed++;
  }

  private static void TestStaleSeries()
  {
    MarketDataStore store = NewStore();
    AddBars(store, "AAAUSDT", 21);
    TrendPeriodFeatures result = Derive(store, "AAAUSDT",
      Start.AddMinutes(270), needATR: true, needExpansion: true);
    Invalid(result.ATRP, "Stale source ATRP");
    Invalid(result.VolumeExpansion, "Stale source VolumeExpansion");
    _passed++;
  }

  private static void TestMedianAndBreadthCoverage()
  {
    MarketDataStore store = NewStore();
    string[] symbols = Enumerable.Range(0, 10).Select(index => "SYM" + index + "USDT").ToArray();
    for (int index = 0; index < 9; index++)
    {
      AddBars(store, symbols[index], 15, baseRange: 10 + 2 * index);
    }

    MarketTrend trend = new MarketTrend
    {
      Name = "Short", TrendMinutes = 10, ATRPeriod = 14,
      Metrics = "MedianATRP,VolatilityBreadth", ATRPBreadthThreshold = 18
    };
    DateTimeOffset asOf = Start.AddMinutes(150).AddSeconds(1);
    Phase1CFeatureResult result = MarketTrendPhase1CEngine.Calculate(
      trend, MarketTrendMetrics.ParseEnabled(trend), symbols, Array.Empty<string>(), store, Interval, asOf);
    Near(result.GlobalMetrics[MarketTrendMetrics.MedianATRP], 18.0, "Median from 9 available ATRP values");
    Near(result.GlobalMetrics[MarketTrendMetrics.VolatilityBreadth], 4.0 / 9.0 * 100.0,
      "Volatility breadth greater than threshold excludes exact equality");

    // 8/10 usable is below the 90% cross-sectional requirement.
    store = NewStore();
    for (int i = 0; i < 8; i++) AddBars(store, symbols[i], 15);
    result = MarketTrendPhase1CEngine.Calculate(
      trend, MarketTrendMetrics.ParseEnabled(trend), symbols, Array.Empty<string>(), store, Interval, asOf);
    Invalid(result.GlobalMetrics[MarketTrendMetrics.MedianATRP], "Median ATRP below 90% coverage");
    Invalid(result.GlobalMetrics[MarketTrendMetrics.VolatilityBreadth], "Volatility breadth below 90% coverage");
    _passed++;
  }

  private static void TestVolumeExpansionBreadthAndCoverage()
  {
    MarketDataStore store = NewStore();
    string[] symbols = Enumerable.Range(0, 10).Select(index => "VOL" + index + "USDT").ToArray();
    for (int i = 0; i < 9; i++)
    {
      AddBars(store, symbols[i], 21, baseSourceQuote: 10, finalSourceQuote: 20);
    }

    MarketTrend trend = new MarketTrend
    {
      Name = "Short", TrendMinutes = 10, VolumeExpansionLookback = 20,
      Metrics = "MedianVolumeExpansion,VolumeExpansionBreadth,StrongVolumeExpansionBreadth",
      VolumeExpansionBreadthThreshold = 1.0,
      StrongVolumeExpansionThreshold = 1.5
    };
    DateTimeOffset asOf = Start.AddMinutes(210).AddSeconds(1);
    Phase1CFeatureResult result = MarketTrendPhase1CEngine.Calculate(
      trend, MarketTrendMetrics.ParseEnabled(trend), symbols, Array.Empty<string>(), store, Interval, asOf);
    Near(result.GlobalMetrics[MarketTrendMetrics.MedianVolumeExpansion], 2.0,
      "Median volume expansion at 90% coverage");
    Near(result.GlobalMetrics[MarketTrendMetrics.VolumeExpansionBreadth], 100.0,
      "Volume expansion breadth above 1.0x");
    Near(result.GlobalMetrics[MarketTrendMetrics.StrongVolumeExpansionBreadth], 100.0,
      "Strong volume expansion breadth above 1.5x");

    store = NewStore();
    for (int i = 0; i < 8; i++)
    {
      AddBars(store, symbols[i], 21, baseSourceQuote: 10, finalSourceQuote: 20);
    }
    result = MarketTrendPhase1CEngine.Calculate(
      trend, MarketTrendMetrics.ParseEnabled(trend), symbols, Array.Empty<string>(), store, Interval, asOf);
    Invalid(result.GlobalMetrics[MarketTrendMetrics.MedianVolumeExpansion],
      "Median volume expansion below 90% coverage");
    Invalid(result.GlobalMetrics[MarketTrendMetrics.VolumeExpansionBreadth],
      "Volume expansion breadth below 90% coverage");
    Invalid(result.GlobalMetrics[MarketTrendMetrics.StrongVolumeExpansionBreadth],
      "Strong volume expansion breadth below 90% coverage");
    _passed++;
  }

  private static void TestVolumeConcentration()
  {
    MarketDataStore store = NewStore();
    string[] symbols = new[] { "BTCUSDT", "ETHUSDT" }.Concat(
      Enumerable.Range(0, 9).Select(index => "ALT" + index + "USDT")).ToArray();
    foreach (string symbol in symbols) AddBars(store, symbol, 1);

    MarketTrend trend = new MarketTrend
    {
      Name = "Short", TrendMinutes = 10, TopNVolumeCount = 3,
      Metrics = "VolumeConcentration,TopNVolumeShare,BTCVolumeShare,BTCETHVolumeShare"
    };
    Phase1CFeatureResult result = MarketTrendPhase1CEngine.Calculate(
      trend, MarketTrendMetrics.ParseEnabled(trend), symbols, Array.Empty<string>(),
      store, Interval, Start.AddMinutes(10).AddSeconds(1));
    Near(result.GlobalMetrics[MarketTrendMetrics.VolumeConcentration], 100.0 / 11.0, "Volume HHI with equal market shares");
    Near(result.GlobalMetrics[MarketTrendMetrics.TopNVolumeShare], 300.0 / 11.0, "Top3 volume percentage share");
    Near(result.GlobalMetrics[MarketTrendMetrics.BTCVolumeShare], 100.0 / 11.0, "BTC volume percentage share");
    Near(result.GlobalMetrics[MarketTrendMetrics.BTCETHVolumeShare], 200.0 / 11.0, "BTC+ETH volume percentage share");
    _passed++;
  }

  private static void TestIncompleteVolumePopulation()
  {
    MarketDataStore store = NewStore();
    string[] symbols = Enumerable.Range(0, 10).Select(i => "SYM" + i + "USDT").ToArray();
    for (int i = 0; i < 9; i++) AddBars(store, symbols[i], 1);
    MarketTrend trend = new MarketTrend
    {
      Name = "Short", TrendMinutes = 10, TopNVolumeCount = 3,
      Metrics = "TopNVolumeShare,VolumeConcentration"
    };
    Phase1CFeatureResult result = MarketTrendPhase1CEngine.Calculate(
      trend, MarketTrendMetrics.ParseEnabled(trend), symbols, Array.Empty<string>(),
      store, Interval, Start.AddMinutes(10).AddSeconds(1));
    Invalid(result.GlobalMetrics[MarketTrendMetrics.TopNVolumeShare],
      "Volume concentration must require 100% eligible quote volumes");
    Invalid(result.GlobalMetrics[MarketTrendMetrics.VolumeConcentration],
      "HHI must require 100% eligible quote volumes");
    _passed++;
  }

  private static void TestRegistryScopesAndSharedIntegration()
  {
    Assert(MarketTrendMetrics.TryGetScope("ATRP", out MarketTrendMetricScope atrScope)
      && atrScope == MarketTrendMetricScope.Asset, "ATRP must be asset-scoped.");
    Assert(MarketTrendMetrics.TryGetScope("MedianATRP", out MarketTrendMetricScope medianScope)
      && medianScope == MarketTrendMetricScope.Global, "MedianATRP must be global-scoped.");
    Assert(MarketTrendMetrics.TryGetScope("VolumeExpansion", out MarketTrendMetricScope expScope)
      && expScope == MarketTrendMetricScope.Asset, "VolumeExpansion must be asset-scoped.");

    MarketDataStore store = NewStore();
    AddBars(store, "BTCUSDT", 21);
    MarketTrend trend = new MarketTrend
    {
      Name = "Short", TrendMinutes = 10,
      Metrics = "ATRP,VolumeExpansion,MedianATRP,MedianVolumeExpansion"
    };
    MarketTrendAnalysisSnapshot analysis = MarketTrendAnalyticsEngine.Calculate(
      trend, new[] { "BTCUSDT" }, new[] { "BTCUSDT" },
      store, Interval, Start.AddMinutes(210).AddSeconds(1));
    Near(analysis.MarketMetrics[MarketTrendMetrics.MedianATRP], 20.0, "Integrated median ATRP");
    Near(analysis.MarketMetrics[MarketTrendMetrics.MedianVolumeExpansion], 1.0, "Integrated median volume expansion");
    Near(analysis.AssetMetrics["BTCUSDT"][MarketTrendMetrics.ATRP], 20.0, "Integrated asset ATRP");
    Near(analysis.AssetMetrics["BTCUSDT"][MarketTrendMetrics.VolumeExpansion], 1.0, "Integrated asset volume expansion");
    _passed++;
  }

  private static void TestTakerFlowAndMissingHistory()
  {
    MarketDataStore store = NewStore();
    // Ratio-of-sums, not a mean of per-candle ratios: (1 + 45) / (10 + 90).
    store.UpsertCandle(new MarketCandle
    {
      Symbol = "BTCUSDT", Interval = Interval, OpenTimeUtc = Start,
      CloseTimeUtc = Start.AddMinutes(5).AddMilliseconds(-1),
      Open = 100, High = 101, Low = 99, Close = 100,
      BaseVolume = 10, QuoteVolume = 10, TakerBuyQuoteVolume = 1,
      TradeCount = 10, IsClosed = true
    });
    store.UpsertCandle(new MarketCandle
    {
      Symbol = "BTCUSDT", Interval = Interval, OpenTimeUtc = Start.AddMinutes(5),
      CloseTimeUtc = Start.AddMinutes(10).AddMilliseconds(-1),
      Open = 100, High = 101, Low = 99, Close = 100,
      BaseVolume = 90, QuoteVolume = 90, TakerBuyQuoteVolume = 45,
      TradeCount = 90, IsClosed = true
    });
    var enabled = new[] { MarketTrendMetrics.TakerBuyRatio, MarketTrendMetrics.TakerImbalance };
    var now = Start.AddMinutes(10).AddSeconds(1);
    var metrics = TakerFlowFeatureEngine.Calculate(new[] { "BTCUSDT" }, enabled,
      store, Interval, 10, now)["BTCUSDT"];
    Near(metrics[MarketTrendMetrics.TakerBuyRatio], 0.46, "Taker ratio uses total quote-volume weights");
    Near(metrics[MarketTrendMetrics.TakerImbalance], -0.08, "Imbalance equals 2 * buy ratio - 1");

    var trend = new MarketTrend { Name = "M10", TrendMinutes = 10,
      Metrics = "TakerBuyRatio,TakerImbalance" };
    var analysis = MarketTrendAnalyticsEngine.Calculate(trend, new[] { "BTCUSDT" },
      new[] { "BTCUSDT" }, store, Interval, now);
    Near(analysis.AssetMetrics["BTCUSDT"][MarketTrendMetrics.TakerBuyRatio], 0.46,
      "Taker ratio integrates into asset analytics");
    Assert(!analysis.MarketMetrics.ContainsKey(MarketTrendMetrics.TakerBuyRatio),
      "Asset taker metrics must not appear as global metrics.");

    store.UpsertCandle(new MarketCandle
    {
      Symbol = "BTCUSDT", Interval = Interval, OpenTimeUtc = Start.AddMinutes(5),
      CloseTimeUtc = Start.AddMinutes(10).AddMilliseconds(-1),
      Open = 100, High = 101, Low = 99, Close = 100,
      BaseVolume = 90, QuoteVolume = 90, TradeCount = 90, IsClosed = true
    });
    var legacy = TakerFlowFeatureEngine.Calculate(new[] { "BTCUSDT" }, enabled,
      store, Interval, 10, now)["BTCUSDT"];
    Invalid(legacy[MarketTrendMetrics.TakerBuyRatio], "Legacy OHLCV cannot imply taker volume");
    Invalid(legacy[MarketTrendMetrics.TakerImbalance], "Legacy OHLCV cannot imply taker imbalance");
    store.RemoveSeries("BTCUSDT", Interval);
    store.UpsertCandle(new MarketCandle
    {
      Symbol = "BTCUSDT", Interval = Interval, OpenTimeUtc = Start,
      CloseTimeUtc = Start.AddMinutes(5).AddMilliseconds(-1),
      Open = 100, High = 101, Low = 99, Close = 100,
      BaseVolume = 0, QuoteVolume = 0, TakerBuyQuoteVolume = 0,
      TradeCount = 0, IsClosed = true
    });
    store.UpsertCandle(new MarketCandle
    {
      Symbol = "BTCUSDT", Interval = Interval, OpenTimeUtc = Start.AddMinutes(5),
      CloseTimeUtc = Start.AddMinutes(10).AddMilliseconds(-1),
      Open = 100, High = 101, Low = 99, Close = 100,
      BaseVolume = 0, QuoteVolume = 0, TakerBuyQuoteVolume = 0,
      TradeCount = 0, IsClosed = true
    });
    var empty = TakerFlowFeatureEngine.Calculate(new[] { "BTCUSDT" }, enabled,
      store, Interval, 10, now)["BTCUSDT"];
    Invalid(empty[MarketTrendMetrics.TakerBuyRatio], "Zero-volume window is unavailable");
    _passed++;
  }

  private static TrendPeriodFeatures Derive(MarketDataStore store, string symbol,
    DateTimeOffset asOf, bool needATR, bool needExpansion)
  {
    return TrendPeriodMetricsCalculator.Calculate(
      store.GetSnapshot(symbol, Interval, asOf), 10, asOf,
      needATR: needATR, needVolumeExpansion: needExpansion, needLastVolume: true);
  }

  private static MarketDataStore NewStore()
  {
    return new MarketDataStore(10000);
  }

  private static void AddBars(MarketDataStore store, string symbol, int barCount,
    double baseRange = 20, double finalRange = 0,
    double baseSourceQuote = 10, double finalSourceQuote = -1, int skipSourceIndex = -1)
  {
    for (int i = 0; i < barCount * 2; i++)
    {
      if (i == skipSourceIndex) continue;
      bool isFinal = i >= (barCount - 1) * 2;
      double range = isFinal && finalRange > 0 ? finalRange : baseRange;
      double quote = isFinal && finalSourceQuote >= 0 ? finalSourceQuote : baseSourceQuote;
      AddSource(store, symbol, Start.AddMinutes(i * 5), 100 - range / 2.0, 100 + range / 2.0, quote);
    }
  }

  private static void AddSourceSequence(MarketDataStore store, string symbol, int samples)
  {
    for (int i = 0; i < samples; i++)
    {
      AddSource(store, symbol, Start.AddMinutes(i * 5), 90, 110, 10);
    }
  }

  private static void AddSource(MarketDataStore store, string symbol,
    DateTimeOffset open, double low, double high, double quote)
  {
    store.UpsertCandle(new MarketCandle
    {
      Symbol = symbol,
      Interval = Interval,
      OpenTimeUtc = open,
      CloseTimeUtc = open.AddMinutes(5).AddMilliseconds(-1),
      Open = 100, High = high, Low = low, Close = 100,
      BaseVolume = quote, QuoteVolume = quote, TradeCount = 1,
      IsClosed = true
    });
  }

  private static void Near(FeatureValueSnapshot snapshot, double expected, string description)
  {
    Assert(snapshot != null && snapshot.IsValid && snapshot.Value.HasValue,
      description + ": metric unavailable: " + snapshot?.InvalidReason);
    double difference = Math.Abs(snapshot!.Value!.Value - expected);
    Assert(difference < 1e-8, description + ": expected " + expected + ", got " + snapshot.Value.Value);
  }

  private static void Invalid(FeatureValueSnapshot snapshot, string description)
  {
    Assert(snapshot != null && !snapshot.IsValid && !snapshot.Value.HasValue, description + ": expected unavailable.");
  }

  private static void Assert(bool expression, string message)
  {
    if (!expression) throw new InvalidOperationException(message);
  }
}
