using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Core.Helper;

namespace Core.MarketAnalyzer
{
  /// <summary>
  /// In-process Binance IP admission state shared by every BaseAnalyzer request.
  /// The Futures historical-data and funding request quotas are separate from
  /// the one-minute request-weight quota. Clock injection permits offline tests.
  /// </summary>
  public sealed class BinanceRateLimitTracker
  {
    private const double SoftLimitRatio = 0.80;
    private const int FuturesWeightPerMinute = 2400;
    private const int SpotWeightPerMinute = 6000;
    // Binance's published Futures historical-data limit is 1000/5 min/IP;
    // fundingRate and fundingInfo share a separate 500/5 min/IP limit.
    private const int FuturesHistoricalRequestsPerFiveMinutes = 1000;
    private const int FuturesFundingRequestsPerFiveMinutes = 500;
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, HostState> _states =
      new ConcurrentDictionary<string, HostState>(StringComparer.OrdinalIgnoreCase);

    private sealed class HostState
    {
      public readonly object SyncRoot = new object();
      public int UsedWeight;
      public DateTimeOffset WindowResetUtc = DateTimeOffset.MinValue;
      public DateTimeOffset CooldownUntilUtc = DateTimeOffset.MinValue;
      public readonly Queue<DateTimeOffset> HistoricalRequests = new Queue<DateTimeOffset>();
      public readonly Queue<DateTimeOffset> FundingRequests = new Queue<DateTimeOffset>();
    }

    public TimeSpan EstimateDelay(Uri requestUri, DateTimeOffset nowUtc)
    {
      if (!TryGetLimit(requestUri, out int limit)) return TimeSpan.Zero;
      HostState state = _states.GetOrAdd(requestUri.Host, _ => new HostState());
      lock (state.SyncRoot)
      {
        DateTimeOffset now = nowUtc.ToUniversalTime();
        RefreshWindows(state, now);
        return RequiredDelay(state, requestUri, limit, now);
      }
    }

    /// <summary>Atomically reserves capacity before an actual HTTP attempt.
    /// A blocked attempt is NOT counted; a dispatched attempt is counted even
    /// if its response has no weight headers or the network subsequently fails.</summary>
    public TimeSpan ReserveOrDelay(Uri requestUri, DateTimeOffset nowUtc)
    {
      if (!TryGetLimit(requestUri, out int limit)) return TimeSpan.Zero;
      HostState state = _states.GetOrAdd(requestUri.Host, _ => new HostState());
      lock (state.SyncRoot)
      {
        DateTimeOffset now = nowUtc.ToUniversalTime();
        RefreshWindows(state, now);
        TimeSpan delay = RequiredDelay(state, requestUri, limit, now);
        if (delay > TimeSpan.Zero) return delay;

        state.UsedWeight += EstimatedWeight(requestUri);
        if (IsFuturesHistorical(requestUri)) state.HistoricalRequests.Enqueue(now);
        if (IsFuturesFunding(requestUri)) state.FundingRequests.Enqueue(now);
        return TimeSpan.Zero;
      }
    }

    public void ObserveResponse(Uri requestUri, HttpResponseMessage response, DateTimeOffset nowUtc)
    {
      if (response == null || !TryGetLimit(requestUri, out _)) return;
      HostState state = _states.GetOrAdd(requestUri.Host, _ => new HostState());
      lock (state.SyncRoot)
      {
        DateTimeOffset now = nowUtc.ToUniversalTime();
        RefreshWindows(state, now);
        if (TryGetUsedWeight(response, out int used))
          state.UsedWeight = Math.Max(state.UsedWeight, used);

        int status = (int)response.StatusCode;
        if (status == 429 || status == 418)
        {
          TimeSpan backoff = GetRetryAfter(response, now, status == 418
            ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(1));
          DateTimeOffset until = now + backoff;
          if (until > state.CooldownUntilUtc) state.CooldownUntilUtc = until;
        }
      }
    }

    private static void RefreshWindows(HostState state, DateTimeOffset now)
    {
      if (now >= state.WindowResetUtc)
      {
        state.UsedWeight = 0;
        state.WindowResetUtc = NextMinuteBoundary(now);
      }
      if (state.CooldownUntilUtc <= now)
        state.CooldownUntilUtc = DateTimeOffset.MinValue;

      while (state.HistoricalRequests.Count > 0 &&
        state.HistoricalRequests.Peek() + FiveMinutes <= now)
        state.HistoricalRequests.Dequeue();
      while (state.FundingRequests.Count > 0 &&
        state.FundingRequests.Peek() + FiveMinutes <= now)
        state.FundingRequests.Dequeue();
    }

    private static TimeSpan RequiredDelay(HostState state, Uri requestUri, int limit, DateTimeOffset now)
    {
      DateTimeOffset until = state.CooldownUntilUtc;
      int safeWeight = (int)Math.Floor(limit * SoftLimitRatio);
      int weight = EstimatedWeight(requestUri);
      if ((state.UsedWeight >= safeWeight || state.UsedWeight + weight > safeWeight)
        && state.WindowResetUtc > until)
        until = state.WindowResetUtc;

      if (IsFuturesHistorical(requestUri) &&
        state.HistoricalRequests.Count >= (int)Math.Floor(FuturesHistoricalRequestsPerFiveMinutes * SoftLimitRatio))
      {
        DateTimeOffset opening = state.HistoricalRequests.Peek() + FiveMinutes + TimeSpan.FromMilliseconds(100);
        if (opening > until) until = opening;
      }
      if (IsFuturesFunding(requestUri) &&
        state.FundingRequests.Count >= (int)Math.Floor(FuturesFundingRequestsPerFiveMinutes * SoftLimitRatio))
      {
        DateTimeOffset opening = state.FundingRequests.Peek() + FiveMinutes + TimeSpan.FromMilliseconds(100);
        if (opening > until) until = opening;
      }
      return until > now ? until - now : TimeSpan.Zero;
    }

    private static bool IsFuturesHistorical(Uri uri) =>
      uri.Host.Equals("fapi.binance.com", StringComparison.OrdinalIgnoreCase) &&
      (uri.AbsolutePath.Equals("/futures/data/openInterestHist", StringComparison.OrdinalIgnoreCase) ||
       uri.AbsolutePath.Equals("/futures/data/globalLongShortAccountRatio", StringComparison.OrdinalIgnoreCase));

    private static bool IsFuturesFunding(Uri uri) =>
      uri.Host.Equals("fapi.binance.com", StringComparison.OrdinalIgnoreCase) &&
      (uri.AbsolutePath.Equals("/fapi/v1/fundingRate", StringComparison.OrdinalIgnoreCase) ||
       uri.AbsolutePath.Equals("/fapi/v1/fundingInfo", StringComparison.OrdinalIgnoreCase));

    private static int EstimatedWeight(Uri uri)
    {
      if (IsFuturesHistorical(uri) || IsFuturesFunding(uri)) return 0;
      if (uri.Host.Equals("fapi.binance.com", StringComparison.OrdinalIgnoreCase))
      {
        if (uri.AbsolutePath.Equals("/fapi/v1/klines", StringComparison.OrdinalIgnoreCase))
        {
          int pageSize = QueryInt(uri, "limit", 500);
          return pageSize < 100 ? 1 : pageSize < 500 ? 2 : pageSize <= 1000 ? 5 : 10;
        }
        if (uri.AbsolutePath.Equals("/fapi/v1/ticker/24hr", StringComparison.OrdinalIgnoreCase))
          return HasQuery(uri, "symbol") ? 1 : 40;
      }
      // Conservative fallback for other shared Binance transport requests.
      return 1;
    }

    private static bool HasQuery(Uri uri, string name) =>
      uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Any(part => part.Split('=')[0].Equals(name, StringComparison.OrdinalIgnoreCase));

    private static int QueryInt(Uri uri, string name, int fallback)
    {
      foreach (string part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
      {
        string[] pieces = part.Split('=', 2);
        if (pieces.Length == 2 && pieces[0].Equals(name, StringComparison.OrdinalIgnoreCase) &&
          int.TryParse(pieces[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
          return number;
      }
      return fallback;
    }

    private static bool TryGetUsedWeight(HttpResponseMessage response, out int usedWeight)
    {
      usedWeight = 0;
      return response.Headers.TryGetValues("X-MBX-USED-WEIGHT-1M", out var values) &&
        int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out usedWeight) &&
        usedWeight >= 0;
    }

    private static TimeSpan GetRetryAfter(HttpResponseMessage response,
      DateTimeOffset now, TimeSpan fallback)
    {
      TimeSpan? duration = response.Headers.RetryAfter?.Delta;
      if (!duration.HasValue && response.Headers.RetryAfter?.Date != null)
        duration = response.Headers.RetryAfter.Date.Value - now;
      return duration.HasValue && duration.Value > TimeSpan.Zero ? duration.Value : fallback;
    }

    private static DateTimeOffset NextMinuteBoundary(DateTimeOffset now)
    {
      DateTimeOffset utc = now.ToUniversalTime();
      return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0,
        TimeSpan.Zero).AddMinutes(1).AddSeconds(1);
    }

    private static bool TryGetLimit(Uri requestUri, out int requestWeightLimit)
    {
      requestWeightLimit = 0;
      if (requestUri == null) return false;
      if (requestUri.Host.Equals("fapi.binance.com", StringComparison.OrdinalIgnoreCase))
        requestWeightLimit = FuturesWeightPerMinute;
      else if (requestUri.Host.Equals("api.binance.com", StringComparison.OrdinalIgnoreCase) ||
               requestUri.Host.Equals("api.binance.us", StringComparison.OrdinalIgnoreCase))
        requestWeightLimit = SpotWeightPerMinute;
      return requestWeightLimit > 0;
    }
  }

  internal static class BinanceRateLimiter
  {
    private static readonly BinanceRateLimitTracker Tracker = new BinanceRateLimitTracker();

    public static TimeSpan EstimatedDelay(Uri requestUri) =>
      Tracker.EstimateDelay(requestUri, DateTimeOffset.UtcNow);

    public static void WaitIfNeeded(Uri requestUri, LogHelper log)
    {
      while (true)
      {
        TimeSpan delay = Tracker.ReserveOrDelay(requestUri, DateTimeOffset.UtcNow);
        if (delay <= TimeSpan.Zero) return;
        log.DoLogWarn("Binance rate limiter pausing requests to " + requestUri.Host +
          " for " + delay.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " seconds.");
        Thread.Sleep(delay);
      }
    }

    public static async Task WaitIfNeededAsync(Uri requestUri, LogHelper log, CancellationToken cancellationToken)
    {
      while (true)
      {
        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan delay = Tracker.ReserveOrDelay(requestUri, DateTimeOffset.UtcNow);
        if (delay <= TimeSpan.Zero) return;
        log.DoLogWarn("Binance rate limiter pausing requests to " + requestUri.Host +
          " for " + delay.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " seconds.");
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
      }
    }

    public static void ObserveResponse(Uri requestUri, HttpResponseMessage response) =>
      Tracker.ObserveResponse(requestUri, response, DateTimeOffset.UtcNow);
  }
}
