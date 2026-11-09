using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Helper;
using Core.Main.DataObjects.PTMagicData;
using Core.MarketAnalyzer.MarketData;
using Microsoft.Extensions.Logging.Abstractions;

internal static class Program
{
  private static int _passed;
  private static readonly DateTimeOffset Pin =
    new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
  private static readonly LogHelper Log = new LogHelper(NullLogger<LogHelper>.Instance);
  private static string _root = "";

  private static async Task<int> Main()
  {
    _root = Path.Combine(Path.GetTempPath(), "PTM_Phase2OI_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(_root);
    try
    {
      TestRegistryAndDefaultConfig();
      TestBudgetAndRetentionBounds();
      await TestZeroBudgetIsDisabled();
      await TestSingleSymbolExactEndpoints();
      await TestRequestBudgetAndFairProgress();
      await TestEmptyResponseNotFabricated();
      await TestMissingEndpointUnavailable();
      await TestIncrementalReuseWhenCurrent();
      await TestOlderPageRequestedForLongHistory();
      await TestInvalidSourceIsUnavailable();
      await TestCacheSurvivesServiceRestart();
      TestCorruptCacheFailsOpen();
      await TestNativeVsValueChange();
      await TestExchangeScopeAndOptIn();
      TestHistoryRequirementsIndependent();
      TestHistoryDerivedFromEnabledTrends();
      await TestLongTrendDoesNotGetTruncated();
      Console.WriteLine("PASS: Phase 2 incremental OI, " + _passed + " case groups.");
      Console.WriteLine("LIMIT: Offline harness only; no live Binance/ProfitTrailer/Monitor requests.");
      return 0;
    }
    catch (Exception exception)
    {
      Console.Error.WriteLine("FAIL: Phase 2 incremental OI: " + exception);
      return 1;
    }
    finally
    {
      try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
      catch (IOException) { }
      catch (UnauthorizedAccessException) { }
    }
  }

  private static void Check(bool pass, string message)
  {
    if (!pass) throw new Exception(message);
  }

  private static void Near(double? measured, double expected, string message)
  {
    Check(measured.HasValue && Math.Abs(measured.Value - expected) <= 1e-9, message);
  }

  private static void Throws<T>(Action action, string message) where T : Exception
  {
    try { action(); }
    catch (T) { return; }
    throw new Exception(message);
  }

  private static FuturesOpenInterestDataService NewService(string name) =>
    new FuturesOpenInterestDataService(Path.Combine(_root, name));

  private static FuturesOpenInterestObservation Row(string symbol, DateTimeOffset time,
    decimal native = 100m, decimal value = 500m) => new FuturesOpenInterestObservation
    {
      Exchange = "BinanceFutures", Symbol = symbol,
      TimestampUtc = time, Period = FuturesOpenInterest.SourcePeriod,
      IsHistoricalStatistic = true, NativeOpenInterest = native,
      ReportedOpenInterestValue = value
    };

  private static Task<IReadOnlyList<FuturesOpenInterestObservation>> FullPage(
    string symbol, DateTimeOffset end, int limit, CancellationToken ct)
  {
    // Synthetic provider respects pinned endTime and its record limit.
    long periodMs = (long)FuturesOpenInterest.SourcePeriod.TotalMilliseconds;
    long endMs = end.ToUnixTimeMilliseconds();
    DateTimeOffset aligned = DateTimeOffset.FromUnixTimeMilliseconds(endMs - endMs % periodMs);
    IReadOnlyList<FuturesOpenInterestObservation> history = Enumerable.Range(0, limit)
      .Select(i => Row(symbol, aligned.AddMinutes(-5 * (limit - i - 1)),
        native: 100m + i, value: 500m + 2m * i)).ToArray();
    return Task.FromResult(history);
  }

  private static void TestRegistryAndDefaultConfig()
  {
    Check(MarketTrendMetrics.TryGetScope(MarketTrendMetrics.OpenInterestChange,
      out MarketTrendMetricScope a) && a == MarketTrendMetricScope.Asset,
      "OI quantity change must be asset scoped.");
    Check(MarketTrendMetrics.TryGetScope(MarketTrendMetrics.OpenInterestValueChange,
      out MarketTrendMetricScope b) && b == MarketTrendMetricScope.Asset,
      "OI value change must be asset scoped.");
    Check(new MarketTrend().Metrics == "" && MarketTrendMetrics.ParseEnabled(new MarketTrend())
      .SequenceEqual(new[] { MarketTrendMetrics.PriceTrend }),
      "OI must not be selected by default.");
    MarketAnalyzer cfg = new MarketAnalyzer();
    Check(cfg.OpenInterestMaxRequestsPerRaid == 60,
      "Only the OI request budget is configured; historical depth comes from MarketTrends.");
    Check(FuturesOpenInterestFeatureEngine.HasMetrics(new[] { "OpenInterestChange" }) &&
      !FuturesOpenInterestFeatureEngine.HasMetrics(new[] { "PriceTrend" }),
      "Only explicit OI metric selection triggers collection.");
    _passed++;
  }

  private static void TestBudgetAndRetentionBounds()
  {
    FuturesOpenInterestDataService.ValidateBudget(0);
    FuturesOpenInterestDataService.ValidateBudget(400);
    FuturesOpenInterestDataService.ValidateHorizon(5);
    FuturesOpenInterestDataService.ValidateHorizon(2880);
    FuturesOpenInterestDataService.ValidateHorizon(60 * 24 * 40);
    Throws<ArgumentOutOfRangeException>(() => FuturesOpenInterestDataService.ValidateBudget(-1),
      "Negative budget must be rejected.");
    Throws<ArgumentOutOfRangeException>(() => FuturesOpenInterestDataService.ValidateBudget(401),
      "Oversized budget must be rejected.");
    Throws<ArgumentOutOfRangeException>(() => FuturesOpenInterestDataService.ValidateHorizon(7),
      "OI horizon must follow the user-defined 5m-aligned TrendMinutes.");
    _passed++;
  }

  private static async Task TestZeroBudgetIsDisabled()
  {
    int calls = 0;
    var service = NewService("disabled");
    var report = await service.RefreshAsync(new[] { "BTCUSDT" }, 60, Pin, 0,
      (s, e, n, ct) => { calls++; return FullPage(s, e, n, ct); }, Log);
    Check(calls == 0 && report.Requests == 0 && report.DeferredSymbols == 1,
      "Zero budget must disable only OI network requests.");
    _passed++;
  }

  private static async Task TestSingleSymbolExactEndpoints()
  {
    var service = NewService("single");
    var report = await service.RefreshAsync(new[] { "BTCUSDT" }, 60, Pin, 2, FullPage, Log);
    Check(report.AvailableSymbols == 1 && report.SuccessfulPages == 1 &&
      report.Requests == 1 && report.FailedPages == 0,
      "One bounded page must cover exact 60-minute OI.");
    Check(service.Snapshot("BTCUSDT", Pin, 60).Count >= 13, "Source history must be retained.");
    _passed++;
  }

  private static async Task TestRequestBudgetAndFairProgress()
  {
    var service = NewService("fairness");
    List<string> first = new(), second = new();
    string[] universe = { "AAAUSDT", "BBBUSDT", "CCCUSDT" };
    var a = await service.RefreshAsync(universe, 60, Pin, 1,
      (symbol, end, n, ct) => { first.Add(symbol); return FullPage(symbol, end, n, ct); }, Log);
    var b = await service.RefreshAsync(universe, 60, Pin, 1,
      (symbol, end, n, ct) => { second.Add(symbol); return FullPage(symbol, end, n, ct); }, Log);
    Check(first.Count == 1 && second.Count == 1 &&
      first[0] == "AAAUSDT" && second[0] == "BBBUSDT" &&
      a.Requests == 1 && b.Requests == 1, "Budget must enforce fairness across raids.");
    _passed++;
  }

  private static async Task TestEmptyResponseNotFabricated()
  {
    var service = NewService("empty");
    var report = await service.RefreshAsync(new[] { "AAAUSDT" }, 60, Pin, 3,
      (s, e, n, ct) => Task.FromResult<IReadOnlyList<FuturesOpenInterestObservation>>(
        Array.Empty<FuturesOpenInterestObservation>()), Log);
    Check(report.Requests == 1 && report.AvailableSymbols == 0 &&
      service.Snapshot("AAAUSDT", Pin, 60).Count == 0,
      "A provider no-trades gap must not become a valid OI sample.");
    _passed++;
  }

  private static async Task TestMissingEndpointUnavailable()
  {
    var service = NewService("missing");
    var report = await service.RefreshAsync(new[] { "AAAUSDT" }, 60, Pin, 2,
      (s, e, n, ct) => Task.FromResult<IReadOnlyList<FuturesOpenInterestObservation>>(
        new[] { Row(s, Pin) }), Log);
    var feature = FuturesOpenInterestFeatureEngine.Calculate(new[] { "AAAUSDT" },
      new[] { MarketTrendMetrics.OpenInterestChange }, 60, Pin, "BinanceFutures", service);
    Check(report.AvailableSymbols == 0 &&
      !feature["AAAUSDT"][MarketTrendMetrics.OpenInterestChange].IsValid,
      "One endpoint or a missing provider period must remain unavailable.");
    _passed++;
  }

  private static async Task TestIncrementalReuseWhenCurrent()
  {
    var service = NewService("reuse");
    await service.RefreshAsync(new[] { "AAAUSDT" }, 60, Pin, 2, FullPage, Log);
    int called = 0;
    var report = await service.RefreshAsync(new[] { "AAAUSDT" }, 60, Pin, 2,
      (s, e, n, ct) => { called++; return FullPage(s, e, n, ct); }, Log);
    Check(report.AvailableSymbols == 1 && called == 0 && report.Requests == 0,
      "Current exact endpoints must be reused without extra HTTP calls.");
    _passed++;
  }

  private static async Task TestOlderPageRequestedForLongHistory()
  {
    var service = NewService("pagination");
    List<DateTimeOffset> ends = new();
    var report = await service.RefreshAsync(new[] { "AAAUSDT" }, 60 * 48, Pin, 2,
      (s, e, n, ct) => { ends.Add(e); return FullPage(s, e, n, ct); }, Log);
    Check(report.Requests == 2 && ends.Count == 2 && ends[1] < ends[0] &&
      service.Snapshot("AAAUSDT", Pin, 60 * 48).Count >= 500, "Long OI horizon must use bounded older pagination.");
    _passed++;
  }

  private static async Task TestInvalidSourceIsUnavailable()
  {
    var service = NewService("invalid");
    var report = await service.RefreshAsync(new[] { "AAAUSDT" }, 60, Pin, 1,
      (s, e, n, ct) => Task.FromResult<IReadOnlyList<FuturesOpenInterestObservation>>(
        new[] { Row("WRONGUSDT", Pin) }), Log);
    Check(report.FailedPages == 1 && report.AvailableSymbols == 0,
      "A symbol mismatch must be rejected and never counted as available.");
    _passed++;
  }

  private static async Task TestCacheSurvivesServiceRestart()
  {
    var s1 = NewService("disk");
    var old = Pin.AddMinutes(-20);
    await s1.RefreshAsync(new[] { "BTCUSDT" }, 60, old, 2, FullPage, Log);
    var s2 = NewService("disk");
    int requested = 0;
    var report = await s2.RefreshAsync(new[] { "BTCUSDT" }, 60, old, 2,
      (s, e, n, ct) => { requested++; return FullPage(s, e, n, ct); }, Log);
    Check(report.RestoredSymbols == 1 && report.AvailableSymbols == 1 &&
      requested == 0, "Persisted OI cache should avoid duplicate full requests.");
    _passed++;
  }

  private static void TestCorruptCacheFailsOpen()
  {
    var disk = new FuturesOpenInterestDiskCache(Path.Combine(_root, "corrupt"));
    string path = disk.CachePath("AAAUSDT");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, "malformed");
    var items = disk.TryRestore("AAAUSDT", Pin, Log);
    Check(items.Count == 0 && !File.Exists(path), "Invalid cache must be discarded without a fabricated history.");
    _passed++;
  }

  private static async Task TestNativeVsValueChange()
  {
    var service = NewService("separate");
    await service.RefreshAsync(new[] { "AAAUSDT" }, 60, Pin, 1,
      (s, e, n, ct) => Task.FromResult<IReadOnlyList<FuturesOpenInterestObservation>>(
        new[] { Row(s, Pin.AddMinutes(-60), 100m, 1000m), Row(s, Pin, 120m, 2000m) }), Log);
    var result = FuturesOpenInterestFeatureEngine.Calculate(new[] { "AAAUSDT" },
      new[] { MarketTrendMetrics.OpenInterestChange, MarketTrendMetrics.OpenInterestValueChange },
      60, Pin, "BinanceFutures", service);
    Near(result["AAAUSDT"][MarketTrendMetrics.OpenInterestChange].Value, 20, "Native OI change must be 20%.");
    Near(result["AAAUSDT"][MarketTrendMetrics.OpenInterestValueChange].Value, 100,
      "Provider-reported value change must be 100%.");
    _passed++;
  }

  private static async Task TestExchangeScopeAndOptIn()
  {
    var service = NewService("scope");
    await service.RefreshAsync(new[] { "AAAUSDT" }, 60, Pin, 1, FullPage, Log);
    var result = FuturesOpenInterestFeatureEngine.Calculate(new[] { "AAAUSDT" },
      new[] { MarketTrendMetrics.OpenInterestChange }, 60, Pin, "BinanceUS", service);
    Check(!result["AAAUSDT"][MarketTrendMetrics.OpenInterestChange].IsValid &&
      result["AAAUSDT"][MarketTrendMetrics.OpenInterestChange].InvalidReason.Contains("Futures"),
      "Spot/BinanceUS cannot inherit USD-M OI cache by accident.");
    _passed++;
  }

  private static void TestHistoryRequirementsIndependent()
  {
    var trend = new MarketTrend { Name = "OI", Platform = "Exchange", TrendMinutes = 60,
      Metrics = MarketTrendMetrics.OpenInterestChange };
    var r = MarketTrendHistoryRequirements.Describe(trend, MarketTrendMetrics.ParseEnabled(trend),
      TimeSpan.FromMinutes(5), Pin, configuredStoreDataMaxHours: 1);
    Check(r.Count == 1 && r[0].SourceDataFamily == "BinanceFuturesOpenInterest" &&
      r[0].HasAllAlignmentCapacity == null && r[0].RequiredStoreDataMaxHours == 0 &&
      r[0].CalculationWindowMinutes == 60,
      "Independent OI history must not be constrained by StoreDataMaxHours.");
    _passed++;
  }

  private static void TestHistoryDerivedFromEnabledTrends()
  {
    var a = new MarketTrend { Name = "M30", Platform = "Exchange", TrendMinutes = 30,
      Metrics = MarketTrendMetrics.OpenInterestChange };
    var b = new MarketTrend { Name = "H8", Platform = "Exchange", TrendMinutes = 480,
      Metrics = MarketTrendMetrics.OpenInterestValueChange };
    var irrelevant = new MarketTrend { Name = "H24", Platform = "Exchange", TrendMinutes = 1440,
      Metrics = MarketTrendMetrics.PriceTrend };
    var other = new MarketTrend { Name = "NotExchange", Platform = "Other", TrendMinutes = 4320,
      Metrics = MarketTrendMetrics.OpenInterestChange };
    Check(FuturesOpenInterestDataService.RequiredHorizonMinutes(new[] { a, b, irrelevant, other }) == 480,
      "Longest eligible OI MarketTrend must control cache history, not PriceTrend/other platform.");
    b.TrendMinutes = 60 * 24 * 35;
    Check(FuturesOpenInterestDataService.RequiredHorizonMinutes(new[] { a, b }) == 60 * 24 * 35,
      "Application must not silently truncate a user-configured long trend to 72h or 672h.");
    Check(FuturesOpenInterestDataService.RequiredHorizonMinutes(new[] { irrelevant }) == 0,
      "Zero explicit OI trends must require no OI data.");
    _passed++;
  }

  private static async Task TestLongTrendDoesNotGetTruncated()
  {
    var service = NewService("longdynamic");
    const int horizon = 60 * 24 * 4;
    var report = await service.RefreshAsync(new[] { "AAAUSDT" }, horizon, Pin, 1, FullPage, Log);
    Check(report.Requests == 1 && report.AvailableSymbols == 0,
      "A partially downloaded four-day history must not be considered available.");
    var snapshot = service.Snapshot("AAAUSDT", Pin, horizon);
    Check(snapshot.Count > 400 && snapshot[0].TimestampUtc >= Pin.AddDays(-4).AddMinutes(-5),
      "Long user-defined horizon must retain available history without a 72-hour ceiling.");
    var r = MarketTrendHistoryRequirements.Describe(
      new MarketTrend { Name = "FourDays", Platform = "Exchange", TrendMinutes = horizon,
        Metrics = MarketTrendMetrics.OpenInterestChange },
      new[] { MarketTrendMetrics.OpenInterestChange }, TimeSpan.FromMinutes(5), Pin, 1);
    Check(r.Single().CalculationWindowMinutes == horizon &&
      r.Single().SourceDataFamily == "BinanceFuturesOpenInterest" &&
      r.Single().RequiredStoreDataMaxHours == 0 && r.Single().ConfiguredStoreDataMaxHours == null,
      "OI requirements must not inherit the independent OHLCV retention setting.");
    _passed++;
  }
}
