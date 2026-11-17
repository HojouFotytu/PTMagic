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
      TestRegistryScopesAndSharedIntegration();
      TestTakerFlowAndMissingHistory();
      TestTakerGlobalAggregates();
      TestTakerGlobalIntegration();
      TestPhase3SharedPass();
      TestPhase3UnavailableAndNeutral();
      TestPhase3H72();
      TestTakerKlineMapper();
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

  private static void TestPhase3H72()
  {
    var store = NewStore();
    for (int i = 0; i < 864; i++)
    {
      var opened = Start.AddMinutes(i * 5);
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
      Start.AddMinutes(4320).AddSeconds(1), configuredStoreDataMaxHours: 96);
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
