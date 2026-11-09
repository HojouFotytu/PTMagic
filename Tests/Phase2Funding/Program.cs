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

  private static async Task<int> Main()
  {
    try
    {
      TestPayload();
      TestSpecialAndUnknownTypes();
      TestPayloadFailures();
      TestConflictAndPin();
      TestUrl();
      TestRegistryAndDerivedHorizon();
      await TestCompleteWindowAndUnits();
      await TestSpecialExcluded();
      await TestUnknownTypeFailsClosed();
      await TestZeroAndMissing();
      await TestBudgetFairness();
      await TestWrongSymbolAtomic();
      await TestPinnedAndReused();
      await TestBeyondOnePage();
      TestSpotUnavailable();
      TestHistoryRequirements();
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
