using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Helper;
using Core.Main.DataObjects.PTMagicData;
using Core.MarketAnalyzer.MarketData;
using Microsoft.Extensions.Logging.Abstractions;

internal static class Program
{
  private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
  private static readonly DateTimeOffset Boundary = new DateTimeOffset(2026, 11, 4, 0, 0, 1, TimeSpan.Zero);
  private static readonly LogHelper Log = new LogHelper(NullLogger<LogHelper>.Instance);
  private static int _passed;

  private static async Task<int> Main()
  {
    try
    {
      await TestPinnedLoaderWithTightRetention();
      await TestFutureCandlesCannotEvictPinnedReference();
      await TestLegacyLoaderCompatibility();
      TestMissingDecisionTimestamp();
      TestFreshnessInclusiveLimit();
      TestExpiredDecisionTimestamp();
      TestClockAndPolicyFailures();
      TestAtrRetentionRequirement();
      TestVolumeRetentionRequirement();
      TestCorrelationRetentionRequirement();
      TestPartialTrendRetentionRequirement();
      TestUnknownConfiguredRetention();
      TestReturnAndConcentrationRequirements();
      TestConcentrationWithOnlyTrendWindow();
      TestConcentrationUsesSameWindowForRanking();
      TestInsufficientConfigurationRemainsUnavailable();
      Console.WriteLine("Phase 1 analytics reliability tests passed: " + _passed + ".");
      return 0;
    }
    catch (Exception error)
    {
      Console.Error.WriteLine("Phase 1 analytics reliability tests FAILED: " + error);
      return 1;
    }
  }

  private static async Task TestPinnedLoaderWithTightRetention()
  {
    DateTimeOffset pin = new DateTimeOffset(2026, 11, 4, 10, 9, 45, TimeSpan.Zero);
    DateTimeOffset lateWallClock = pin.AddMinutes(2);
    string[] symbols = { "EARLYUSDT", "LATEUSDT" };
    MarketDataService service = new MarketDataService(new MarketDataStore(4));
    List<DateTimeOffset> receivedPins = new List<DateTimeOffset>();
    Task<List<MarketCandle>> Loader(string symbol, int count, LogHelper log, CancellationToken ct, DateTimeOffset requestAsOf)
    {
      receivedPins.Add(requestAsOf);
      // Simulate late invocation after a close. Which history is returned is
      // governed by the request boundary, never by this later wall clock.
      DateTimeOffset invocationTime = symbol == "LATEUSDT" ? lateWallClock : pin;
      Assert(invocationTime >= requestAsOf, "Fixture request cannot be in the future.");
      DateTimeOffset newestOpen = FloorSource(requestAsOf);
      return Task.FromResult(Enumerable.Range(0, count).Select(i => Candle(symbol,
        newestOpen.AddMinutes((i - count + 1) * 5), 100 + i, requestAsOf)).ToList());
    }

    IReadOnlyList<MarketDataRefreshStatus> statuses = await service.RefreshAtAsync(symbols, Interval,
      4, Loader, Log, maxConcurrency: 1, refreshAsOfUtc: pin);
    Assert(statuses.All(status => status.Succeeded) && receivedPins.Count == 2 && receivedPins.All(value => value == pin),
      "Every loader must receive exactly the same raid observation boundary.");
    MarketTrend trend = new MarketTrend { Name = "Pinned", TrendMinutes = 10, Metrics = "PriceTrend" };
    MarketTrendAnalysisSnapshot analysis = MarketTrendAnalyticsEngine.Calculate(trend, symbols, symbols,
      service.Store, Interval, pin);
    Assert(analysis.ValidGlobalReturnCount == 2 && analysis.MarketMetrics[MarketTrendMetrics.PriceTrend].IsValid,
      "Tight retention must preserve the pinned reference for both early and late requests.");
    foreach (string symbol in symbols)
    {
      MarketDataSeriesSnapshot snapshot = service.Store.GetSnapshot(symbol, Interval, pin);
      Assert(snapshot.SampleCount == 4 && snapshot.FirstOpenTimeUtc == FloorSource(pin).AddMinutes(-15),
        "An arriving late response must not advance/trim this raid's requested history.");
    }
    _passed++;
  }

  private static async Task TestFutureCandlesCannotEvictPinnedReference()
  {
    DateTimeOffset pin = Boundary.AddMinutes(9);
    DateTimeOffset latestOpen = FloorSource(pin);
    MarketDataService service = new MarketDataService(new MarketDataStore(3));
    Task<List<MarketCandle>> Loader(string symbol, int count, LogHelper log, CancellationToken ct, DateTimeOffset requestAsOf) =>
      Task.FromResult(Enumerable.Range(-2, 5).Select(i => Candle(symbol, latestOpen.AddMinutes(i * 5),
        100 + i, requestAsOf.AddMinutes(20))).ToList());
    await service.RefreshAtAsync(new[] { "AAAUSDT" }, Interval, 3, Loader, Log, refreshAsOfUtc: pin);
    MarketDataSeriesSnapshot snapshot = service.Store.GetSnapshot("AAAUSDT", Interval, pin);
    Assert(snapshot.SampleCount == 3 && snapshot.FirstOpenTimeUtc == latestOpen.AddMinutes(-10),
      "Unexpected future opens must be removed before bounded-store insertion.");
    FeatureValueSnapshot feature = MarketTrendAnalyticsEngine.CalculateReturn(snapshot, 5, pin);
    Assert(feature.IsValid, "Pinned return reference must survive an overlong future response.");
    _passed++;
  }

  private static async Task TestLegacyLoaderCompatibility()
  {
    MarketDataService service = new MarketDataService(new MarketDataStore(3));
    Task<List<MarketCandle>> Loader(string symbol, int count, LogHelper log, CancellationToken ct) =>
      Task.FromResult(new List<MarketCandle> { Candle(symbol, FloorSource(Boundary).AddMinutes(-5), 100, Boundary) });
    IReadOnlyList<MarketDataRefreshStatus> statuses = await service.RefreshAsync(new[] { "AAAUSDT" }, Interval,
      3, Loader, Log, refreshAsOfUtc: Boundary);
    Assert(statuses.Count == 1 && statuses[0].Succeeded, "Existing four-argument loaders must remain source compatible.");
    _passed++;
  }

  private static void TestMissingDecisionTimestamp()
  {
    Assert(!AnalysisDecisionFreshness.IsFresh(null, Boundary, 300, out string reason) && reason.Length > 0,
      "A missing raid pin must prevent decisions.");
    _passed++;
  }

  private static void TestFreshnessInclusiveLimit()
  {
    Assert(AnalysisDecisionFreshness.IsFresh(Boundary, Boundary.AddSeconds(300), 300, out string reason) && reason == "",
      "Exactly MaxAnalysisAgeSeconds is allowed.");
    Assert(AnalysisDecisionFreshness.IsFresh(Boundary.ToOffset(TimeSpan.FromHours(5)), Boundary, 300, out _),
      "UTC-equivalent timestamp offsets cannot change freshness.");
    _passed++;
  }

  private static void TestExpiredDecisionTimestamp()
  {
    Assert(!AnalysisDecisionFreshness.IsFresh(Boundary, Boundary.AddSeconds(300).AddMilliseconds(1), 300, out string reason)
      && reason.Contains("Retain") && reason.Contains("startup SOM"), "Stale decisions must preserve the applied configuration/SOM.");
    _passed++;
  }

  private static void TestClockAndPolicyFailures()
  {
    Assert(!AnalysisDecisionFreshness.IsFresh(Boundary.AddMilliseconds(1), Boundary, 300, out _),
      "A future pin must fail closed.");
    Assert(!AnalysisDecisionFreshness.IsFresh(Boundary, Boundary, 0, out _), "A zero freshness limit is invalid.");
    Assert(!AnalysisDecisionFreshness.IsFresh(Boundary, Boundary, -1, out _), "A negative freshness limit is invalid.");
    _passed++;
  }

  private static void TestAtrRetentionRequirement()
  {
    MarketTrendHistoryRequirement requirement = Requirement("ATRP", 1440, 72);
    Assert(requirement.CalculationWindowMinutes == 15 * 1440 && requirement.MinimumClosedSourceCandles == 15 * 288,
      "ATR14 requires 15 full trend periods, including the previous-close seed period.");
    Assert(requirement.RequiredStoreDataMaxHours == 360 && requirement.RecommendedStoreDataMaxHours == 384 &&
      requirement.HasCurrentRaidCapacity == false && requirement.HasAllAlignmentCapacity == false,
      "24h ATR14 needs 360h at the boundary, 384h for every UTC alignment; 72h cannot suffice.");
    _passed++;
  }

  private static void TestVolumeRetentionRequirement()
  {
    MarketTrendHistoryRequirement requirement = Requirement("VolumeExpansion", 1440, 72);
    Assert(requirement.CalculationWindowMinutes == 21 * 1440 && requirement.MinimumClosedSourceCandles == 21 * 288 &&
      requirement.RequiredStoreDataMaxHours == 504 && requirement.RecommendedStoreDataMaxHours == 528,
      "Volume expansion20 requires a current period and 20 complete previous periods.");
    _passed++;
  }

  private static void TestCorrelationRetentionRequirement()
  {
    MarketTrendHistoryRequirement requirement = Requirement("CorrelationToBTC", 1440, 72);
    Assert(requirement.CalculationWindowMinutes == 30 * 1440 && requirement.MinimumClosedSourceCandles == 30 * 288 + 1,
      "Thirty correlation returns need their thirty-first exact endpoint.");
    Assert(requirement.RequiredStoreDataMaxHours == 720 && requirement.RecommendedStoreDataMaxHours == 744,
      "Correlation retained history must include the reference and partial trend alignment.");
    _passed++;
  }

  private static void TestPartialTrendRetentionRequirement()
  {
    MarketTrend trend = new MarketTrend { Name = "DailyATR", TrendMinutes = 1440, Metrics = "ATRP" };
    MarketTrendHistoryRequirement early = MarketTrendHistoryRequirements.Describe(trend,
      MarketTrendMetrics.ParseEnabled(trend), Interval, Boundary, 360).Single();
    MarketTrendHistoryRequirement late = MarketTrendHistoryRequirements.Describe(trend,
      MarketTrendMetrics.ParseEnabled(trend), Interval, Boundary.AddHours(23).AddMinutes(55), 360).Single();
    Assert(early.HasCurrentRaidCapacity == true && early.HasAllAlignmentCapacity == false,
      "Retention can fit at a boundary while still being insufficient for other alignments.");
    Assert(late.RequiredStoredSourceCandles - early.RequiredStoredSourceCandles == 287 && late.HasCurrentRaidCapacity == false,
      "287 closed source candles in the partial daily period consume retention.");
    _passed++;
  }

  private static void TestUnknownConfiguredRetention()
  {
    MarketTrend trend = new MarketTrend { Name = "UnknownCapacity", TrendMinutes = 60, Metrics = "ATRP" };
    MarketTrendHistoryRequirement requirement = MarketTrendHistoryRequirements.Describe(trend,
      MarketTrendMetrics.ParseEnabled(trend), Interval, Boundary).Single();
    Assert(!requirement.ConfiguredStoreDataMaxHours.HasValue && !requirement.ConfiguredSourceCapacity.HasValue &&
      !requirement.HasCurrentRaidCapacity.HasValue && !requirement.HasAllAlignmentCapacity.HasValue,
      "A pure calculator with no supplied retention policy must report unknown capacity honestly.");
    _passed++;
  }

  private static void TestReturnAndConcentrationRequirements()
  {
    MarketTrendHistoryRequirement price = Requirement("PriceTrend", 360, 6);
    MarketTrendHistoryRequirement concentration = Requirement("Concentration", 360, 6);
    Assert(price.MinimumClosedSourceCandles == 73 && price.RequiredStoredSourceCandles == 74 &&
      price.RecommendedStoreDataMaxHours == 6 && price.HasAllAlignmentCapacity == true,
      "Return retention accounts for an exact older close plus the forming slot.");
    Assert(concentration.MinimumClosedSourceCandles == price.MinimumClosedSourceCandles &&
      concentration.CalculationWindowMinutes == 360 && concentration.RecommendedStoreDataMaxHours == 6,
      "Concentration cannot retain an undocumented 24-hour ranking requirement.");
    _passed++;
  }

  private static void TestConcentrationWithOnlyTrendWindow()
  {
    MarketDataStore store = new MarketDataStore(14);
    string[] symbols = Enumerable.Range(0, 11).Select(i => "SHORT" + i.ToString("00") + "USDT").ToArray();
    DateTimeOffset latestOpen = FloorSource(Boundary).AddMinutes(-5);
    foreach (string symbol in symbols)
    {
      for (int index = 0; index <= 12; index++)
        store.UpsertCandle(Candle(symbol, latestOpen.AddMinutes((index - 12) * 5), 100 + index, Boundary, 100));
    }
    FeatureValueSnapshot feature = Concentration(store, symbols);
    Assert(feature.IsValid, "A 60m Concentration needs only its 60m ranking and return reference, not 24h history.");
    _passed++;
  }

  private static void TestConcentrationUsesSameWindowForRanking()
  {
    MarketDataStore store = new MarketDataStore(300);
    string[] symbols = Enumerable.Range(0, 11).Select(i => "RANK" + i.ToString("00") + "USDT").ToArray();
    DateTimeOffset latestOpen = FloorSource(Boundary).AddMinutes(-5);
    for (int market = 0; market < symbols.Length; market++)
    {
      for (int index = 0; index <= 288; index++)
      {
        bool insideTrend = index > 276;
        double quote = market == 10 ? insideTrend ? 1 : 1000000 : 100;
        double price = insideTrend ? market == 10 ? 90 : 110 : 100;
        store.UpsertCandle(Candle(symbols[market], latestOpen.AddMinutes((index - 288) * 5), price, Boundary, quote));
      }
    }
    FeatureValueSnapshot feature = Concentration(store, symbols);
    Assert(feature.IsValid && feature.Value.HasValue && Math.Abs(feature.Value.Value - 20) < 1e-9,
      "60m volume Top10 return advantage must be 20pp; obsolete 24h ranking would produce -2pp.");
    _passed++;
  }

  private static void TestInsufficientConfigurationRemainsUnavailable()
  {
    MarketTrend trend = new MarketTrend { Name = "TooShort", TrendMinutes = 1440, Metrics = "ATRP,CorrelationToBTC" };
    MarketTrendAnalysisSnapshot snapshot = MarketTrendAnalyticsEngine.Calculate(trend, new[] { "AAAUSDT" },
      new[] { "AAAUSDT" }, new MarketDataStore(866), Interval, Boundary, configuredStoreDataMaxHours: 72);
    Assert(snapshot.HistoryRequirements.Count == 2 && snapshot.HistoryRequirements.All(r =>
      r.HasCurrentRaidCapacity == false && r.Diagnostic.Contains("StoreDataMaxHours")), "Insufficient settings need explicit diagnostics.");
    Assert(snapshot.AssetMetrics["AAAUSDT"].Values.All(feature => !feature.IsValid && !feature.Value.HasValue),
      "Missing retained data stays unavailable; it cannot throw the analyzer into a permanent startup failure.");
    _passed++;
  }

  private static MarketTrendHistoryRequirement Requirement(string metric, int minutes, int hours)
  {
    MarketTrend trend = new MarketTrend { Name = "Requirement", TrendMinutes = minutes, Metrics = metric };
    return MarketTrendHistoryRequirements.Describe(trend, MarketTrendMetrics.ParseEnabled(trend), Interval, Boundary, hours).Single();
  }

  private static FeatureValueSnapshot Concentration(MarketDataStore store, string[] symbols)
  {
    MarketTrend trend = new MarketTrend { Name = "Concentration", TrendMinutes = 60, Metrics = "Concentration" };
    return MarketTrendAnalyticsEngine.Calculate(trend, symbols, Array.Empty<string>(), store, Interval, Boundary)
      .MarketMetrics[MarketTrendMetrics.Concentration];
  }

  private static DateTimeOffset FloorSource(DateTimeOffset asOf) => DateTimeOffset.FromUnixTimeMilliseconds(
    asOf.ToUnixTimeMilliseconds() / (long)Interval.TotalMilliseconds * (long)Interval.TotalMilliseconds);

  private static MarketCandle Candle(string symbol, DateTimeOffset open, double price,
    DateTimeOffset asOf, double quote = 10) => new MarketCandle
  {
    Symbol = symbol, Interval = Interval, OpenTimeUtc = open, CloseTimeUtc = open.Add(Interval).AddMilliseconds(-1),
    Open = price, High = price, Low = price, Close = price, BaseVolume = quote, QuoteVolume = quote, TradeCount = 1,
    IsClosed = open.Add(Interval).AddMilliseconds(-1) <= asOf
  };

  private static void Assert(bool condition, string message)
  { if (!condition) throw new InvalidOperationException(message); }
}
