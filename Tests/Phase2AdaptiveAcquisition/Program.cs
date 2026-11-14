using System;
using System.Collections.Generic;
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
  private static readonly LogHelper Log = new LogHelper(NullLogger<LogHelper>.Instance);
  private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
  private static readonly Uri Funding = new Uri("https://fapi.binance.com/fapi/v1/fundingRate?symbol=BTCUSDT");
  private static readonly Uri Klines = new Uri("https://fapi.binance.com/fapi/v1/klines?symbol=BTCUSDT&limit=500");
  private static void Check(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
  private static void Passed(string reason) { ++_groups; Console.WriteLine("PASS " + reason); }

  public static async Task<int> Main()
  {
    try
    {
      MetricRegistry();
      FundingQuota();
      IPWeight();
      Cooldowns();
      await RecoverableWait();
      await CancellationWait();
      await Deadline();
      await UnboundedFunding();
      Console.WriteLine("PASS: Funding-only admission, " + _groups + " focused groups.");
      Console.WriteLine("LIMIT: Offline checks; configured runtime acceptance separate.");
      return 0;
    }
    catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
  }

  private static void MetricRegistry()
  {
    string[] names = MarketTrendMetrics.Names.ToArray();
    Check(names.Length == 29 && names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 29, "Registry must have 29 distinct metrics.");
    int global = names.Count(name => MarketTrendMetrics.TryGetScope(name, out var scope) &&
      (scope == MarketTrendMetricScope.Both || scope == MarketTrendMetricScope.Global));
    int assetOnly = names.Count(name => MarketTrendMetrics.TryGetScope(name, out var scope) &&
      scope == MarketTrendMetricScope.Asset);
    Check(global == 20 && assetOnly == 9 && names.Contains(MarketTrendMetrics.TakerBuyRatio) &&
      names.Contains(MarketTrendMetrics.TakerImbalance) && names.Contains(MarketTrendMetrics.FundingRate) &&
      names.Contains(MarketTrendMetrics.FundingSum) &&
      names.Contains(MarketTrendMetrics.MedianFundingRate) &&
      names.Contains(MarketTrendMetrics.PositiveFundingBreadth) &&
      names.Contains(MarketTrendMetrics.NegativeFundingBreadth), "Wrong retained metric scope.");
    Passed("29-metric catalogue: 20 global-capable and 9 asset-only");
  }

  private static void FundingQuota()
  {
    var limiter = new BinanceRateLimitTracker();
    for (int i = 0; i < 400; i++)
      Check(limiter.ReserveOrDelay(Funding, Now) == TimeSpan.Zero, "Funding quota exhausted prematurely.");
    Check(limiter.ReserveOrDelay(Funding, Now) > TimeSpan.Zero, "Funding quota missing safe cap.");
    Check(limiter.ReserveOrDelay(Funding, Now.AddMinutes(5).AddSeconds(1)) == TimeSpan.Zero, "Quota did not recover.");
    Passed("Funding five-minute provider quota");
  }

  private static void IPWeight()
  {
    var limiter = new BinanceRateLimitTracker();
    using var result = new HttpResponseMessage(HttpStatusCode.OK);
    result.Headers.TryAddWithoutValidation("X-MBX-USED-WEIGHT-1M", "1918");
    limiter.ObserveResponse(Klines, result, Now);
    Check(limiter.ReserveOrDelay(Klines, Now) > TimeSpan.Zero, "Shared IP header was ignored.");
    Check(limiter.ReserveOrDelay(Klines, Now.AddMinutes(1).AddSeconds(1)) == TimeSpan.Zero, "IP weight failed to reset.");
    Passed("Shared IP weight");
  }

  private static void Cooldowns()
  {
    var limiter = new BinanceRateLimitTracker();
    using (var result = new HttpResponseMessage((HttpStatusCode)429))
    {
      result.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(11));
      limiter.ObserveResponse(Funding, result, Now);
    }
    Check(limiter.EstimateDelay(Klines, Now.AddSeconds(1)) > TimeSpan.Zero &&
      limiter.ReserveOrDelay(Funding, Now.AddSeconds(1)) > TimeSpan.Zero, "429 shared-host cooldown missing.");
    Check(limiter.ReserveOrDelay(Funding, Now.AddSeconds(12)) == TimeSpan.Zero, "429 cooldown did not expire.");
    var banned = new BinanceRateLimitTracker();
    using (var result = new HttpResponseMessage((HttpStatusCode)418)) banned.ObserveResponse(Funding, result, Now);
    Check(banned.ReserveOrDelay(Klines, Now.AddMinutes(1)) > TimeSpan.Zero &&
      banned.ReserveOrDelay(Funding, Now.AddMinutes(2).AddSeconds(1)) == TimeSpan.Zero, "418 cooldown missing.");
    Passed("429/418 cooldowns");
  }

  private static async Task RecoverableWait()
  {
    DateTimeOffset clock = Now;
    int observations = 0, waits = 0;
    var scheduler = new BinanceFundingAcquisitionScheduler(Now.AddMinutes(1),
      _ => ++observations == 1 ? TimeSpan.FromSeconds(12) : observations == 2 ? TimeSpan.FromSeconds(7) : TimeSpan.Zero,
      () => clock, (delay, ct) => { ++waits; clock = clock.Add(delay); return Task.CompletedTask; });
    Check(await scheduler.TryPermitAsync() && waits == 2 && observations == 3 &&
      clock == Now.AddSeconds(19), "Funding quota wait/recheck failed.");
    Passed("Recoverable provider quota wait");
  }

  private static async Task CancellationWait()
  {
    using var cancellation = new CancellationTokenSource();
    var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var scheduler = new BinanceFundingAcquisitionScheduler(Now.AddMinutes(1),
      _ => TimeSpan.FromSeconds(10), () => Now,
      async (_, ct) => { waiting.TrySetResult(true); await Task.Delay(Timeout.InfiniteTimeSpan, ct); });
    Task<bool> pending = scheduler.TryPermitAsync(cancellation.Token);
    Check(ReferenceEquals(await Task.WhenAny(waiting.Task, Task.Delay(TimeSpan.FromSeconds(2))), waiting.Task),
      "Did not enter cancellable wait.");
    cancellation.Cancel();
    try { await pending; throw new InvalidOperationException("Cancelled funding wait succeeded."); }
    catch (OperationCanceledException) { }
    Passed("Cancellation while funding waits");
  }

  private static async Task Deadline()
  {
    var expired = new BinanceFundingAcquisitionScheduler(Now.AddSeconds(-1), _ => TimeSpan.Zero, () => Now);
    Check(!await expired.TryPermitAsync(), "Expired raid accepted acquisition.");
    var blocked = new BinanceFundingAcquisitionScheduler(Now.AddMinutes(1), _ => TimeSpan.FromMinutes(2), () => Now);
    Check(!await blocked.TryPermitAsync(), "Insufficient quota capacity accepted request.");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try { await blocked.TryPermitAsync(cancellation.Token); throw new InvalidOperationException("Cancelled request succeeded."); }
    catch (OperationCanceledException) { }
    Passed("Freshness and capacity deadline");
  }

  private static async Task UnboundedFunding()
  {
    string[] symbols = Enumerable.Range(0, 70).Select(i => "TEST" + i.ToString("D3") + "USDT").ToArray();
    var service = new FuturesFundingDataService();
    var report = await service.RefreshAsync(symbols, 60, Now, int.MaxValue,
      (symbol, from, until, limit, ct) => Task.FromResult<IReadOnlyList<FuturesFundingObservation>>(Array.Empty<FuturesFundingObservation>()),
      Log, default, _ => Task.FromResult(true));
    Check(report.Requests == 70 && report.CompleteSymbols == 70, "Funding has an artificial 60-request ceiling.");
    Passed("Funding has no fixed sixty-page limit");
  }
}
