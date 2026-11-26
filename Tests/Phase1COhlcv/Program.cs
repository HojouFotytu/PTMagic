using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
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
      TestEconomicVolumeCoverage();
      TestRegistryScopesAndSharedIntegration();
      TestTakerFlowAndMissingHistory();
      TestTakerFailureReasonCategories();
      TestTakerGlobalAggregates();
      TestTakerGlobalIntegration();
      TestPhase3SharedPass();
      TestPhase3UnavailableAndNeutral();
      TestPhase3VerifiedNoTradesAndCoverage();
      TestPhase3H72();
      TestTakerKlineMapper();
      TestPathEfficiencyMath();
      TestPathEfficiencyCoverage();
      TestPathEfficiencyFiatAndValidity();
      TestCurrentLegAndCandidateBreadth();
      TestMedianReturnInPriorATR();
      TestContextualRisk();
      TestMarketTrendDelta();
      TestMarketTrendDeltaCoverageAndTime();
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
    Phase1CFeatureResult published = MarketTrendPhase1CEngine.Calculate(
      trend, MarketTrendMetrics.ParseEnabled(trend), symbols, symbols, store, Interval, asOf);
    Assert(published.AssetMetrics.Count == 10 &&
      published.AssetMetrics.Values.All(item => item.Count == 1 &&
        item.ContainsKey(MarketTrendMetrics.ATRP)),
      "Global ATRP metrics publish the already-calculated per-asset input even when asset ATRP is not selected.");
    Assert(published.AssetMetrics[symbols[0]][MarketTrendMetrics.ATRP].IsValid,
      "Previously evaluated individual ATRP remains available under this trend.");

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
      "Missing volume without comparable history remains unavailable");
    Invalid(result.GlobalMetrics[MarketTrendMetrics.VolumeConcentration],
      "HHI without missing-volume estimation history remains unavailable");
    _passed++;
  }

  private static void TestEconomicVolumeCoverage()
  {
    var store = NewStore();
    string[] symbols = new[] { "BTCUSDT", "ETHUSDT" }.Concat(
      Enumerable.Range(0, 8).Select(i => "ALT" + i + "USDT")).ToArray();
    var trend = new MarketTrend { Name = "Coverage", TrendMinutes = 10, TopNVolumeCount = 3,
      Metrics = "VolumeConcentration,TopNVolumeShare,BTCVolumeShare,BTCETHVolumeShare" };
    var metrics = MarketTrendMetrics.ParseEnabled(trend);
    foreach (string s in symbols.Take(9)) AddBars(store, s, 3);
    AddBars(store, symbols[9], 2, baseSourceQuote: 1);
    var at = Start.AddMinutes(30).AddSeconds(1);
    var current = MarketTrendAnalyticsEngine.Calculate(trend, symbols, Array.Empty<string>(), store, Interval, at, metrics);
    var hhi = current.MarketMetrics[MarketTrendMetrics.VolumeConcentration];
    Near(hhi, 100.0 / 9, "Negligible missing volume accepted");
    Assert(hhi.VolumeCoverage.IsPartial && hhi.SampleCount == 9 && hhi.EligibleSampleCount == 10 &&
      hhi.VolumeCoverage.ActualQuoteVolumeDenominator == 180, "Partial denominator and counts preserved");
    var delta = MarketTrendDeltaEngine.Calculate(trend, metrics, current, store, Interval);
    Near(delta.MarketMetrics[MarketTrendMetrics.VolumeConcentration].Delta, 0, "Matched Delta ignores population-only change");
    Near(delta.MarketMetrics[MarketTrendMetrics.TopNVolumeShare].Delta, 0, "Matched TopN Delta");
    AddBars(store, symbols[9], 2, baseSourceQuote: 100);
    current = MarketTrendAnalyticsEngine.Calculate(trend, symbols, Array.Empty<string>(), store, Interval, at, metrics);
    Invalid(current.MarketMetrics[MarketTrendMetrics.VolumeConcentration], "Dominant missing asset rejected");
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
    Assert(legacy[MarketTrendMetrics.TakerBuyRatio].InvalidReason.Contains("missing or unverified"),
      "Legacy null taker fields must have their own diagnostic reason.");
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
    Assert(empty[MarketTrendMetrics.TakerBuyRatio].InvalidReason.Contains("Zero total quote volume"),
      "Zero-volume history must not be conflated with invalid taker volume.");
    _passed++;
  }

  private static void TestTakerFailureReasonCategories()
  {
    var store = NewStore();
    var now = Start.AddMinutes(10).AddSeconds(1);
    var enabled = new[] { MarketTrendMetrics.TakerBuyRatio, MarketTrendMetrics.TakerImbalance };

    MarketCandle Bar(int index, double quote, double? buy) => new MarketCandle
    {
      Symbol = "DIAGUSDT", Interval = Interval,
      OpenTimeUtc = Start.AddMinutes(index * 5),
      CloseTimeUtc = Start.AddMinutes((index + 1) * 5).AddMilliseconds(-1),
      Open = 100, High = 101, Low = 99, Close = 100,
      QuoteVolume = quote, TakerBuyQuoteVolume = buy,
      BaseVolume = quote == 0 ? 0 : 1, TradeCount = quote == 0 ? 0 : 1, IsClosed = true
    };

    FeatureValueSnapshot Ratio() => TakerFlowFeatureEngine.Calculate(
      new[] { "DIAGUSDT" }, enabled, store, Interval, 10, now)["DIAGUSDT"][MarketTrendMetrics.TakerBuyRatio];

    store.UpsertCandle(Bar(0, 0, 0));
    var missing = Ratio();
    Invalid(missing, "Missing second source candle");
    Assert(missing.InvalidReason.StartsWith("Missing normalized source candle") &&
      missing.SampleCount == 1,
      "A missing source candle must not be labeled zero volume or invalid taker data.");

    store.UpsertCandle(Bar(1, 10, null));
    var unverified = Ratio();
    Invalid(unverified, "Unverified taker source field");
    Assert(unverified.InvalidReason.Contains("missing or unverified") &&
      unverified.SampleCount == 1,
      "A null taker field must be distinguished from missing OHLCV or zero total volume.");

    store.UpsertCandle(Bar(1, 0, 0));
    var zero = Ratio();
    Invalid(zero, "Fully present zero-total-volume period");
    Assert(zero.InvalidReason.StartsWith("Zero total quote volume") &&
      zero.SampleCount == 2 && zero.IsNoTrades &&
      zero.SourceDataTimestampUtc == Start.AddMinutes(10).AddMilliseconds(-1),
      "Only complete zero-volume source periods may be classified NO_TRADES.");

    store.UpsertCandle(new MarketCandle
    {
      Symbol = "DIAGUSDT", Interval = Interval, OpenTimeUtc = Start.AddMinutes(5),
      CloseTimeUtc = Start.AddMinutes(10).AddMilliseconds(-1),
      Open = 100, High = 101, Low = 99, Close = 100,
      BaseVolume = 1, QuoteVolume = 0, TakerBuyQuoteVolume = 0,
      TradeCount = 1, IsClosed = true
    });
    var contradictory = Ratio();
    Invalid(contradictory, "Zero quote volume with trades is inconsistent");
    Assert(!contradictory.IsNoTrades && contradictory.InvalidReason.Contains("conflicts"),
      "Contradictory source volume must remain a genuine warning.");

    store.UpsertCandle(Bar(1, 10, 5));
    Near(Ratio(), 0.5, "A zero-volume constituent candle is valid if the period total is positive");

    // Defensive numeric guard: the normal store rejects invalid taker volume
    // before it reaches this point. Construct an unvalidated read boundary only
    // to test the calculator's independently retained fail-closed diagnostic.
    var badCandles = new[] { Bar(0, 10, 11), Bar(1, 10, 5) };
    var constructor = typeof(MarketDataSeriesSnapshot)
      .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
    var snapshot = (MarketDataSeriesSnapshot)constructor.Invoke(new object[]
    {
      new MarketSeriesKey("DIAGUSDT", Interval), badCandles, now, TimeSpan.FromMinutes(10)
    });
    var calculate = typeof(TakerFlowFeatureEngine).GetMethod(
      "CalculateRatio", BindingFlags.NonPublic | BindingFlags.Static)
      ?? throw new InvalidOperationException("Private taker calculation not found.");
    object[] arguments = { snapshot, 10, now, false, null, null };
    var invalidBuy = (FeatureValueSnapshot)(calculate.Invoke(null, arguments)
      ?? throw new InvalidOperationException("No taker result."));
    Invalid(invalidBuy, "Invalid numeric taker-buy volume");
    Assert(invalidBuy.InvalidReason.StartsWith("Invalid taker-buy quote-volume value"),
      "Invalid numeric taker-buy volume needs its own diagnostic.");

    _passed++;
  }

  private static void TestTakerGlobalAggregates()
  {
    string[] symbols = Enumerable.Range(1, 10)
      .Select(i => "TAKER" + i.ToString("D2") + "USDT").ToArray();
    string[] selected = {
      MarketTrendMetrics.MedianTakerBuyRatio, MarketTrendMetrics.BuyerDominanceBreadth,
      MarketTrendMetrics.StrongBuyerDominanceBreadth, MarketTrendMetrics.TakerImbalanceDispersion
    };
    DateTimeOffset now = Start.AddMinutes(10).AddSeconds(1);
    var samples = new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(
      StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < symbols.Length; i++)
    {
      double r = (i + 1) / 10.0;
      samples[symbols[i]] = new Dictionary<string, FeatureValueSnapshot>
      {
        [MarketTrendMetrics.TakerBuyRatio] = new FeatureValueSnapshot
        {
          CalculatedAtUtc = now, SourceDataTimestampUtc = Start.AddMinutes(10).AddMilliseconds(-1),
          Value = r, IsValid = true, SampleCount = 2
        },
        [MarketTrendMetrics.TakerImbalance] = new FeatureValueSnapshot
        {
          CalculatedAtUtc = now, SourceDataTimestampUtc = Start.AddMinutes(10).AddMilliseconds(-1),
          Value = 2.0 * r - 1.0, IsValid = true, SampleCount = 2
        }
      };
    }
    var got = TakerFlowFeatureEngine.CalculateGlobal(symbols, selected, samples, now);
    Near(got[MarketTrendMetrics.MedianTakerBuyRatio], 0.55, "Median ratio");
    Near(got[MarketTrendMetrics.BuyerDominanceBreadth], 50, "Buyer breadth (strict > 0.5)");
    Near(got[MarketTrendMetrics.StrongBuyerDominanceBreadth], 50,
      "Strong buyer breadth (>= default 0.60)");
    Near(got[MarketTrendMetrics.TakerImbalanceDispersion], Math.Sqrt(0.33),
      "Population SD of -0.8..1.0 imbalance series");
    Assert(got.Values.All(v => v.IsValid && v.SampleCount == 10 &&
      v.EligibleSampleCount == 10 && v.CoveragePercent == 100),
      "Exact global coverage/provenance");
    var custom = TakerFlowFeatureEngine.CalculateGlobal(symbols, selected, samples, now,
      new MarketTrend { StrongBuyerDominanceThreshold = 0.8 });
    Near(custom[MarketTrendMetrics.StrongBuyerDominanceBreadth], 30,
      "Configurable 0.8 inclusive boundary");
    foreach (double bad in new[] { 0.5, 0.49, 1.01, Double.NaN, Double.PositiveInfinity })
    {
      var invalid = TakerFlowFeatureEngine.CalculateGlobal(symbols, selected, samples, now,
        new MarketTrend { StrongBuyerDominanceThreshold = bad });
      Invalid(invalid[MarketTrendMetrics.StrongBuyerDominanceBreadth],
        "Invalid strong buyer thresholds fail closed");
      Near(invalid[MarketTrendMetrics.MedianTakerBuyRatio], 0.55,
        "Invalid strong threshold cannot affect independent aggregate");
    }
    var partial = samples.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
    partial.Remove(symbols[9]);
    var ninety = TakerFlowFeatureEngine.CalculateGlobal(symbols, selected, partial, now);
    Assert(ninety.Values.All(v => v.IsValid && v.SampleCount == 9 &&
      v.EligibleSampleCount == 10 && v.CoveragePercent == 90), "90 percent boundary");
    partial.Remove(symbols[8]);
    var eighty = TakerFlowFeatureEngine.CalculateGlobal(symbols, selected, partial, now);
    Assert(eighty.Values.All(v => !v.IsValid && !v.Value.HasValue &&
      v.SampleCount == 8 && v.EligibleSampleCount == 10), "Below 90 percent unavailable");
    var noImbalance = samples.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
    foreach (string symbol in symbols.Take(2))
      noImbalance[symbol] = new Dictionary<string, FeatureValueSnapshot>
      {
        [MarketTrendMetrics.TakerBuyRatio] = samples[symbol][MarketTrendMetrics.TakerBuyRatio]
      };
    var missing = TakerFlowFeatureEngine.CalculateGlobal(symbols, selected, noImbalance, now);
    Invalid(missing[MarketTrendMetrics.TakerImbalanceDispersion],
      "Distribution requires actual previously computed imbalance inputs");
    Near(missing[MarketTrendMetrics.MedianTakerBuyRatio], 0.55,
      "Missing imbalance must not erase ratio inputs");
    var empty = TakerFlowFeatureEngine.CalculateGlobal(symbols, selected,
      new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(), now);
    Assert(empty.Values.All(v => !v.IsValid && !v.Value.HasValue),
      "No valid source must not yield fabricated zeros");
    _passed++;
  }

  private static void TestTakerGlobalIntegration()
  {
    MarketDataStore store = NewStore();
    string[] globals = { "ALPHAUSDT", "BETAUSDT" };
    string[] assets = { "ALPHAUSDT", "BETAUSDT", "GAMMAUSDT" };
    DateTimeOffset now = Start.AddMinutes(10).AddSeconds(1);
    for (int i = 0; i < assets.Length; i++)
    {
      double ratio = i == 0 ? 0.25 : i == 1 ? 0.75 : 0.9;
      for (int candle = 0; candle < 2; candle++)
      {
        DateTimeOffset opened = Start.AddMinutes(5 * candle);
        store.UpsertCandle(new MarketCandle
        {
          Symbol = assets[i], Interval = Interval,
          OpenTimeUtc = opened, CloseTimeUtc = opened.AddMinutes(5).AddMilliseconds(-1),
          Open = 100, High = 101, Low = 99, Close = 100,
          BaseVolume = 100, QuoteVolume = 100, TakerBuyQuoteVolume = 100 * ratio,
          TradeCount = 10, IsClosed = true
        });
      }
    }
    var trend = new MarketTrend
    {
      Name = "M10", TrendMinutes = 10,
      Metrics = "MedianTakerBuyRatio,BuyerDominanceBreadth,StrongBuyerDominanceBreadth,TakerImbalanceDispersion"
    };
    Assert(TakerFlowFeatureEngine.HasGlobalMetrics(MarketTrendMetrics.ParseEnabled(trend)) &&
      TakerFlowFeatureEngine.HasAnyMetrics(MarketTrendMetrics.ParseEnabled(trend)),
      "Global-only metrics must activate existing taker source");
    var analysis = MarketTrendAnalyticsEngine.Calculate(trend, globals, assets, store, Interval, now);
    Near(analysis.MarketMetrics[MarketTrendMetrics.MedianTakerBuyRatio], 0.5, "Integrated median");
    Near(analysis.MarketMetrics[MarketTrendMetrics.BuyerDominanceBreadth], 50, "Integrated breadth");
    Near(analysis.MarketMetrics[MarketTrendMetrics.StrongBuyerDominanceBreadth], 50,
      "Integrated strong breadth");
    Near(analysis.MarketMetrics[MarketTrendMetrics.TakerImbalanceDispersion], 0.5,
      "Integrated imbalance dispersion");
    Assert(analysis.AssetMetrics.Count == 2 && !analysis.AssetMetrics.ContainsKey(assets[2]) &&
      globals.All(symbol => analysis.AssetMetrics[symbol].Count == 2 &&
        analysis.AssetMetrics[symbol].ContainsKey(MarketTrendMetrics.TakerBuyRatio) &&
        analysis.AssetMetrics[symbol].ContainsKey(MarketTrendMetrics.TakerImbalance)),
      "Global-only taker flow publishes already needed inputs on global cohort only");
    Assert(analysis.EnabledMetrics.Count == 4 &&
      !analysis.EnabledMetrics.Contains(MarketTrendMetrics.TakerBuyRatio) &&
      !analysis.EnabledMetrics.Contains(MarketTrendMetrics.TakerImbalance),
      "Published inputs cannot silently enable triggers");

    var medianOnly = new MarketTrend
    {
      Name = "M10Median", TrendMinutes = 10, Metrics = "MedianTakerBuyRatio"
    };
    var median = MarketTrendAnalyticsEngine.Calculate(medianOnly, globals, assets,
      store, Interval, now);
    Assert(median.AssetMetrics.Count == 2 && median.AssetMetrics.Values.All(value =>
      value.Count == 1 && value.ContainsKey(MarketTrendMetrics.TakerBuyRatio)),
      "Median-only must not calculate unnecessary per-asset imbalance");
    var smsImbalance = TakerFlowFeatureEngine.Calculate(assets,
      new[] { MarketTrendMetrics.TakerImbalance }, store, Interval, 10, now);
    Assert(smsImbalance.Count == 3 && smsImbalance.Values.All(value =>
      value.ContainsKey(MarketTrendMetrics.TakerImbalance) &&
      value.ContainsKey(MarketTrendMetrics.TakerBuyRatio)),
      "SMS imbalance computes ratio already, so display both at no extra source cost");
    var requiredHistory = MarketTrendHistoryRequirements.Describe(
      trend, MarketTrendMetrics.ParseEnabled(trend), Interval, now, 1);
    Assert(requiredHistory.Count == 4 && requiredHistory.All(item =>
      item.SourceDataFamily == "NormalizedOHLCV" &&
      item.CalculationWindowMinutes == 10 &&
      item.MinimumClosedSourceCandles == 2), "Global taker history matches asset taker history");
    store.RemoveSeries(globals[1], Interval);
    var incomplete = MarketTrendAnalyticsEngine.Calculate(trend, globals, assets,
      store, Interval, now);
    Assert(incomplete.MarketMetrics.Values.All(value => !value.IsValid &&
      !value.Value.HasValue), "Insufficient fixed global taker coverage fails closed");
    _passed++;
  }

  private static void AddFlowBars(MarketDataStore store, string name,
    double priceClose, double quote, double buyRatio, bool verified = true)
  {
    for (int i = 0; i < 2; ++i)
    {
      var time = Start.AddMinutes(i * 5);
      double close = i == 1 ? priceClose : 100;
      store.UpsertCandle(new MarketCandle
      {
        Symbol = name, Interval = Interval, OpenTimeUtc = time,
        CloseTimeUtc = time.AddMinutes(5).AddMilliseconds(-1),
        Open = 100, Close = close, High = Math.Max(100, close) + 1,
        Low = Math.Min(100, close) - 1,
        QuoteVolume = quote / 2, TakerBuyQuoteVolume = verified ? quote * buyRatio / 2 : null,
        BaseVolume = 1, TradeCount = 1, IsClosed = true
      });
    }
  }

  private static void TestPhase3SharedPass()
  {
    var store = NewStore();
    string[] symbols = { "AAAUSDT", "BBBUSDT", "CCCUSDT", "DDDUSDT" };
    AddFlowBars(store, symbols[0], 110, 100, 0.75);
    AddFlowBars(store, symbols[1], 90, 200, 0.25);
    AddFlowBars(store, symbols[2], 90, 300, 0.75);
    AddFlowBars(store, symbols[3], 110, 400, 0.25);
    var trend = new MarketTrend
    {
      Name = "M10Phase3", TrendMinutes = 10,
      Metrics = "BuyerFlowPriceConfirmation,SellerFlowPriceConfirmation," +
        "BuyerFlowPriceDivergence,SellerFlowPriceDivergence," +
        "BuyerFlowPriceConfirmationVolumeShare,SellerFlowPriceConfirmationVolumeShare," +
        "BuyerFlowPriceDivergenceVolumeShare,SellerFlowPriceDivergenceVolumeShare"
    };
    var now = Start.AddMinutes(10).AddSeconds(1);
    var a = MarketTrendAnalyticsEngine.Calculate(trend, symbols, symbols, store, Interval, now);
    Near(a.MarketMetrics[MarketTrendMetrics.BuyerFlowPriceConfirmationVolumeShare], 5,
      "Buyer/up represents 50 of 1000 quote units");
    Near(a.MarketMetrics[MarketTrendMetrics.SellerFlowPriceConfirmationVolumeShare], 10,
      "Seller/down represents 100 of 1000 quote units");
    Near(a.MarketMetrics[MarketTrendMetrics.BuyerFlowPriceDivergenceVolumeShare], 15,
      "Buyer/down represents 150 of 1000 quote units");
    Near(a.MarketMetrics[MarketTrendMetrics.SellerFlowPriceDivergenceVolumeShare], 20,
      "Seller/up represents 200 of 1000 quote units");
    Near(a.AssetMetrics[symbols[0]][MarketTrendMetrics.BuyerFlowPriceConfirmation], 1, "Buyer confirmation flag");
    Near(a.AssetMetrics[symbols[1]][MarketTrendMetrics.SellerFlowPriceConfirmation], 1, "Seller confirmation flag");
    Near(a.AssetMetrics[symbols[2]][MarketTrendMetrics.BuyerFlowPriceDivergence], 1, "Buyer divergence flag");
    Near(a.AssetMetrics[symbols[3]][MarketTrendMetrics.SellerFlowPriceDivergence], 1, "Seller divergence flag");
    Near(a.AssetMetrics[symbols[0]][MarketTrendMetrics.BuyerFlowPriceDivergence], 0, "Nonmatch is valid zero");
    Assert(!a.EnabledMetrics.Contains(MarketTrendMetrics.TakerBuyRatio) &&
      a.AssetMetrics[symbols[0]].ContainsKey(MarketTrendMetrics.TakerBuyRatio),
      "Already calculated dependency does not become a trigger-enabled metric.");
    Assert(a.AssetMetrics[symbols[0]][MarketTrendMetrics.BuyerFlowPriceConfirmation]
      .SourceDataTimestampUtc == Start.AddMinutes(10).AddMilliseconds(-1),
      "Aligned source must end at exact UTC period close.");
    store.UpsertCandle(new MarketCandle
    {
      Symbol = symbols[0], Interval = Interval,
      OpenTimeUtc = Start.AddMinutes(10),
      CloseTimeUtc = Start.AddMinutes(15).AddMilliseconds(-1),
      Open = 110, Close = 1000, High = 1001, Low = 109,
      QuoteVolume = 90000, TakerBuyQuoteVolume = 0,
      BaseVolume = 1, TradeCount = 1, IsClosed = true
    });
    var next = MarketTrendAnalyticsEngine.Calculate(trend, symbols, symbols, store,
      Interval, Start.AddMinutes(15).AddSeconds(1));
    Near(next.MarketMetrics[MarketTrendMetrics.BuyerFlowPriceConfirmationVolumeShare], 5,
      "Incomplete following UTC period must be excluded.");
    _passed++;
  }

  private static void TestPhase3UnavailableAndNeutral()
  {
    var store = NewStore();
    string[] symbols = { "AAAUSDT", "BBBUSDT" };
    AddFlowBars(store, symbols[0], 100, 100, 0.5);
    AddFlowBars(store, symbols[1], 110, 100, 0.75);
    var trend = new MarketTrend { Name = "M10Flow", TrendMinutes = 10,
      Metrics = "BuyerFlowPriceConfirmation,BuyerFlowPriceConfirmationVolumeShare" };
    var now = Start.AddMinutes(10).AddSeconds(1);
    var valid = MarketTrendAnalyticsEngine.Calculate(trend, symbols, symbols, store, Interval, now);
    Near(valid.AssetMetrics[symbols[0]][MarketTrendMetrics.BuyerFlowPriceConfirmation], 0,
      "Neutral is a valid nonmatch");
    Near(valid.MarketMetrics[MarketTrendMetrics.BuyerFlowPriceConfirmationVolumeShare], 25,
      "Net aggressive buying volume / overall quote volume");
    AddFlowBars(store, symbols[1], 110, 100, 0.75, false);
    var missing = MarketTrendAnalyticsEngine.Calculate(trend, symbols, symbols, store, Interval, now);
    Invalid(missing.AssetMetrics[symbols[1]][MarketTrendMetrics.BuyerFlowPriceConfirmation],
      "Missing taker data cannot be treated as zero");
    Invalid(missing.MarketMetrics[MarketTrendMetrics.BuyerFlowPriceConfirmationVolumeShare],
      "Global quote volume shares require 100% eligible coverage");
    Assert(missing.MarketMetrics[MarketTrendMetrics.BuyerFlowPriceConfirmationVolumeShare]
      .EligibleSampleCount == 2, "Fixed market denominator must be retained.");
    _passed++;
  }

  private static void TestPhase3VerifiedNoTradesAndCoverage()
  {
    var store = NewStore();
    string[] symbols = Enumerable.Range(1, 10)
      .Select(i => "COVER" + i.ToString("D2") + "USDT").ToArray();
    foreach (string symbol in symbols)
      AddFlowBars(store, symbol, 110, 100, 0.75);

    var trend = new MarketTrend
    {
      Name = "M10Coverage", TrendMinutes = 10,
      Metrics = "TakerBuyRatio,TakerImbalance,BuyerFlowPriceConfirmation," +
        "BuyerFlowPriceConfirmationVolumeShare"
    };
    var now = Start.AddMinutes(10).AddSeconds(1);
    FeatureValueSnapshot Global() =>
      MarketTrendAnalyticsEngine.Calculate(trend, symbols, symbols, store, Interval, now)
        .MarketMetrics[MarketTrendMetrics.BuyerFlowPriceConfirmationVolumeShare];

    // One missing asset: exactly 90% observed, using only the 9 valid
    // source volumes as numerator and denominator (never imputing zero).
    AddFlowBars(store, symbols[9], 110, 100, 0.75, verified: false);
    var ninety = Global();
    Near(ninety, 50, "Phase 3 global volume share accepts 90% valid coverage");
    Assert(ninety.SampleCount == 9 && ninety.EligibleSampleCount == 10 &&
      ninety.CoveragePercent == 90, "Global coverage retains the 10-asset cohort.");
    AddFlowBars(store, symbols[8], 110, 100, 0.75, verified: false);
    Invalid(Global(), "80% valid observations must fail the 90% threshold");

    // Verified inactivity restores one complete source observation but
    // does not invent a taker-buy ratio or an active-volume contribution.
    for (int i = 0; i < 2; i++)
    {
      var opened = Start.AddMinutes(i * 5);
      store.UpsertCandle(new MarketCandle
      {
        Symbol = symbols[9], Interval = Interval,
        OpenTimeUtc = opened, CloseTimeUtc = opened.AddMinutes(5).AddMilliseconds(-1),
        Open = 100, High = 100, Low = 100, Close = 100,
        BaseVolume = 0, QuoteVolume = 0, TakerBuyQuoteVolume = 0,
        TradeCount = 0, IsClosed = true
      });
    }
    var mixed = MarketTrendAnalyticsEngine.Calculate(trend, symbols, symbols, store, Interval, now);
    Near(mixed.MarketMetrics[MarketTrendMetrics.BuyerFlowPriceConfirmationVolumeShare], 50,
      "Eight traded and one verified inactive markets satisfy 90% coverage");
    Assert(mixed.MarketMetrics[MarketTrendMetrics.BuyerFlowPriceConfirmationVolumeShare].SampleCount == 9,
      "Zero-volume verified market counts as complete data, not fabricated activity.");
    foreach (string name in new[] { MarketTrendMetrics.TakerBuyRatio,
      MarketTrendMetrics.TakerImbalance, MarketTrendMetrics.BuyerFlowPriceConfirmation })
    {
      var feature = mixed.AssetMetrics[symbols[9]][name];
      Invalid(feature, "No-trade " + name + " must stay undefined");
      Assert(feature.IsNoTrades, "Per-asset NO_TRADES marker must propagate to " + name + ".");
    }

    // A contradictory source candle must not satisfy the coverage threshold.
    store.UpsertCandle(new MarketCandle
    {
      Symbol = symbols[9], Interval = Interval,
      OpenTimeUtc = Start.AddMinutes(5),
      CloseTimeUtc = Start.AddMinutes(10).AddMilliseconds(-1),
      Open = 100, High = 100, Low = 100, Close = 100,
      BaseVolume = 1, QuoteVolume = 0, TakerBuyQuoteVolume = 0,
      TradeCount = 1, IsClosed = true
    });
    Invalid(Global(), "One missing and one contradictory market leaves 80% coverage");

    // All complete sources with zero volume: no global 0/0 value.
    foreach (string symbol in symbols)
      for (int i = 0; i < 2; i++)
      {
        var opened = Start.AddMinutes(i * 5);
        store.UpsertCandle(new MarketCandle
        {
          Symbol = symbol, Interval = Interval,
          OpenTimeUtc = opened, CloseTimeUtc = opened.AddMinutes(5).AddMilliseconds(-1),
          Open = 100, High = 100, Low = 100, Close = 100,
          BaseVolume = 0, QuoteVolume = 0, TakerBuyQuoteVolume = 0,
          TradeCount = 0, IsClosed = true
        });
      }
    var idle = Global();
    Invalid(idle, "A zero global denominator is unavailable even at full coverage");
    Assert(idle.SampleCount == 10 && idle.EligibleSampleCount == 10 &&
      idle.InvalidReason.Contains("denominator"),
      "100% source coverage must not fabricate a zero-volume global ratio.");
    _passed++;
  }

  private static void TestPhase3H72()
  {
    var store = NewStore();
    // UTC H72 boundaries are 72-hour multiples of the Unix epoch, not
    // necessarily midnight on the test's fixed Start date (2026-11-01).
    const int trendMinutes = 4320;
    long periodMs = checked((long)TimeSpan.FromMinutes(trendMinutes).TotalMilliseconds);
    DateTimeOffset alignedStart = DateTimeOffset.FromUnixTimeMilliseconds(
      Start.ToUnixTimeMilliseconds() / periodMs * periodMs);
    for (int i = 0; i < 864; i++)
    {
      var opened = alignedStart.AddMinutes(i * 5);
      store.UpsertCandle(new MarketCandle
      {
        Symbol = "AAAUSDT", Interval = Interval,
        OpenTimeUtc = opened, CloseTimeUtc = opened.AddMinutes(5).AddMilliseconds(-1),
        Open = 100, Close = i == 863 ? 120 : 100,
        High = 121, Low = 99,
        QuoteVolume = 10, TakerBuyQuoteVolume = 6,
        BaseVolume = 1, TradeCount = 1, IsClosed = true
      });
    }
    var trend = new MarketTrend { Name = "H72Flow", TrendMinutes = 4320,
      Metrics = "BuyerFlowPriceConfirmationVolumeShare" };
    var a = MarketTrendAnalyticsEngine.Calculate(trend,
      new[] { "AAAUSDT" }, new[] { "AAAUSDT" }, store, Interval,
      alignedStart.AddMinutes(trendMinutes).AddSeconds(1), configuredStoreDataMaxHours: 96);
    Near(a.MarketMetrics[MarketTrendMetrics.BuyerFlowPriceConfirmationVolumeShare], 20,
      "H72 uses one complete period of already traversed taker data");
    Assert(a.HistoryRequirements.Single().HasCurrentRaidCapacity == true,
      "H72 must not acquire unrelated 20-period quote-volume lookback");
    _passed++;
  }

  private static void TestTakerKlineMapper()
  {
    Type mapper = typeof(MarketCandle).Assembly.GetType("Core.MarketAnalyzer.MarketData.BinanceKlineClient")
      ?? throw new InvalidOperationException("Existing kline mapper type is missing.");
    MethodInfo map = mapper.GetMethod("MapKlines", BindingFlags.Static | BindingFlags.NonPublic)
      ?? throw new InvalidOperationException("Existing kline mapper method is missing.");
    long opened = Start.ToUnixTimeMilliseconds();
    long closed = Start.AddMinutes(5).ToUnixTimeMilliseconds() - 1;
    JArray raw = JArray.FromObject(new object[]
    {
      new object[] { opened, "100", "101", "99", "100", "10", closed, "100", 20, "4", "35", "0" },
      new object[] { opened + 300000, "100", "101", "99", "100", "10", closed + 300000, "100", 20, "4", "invalid", "0" },
      new object[] { opened + 600000, "100", "101", "99", "100", "10", closed + 600000, "100", 20 }
    });
    var candles = (List<MarketCandle>)(map.Invoke(null, new object[]
      { "BTCUSDT", Interval, raw, closed + 900000 })
      ?? throw new InvalidOperationException("Binance kline mapper returned no candles."));
    Assert(candles.Count == 3 && candles[0].QuoteVolume == 100 &&
      candles[0].TakerBuyQuoteVolume == 35,
      "Binance index 10 is taker-buy quote volume; index 9 is base volume.");
    Assert(candles[1].QuoteVolume == 100 && !candles[1].TakerBuyQuoteVolume.HasValue &&
      candles[2].QuoteVolume == 100 && !candles[2].TakerBuyQuoteVolume.HasValue,
      "Malformed or absent taker fields must not fabricate zero or destroy valid OHLCV.");
    _passed++;
  }


  private static void AddPathCandles(MarketDataStore store, string symbol,
    IReadOnlyList<double> closes, int skip = -1)
  {
    for (int i = 0; i < closes.Count; i++)
    {
      if (i == skip) continue;
      double close = closes[i];
      store.UpsertCandle(new MarketCandle
      {
        Symbol = symbol, Interval = Interval,
        OpenTimeUtc = Start.AddMinutes(i * 5),
        CloseTimeUtc = Start.AddMinutes((i + 1) * 5).AddMilliseconds(-1),
        Open = close, High = close, Low = close, Close = close,
        BaseVolume = 1, QuoteVolume = 1, TradeCount = 1, IsClosed = true
      });
    }
  }

  private static FeatureValueSnapshot PathValue(MarketDataStore store, string symbol,
    int minutes, DateTimeOffset at, string currency = "Market", string quote = "USDT")
  {
    var trend = new MarketTrend { Name = "PathTest", TrendMinutes = minutes,
      TrendCurrency = currency, Metrics = "PricePathEfficiency" };
    return PricePathEfficiencyEngine.Calculate(
      trend, MarketTrendMetrics.ParseEnabled(trend), Array.Empty<string>(),
      new[] { symbol }, store, Interval, at, quote)
      .AssetMetrics[symbol][MarketTrendMetrics.PricePathEfficiency];
  }

  private static void TestPathEfficiencyMath()
  {
    var store = NewStore();
    AddPathCandles(store, "MONOUSDT", new[] { 100d, 101d, 102d, 103d, 104d });
    AddPathCandles(store, "BACKUSDT", new[] { 100d, 102d, 99d, 103d, 100d });
    AddPathCandles(store, "FLATUSDT", new[] { 100d, 100d, 100d, 100d, 100d });
    var now = Start.AddMinutes(25).AddSeconds(1);
    Near(PathValue(store, "MONOUSDT", 20, now), 1,
      "Monotone directness is 1");
    Near(PathValue(store, "BACKUSDT", 20, now), 0,
      "Return-to-origin path directness is zero");
    Invalid(PathValue(store, "FLATUSDT", 20, now),
      "All-flat paths have an undefined denominator");
    Invalid(PathValue(store, "MONOUSDT", 5, now),
      "One price step cannot describe a path");
    Near(PathValue(store, "MONOUSDT", 15, now), 1,
      "Arbitrary configured valid 15m duration");
    _passed++;
  }

  private static void TestPathEfficiencyCoverage()
  {
    var now = Start.AddMinutes(25).AddSeconds(1);
    var trend = new MarketTrend { Name = "PathGlobal", TrendMinutes = 20,
      Metrics = "MedianPricePathEfficiency" };
    var symbols = Enumerable.Range(0, 10)
      .Select(i => "PATH" + i.ToString("D2") + "USDT").ToArray();
    MarketDataStore Build(int missing)
    {
      var store = NewStore();
      foreach (string symbol in symbols)
        AddPathCandles(store, symbol,
          new[] { 100d, 102d, 101d, 103d, 104d },
          skip: Array.IndexOf(symbols, symbol) >= 10 - missing ? 2 : -1);
      return store;
    }
    var full = MarketTrendAnalyticsEngine.Calculate(
      trend, symbols, symbols, Build(0), Interval, now);
    double expected = Math.Abs(Math.Log(104d / 100d)) /
      (Math.Abs(Math.Log(102d / 100d)) + Math.Abs(Math.Log(101d / 102d)) +
       Math.Abs(Math.Log(103d / 101d)) + Math.Abs(Math.Log(104d / 103d)));
    Near(full.MarketMetrics[MarketTrendMetrics.MedianPricePathEfficiency], expected,
      "Median across eligible asset path values");
    Assert(full.AssetMetrics.Count == 10 &&
      full.AssetMetrics.Values.All(m => m.ContainsKey(MarketTrendMetrics.PricePathEfficiency)),
      "Global-only mode publishes already-calculated per-asset values");
    Assert(!full.EnabledMetrics.Contains(MarketTrendMetrics.PricePathEfficiency),
      "Display-only dependency does not enable an asset trigger.");
    var ninety = MarketTrendAnalyticsEngine.Calculate(trend,
      symbols, symbols, Build(1), Interval, now)
      .MarketMetrics[MarketTrendMetrics.MedianPricePathEfficiency];
    Near(ninety, expected, "90% global coverage passes");
    Assert(ninety.SampleCount == 9 && ninety.EligibleSampleCount == 10,
      "Global 90% coverage preserves fixed cohort");
    var eighty = MarketTrendAnalyticsEngine.Calculate(trend,
      symbols, symbols, Build(2), Interval, now)
      .MarketMetrics[MarketTrendMetrics.MedianPricePathEfficiency];
    Invalid(eighty, "80% global coverage fails");
    Assert(eighty.EligibleSampleCount == 10,
      "Invalid observations cannot shrink global denominator");
    var assetOnly = new MarketTrend { Name = "AssetOnly", TrendMinutes = 20,
      Metrics = "PricePathEfficiency" };
    var asset = MarketTrendAnalyticsEngine.Calculate(assetOnly,
      symbols, symbols, Build(0), Interval, now);
    Assert(!asset.MarketMetrics.ContainsKey(MarketTrendMetrics.MedianPricePathEfficiency)
      && asset.AssetMetrics.Count == 10, "Asset-only selection stays independent");
    _passed++;
  }

  private static void TestPathEfficiencyFiatAndValidity()
  {
    var store = NewStore();
    AddPathCandles(store, "BTCETH", new[] { 100d, 105d, 100d, 105d, 100d });
    AddPathCandles(store, "ETHUSDT", new[] { 1d, 1.1d, 1.2d, 1.3d, 1.4d });
    var now = Start.AddMinutes(25).AddSeconds(1);
    Near(PathValue(store, "BTCETH", 20, now, "Fiat", "ETH"), 1,
      "Synchronized Fiat conversion must use every intermediate close");
    Invalid(PathValue(store, "BTCETH", 20, now, "Fiat", "UNKNOWN"),
      "Missing quote conversion is unavailable");
    var gap = NewStore();
    AddPathCandles(gap, "BTCETH", new[] { 100d, 105d, 100d, 105d, 100d });
    AddPathCandles(gap, "ETHUSDT", new[] { 1d, 1.1d, 1.2d, 1.3d, 1.4d }, skip: 2);
    Invalid(PathValue(gap, "BTCETH", 20, now, "Fiat", "ETH"),
      "Gap in conversion path fails closed");
    Invalid(PathValue(store, "BTCETH", 20, now.AddMinutes(30), "Fiat", "ETH"),
      "Stale source/conversion fails closed");
    Invalid(PathValue(store, "BTCETH", 30, now),
      "Missing earliest history is unavailable");
    store.UpsertCandle(new MarketCandle
    {
      Symbol = "BTCETH", Interval = Interval,
      OpenTimeUtc = Start.AddMinutes(25),
      CloseTimeUtc = Start.AddMinutes(30).AddMilliseconds(-1),
      Open = 100, High = 1000, Low = 1, Close = 1000,
      BaseVolume = 1, QuoteVolume = 1, TradeCount = 1, IsClosed = false
    });
    Near(PathValue(store, "BTCETH", 20, now, "Fiat", "ETH"), 1,
      "Currently forming candle must be excluded");
    _passed++;
  }

  private static void TestCurrentLegAndCandidateBreadth()
  {
    var names = new[] { "AAAUSDT", "BBBUSDT", "CCCUSDT", "DDDUSDT" };
    var turn = new[] { 80d, 90d, 95d, 105d };
    var trend = new MarketTrend { Name = "Leg", TrendMinutes = 10, ATRPeriod = 1, Metrics =
      "DrawdownSizeInATR,DrawdownRecoveryPercent,DrawdownParticipationRecoveryPercent,DrawdownLeaderOverlapPercent,"
      + "RallySizeInATR,RallyRetracementPercent,RallyParticipationReversalPercent,RallyLeaderOverlapPercent,DrawdownLeaderOverlapExcess,RallyLeaderOverlapExcess,"
      + "MedianReturnInATR,ReturnInATR,RelativeStrengthInATR" };
    MarketTrendAnalysisSnapshot Build(double[] end, bool mirror = false, bool gap = false)
    {
      var store = NewStore();
      for (int i = 0; i < names.Length; i++)
      {
        var path = new[] { 100d, 100d, 100d, 100d, 100d, turn[i], end[i] };
        if (mirror) path = path.Select(price => 200 - price).ToArray();
        AddPathCandles(store, names[i], path, gap && i == 0 ? 5 : -1);
      }
      return MarketTrendAnalyticsEngine.Calculate(trend, names, names, store, Interval, Start.AddMinutes(35).AddSeconds(1));
    }
    var recovered = new[] { 95d, 95d, 94d, 101d };
    var down = Build(recovered);
    Near(down.MarketMetrics["DrawdownLeaderOverlapExcess"], 75, "observed 100% overlap minus actual 25% marginal incidence");
    Near(Build(recovered, true).MarketMetrics["RallyLeaderOverlapExcess"], 75, "mirrored matched leader incidence");
    Near(down.MarketMetrics["DrawdownRecoveryPercent"], 50, "exact 50% of the common originating decline recovered");
    Near(down.MarketMetrics["DrawdownParticipationRecoveryPercent"], 200.0 / 3, "two advancing vs three originally declining");
    Near(down.MarketMetrics["DrawdownLeaderOverlapPercent"], 100, "largest responding symbol also led original decline");
    Assert(down.MarketMetrics["DrawdownSizeInATR"].Value > 0, "swing size normalized by same-horizon median ATRP");
    Near(down.MarketMetrics["MedianReturnInATR"], -5 / down.MarketMetrics["MedianATRP"].Value!.Value, "continuous signed shock scale");
    double atr = down.AssetMetrics[names[2]]["ATRP"].Value!.Value;
    Near(down.AssetMetrics[names[2]]["ReturnInATR"], -6 / atr, "own rolling return in units of own ATR");
    Near(down.AssetMetrics[names[2]]["RelativeStrengthInATR"], -1 / atr, "median-excess return in units of own ATR");
    Assert(!down.EnabledMetrics.Contains("ATRP"), "internal ATR dependency does not enable a standalone trigger");
    var up = Build(recovered, mirror: true);
    Near(up.MarketMetrics["RallyRetracementPercent"], 50, "half of original common rally retraced");
    Near(up.MarketMetrics["RallyParticipationReversalPercent"], 200.0 / 3, "mirror downward participation");
    Near(up.MarketMetrics["RallyLeaderOverlapPercent"], 100, "same rally leader now declining");
    Near(Build(new[] { 81d, 100d, 104d, 100d }).MarketMetrics["DrawdownLeaderOverlapPercent"], 0,
      "equal market recovery with a different leader");
    Near(Build(turn).MarketMetrics["DrawdownRecoveryPercent"], 0, "fresh extreme has observed zero recovery");
    Invalid(Build(recovered, gap: true).MarketMetrics["DrawdownRecoveryPercent"], "three complete paths out of four fail coverage");

    turn[1] = 80;
    Near(Build(recovered).MarketMetrics["DrawdownLeaderOverlapExcess"], 50,
      "two tied origin leaders among four make incidental overlap 50 percent");
    Near(Build(recovered, true).MarketMetrics["RallyLeaderOverlapExcess"], 50,
      "the measured tied origin incidence also applies to rallies");

    // A fresh, smaller excursion after complete recovery replaces the prior large decline.
    var recoveredMarket = NewStore();
    AddPathCandles(recoveredMarket, names[0], new[] { 100d, 60d, 100d, 96d, 98d });
    var longer = new MarketTrend { Name = "Current", TrendMinutes = 20, Metrics = "DrawdownRecoveryPercent" };
    var one = new[] { names[0] };
    var current = MarketTrendAnalyticsEngine.Calculate(longer, one, one, recoveredMarket, Interval, Start.AddMinutes(25));
    Near(current.MarketMetrics["DrawdownRecoveryPercent"], 50, "use the new 100 to 96 decline after the old one was reclaimed");
    var monotonic = NewStore();
    AddPathCandles(monotonic, names[0], new[] { 100d, 101d, 102d, 103d, 104d });
    var rising = MarketTrendAnalyticsEngine.Calculate(longer, one, one, monotonic, Interval, Start.AddMinutes(25));
    Invalid(rising.MarketMetrics["DrawdownRecoveryPercent"], "no originating decline makes recovery undefined");

    var store = NewStore();
    var ranked = Enumerable.Range(0, 100).Select(i => "R" + (100-i).ToString("D3") + "USDT").ToArray();
    for (int i = 0; i < ranked.Length; i++)
      AddPathCandles(store, ranked[i], new[] { 100d, i < 50 ? 99d : 102d });
    var ranks = new MarketTrend { Name = "Liquid", TrendMinutes = 5, Metrics = "Top50Breadth,Top75Breadth,Top100Breadth,Breadth" };
    var observed = MarketTrendAnalyticsEngine.Calculate(ranks, ranked, ranked, store, Interval, Start.AddMinutes(10));
    Near(observed.MarketMetrics["Top50Breadth"], 0, "retain turnover order instead of alphabetical order");
    Near(observed.MarketMetrics["Top75Breadth"], 100.0 / 3, "nested top 75 breadth");
    Near(observed.MarketMetrics["Top100Breadth"], 50, "top 100 breadth");
    Near(observed.MarketMetrics["Breadth"], 50, "preserve broad breadth alongside candidate group");
    foreach (var symbol in ranked.Take(6)) store.RemoveSeries(symbol, Interval);
    var sparse = MarketTrendAnalyticsEngine.Calculate(ranks, ranked, ranked, store, Interval, Start.AddMinutes(10));
    Invalid(sparse.MarketMetrics["Top50Breadth"], "do not replace six missing ranked names with lower-turnover assets");
    Assert(sparse.MarketMetrics["Top50Breadth"].EligibleSampleCount == 50 && sparse.MarketMetrics["Top50Breadth"].SampleCount == 44,
      "record the within-cohort numerator and original denominator");
    Near(sparse.MarketMetrics["Top100Breadth"], 100d * 50 / 94, "larger cohort has adequate independent coverage");
    _passed++;
  }

  private static void TestMedianReturnInPriorATR()
  {
    var trend = new MarketTrend { Name = "PriorShock", TrendMinutes = 10, ATRPeriod = 1, Metrics = "MedianReturnInPriorATR" };
    const string metric = MarketTrendMetrics.MedianReturnInPriorATR;
    var asOf = Start.AddMinutes(35).AddSeconds(1);
    FeatureValueSnapshot Score(double priorRange, double shockRange, double end = 94, bool gap = false)
    {
      var store = NewStore();
      const string symbol = "SCALEUSDT";
      for (int i = 0; i < 7; i++)
      {
        if (i == 2 && gap) continue;
        double close = i < 5 ? 100 : end;
        double range = i < 4 ? priorRange : shockRange;
        store.UpsertCandle(new MarketCandle
        {
          Symbol = symbol, Interval = Interval, OpenTimeUtc = Start.AddMinutes(5 * i),
          CloseTimeUtc = Start.AddMinutes(5 * (i + 1)).AddMilliseconds(-1),
          Open = close, Close = close, High = close + range / 2, Low = close - range / 2,
          BaseVolume = 1, QuoteVolume = 1, TradeCount = 1, IsClosed = true
        });
      }
      // Only this frozen Global member may enter either median.
      AddPathCandles(store, "EXCLUDEDUSDT", new[] { 10d, 80d, 1d, 99d, 100d, 190d, 190d });
      var result = MarketTrendAnalyticsEngine.Calculate(trend, new[] { symbol }, new[] { symbol, "EXCLUDEDUSDT" },
        store, Interval, asOf);
      Assert(!result.EnabledMetrics.Contains(MarketTrendMetrics.MedianATRP), "a prior ATR dependency is not a standalone trigger");
      return result.MarketMetrics[metric];
    }
    var down = Score(2, 8);
    Near(down, -3, "six-percent drop is three prior ATRs when prior ATRP is 2%");
    Near(Score(2, 80), -3, "widening current or incomplete reference-period ranges cannot dilute the shock");
    Near(Score(8, 80), -.75, "identical drop against a four times wider prior baseline");
    Near(Score(2, 80, 106), 3, "upward shocks retain positive sign");
    Invalid(Score(0, 80), "zero prior volatility cannot supply a ratio");
    Invalid(Score(2, 80, gap: true), "missing contiguous prior trend period cannot be filled using the shock");
    Assert(down.CalculatedAtUtc == asOf && down.SourceDataTimestampUtc == Start.AddMinutes(20).AddMilliseconds(-1),
      "report current evaluation time and conservative source time of strictly preceding aligned ATR");
    var req = MarketTrendHistoryRequirements.Describe(trend, new[] { metric }, Interval, asOf, 168, new[] { metric }).Single();
    Assert(req.MinimumClosedSourceCandles == 8 && req.RequiredStoredSourceCandles == 10 && req.HasAllAlignmentCapacity == true,
      "history includes prior ATR, current return, requested Delta, UTC skew and forming candle");
    _passed++;
  }

  private static void TestContextualRisk()
  {
    var two = new[] { "AUSDT", "BUSDT" };
    const string metrics = "MarketReversalStrength,PreviousMedianReturnInPriorATR,MedianReturnInPriorATR,MedianATRPRatioToPrior,ReturnInPriorATR,RelativeStrengthInPriorATR";
    var trend = new MarketTrend { Name = "Risk", TrendMinutes = 5, ATRPeriod = 1, Metrics = metrics };
    MarketTrendAnalysisSnapshot Build(double endA, double endB, double amplitude)
    {
      var store = NewStore();
      foreach (var symbol in two)
      {
        var closes = new[] { 100d, 100d, 100d, 100d, 98d, symbol == two[0] ? endA : endB };
        for (int i = 0; i < closes.Length; i++)
        {
          var close = closes[i];
          var spread = i == closes.Length - 1 ? amplitude : 1;
          store.UpsertCandle(new MarketCandle { Symbol = symbol, Interval = Interval,
            OpenTimeUtc = Start.AddMinutes(5 * i), CloseTimeUtc = Start.AddMinutes(5 * (i+1)).AddMilliseconds(-1),
            Open = close, High = close + spread, Low = close - spread, Close = close, QuoteVolume = 1, BaseVolume = 1,
            TradeCount = 1, IsClosed = true });
        }
      }
      return MarketTrendAnalyticsEngine.Calculate(trend, two, two, store, Interval, Start.AddMinutes(30));
    }
    var low = Build(100, 102, 1); var wide = Build(100, 102, 10);
    Near(low.MarketMetrics["PreviousMedianReturnInPriorATR"], -1, "previous leg uses its own historical baseline");
    Near(low.MarketMetrics["MarketReversalStrength"], 1, "reversal recovers the larger of the previous leg and its pre-move range");
    Near(low.AssetMetrics[two[0]]["ReturnInPriorATR"], 2d/3, "own adverse or favorable move uses prior own ATRP");
    Near(low.AssetMetrics[two[0]]["RelativeStrengthInPriorATR"], -1d/3, "directional excess versus broad median in own prior ATR");
    Near(wide.MarketMetrics["MarketReversalStrength"], 1, "current range widening cannot dilute a reversal");
    Near(wide.AssetMetrics[two[0]]["ReturnInPriorATR"], 2d/3, "current high/low cannot move prior asset scale");
    Assert(wide.MarketMetrics["MedianATRPRatioToPrior"].Value > low.MarketMetrics["MedianATRPRatioToPrior"].Value,
      "current expansion relative to starting ATR is observable independently");
    Near(Build(94, 96, 1).MarketMetrics["MarketReversalStrength"], 0, "continuation has no reversal score");
    Assert(Build(110, 112, 1).MarketMetrics["MarketReversalStrength"].Value > 3, "larger reversal has no upper gate");
    _passed++;
  }

  private static void TestMarketTrendDelta()
  {
    var store = NewStore();
    var closes = new[] { 100d, 101d, 102d, 103d, 104d, 105d, 107d, 106d, 110d };
    AddPathCandles(store, "DELTAUSDT", closes);
    var at = Start.AddMinutes(45).AddSeconds(1);
    var trend = new MarketTrend { Name = "D20", TrendMinutes = 20,
      Metrics = "PriceTrend,PricePathEfficiency,MedianPricePathEfficiency" };
    var names = new[] { "DELTAUSDT" };
    var current = MarketTrendAnalyticsEngine.Calculate(
      trend, names, names, store, Interval, at);
    var requested = new[] {
      MarketTrendMetrics.PriceTrend, MarketTrendMetrics.PricePathEfficiency,
      MarketTrendMetrics.MedianPricePathEfficiency
    };
    var delta = MarketTrendDeltaEngine.Calculate(trend, requested, current, store, Interval);
    double expected = (110d / 104d - 1d) * 100 - (104d / 100d - 1d) * 100;
    Near(delta.MarketMetrics[MarketTrendMetrics.PriceTrend].Delta, expected,
      "Delta global PriceTrend compares rolling endpoints one configured trend apart");
    Near(delta.AssetMetrics[names[0]][MarketTrendMetrics.PriceTrend].Delta, expected,
      "Delta asset PriceTrend uses the same source reference");
    Near(delta.MarketMetrics[MarketTrendMetrics.MedianPricePathEfficiency].Delta,
      delta.AssetMetrics[names[0]][MarketTrendMetrics.PricePathEfficiency].Delta.Value.Value,
      "Global median and asset efficiency of single eligible asset agree");
    Assert(delta.PreviousAsOfUtc == at.AddMinutes(-20),
      "Previous observation time comes from TrendMinutes, not a new user field");
    Assert(delta.MarketMetrics[MarketTrendMetrics.PriceTrend].PreviousSourceDataTimestampUtc ==
      Start.AddMinutes(25).AddMilliseconds(-1),
      "Prior source timestamp is preserved");
    var config = new AnalyzerSettings
    {
      MarketAnalyzer = new MarketAnalyzer { MarketTrends = new List<MarketTrend> { trend } },
      GlobalSettings = new List<GlobalSetting> {
        new GlobalSetting { SettingName = "D", Triggers = new List<Trigger> {
          new Trigger { MarketTrendName = "D20", Metric = "PriceTrend", MarketTrendRelation = "Delta" } } } },
      SingleMarketSettings = new List<SingleMarketSetting>()
    };
    var selected = MarketTrendDeltaSelection.Select(trend, config);
    Assert(selected.SequenceEqual(new[] { MarketTrendMetrics.PriceTrend }),
      "Only a referenced Delta metric is evaluated as historical");
    config.GlobalSettings[0].Triggers[0].MarketTrendRelation = "Absolute";
    Assert(MarketTrendDeltaSelection.Select(trend, config).Count == 0,
      "Legacy Absolute triggers do not request historical metrics");
    _passed++;
  }

  private static void TestMarketTrendDeltaCoverageAndTime()
  {
    var trend = new MarketTrend { Name = "D20", TrendMinutes = 20,
      Metrics = "PriceTrend,Breadth" };
    var at = Start.AddMinutes(45).AddSeconds(1);
    var markets = Enumerable.Range(0, 10).Select(i => "D" + i + "USDT").ToArray();
    MarketDataStore Source(int incomplete)
    {
      var store = NewStore();
      for (int i = 0; i < markets.Length; i++)
        AddPathCandles(store, markets[i],
          new[] { 100d, 101d, 102d, 103d, 104d, 105d, 107d, 106d, 110d },
          skip: i >= markets.Length - incomplete ? 0 : -1);
      return store;
    }
    MarketTrendDeltaAnalysisSnapshot Check(int missing)
    {
      var store = Source(missing);
      var current = MarketTrendAnalyticsEngine.Calculate(
        trend, markets, markets, store, Interval, at);
      return MarketTrendDeltaEngine.Calculate(trend, new[] { MarketTrendMetrics.PriceTrend,
        MarketTrendMetrics.Breadth }, current, store, Interval);
    }
    Near(Check(0).MarketMetrics[MarketTrendMetrics.PriceTrend].Delta,
      (110d / 104d - 1d) * 100 - (104d / 100d - 1d) * 100,
      "Valid global delta when 100% eligible observations present");
    Near(Check(1).MarketMetrics[MarketTrendMetrics.PriceTrend].Delta,
      (110d / 104d - 1d) * 100 - (104d / 100d - 1d) * 100,
      "90% historical eligible source coverage succeeds");
    Invalid(Check(2).MarketMetrics[MarketTrendMetrics.PriceTrend].Delta,
      "80% prior historical coverage stays unavailable");
    FeatureValueSnapshot Value(double value, DateTimeOffset source) =>
      new FeatureValueSnapshot { Value = value, IsValid = true,
        SourceDataTimestampUtc = source, CalculatedAtUtc = at };
    Invalid(MarketTrendDeltaEngine.Compare(
      Value(5, at), Value(3, at), at).Delta,
      "Reposted same completed period must not be treated as new observation");
    Near(MarketTrendDeltaEngine.Compare(
      Value(5, at), Value(5, at.AddMinutes(-20)), at).Delta, 0,
      "Zero numeric delta between distinct observations is valid");
    Invalid(MarketTrendDeltaEngine.Compare(
      Value(5, at), null, at).Delta, "Missing historical measurement fails closed");
    var required = MarketTrendHistoryRequirements.Describe(trend,
      new[] { MarketTrendMetrics.PriceTrend }, Interval, at, 12,
      new[] { MarketTrendMetrics.PriceTrend }).Single();
    Assert(required.MinimumClosedSourceCandles == 9 &&
      required.CalculationWindowMinutes == 40,
      "History calculator requires two configured windows, not one");
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
