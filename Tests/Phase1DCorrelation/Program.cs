using System;
using System.Collections.Generic;
using System.Linq;
using Core.Main.DataObjects.PTMagicData;
using Core.MarketAnalyzer.MarketData;

internal static class Program
{
  private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
  private static readonly DateTimeOffset AsOf = new DateTimeOffset(2026, 11, 1, 0, 0, 1, TimeSpan.Zero);
  private static readonly double[] BenchmarkReturns = { -0.03, -0.01, 0.01, 0.03 };
  private static int _passed;

  private static int Main()
  {
    try
    {
      TestDefaultsScopesAndUnits();
      TestPositiveCorrelation();
      TestNegativeCorrelation();
      TestZeroCorrelation();
      TestKnownNontrivialCoefficient();
      TestZeroVariance();
      TestTimestampMismatch();
      TestSampleCountMismatch();
      TestOldAndFutureVectors();
      TestExactSimpleReturnVector();
      TestMissingInteriorSourceCandle();
      TestMissingReferenceClose();
      TestIncompleteTrendPeriodExcluded();
      TestConfiguredTrendDurations();
      TestStaleSource();
      TestLookbackBounds();
      TestGlobalCoverage();
      TestExactPopulationAndBtcInclusion();
      TestUnavailableBenchmark();
      TestFiatConversionAndBtcQuote();
      TestSharedEngineIntegration();
      Console.WriteLine("Phase 1D correlation tests passed: " + _passed + ".");
      return 0;
    }
    catch (Exception error)
    {
      Console.Error.WriteLine("Phase 1D correlation tests FAILED: " + error);
      return 1;
    }
  }

  private static void TestDefaultsScopesAndUnits()
  {
    Assert(new MarketTrend().CorrelationLookback == 30, "Correlation default must be 30 synchronized returns.");
    Assert(MarketTrendMetrics.TryGetScope("correlationtobtc", out MarketTrendMetricScope asset) &&
      asset == MarketTrendMetricScope.Asset, "CorrelationToBTC must be asset scoped.");
    foreach (string metric in new[] { MarketTrendMetrics.MeanCorrelationToBTC, MarketTrendMetrics.MedianCorrelationToBTC })
      Assert(MarketTrendMetrics.TryGetScope(metric, out MarketTrendMetricScope scope) &&
        scope == MarketTrendMetricScope.Global, "Correlation aggregates must be global scoped.");
    foreach (string metric in MarketTrendPhase1DEngine.Metrics)
      Assert(MarketTrendMetricTriggerResolver.GetUnit(metric) == "", "A coefficient cannot carry a percent unit.");
    _passed++;
  }

  private static void TestPositiveCorrelation()
  {
    Near(Correlate(new[] { 0.01, 0.02, -0.01, 0.03 }, new[] { 0.02, 0.04, -0.02, 0.06 }), 1, "Positive linear relation");
    _passed++;
  }

  private static void TestNegativeCorrelation()
  {
    Near(Correlate(BenchmarkReturns, BenchmarkReturns.Select(value => -2 * value).ToArray()), -1, "Negative linear relation");
    _passed++;
  }

  private static void TestZeroCorrelation()
  {
    Near(Correlate(BenchmarkReturns, new[] { 0.01, -0.01, -0.01, 0.01 }), 0, "Orthogonal centered vectors");
    _passed++;
  }

  private static void TestKnownNontrivialCoefficient()
  {
    Near(Correlate(new[] { 0.01, 0.02, 0.03, 0.04 }, new[] { 0.01, 0.04, 0.02, 0.03 }), 0.4,
      "Independent hand-calculated Pearson coefficient");
    _passed++;
  }

  private static void TestZeroVariance()
  {
    Invalid(Correlate(new[] { 0.0, 0.0, 0.0, 0.0 }, BenchmarkReturns), "Zero asset variance");
    Invalid(Correlate(BenchmarkReturns, new[] { 0.02, 0.02, 0.02, 0.02 }), "Constant BTC returns");
    Invalid(Correlate(new[] { 0.01, 0.01, 0.01, 0.01 + 1e-17 }, BenchmarkReturns), "Numerically constant returns");
    _passed++;
  }

  private static void TestTimestampMismatch()
  {
    SynchronizedReturnVector valid = Vector(BenchmarkReturns);
    DateTimeOffset[] shifted = valid.EndTimesUtc.ToArray();
    shifted[1] = shifted[1].AddMinutes(-5);
    SynchronizedReturnVector mismatch = new SynchronizedReturnVector
    { Returns = valid.Returns, EndTimesUtc = shifted, TrendMinutes = 5, IsValid = true };
    Invalid(SynchronizedReturnCorrelationCalculator.Calculate(valid, mismatch, AsOf), "Index-only alignment is forbidden");
    _passed++;
  }

  private static void TestSampleCountMismatch()
  {
    Invalid(SynchronizedReturnCorrelationCalculator.Calculate(Vector(BenchmarkReturns),
      Vector(new[] { -0.01, 0.01, 0.03 }), AsOf), "A shortened benchmark vector must be unavailable");
    _passed++;
  }

  private static void TestOldAndFutureVectors()
  {
    foreach (int shift in new[] { -5, 5 })
    {
      SynchronizedReturnVector vector = Vector(BenchmarkReturns, shiftMinutes: shift);
      Invalid(SynchronizedReturnCorrelationCalculator.Calculate(vector, vector, AsOf), "Wrong latest UTC endpoint");
    }
    _passed++;
  }

  private static void TestExactSimpleReturnVector()
  {
    MarketDataStore store = NewStore();
    AddReturns(store, "AAAUSDT", 10, BenchmarkReturns);
    SynchronizedReturnVector vector = Read(store, "AAAUSDT", 10, 4);
    Assert(vector.IsValid && vector.Returns.Count == 4, "Five close endpoints must create exactly four returns.");
    for (int index = 0; index < 4; index++)
      Assert(Math.Abs(vector.Returns[index] - BenchmarkReturns[index]) < 1e-12, "Use simple returns, not price levels or log returns.");
    Assert(vector.EndTimesUtc[3] == LastClose(10), "Vector must end at the last complete UTC trend close.");
    Near(SynchronizedReturnCorrelationCalculator.Calculate(vector, vector, AsOf), 1, "BTC self-correlation with variance");
    _passed++;
  }

  private static void TestMissingInteriorSourceCandle()
  {
    MarketDataStore store = NewStore();
    // Index 3 lies between 10m close endpoints. Endpoint-only/index alignment
    // would conceal it, but the required closed source sequence is incomplete.
    AddReturns(store, "AAAUSDT", 10, BenchmarkReturns, skipSourceIndex: 3);
    Assert(!Read(store, "AAAUSDT", 10, 4).IsValid, "Missing interior 5m source candle must not be filled.");
    _passed++;
  }

  private static void TestMissingReferenceClose()
  {
    MarketDataStore store = NewStore();
    AddReturns(store, "AAAUSDT", 5, BenchmarkReturns, skipSourceIndex: 0);
    Assert(!Read(store, "AAAUSDT", 5, 4).IsValid, "N closes cannot be treated as N returns.");
    _passed++;
  }

  private static void TestIncompleteTrendPeriodExcluded()
  {
    MarketDataStore store = NewStore();
    AddReturns(store, "AAAUSDT", 10, BenchmarkReturns);
    store.UpsertCandle(Candle("AAAUSDT", LastClose(10).AddMilliseconds(1), 100000));
    DateTimeOffset partialAsOf = AsOf.AddMinutes(5);
    SynchronizedReturnVector vector = SynchronizedReturnCorrelationCalculator.BuildVector(
      store.GetSnapshot("AAAUSDT", Interval, partialAsOf), Interval, 10, 4, partialAsOf);
    Assert(vector.IsValid && vector.EndTimesUtc.Last() == LastClose(10), "Partial trend period cannot move vector endpoint.");
    Near(SynchronizedReturnCorrelationCalculator.Calculate(vector, Vector(BenchmarkReturns, 10), partialAsOf), 1,
      "Partial trend source candle must not change returns");
    _passed++;
  }

  private static void TestConfiguredTrendDurations()
  {
    foreach (int minutes in new[] { 5, 60, 120, 240, 480, 720, 1440 })
    {
      MarketDataStore store = NewStore();
      AddReturns(store, "AAAUSDT", minutes, BenchmarkReturns);
      SynchronizedReturnVector vector = Read(store, "AAAUSDT", minutes, 4);
      Assert(vector.IsValid && vector.EndTimesUtc[1] - vector.EndTimesUtc[0] == TimeSpan.FromMinutes(minutes),
        "Return spacing must honor TrendMinutes=" + minutes);
      Near(SynchronizedReturnCorrelationCalculator.Calculate(vector, vector, AsOf), 1, "Configured-horizon self correlation");
    }
    _passed++;
  }

  private static void TestStaleSource()
  {
    MarketDataStore store = NewStore();
    AddReturns(store, "AAAUSDT", 5, BenchmarkReturns);
    DateTimeOffset late = AsOf.AddMinutes(20);
    SynchronizedReturnVector vector = SynchronizedReturnCorrelationCalculator.BuildVector(
      store.GetSnapshot("AAAUSDT", Interval, late), Interval, 5, 4, late);
    Assert(!vector.IsValid && vector.IsStale, "Stale source must remain unavailable.");
    _passed++;
  }

  private static void TestLookbackBounds()
  {
    MarketDataStore store = NewStore();
    double[] returns = Enumerable.Range(0, 1000).Select(i => i % 2 == 0 ? -0.001 : 0.002).ToArray();
    AddReturns(store, "AAAUSDT", 5, returns);
    Assert(Read(store, "AAAUSDT", 5, 3).IsValid, "Minimum lookback 3 must be supported.");
    Assert(Read(store, "AAAUSDT", 5, 1000).IsValid, "Maximum lookback 1000 must be supported.");
    Assert(!Read(store, "AAAUSDT", 5, 2).IsValid && !Read(store, "AAAUSDT", 5, 1001).IsValid,
      "Out-of-range lookbacks must not be clamped.");
    _passed++;
  }

  private static void TestGlobalCoverage()
  {
    string[] symbols = Enumerable.Range(0, 10).Select(i => "ALT" + i + "USDT").ToArray();
    foreach (int validCount in new[] { 9, 8 })
    {
      MarketDataStore store = NewStore();
      AddReturns(store, "BTCUSDT", 5, BenchmarkReturns);
      for (int i = 0; i < validCount; i++) AddReturns(store, symbols[i], 5, BenchmarkReturns);
      Phase1DFeatureResult result = Calculate(store, symbols, Array.Empty<string>());
      foreach (string metric in new[] { MarketTrendMetrics.MeanCorrelationToBTC, MarketTrendMetrics.MedianCorrelationToBTC })
      {
        FeatureValueSnapshot feature = result.GlobalMetrics[metric];
        Assert(feature.SampleCount == validCount && feature.EligibleSampleCount == 10,
          "Global coverage denominator must remain the selected universe.");
        if (validCount == 9) Near(feature, 1, "Exactly 90% coverage");
        else Invalid(feature, "Below 90% coverage");
      }
    }
    _passed++;
  }

  private static void TestExactPopulationAndBtcInclusion()
  {
    MarketDataStore store = NewStore();
    AddReturns(store, "BTCUSDT", 5, BenchmarkReturns);
    AddReturns(store, "NEGUSDT", 5, BenchmarkReturns.Select(value => -value).ToArray());
    AddReturns(store, "ZEROUSDT", 5, new[] { 0.01, -0.01, -0.01, 0.01 });
    Phase1DFeatureResult withBtc = Calculate(store, new[] { "BTCUSDT", "NEGUSDT", "ZEROUSDT" }, new[] { "NEGUSDT" });
    Near(withBtc.GlobalMetrics[MarketTrendMetrics.MeanCorrelationToBTC], 0, "Selected BTC contributes its self-correlation");
    Near(withBtc.GlobalMetrics[MarketTrendMetrics.MedianCorrelationToBTC], 0, "Odd median");
    Assert(withBtc.GlobalMetrics[MarketTrendMetrics.MeanCorrelationToBTC].EligibleSampleCount == 3 &&
      withBtc.AssetMetrics.Count == 1 && !withBtc.AssetMetrics.ContainsKey("BTCUSDT"), "Asset/global populations must remain independent.");
    Phase1DFeatureResult withoutBtc = Calculate(store, new[] { "NEGUSDT", "ZEROUSDT" }, Array.Empty<string>());
    Near(withoutBtc.GlobalMetrics[MarketTrendMetrics.MeanCorrelationToBTC], -0.5, "Unselected BTC cannot enter mean");
    Near(withoutBtc.GlobalMetrics[MarketTrendMetrics.MedianCorrelationToBTC], -0.5, "Even median");
    _passed++;
  }

  private static void TestUnavailableBenchmark()
  {
    MarketDataStore store = NewStore();
    AddReturns(store, "AAAUSDT", 5, BenchmarkReturns);
    Phase1DFeatureResult result = Calculate(store, new[] { "AAAUSDT" }, new[] { "AAAUSDT" });
    Invalid(result.GlobalMetrics[MarketTrendMetrics.MeanCorrelationToBTC], "Missing BTC global mean");
    Invalid(result.AssetMetrics["AAAUSDT"][MarketTrendMetrics.CorrelationToBTC], "Missing BTC asset correlation");
    _passed++;
  }

  private static void TestFiatConversionAndBtcQuote()
  {
    MarketDataStore store = NewStore();
    AddReturns(store, "BTCUSDT", 5, BenchmarkReturns);
    AddReturns(store, "ALTBTC", 5, new double[4]);
    MarketTrend trend = Trend();
    trend.TrendCurrency = "Fiat";
    Phase1DFeatureResult result = MarketTrendPhase1DEngine.Calculate(trend, MarketTrendMetrics.ParseEnabled(trend),
      new[] { "ALTBTC" }, new[] { "ALTBTC" }, store, Interval, AsOf, "BTCBTC");
    Near(result.AssetMetrics["ALTBTC"][MarketTrendMetrics.CorrelationToBTC], 1,
      "Flat BTC-quoted asset follows BTC in Fiat units using synchronized conversion closes");
    trend.TrendCurrency = "Market";
    result = MarketTrendPhase1DEngine.Calculate(trend, MarketTrendMetrics.ParseEnabled(trend),
      new[] { "ALTBTC" }, new[] { "ALTBTC" }, store, Interval, AsOf, "BTCBTC");
    Invalid(result.AssetMetrics["ALTBTC"][MarketTrendMetrics.CorrelationToBTC], "BTC/BTC Market benchmark has zero variance");
    store.RemoveSeries("BTCUSDT", Interval);
    trend.TrendCurrency = "Fiat";
    result = MarketTrendPhase1DEngine.Calculate(trend, MarketTrendMetrics.ParseEnabled(trend),
      new[] { "ALTBTC" }, new[] { "ALTBTC" }, store, Interval, AsOf, "BTCBTC");
    Invalid(result.AssetMetrics["ALTBTC"][MarketTrendMetrics.CorrelationToBTC], "Missing Fiat conversion cannot use Market fallback");
    _passed++;
  }

  private static void TestSharedEngineIntegration()
  {
    MarketDataStore store = NewStore();
    AddReturns(store, "BTCUSDT", 5, BenchmarkReturns);
    AddReturns(store, "AAAUSDT", 5, BenchmarkReturns);
    MarketTrend trend = Trend();
    trend.Metrics += ",PriceTrend";
    MarketTrendAnalysisSnapshot snapshot = MarketTrendAnalyticsEngine.Calculate(trend,
      new[] { "AAAUSDT" }, new[] { "AAAUSDT" }, store, Interval, AsOf,
      configuredStoreDataMaxHours: 72);
    Near(snapshot.AssetMetrics["AAAUSDT"][MarketTrendMetrics.CorrelationToBTC], 1, "Shared asset integration");
    Near(snapshot.MarketMetrics[MarketTrendMetrics.MedianCorrelationToBTC], 1, "Shared global integration");
    Assert(snapshot.AssetMetrics["AAAUSDT"].ContainsKey(MarketTrendMetrics.PriceTrend), "Phase1D cannot replace earlier asset metrics.");
    Assert(snapshot.HistoryRequirements.Count == 4 && snapshot.HistoryRequirements.All(r => r.HasAllAlignmentCapacity == true),
      "Integrated snapshot must carry sufficient history diagnostics for every selected metric.");
    _passed++;
  }

  private static MarketTrend Trend() => new MarketTrend
  { Name = "Correlation", TrendMinutes = 5, CorrelationLookback = 4, Metrics = String.Join(",", MarketTrendPhase1DEngine.Metrics) };

  private static Phase1DFeatureResult Calculate(MarketDataStore store, string[] global, string[] assets)
  {
    MarketTrend trend = Trend();
    return MarketTrendPhase1DEngine.Calculate(trend, MarketTrendMetrics.ParseEnabled(trend), global, assets, store, Interval, AsOf);
  }

  private static SynchronizedReturnVector Read(MarketDataStore store, string symbol, int minutes, int lookback) =>
    SynchronizedReturnCorrelationCalculator.BuildVector(store.GetSnapshot(symbol, Interval, AsOf), Interval, minutes, lookback, AsOf);

  private static FeatureValueSnapshot Correlate(double[] left, double[] right) =>
    SynchronizedReturnCorrelationCalculator.Calculate(Vector(left), Vector(right), AsOf);

  private static SynchronizedReturnVector Vector(double[] values, int minutes = 5, int shiftMinutes = 0) => new SynchronizedReturnVector
  {
    Returns = values, TrendMinutes = minutes, IsValid = true,
    EndTimesUtc = Enumerable.Range(0, values.Length).Select(i => LastClose(minutes)
      .AddMinutes(-(values.Length - 1 - i) * minutes + shiftMinutes)).ToArray()
  };

  private static DateTimeOffset LastClose(int minutes)
  {
    long periodMs = (long)TimeSpan.FromMinutes(minutes).TotalMilliseconds;
    return DateTimeOffset.FromUnixTimeMilliseconds(AsOf.ToUnixTimeMilliseconds() / periodMs * periodMs - 1);
  }

  private static MarketDataStore NewStore() => new MarketDataStore(20000);

  private static void AddReturns(MarketDataStore store, string symbol, int minutes, double[] returns, int skipSourceIndex = -1)
  {
    int steps = minutes / 5;
    double[] closes = new double[returns.Length + 1];
    closes[0] = 100;
    for (int i = 0; i < returns.Length; i++) closes[i + 1] = closes[i] * (1 + returns[i]);
    DateTimeOffset firstClose = LastClose(minutes).AddMinutes(-returns.Length * minutes);
    for (int index = 0; index <= returns.Length * steps; index++)
    {
      if (index == skipSourceIndex) continue;
      DateTimeOffset open = firstClose.AddMinutes(index * 5).AddMilliseconds(1).Subtract(Interval);
      store.UpsertCandle(Candle(symbol, open, closes[index / steps]));
    }
  }

  private static MarketCandle Candle(string symbol, DateTimeOffset open, double price) => new MarketCandle
  {
    Symbol = symbol, Interval = Interval, OpenTimeUtc = open, CloseTimeUtc = open.Add(Interval).AddMilliseconds(-1),
    Open = price, High = price, Low = price, Close = price,
    BaseVolume = 10, QuoteVolume = 10, TradeCount = 1, IsClosed = true
  };

  private static void Near(FeatureValueSnapshot feature, double expected, string message)
  {
    Assert(feature.IsValid && feature.Value.HasValue && !feature.IsStale, message + ": unavailable: " + feature.InvalidReason);
    Assert(Math.Abs(feature.Value!.Value - expected) < 1e-9, message + ": expected " + expected + ", got " + feature.Value);
  }
  private static void Invalid(FeatureValueSnapshot feature, string message) =>
    Assert(!feature.IsValid && !feature.Value.HasValue, message + ": expected unavailable.");
  private static void Assert(bool condition, string message)
  { if (!condition) throw new InvalidOperationException(message); }
}
