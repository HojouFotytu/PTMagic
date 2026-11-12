using System;
using System.IO;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Helper;
using Core.Main.DataObjects.PTMagicData;
using Core.MarketAnalyzer.MarketData;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

internal static class Program
{
  private static readonly DateTimeOffset Pin = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
  private static readonly LogHelper Log = new LogHelper(NullLogger<LogHelper>.Instance);
  private static int _passed;

  private static void Assert(bool value, string message)
  {
    if (!value) throw new Exception(message);
  }

  private static void Near(double? got, double expected, string message)
  {
    Assert(got.HasValue && Math.Abs(got.Value - expected) < 1e-10, message);
  }

  private static void MustThrow<T>(Action call) where T : Exception
  {
    try { call(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
  }

  private static FuturesFundingObservation Event(string symbol, int minutesAgo, decimal rate,
    string type = "Regular") => new FuturesFundingObservation
    {
      Symbol = symbol, FundingTimeUtc = Pin.AddMinutes(-minutesAgo),
      NativeRate = rate, RateType = type, MarkPrice = 100m
    };

  private static Task<IReadOnlyList<FuturesFundingObservation>> Read(
    string symbol, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken ct) =>
    Task.FromResult<IReadOnlyList<FuturesFundingObservation>>(
      new[] { Event(symbol, 120, 0.0001m), Event(symbol, 60, -0.0002m) }
        .Where(row => row.FundingTimeUtc >= from && row.FundingTimeUtc <= to)
        .Take(limit).ToArray());

  private static void TestFundingDiskCache()
  {
    string root = Path.Combine(Path.GetTempPath(), "PTM_FundingCache_" + Guid.NewGuid().ToString("N"));
    try
    {
      var disk = new FuturesFundingDiskCache(root);
      var rows = new[]
      {
        new FuturesFundingObservation
        {
          Symbol = "BTCUSDT", FundingTimeUtc = Pin.AddHours(-1),
          NativeRate = -0.0002m, RateType = "Regular", MarkPrice = 123.45m
        }
      };
      long start = Pin.AddDays(-1).ToUnixTimeMilliseconds();
      long end = Pin.ToUnixTimeMilliseconds();
      Assert(disk.TrySave("BTCUSDT", rows, new[] { (start, end) }, Pin, Log),
        "Funding cache should save observations and verified query coverage.");
      var restored = disk.TryRestore("BTCUSDT", Pin, Log);
      Assert(restored.Rows.Count == 1 && restored.CompleteIntervals.Count == 1 &&
        restored.Rows[0].NativeRate == -0.0002m && restored.Rows[0].MarkPrice == 123.45m,
        "Funding cache should restore exact native decimals and coverage after restart.");
      string corrupt = disk.CachePath("ETHUSDT");
      Directory.CreateDirectory(Path.GetDirectoryName(corrupt)!);
      File.WriteAllText(corrupt, "corrupt");
      Assert(disk.TryRestore("ETHUSDT", Pin, Log).Rows.Count == 0 && !File.Exists(corrupt),
        "Corrupt funding cache should be discarded without fabricating data.");
    }
    finally
    {
      if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
    _passed++;
  }

  private static async Task TestServiceRestartPersistence()
  {
    string root = Path.Combine(Path.GetTempPath(), "PTM_FundingServiceRestart_" + Guid.NewGuid().ToString("N"));
    try
    {
      int firstRequests = 0;
      var first = new FuturesFundingDataService(root);
      var initial = await first.RefreshAsync(new[] { "BTCUSDT" }, 180, Pin, 2,
        (symbol, from, to, limit, ct) =>
        {
          firstRequests++;
          return Read(symbol, from, to, limit, ct);
        }, Log);
      Assert(firstRequests == 1 && initial.CompleteSymbols == 1, "First funding service persists complete coverage.");

      int restartRequests = 0;
      var restarted = new FuturesFundingDataService(root);
      var restored = await restarted.RefreshAsync(new[] { "BTCUSDT" }, 180, Pin, 2,
        (symbol, from, to, limit, ct) =>
        {
          restartRequests++;
          return Read(symbol, from, to, limit, ct);
        }, Log);
      Assert(restored.CompleteSymbols == 1 && restartRequests == 0,
        "New funding service restores complete coverage without another provider request.");
      Assert(restarted.TrySnapshot("BTCUSDT", 180, Pin, out var history) &&
        history.Count == 2 && history.Sum(row => row.NativeRate) == -0.0001m,
        "Restarted funding service exposes the same restored observations.");

      string corruptPath = new FuturesFundingDiskCache(root).CachePath("ETHUSDT");
      Directory.CreateDirectory(Path.GetDirectoryName(corruptPath)!);
      File.WriteAllText(corruptPath, "corrupt");
      int repairRequests = 0;
      var repair = new FuturesFundingDataService(root);
      var repaired = await repair.RefreshAsync(new[] { "ETHUSDT" }, 180, Pin, 1,
        (symbol, from, to, limit, ct) =>
        {
          repairRequests++;
          return Read(symbol, from, to, limit, ct);
        }, Log);
      var repairedCache = new FuturesFundingDiskCache(root).TryRestore("ETHUSDT", Pin, Log);
      Assert(repairRequests == 1 && repaired.Requests == 1 && repaired.CompleteSymbols == 1 &&
        File.Exists(corruptPath) && repairedCache.Rows.Count == 2 &&
        repairedCache.CompleteIntervals.Count > 0,
        "Corrupt funding cache is replaced with valid observations and coverage after one bounded request.");
    }
    finally
    {
      if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
    _passed++;
  }

  private static void TestPayload()
  {
    var raw = new JArray(new JObject
    {
      ["symbol"] = "BTCUSDT", ["fundingRate"] = "-0.000125",
      ["fundingTime"] = Pin.AddHours(-1).ToUnixTimeMilliseconds(),
      ["markPrice"] = "110.345", ["rateType"] = "Regular"
    });
    var result = FuturesFunding.MapHistory(raw, "BTCUSDT", Pin);
    Assert(result.Count == 1 && result[0].NativeRate == -0.000125m &&
      result[0].MarkPrice == 110.345m && result[0].IsRegular,
      "Native rate, mark price, and rate type must survive mapping.");
    _passed++;
  }

  private static void TestSpecialAndUnknownTypes()
  {
    var raw = new JArray(
      new JObject { ["symbol"]="BTCUSDT", ["fundingRate"]="0.0001", ["fundingTime"]=Pin.AddHours(-1).ToUnixTimeMilliseconds(), ["rateType"]="Special" },
      new JObject { ["symbol"]="BTCUSDT", ["fundingRate"]="0.0002", ["fundingTime"]=Pin.AddHours(-1).ToUnixTimeMilliseconds(), ["rateType"]="Regular" },
      new JObject { ["symbol"]="BTCUSDT", ["fundingRate"]="0.0003", ["fundingTime"]=Pin.AddHours(-2).ToUnixTimeMilliseconds() });
    var mapped = FuturesFunding.MapHistory(raw, "BTCUSDT", Pin);
    Assert(mapped.Count == 3 && mapped.Count(x => x.IsRegular) == 1 &&
      mapped.Any(x => x.RateType == "Unknown"), "Rate types must remain separate, not coerced.");
    _passed++;
  }

  private static void TestPayloadFailures()
  {
    JArray Raw(string symbol, string rate, long time) =>
      new JArray(new JObject { ["symbol"]=symbol, ["fundingRate"]=rate, ["fundingTime"]=time });
    MustThrow<InvalidDataException>(() => FuturesFunding.MapHistory(Raw("ETHUSDT","0.01",Pin.ToUnixTimeMilliseconds()),"BTCUSDT",Pin));
    MustThrow<InvalidDataException>(() => FuturesFunding.MapHistory(Raw("BTCUSDT","not-a-rate",Pin.ToUnixTimeMilliseconds()),"BTCUSDT",Pin));
    MustThrow<InvalidDataException>(() => FuturesFunding.MapHistory(Raw("BTCUSDT","0.01",-1),"BTCUSDT",Pin));
    MustThrow<InvalidDataException>(() => FuturesFunding.MapHistory(Raw("BTCUSDT","0,01",Pin.ToUnixTimeMilliseconds()),"BTCUSDT",Pin));
    _passed++;
  }

  private static void TestConflictAndPin()
  {
    var a = new JObject { ["symbol"]="BTCUSDT", ["fundingRate"]="0.0001", ["fundingTime"]=Pin.ToUnixTimeMilliseconds(), ["rateType"]="Regular" };
    var b = (JObject)a.DeepClone();
    b["fundingRate"] = "0.0002";
    MustThrow<InvalidDataException>(() => FuturesFunding.MapHistory(new JArray(a,b),"BTCUSDT",Pin));
    var future = (JObject)a.DeepClone();
    future["fundingTime"] = Pin.AddSeconds(1).ToUnixTimeMilliseconds();
    Assert(FuturesFunding.MapHistory(new JArray(a,future),"BTCUSDT",Pin).Count == 1,
      "Future settlements must not leak into the pinned analysis.");
    _passed++;
  }

  private static void TestUrl()
  {
    string url = BinanceFuturesFundingClient.HistoryUrl("btcusdt",
      Pin.AddDays(-2), Pin, 1000);
    Assert(url.Contains("/fapi/v1/fundingRate?symbol=BTCUSDT") &&
      url.Contains("startTime=" + Pin.AddDays(-2).ToUnixTimeMilliseconds()) &&
      url.Contains("endTime=" + Pin.ToUnixTimeMilliseconds()) &&
      url.Contains("limit=1000"), "Funding request requires bounded start and end.");
    MustThrow<ArgumentOutOfRangeException>(() => BinanceFuturesFundingClient.HistoryUrl(
      "BTCUSDT", Pin, Pin.AddDays(-1), 1));
    MustThrow<ArgumentOutOfRangeException>(() => BinanceFuturesFundingClient.HistoryUrl(
      "BTCUSDT", Pin.AddHours(-1), Pin, 1001));
    _passed++;
  }

  private static async Task TestUnicodeSymbolServiceAndCache()
  {
    const string symbol = "币安人生USDT";
    string root = Path.Combine(Path.GetTempPath(), "PTM_FundingUnicode_" + Guid.NewGuid().ToString("N"));
    try
    {
      var service = new FuturesFundingDataService(root);
      var report = await service.RefreshAsync(new[] { symbol }, 60, Pin, 1,
        (requested, from, to, limit, ct) =>
          Task.FromResult<IReadOnlyList<FuturesFundingObservation>>(
            new[] { Event(requested, 30, 0.0001m) }), Log);
      Assert(report.Requests == 1 && report.CompleteSymbols == 1 &&
        service.TrySnapshot(symbol, 60, Pin, out var snapshot) && snapshot.Count == 1,
        "Funding service accepts and calculates history for a Unicode Binance symbol.");
      var restored = new FuturesFundingDiskCache(root).TryRestore(symbol, Pin, Log);
      Assert(restored.Rows.Count == 1 && restored.Rows[0].Symbol == symbol,
        "Funding disk cache restores Unicode symbol identity.");
      string url = BinanceFuturesFundingClient.HistoryUrl(symbol, Pin.AddHours(-1), Pin, 100);
      Assert(url.Contains(Uri.EscapeDataString(symbol), StringComparison.Ordinal),
        "Funding request URL encodes Unicode Binance symbols.");
    }
    finally
    {
      if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
    _passed++;
  }

  private static void TestRegistryAndDerivedHorizon()
  {
    Assert(MarketTrendMetrics.TryGetScope(MarketTrendMetrics.FundingRate, out var rate) &&
      rate == MarketTrendMetricScope.Asset &&
      MarketTrendMetrics.TryGetScope(MarketTrendMetrics.FundingSum, out var sum) &&
      sum == MarketTrendMetricScope.Asset, "Funding metrics must be asset scoped.");
    var horizons = new[] {
      new MarketTrend { Name="H1",TrendMinutes=60,Metrics="FundingRate" },
      new MarketTrend { Name="H80",TrendMinutes=4800,Metrics="FundingSum" },
      new MarketTrend { Name="LongPrice",TrendMinutes=10080,Metrics="PriceTrend" }
    };
    Assert(FuturesFundingDataService.RequiredHorizonMinutes(horizons) == 4800 &&
      FuturesFundingDataService.RequiredHorizonMinutes(new[] { horizons[2] }) == 0,
      "Funding retention must derive solely from selected funding MarketTrends.");
    horizons[1].TrendMinutes = 60 * 24 * 40;
    Assert(FuturesFundingDataService.RequiredHorizonMinutes(horizons) == 60 * 24 * 40,
      "User TrendMinutes must not be silently capped at fixed 72h/8h/28d.");
    _passed++;
  }

  private static async Task TestCompleteWindowAndUnits()
  {
    var service = new FuturesFundingDataService();
    var report = await service.RefreshAsync(new[]{"BTCUSDT"},180,Pin,2,Read,Log);
    Assert(report.CompleteSymbols == 1 && report.Requests == 1,"Complete provider page should verify window.");
    var feature = FuturesFundingFeatureEngine.Calculate(new[]{"BTCUSDT"},
      new[]{"FundingRate","FundingSum"},180,Pin,"BinanceFutures",service)["BTCUSDT"];
    Near(feature["FundingRate"].Value,-0.02,"Latest negative regular funding, percentage points.");
    Near(feature["FundingSum"].Value,-0.01,"Sum of different settlement times, percentage points.");
    Assert(feature["FundingRate"].SourceDataTimestampUtc == Pin.AddMinutes(-60),"Preserve settlement timestamp.");
    _passed++;
  }

  private static async Task TestSpecialExcluded()
  {
    var service = new FuturesFundingDataService();
    await service.RefreshAsync(new[]{"BTCUSDT"},180,Pin,1,
      (s,f,t,n,ct)=>Task.FromResult<IReadOnlyList<FuturesFundingObservation>>(
        new[] { Event(s,60,0.0002m,"Regular"),Event(s,60,0.5m,"Special") }),Log);
    var metrics=FuturesFundingFeatureEngine.Calculate(new[]{"BTCUSDT"},
      new[]{"FundingRate","FundingSum"},180,Pin,"BinanceFutures",service)["BTCUSDT"];
    Near(metrics["FundingRate"].Value,0.02,"Special event cannot contaminate FundingRate.");
    Near(metrics["FundingSum"].Value,0.02,"Special event cannot contaminate FundingSum.");
    _passed++;
  }

  private static async Task TestUnknownTypeFailsClosed()
  {
    var service = new FuturesFundingDataService();
    await service.RefreshAsync(new[]{"BTCUSDT"},180,Pin,1,
      (symbol,from,to,limit,ct) => Task.FromResult<IReadOnlyList<FuturesFundingObservation>>(
        new[]{Event(symbol,60,0.0002m,"Unknown")}),Log);
    var metrics = FuturesFundingFeatureEngine.Calculate(new[]{"BTCUSDT"},
      new[]{"FundingRate","FundingSum"},180,Pin,"BinanceFutures",service)["BTCUSDT"];
    Assert(!metrics["FundingRate"].IsValid && !metrics["FundingSum"].IsValid,
      "An unclassified historical funding event cannot be ignored or counted as zero.");
    _passed++;
  }

  private static async Task TestZeroAndMissing()
  {
    var service=new FuturesFundingDataService();
    var notReady=FuturesFundingFeatureEngine.Calculate(new[]{"BTCUSDT"},new[]{"FundingSum"},
      60,Pin,"BinanceFutures",service);
    Assert(!notReady["BTCUSDT"]["FundingSum"].IsValid,"Uncollected is not zero.");
    await service.RefreshAsync(new[]{"BTCUSDT"},60,Pin,1,
      (s,f,t,n,ct)=>Task.FromResult<IReadOnlyList<FuturesFundingObservation>>(
        Array.Empty<FuturesFundingObservation>()),Log);
    var ready=FuturesFundingFeatureEngine.Calculate(new[]{"BTCUSDT"},
      new[]{"FundingSum","FundingRate"},60,Pin,"BinanceFutures",service)["BTCUSDT"];
    Near(ready["FundingSum"].Value,0,"Verified empty period should sum to zero.");
    Assert(!ready["FundingRate"].IsValid,"No regular settlement has no latest rate.");
    _passed++;
  }

  private static async Task TestBudgetFairness()
  {
    var service = new FuturesFundingDataService();
    var calls = new List<string>();
    FundingHistoryLoader loader=(s,f,t,n,ct)=>{
      calls.Add(s);
      return Task.FromResult<IReadOnlyList<FuturesFundingObservation>>(
        Array.Empty<FuturesFundingObservation>());
    };
    var first=await service.RefreshAsync(new[]{"AAAUSDT","BBBUSDT","CCCUSDT"},60,Pin,1,loader,Log);
    var second=await service.RefreshAsync(new[]{"AAAUSDT","BBBUSDT","CCCUSDT"},60,Pin,1,loader,Log);
    Assert(first.Requests==1 && second.Requests==1 && calls.SequenceEqual(new[]{"AAAUSDT","BBBUSDT"}),
      "Budget must be respected and unserved symbols must progress.");
    var disabled=await service.RefreshAsync(new[]{"AAAUSDT"},60,Pin,0,loader,Log);
    Assert(disabled.Requests==0 && calls.Count==2,"Zero disables network acquisition.");
    _passed++;
  }

  private static async Task TestWrongSymbolAtomic()
  {
    var service=new FuturesFundingDataService();
    var report=await service.RefreshAsync(new[]{"BTCUSDT"},60,Pin,1,
      (s,f,t,n,ct)=>Task.FromResult<IReadOnlyList<FuturesFundingObservation>>(
        new[]{Event("BTCUSDT",30,0.0001m),Event("ETHUSDT",20,0.0002m)}),Log);
    Assert(report.Failures==1 && report.CompleteSymbols==0 &&
      !service.TrySnapshot("BTCUSDT",60,Pin,out _),
      "One bad row must not partially accept a provider page.");
    _passed++;
  }

  private static async Task TestPinnedAndReused()
  {
    var service=new FuturesFundingDataService();
    int called=0;
    FundingHistoryLoader loader=(s,f,t,n,ct)=>{
      called++;
      return Read(s,f,t,n,ct);
    };
    await service.RefreshAsync(new[]{"BTCUSDT"},180,Pin,1,loader,Log);
    var second=await service.RefreshAsync(new[]{"BTCUSDT"},180,Pin,1,loader,Log);
    Assert(called==1 && second.Requests==0,"Verified coverage must reuse within same pinned raid.");
    Assert(!service.TrySnapshot("BTCUSDT",240,Pin,out _),
      "A larger user TrendMinutes requires more history, not a shorter fallback.");
    _passed++;
  }

  private static async Task TestBeyondOnePage()
  {
    var service = new FuturesFundingDataService();
    var requested = new List<DateTimeOffset>();
    FundingHistoryLoader provider=(s,f,t,n,ct)=>{
      requested.Add(f);
      IReadOnlyList<FuturesFundingObservation> page = Enumerable.Range(0,
        requested.Count==1?1000:1).Select(i=>Event(s, 100000 - (requested.Count-1)*1000-i,0.0001m)).ToArray();
      return Task.FromResult(page);
    };
    // A long user-defined horizon is valid; the first full page cannot
    // certify coverage and must request the next page.
    var report=await service.RefreshAsync(new[]{"BTCUSDT"},120000,Pin,2,provider,Log);
    Assert(report.Requests==2 && requested.Count==2 && requested[1]>requested[0],
      "Ascending pages must advance the startTime.");
    _passed++;
  }

  private static async Task TestPartialPageFailsClosed()
  {
    var service = new FuturesFundingDataService();
    int calls = 0;
    FundingHistoryLoader provider = (symbol, from, to, limit, ct) =>
    {
      calls++;
      int count = calls == 1 ? 1000 : 1;
      IReadOnlyList<FuturesFundingObservation> rows = Enumerable.Range(0,count)
        .Select(index => Event(symbol,100000 - (calls-1)*1000 - index,0.0001m))
        .ToArray();
      return Task.FromResult(rows);
    };
    const int trend = 120000;
    var first = await service.RefreshAsync(new[]{"BTCUSDT"},trend,Pin,1,provider,Log);
    Assert(first.Requests==1 && first.CompleteSymbols==0 &&
      !service.TrySnapshot("BTCUSDT",trend,Pin,out _),
      "One full page is only partial evidence and must never publish a funding metric.");
    var second = await service.RefreshAsync(new[]{"BTCUSDT"},trend,Pin,1,provider,Log);
    Assert(second.Requests==1 && second.CompleteSymbols==1 &&
      service.TrySnapshot("BTCUSDT",trend,Pin,out _),
      "The following raid must resume and complete the remaining funding history.");
    _passed++;
  }

  private static FuturesFundingObservation At(string symbol, DateTimeOffset when) => new FuturesFundingObservation
  {
    Symbol = symbol, FundingTimeUtc = when, NativeRate = 0.0001m,
    RateType = "Regular", MarkPrice = 100m
  };

  private static async Task TestPartialCoverageSurvivesRestart()
  {
    string root = Path.Combine(Path.GetTempPath(), "PTM_FundingPartialRestart_" + Guid.NewGuid().ToString("N"));
    try
    {
      const int trend = 120000;
      DateTimeOffset firstLast = default;
      var first = new FuturesFundingDataService(root);
      var firstReport = await first.RefreshAsync(new[] { "BTCUSDT" }, trend, Pin, 1,
        (symbol, from, to, limit, ct) =>
        {
          var page = Enumerable.Range(1, 1000)
            .Select(index => At(symbol, from.AddMinutes(index))).ToArray();
          firstLast = page.Last().FundingTimeUtc;
          return Task.FromResult<IReadOnlyList<FuturesFundingObservation>>(page);
        }, Log);
      Assert(firstReport.Requests == 1 && firstReport.CompleteSymbols == 0,
        "First funding service stores one bounded partial page.");

      DateTimeOffset secondFrom = default;
      DateTimeOffset secondLast = default;
      var second = new FuturesFundingDataService(root);
      var secondReport = await second.RefreshAsync(new[] { "BTCUSDT" }, trend, Pin, 1,
        (symbol, from, to, limit, ct) =>
        {
          secondFrom = from;
          var page = Enumerable.Range(0, 1000)
            .Select(index => At(symbol, from.AddMinutes(index))).ToArray();
          secondLast = page.Last().FundingTimeUtc;
          return Task.FromResult<IReadOnlyList<FuturesFundingObservation>>(page);
        }, Log);
      Assert(secondReport.Requests == 1 && secondReport.CompleteSymbols == 0 &&
        secondFrom == firstLast,
        "Restarted funding service resumes at the first uncovered settlement boundary.");

      DateTimeOffset thirdFrom = default;
      var third = new FuturesFundingDataService(root);
      var thirdReport = await third.RefreshAsync(new[] { "BTCUSDT" }, trend, Pin, 1,
        (symbol, from, to, limit, ct) =>
        {
          thirdFrom = from;
          return Task.FromResult<IReadOnlyList<FuturesFundingObservation>>(
            Array.Empty<FuturesFundingObservation>());
        }, Log);
      Assert(thirdReport.Requests == 1 && thirdFrom == secondLast,
        "Second partial funding page is persisted immediately and survives another restart.");
    }
    finally
    {
      if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
    _passed++;
  }

  private static void TestSpotUnavailable()
  {
    var result=FuturesFundingFeatureEngine.Calculate(new[]{"BTCUSDT"},
      new[]{"FundingRate"},60,Pin,"BinanceUS",new FuturesFundingDataService());
    Assert(!result["BTCUSDT"]["FundingRate"].IsValid,"Spot/US must not inherit Futures funding.");
    _passed++;
  }

  private static void TestHistoryRequirements()
  {
    var t=new MarketTrend { Name="FundingH24", TrendMinutes=1440,
      Metrics="FundingRate,FundingSum" };
    var details=MarketTrendHistoryRequirements.Describe(t,MarketTrendMetrics.ParseEnabled(t),
      TimeSpan.FromMinutes(5),Pin,configuredStoreDataMaxHours:1);
    Assert(details.Count==2 && details.All(x=>x.SourceDataFamily=="BinanceFuturesFunding" &&
      x.CalculationWindowMinutes==1440 && x.ConfiguredStoreDataMaxHours==null &&
      x.HasAllAlignmentCapacity==null),"Funding history must not inherit OHLCV StoreDataMaxHours.");
    _passed++;
  }


  private static async Task TestBulkCompleteAllSymbols()
  {
    var service = new FuturesFundingDataService();
    int symbolCalls = 0, bulkCalls = 0;
    var report = await service.RefreshWithBulkAsync(new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT" },
      60, Pin, 10,
      (symbol, from, to, limit, ct) =>
      {
        symbolCalls++;
        return Read(symbol, from, to, limit, ct);
      },
      (from, to, limit, ct) =>
      {
        bulkCalls++;
        return Task.FromResult<IReadOnlyList<FuturesFundingObservation>>(new[]
        {
          Event("BTCUSDT", 30, 0.0001m),
          Event("ETHUSDT", 30, -0.0002m)
        });
      }, Log);
    Assert(report.CompleteSymbols == 3 && report.BulkRequests == 1 &&
      report.SymbolRequests == 0 && symbolCalls == 0 && bulkCalls == 1,
      "One exhausted bulk page must certify every selected symbol, including empty SOL history.");
    Assert(service.TrySnapshot("ETHUSDT", 60, Pin, out var eth) && eth.Count == 1 &&
      eth[0].NativeRate == -0.0002m &&
      service.TrySnapshot("SOLUSDT", 60, Pin, out var sol) && sol.Count == 0,
      "Bulk observations retain per-symbol identities and verified empty windows.");
    _passed++;
  }

  private static async Task TestBulkFullPageBoundary()
  {
    var service = new FuturesFundingDataService();
    DateTimeOffset boundary = Pin.AddMinutes(-20);
    int calls = 0;
    var report = await service.RefreshWithBulkAsync(new[] { "BTCUSDT", "ETHUSDT" },
      60, Pin, 10, Read,
      (from, to, limit, ct) =>
      {
        calls++;
        if (calls == 1)
        {
          var page = Enumerable.Range(0, 999).Select(index =>
            Event("OTHER" + index.ToString("D4") + "USDT", 30, 0.0001m)).ToList();
          page.Add(Event("BTCUSDT", 20, 0.0002m));
          return Task.FromResult<IReadOnlyList<FuturesFundingObservation>>(page);
        }
        Assert(from == boundary, "Full bulk page must repeat its final timestamp inclusively.");
        return Task.FromResult<IReadOnlyList<FuturesFundingObservation>>(new[]
        {
          Event("BTCUSDT", 20, 0.0002m),
          Event("ETHUSDT", 20, -0.0003m)
        });
      }, Log);
    Assert(calls == 2 && report.BulkRequests == 2 &&
      report.SymbolRequests == 0 && report.CompleteSymbols == 2 &&
      service.TrySnapshot("BTCUSDT", 60, Pin, out var btc) &&
      btc.Count == 1 && btc[0].NativeRate == 0.0002m &&
      service.TrySnapshot("ETHUSDT", 60, Pin, out var eth) &&
      eth.Count == 1 && eth[0].NativeRate == -0.0003m,
      "Full-page timestamp overlap must retain all settlements without substituting missing rows.");
    _passed++;
  }

  private static async Task TestBulkDenseTimestampFallback()
  {
    var service = new FuturesFundingDataService();
    int bulkCalls = 0, fallbackCalls = 0;
    var report = await service.RefreshWithBulkAsync(new[] { "BTCUSDT", "ETHUSDT" },
      60, Pin, 10,
      (symbol, from, to, limit, ct) =>
      {
        fallbackCalls++;
        return Task.FromResult<IReadOnlyList<FuturesFundingObservation>>(new[]
        {
          Event(symbol, 30, 0.0001m)
        });
      },
      (from, to, limit, ct) =>
      {
        bulkCalls++;
        return Task.FromResult<IReadOnlyList<FuturesFundingObservation>>(
          Enumerable.Range(0, 1000).Select(index =>
            new FuturesFundingObservation
            {
              Symbol = "OTHER" + index.ToString("D4") + "USDT",
              FundingTimeUtc = from, NativeRate = 0.0001m,
              RateType = "Regular", MarkPrice = 100m
            }).ToArray());
      }, Log);
    Assert(bulkCalls == 1 && fallbackCalls == 2 && report.BulkRequests == 1 &&
      report.SymbolRequests == 2 && report.CompleteSymbols == 2,
      "A saturated shared timestamp must not certify incomplete bulk history.");
    _passed++;
  }

  private static async Task TestBulkProviderFailureFallback()
  {
    var service = new FuturesFundingDataService();
    int fallbackCalls = 0;
    var report = await service.RefreshWithBulkAsync(new[] { "BTCUSDT", "ETHUSDT" },
      60, Pin, 10,
      (symbol, from, to, limit, ct) =>
      {
        fallbackCalls++;
        return Task.FromResult<IReadOnlyList<FuturesFundingObservation>>(
          new[] { Event(symbol, 30, 0.0001m) });
      },
      (from, to, limit, ct) =>
        throw new InvalidDataException("deterministic malformed bulk page"),
      Log);
    Assert(report.BulkRequests == 1 && report.Failures == 1 &&
      fallbackCalls == 2 && report.CompleteSymbols == 2,
      "Malformed bulk response must use existing strict per-symbol fallback.");
    _passed++;
  }

  private static async Task TestBulkUnboundedFallbackBudget()
  {
    var service = new FuturesFundingDataService();
    int bulkCalls = 0, fallbackCalls = 0;
    var report = await service.RefreshWithBulkAsync(new[] { "BTCUSDT", "ETHUSDT" },
      60, Pin, int.MaxValue,
      (symbol, from, to, limit, ct) =>
      {
        fallbackCalls++;
        return Task.FromResult<IReadOnlyList<FuturesFundingObservation>>(
          new[] { Event(symbol, 30, 0.0001m) });
      },
      (from, to, limit, ct) =>
      {
        bulkCalls++;
        throw new InvalidDataException("deterministic bulk failure");
      },
      Log);
    Assert(bulkCalls == 1 && fallbackCalls == 2 &&
      report.BulkRequests == 1 && report.SymbolRequests == 2 &&
      report.Failures == 1 && report.CompleteSymbols == 2,
      "An unbounded runtime budget must remain unbounded when bulk falls back.");
    _passed++;
  }

  private static async Task TestBulkPersistence()
  {
    string root = Path.Combine(Path.GetTempPath(), "PTM_BulkFunding_" + Guid.NewGuid().ToString("N"));
    try
    {
      var initial = new FuturesFundingDataService(root);
      var first = await initial.RefreshWithBulkAsync(new[] { "BTCUSDT", "ETHUSDT" },
        60, Pin, 10, Read,
        (from, to, limit, ct) => Task.FromResult<IReadOnlyList<FuturesFundingObservation>>(
          new[] { Event("BTCUSDT", 30, 0.0001m) }), Log);
      Assert(first.BulkRequests == 1 && first.CompleteSymbols == 2,
        "Bulk verified histories must be persisted.");
      var restarted = new FuturesFundingDataService(root);
      int network = 0;
      var second = await restarted.RefreshWithBulkAsync(new[] { "BTCUSDT", "ETHUSDT" },
        60, Pin, 10,
        (symbol, from, to, limit, ct) =>
        {
          network++;
          return Read(symbol, from, to, limit, ct);
        },
        (from, to, limit, ct) =>
        {
          network++;
          return Task.FromResult<IReadOnlyList<FuturesFundingObservation>>(
            Array.Empty<FuturesFundingObservation>());
        }, Log);
      Assert(second.CompleteSymbols == 2 && second.Requests == 0 && network == 0,
        "Restart must reuse persisted bulk funding proof without another API request.");
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    _passed++;
  }

  private static void TestBulkMappingAndUrl()
  {
    string url = BinanceFuturesFundingClient.HistoryUrl(null, Pin.AddHours(-1), Pin, 1000);
    Assert(url.Contains("/fapi/v1/fundingRate?startTime=", StringComparison.Ordinal) &&
      !url.Contains("symbol=", StringComparison.Ordinal),
      "Bulk URL must omit symbol and preserve exact request bounds.");
    var raw = new JArray(
      new JObject { ["symbol"]="BTCUSDT",["fundingRate"]="0.0001",["fundingTime"]=Pin.AddMinutes(-30).ToUnixTimeMilliseconds() },
      new JObject { ["symbol"]="ETHUSDT",["fundingRate"]="-0.0002",["fundingTime"]=Pin.AddMinutes(-20).ToUnixTimeMilliseconds() });
    var mapped = FuturesFunding.MapBulkHistory(raw, Pin);
    Assert(mapped.Count == 2 && mapped[0].Symbol == "BTCUSDT" &&
      mapped[1].Symbol == "ETHUSDT" &&
      mapped[1].NativeRate == -0.0002m,
      "Bulk mapping must preserve each source symbol and native rates.");
    _passed++;
  }

  private static async Task<int> Main()
  {
    try
    {
      TestFundingDiskCache();
      await TestServiceRestartPersistence();
      await TestPartialCoverageSurvivesRestart();
      TestPayload();
      TestSpecialAndUnknownTypes();
      TestPayloadFailures();
      TestConflictAndPin();
      TestUrl();
      await TestUnicodeSymbolServiceAndCache();
      TestRegistryAndDerivedHorizon();
      await TestCompleteWindowAndUnits();
      await TestSpecialExcluded();
      await TestUnknownTypeFailsClosed();
      await TestZeroAndMissing();
      await TestBudgetFairness();
      await TestWrongSymbolAtomic();
      await TestPinnedAndReused();
      await TestBeyondOnePage();
      await TestPartialPageFailsClosed();
      TestSpotUnavailable();
      TestHistoryRequirements();
      TestBulkMappingAndUrl();
      await TestBulkCompleteAllSymbols();
      await TestBulkFullPageBoundary();
      await TestBulkDenseTimestampFallback();
      await TestBulkProviderFailureFallback();
      await TestBulkUnboundedFallbackBudget();
      await TestBulkPersistence();
      Console.WriteLine("PASS: Phase 2 funding history and metrics, " + _passed + " offline case groups.");
      Console.WriteLine("LIMIT: No live Binance/provider data, configured raids or PT API actions.");
      return 0;
    }
    catch(Exception ex)
    {
      Console.Error.WriteLine("FAIL: Phase 2 funding: " + ex);
      return 1;
    }
  }
}
