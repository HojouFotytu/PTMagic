using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Core.Helper;
using Core.MarketAnalyzer;
using Core.MarketAnalyzer.MarketData;
using Microsoft.Extensions.Logging.Abstractions;

internal static class Program
{
  private static int _groups;
  private static readonly LogHelper Log =
    new LogHelper(NullLogger<LogHelper>.Instance);
  private static readonly DateTimeOffset Now =
    new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
  private static readonly Uri Oi = new Uri("https://fapi.binance.com/futures/data/openInterestHist?symbol=BTCUSDT");
  private static readonly Uri Ratio = new Uri("https://fapi.binance.com/futures/data/globalLongShortAccountRatio?symbol=BTCUSDT");
  private static readonly Uri Funding = new Uri("https://fapi.binance.com/fapi/v1/fundingRate?symbol=BTCUSDT");
  private static readonly Uri Klines = new Uri("https://fapi.binance.com/fapi/v1/klines?symbol=BTCUSDT&limit=500");

  private static void Check(bool valid, string reason)
  {
    if (!valid) throw new InvalidOperationException(reason);
  }

  private static void Passed(string reason)
  {
    _groups++;
    Console.WriteLine("PASS: " + reason);
  }

  public static async Task<int> Main()
  {
    try
    {
      TestSharedHistoricalQuota();
      TestSeparateFundingQuota();
      TestSharedIPWeight();
      TestCooldowns();
      await TestFamilyFairness();
      await TestRecoverableCapacityWait();
      await TestDelayedFamilyDoesNotBlockOthers();
      await TestCancellationWhileWaiting();
      await TestFreshnessAndCapacityVeto();
      await TestUnboundedProductionOI();
      await TestUnboundedProductionFunding();
      await TestUnboundedProductionRatios();
      Console.WriteLine("PASS: Adaptive derivative acquisition, " + _groups + " focused groups.");
      Console.WriteLine("LIMIT: Offline-only; no live Binance/PT/Monitor integration.");
      return 0;
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine("FAIL: Adaptive derivative acquisition: " + ex);
      return 1;
    }
  }

  private static void TestSharedHistoricalQuota()
  {
    var limiter = new BinanceRateLimitTracker();
    for (int i = 0; i < 800; i++)
    {
      Uri endpoint = i % 2 == 0 ? Oi : Ratio;
      Check(limiter.ReserveOrDelay(endpoint, Now) == TimeSpan.Zero,
        "Shared OI/ratio quota rejected request #" + i);
    }
    Check(limiter.ReserveOrDelay(Oi, Now) > TimeSpan.Zero &&
      limiter.ReserveOrDelay(Ratio, Now) > TimeSpan.Zero,
      "OI and global ratio must share one five-minute historical-data allowance.");
    Check(limiter.ReserveOrDelay(Oi, Now.AddMinutes(5).AddSeconds(1)) == TimeSpan.Zero,
      "Historical-data allowance must recover after the rolling five-minute window.");
    Passed("OI and global-account ratio share historical-data allowance");
  }

  private static void TestSeparateFundingQuota()
  {
    var limiter = new BinanceRateLimitTracker();
    for (int i = 0; i < 400; i++)
      Check(limiter.ReserveOrDelay(Funding, Now) == TimeSpan.Zero,
        "Funding quota unexpectedly exhausted.");
    Check(limiter.ReserveOrDelay(Funding, Now) > TimeSpan.Zero,
      "Funding must stop at its own safe five-minute allowance.");
    Check(limiter.ReserveOrDelay(Oi, Now) == TimeSpan.Zero,
      "Funding-only limit must not block the distinct historical-data quota.");
    Check(limiter.ReserveOrDelay(Funding, Now.AddMinutes(5).AddSeconds(1)) == TimeSpan.Zero,
      "Funding allowance must recover after five minutes.");
    Passed("Funding shares its separate five-minute source allowance");
  }

  private static void TestSharedIPWeight()
  {
    var limiter = new BinanceRateLimitTracker();
    using var response = new HttpResponseMessage(HttpStatusCode.OK);
    response.Headers.TryAddWithoutValidation("X-MBX-USED-WEIGHT-1M", "1918");
    limiter.ObserveResponse(Klines, response, Now);
    Check(limiter.ReserveOrDelay(Klines, Now) > TimeSpan.Zero,
      "A five-weight Futures kline request must respect observed shared IP usage.");
    Check(limiter.ReserveOrDelay(Klines, Now.AddMinutes(1).AddSeconds(1)) == TimeSpan.Zero,
      "Shared minute weight must reset on the next minute.");
    Passed("Shared IP weight honors response headers and endpoint weights");
  }

  private static void TestCooldowns()
  {
    var limiter = new BinanceRateLimitTracker();
    using (var response = new HttpResponseMessage((HttpStatusCode)429))
    {
      response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(11));
      limiter.ObserveResponse(Oi, response, Now);
    }
    Check(limiter.EstimateDelay(Ratio, Now.AddSeconds(1)) > TimeSpan.Zero &&
      limiter.ReserveOrDelay(Funding, Now.AddSeconds(1)) > TimeSpan.Zero,
      "429 cooldown must affect every family on the same Futures IP host.");
    Check(limiter.ReserveOrDelay(Funding, Now.AddSeconds(12)) == TimeSpan.Zero,
      "429 cooldown must expire at the stated time.");

    var banned = new BinanceRateLimitTracker();
    using (var response = new HttpResponseMessage((HttpStatusCode)418))
      banned.ObserveResponse(Oi, response, Now);
    Check(banned.ReserveOrDelay(Oi, Now.AddMinutes(1)) > TimeSpan.Zero &&
      banned.ReserveOrDelay(Oi, Now.AddMinutes(2).AddSeconds(1)) == TimeSpan.Zero,
      "418 must impose a longer fallback cooldown without Retry-After.");
    Passed("429 and 418 enforce shared cooldowns");
  }

  private static async Task TestFamilyFairness()
  {
    var granted = new List<string>();
    var scheduler = new BinanceDerivativeAcquisitionScheduler(Now.AddMinutes(1),
      uri => { granted.Add(uri.AbsolutePath); return TimeSpan.Zero; }, () => Now);
    Task<bool> a = scheduler.TryPermitAsync(FuturesDerivativeFamily.OpenInterest);
    Task<bool> b = scheduler.TryPermitAsync(FuturesDerivativeFamily.Funding);
    Task<bool> c = scheduler.TryPermitAsync(FuturesDerivativeFamily.GlobalAccountRatio);
    Check((await Task.WhenAll(a, b, c)).All(value => value),
      "Eligible families must obtain admission.");
    Check(granted.Count == 3 && granted.Any(path => path.Contains("openInterestHist")) &&
      granted.Any(path => path.Contains("fundingRate")) &&
      granted.Any(path => path.Contains("globalLongShortAccountRatio")),
      "Every eligible derivative family must be admitted without starvation.");
    Passed("Family admission follows round-robin scheduling");
  }

  private static async Task TestRecoverableCapacityWait()
  {
    DateTimeOffset clock = Now;
    int checks = 0;
    var waited = new List<TimeSpan>();
    var scheduler = new BinanceDerivativeAcquisitionScheduler(Now.AddMinutes(1),
      _ =>
      {
        checks++;
        return checks == 1 ? TimeSpan.FromSeconds(12) :
          checks == 2 ? TimeSpan.FromSeconds(7) : TimeSpan.Zero;
      },
      () => clock,
      (delay, ct) =>
      {
        waited.Add(delay);
        clock = clock.Add(delay);
        return Task.CompletedTask;
      });
    Check(await scheduler.TryPermitAsync(FuturesDerivativeFamily.Funding) &&
      checks == 3 && waited.Count == 2 && clock == Now.AddSeconds(19),
      "An available quota window within the freshness deadline must be awaited and rechecked.");
    Passed("Recoverable quota delay waits and rechecks before the deadline");
  }

  private static async Task TestDelayedFamilyDoesNotBlockOthers()
  {
    bool oiBlocked = true;
    var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var scheduler = new BinanceDerivativeAcquisitionScheduler(Now.AddMinutes(1),
      uri => uri.AbsolutePath.Contains("openInterestHist") && oiBlocked
        ? TimeSpan.FromSeconds(10) : TimeSpan.Zero,
      () => Now,
      (_, ct) => release.Task);
    Task<bool> blocked = scheduler.TryPermitAsync(FuturesDerivativeFamily.OpenInterest);
    Task<bool> ready = scheduler.TryPermitAsync(FuturesDerivativeFamily.Funding);
    Task completed = await Task.WhenAny(ready, Task.Delay(TimeSpan.FromSeconds(2)));
    Check(ReferenceEquals(completed, ready) && await ready,
      "A delayed OI family must not prevent an eligible funding family from proceeding.");
    oiBlocked = false;
    release.TrySetResult(true);
    Check(await blocked, "Delayed OI family must resume when capacity returns.");
    Passed("A delayed derivative family does not block another ready family");
  }

  private static async Task TestCancellationWhileWaiting()
  {
    using var cancellation = new CancellationTokenSource();
    var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var scheduler = new BinanceDerivativeAcquisitionScheduler(Now.AddMinutes(1),
      _ => TimeSpan.FromSeconds(10), () => Now,
      async (_, ct) =>
      {
        waiting.TrySetResult(true);
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
      });
    Task<bool> permit = scheduler.TryPermitAsync(FuturesDerivativeFamily.GlobalAccountRatio, cancellation.Token);
    Check(ReferenceEquals(await Task.WhenAny(waiting.Task, Task.Delay(TimeSpan.FromSeconds(2))), waiting.Task),
      "Scheduler must enter its cancellation-aware quota wait.");
    cancellation.Cancel();
    try
    {
      await permit;
      throw new InvalidOperationException("Cancellation during a quota wait was not propagated.");
    }
    catch (OperationCanceledException) { }
    Passed("Quota waiting propagates cancellation");
  }

  private static async Task TestFreshnessAndCapacityVeto()
  {
    var stale = new BinanceDerivativeAcquisitionScheduler(Now.AddSeconds(-1),
      _ => TimeSpan.Zero, () => Now);
    Check(!await stale.TryPermitAsync(FuturesDerivativeFamily.OpenInterest),
      "Expired analysis must not acquire another derivative page.");
    var saturated = new BinanceDerivativeAcquisitionScheduler(Now.AddMinutes(1),
      _ => TimeSpan.FromMinutes(2), () => Now);
    Check(!await saturated.TryPermitAsync(FuturesDerivativeFamily.Funding),
      "Saturated provider capacity must defer additional pages.");
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    try
    {
      await saturated.TryPermitAsync(FuturesDerivativeFamily.GlobalAccountRatio, cts.Token);
      throw new InvalidOperationException("Cancellation was not propagated.");
    }
    catch (OperationCanceledException) { }
    Passed("Expired freshness, saturated capacity, and cancellation");
  }

  private static string[] Symbols() => Enumerable.Range(0, 70)
    .Select(index => "TEST" + index.ToString("D3") + "USDT").ToArray();

  private static async Task TestUnboundedProductionOI()
  {
    string root = Path.Combine(Path.GetTempPath(), "PTM_AdaptiveOI_" + Guid.NewGuid().ToString("N"));
    try
    {
      var service = new FuturesOpenInterestDataService(root);
      var report = await service.RefreshAsync(Symbols(), 60, Now, int.MaxValue,
        (symbol, end, limit, ct) =>
        {
          IReadOnlyList<FuturesOpenInterestObservation> page = Enumerable.Range(0, 13)
            .Select(index => new FuturesOpenInterestObservation
            {
              Exchange = "BinanceFutures", Symbol = symbol,
              TimestampUtc = end.AddMinutes(-60 + index * 5),
              Period = TimeSpan.FromMinutes(5), IsHistoricalStatistic = true,
              NativeOpenInterest = 100m, ReportedOpenInterestValue = 500m
            }).ToArray();
          return Task.FromResult(page);
        }, Log, default, _ => Task.FromResult(true));
      Check(report.Requests == 70 && report.AvailableSymbols == 70,
        "Production OI must be able to acquire over sixty pages when capacity permits.");
      Passed("OI acquisition exceeds 60 pages without a fixed ceiling");
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
  }

  private static async Task TestUnboundedProductionFunding()
  {
    var service = new FuturesFundingDataService();
    var report = await service.RefreshAsync(Symbols(), 60, Now, int.MaxValue,
      (symbol, from, end, limit, ct) =>
        Task.FromResult<IReadOnlyList<FuturesFundingObservation>>(Array.Empty<FuturesFundingObservation>()),
      Log, default, _ => Task.FromResult(true));
    Check(report.Requests == 70 && report.CompleteSymbols == 70,
      "Production funding must acquire over sixty complete pages when capacity permits.");
    Passed("Funding acquisition exceeds 60 pages without a fixed ceiling");
  }

  private static async Task TestUnboundedProductionRatios()
  {
    var service = new FuturesLongShortRatioDataService();
    var report = await service.RefreshAsync(new[]
    {
      new FuturesLongShortRatioSeriesRequest
      {
        Source = FuturesLongShortRatioSource.GlobalAccount,
        HorizonMinutes = 60, Symbols = Symbols()
      }
    }, Now, int.MaxValue, (source, symbol, from, to, limit, ct) =>
    {
      IReadOnlyList<FuturesLongShortRatioObservation> page =
        Enumerable.Range(1, 12).Select(index => new FuturesLongShortRatioObservation
        {
          Source = source, Symbol = symbol, Period = TimeSpan.FromMinutes(5),
          TimestampUtc = to.AddMinutes(-60 + index * 5),
          LongShortRatio = 1.5m, LongSideShare = .6m, ShortSideShare = .4m
        }).ToArray();
      return Task.FromResult(page);
    }, Log, default, _ => Task.FromResult(true));
    Check(report.Requests == 70 && report.CompleteSeries == 70,
      "Production ratio must acquire over sixty complete pages when capacity permits.");
    Passed("Global-account ratio acquisition exceeds 60 pages without a fixed ceiling");
  }
}
