using System;
using System.Threading;
using System.Threading.Tasks;
using Core.MarketAnalyzer;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Deadline-bound admission for retained Binance Futures settled-funding pages.
  /// The shared Binance rate limiter still owns actual IP/provider request
  /// reservations, response-header feedback, and 429/418 cooldown handling.
  /// </summary>
  public sealed class BinanceFundingAcquisitionScheduler
  {
    private static readonly Uri FundingEndpoint =
      new Uri("https://fapi.binance.com/fapi/v1/fundingRate");

    private readonly Func<DateTimeOffset> _now;
    private readonly Func<Uri, TimeSpan> _estimatedDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _waitAsync;

    public DateTimeOffset DeadlineUtc { get; }

    public BinanceFundingAcquisitionScheduler(DateTimeOffset deadlineUtc,
      Func<Uri, TimeSpan> estimatedDelay = null,
      Func<DateTimeOffset> utcNow = null,
      Func<TimeSpan, CancellationToken, Task> waitAsync = null)
    {
      DeadlineUtc = deadlineUtc.ToUniversalTime();
      _estimatedDelay = estimatedDelay ?? BinanceRateLimiter.EstimatedDelay;
      _now = utcNow ?? (() => DateTimeOffset.UtcNow);
      _waitAsync = waitAsync ?? ((delay, ct) => Task.Delay(delay, ct));
    }

    public async Task<bool> TryPermitAsync(CancellationToken cancellationToken = default)
    {
      while (true)
      {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = _now().ToUniversalTime();
        TimeSpan remaining = DeadlineUtc - now;
        if (remaining <= TimeSpan.Zero) return false;

        TimeSpan delay = _estimatedDelay(FundingEndpoint);
        if (delay <= TimeSpan.Zero) return true;
        if (delay >= remaining) return false;

        // Recheck after every wait because other requests may use the capacity.
        await _waitAsync(delay, cancellationToken).ConfigureAwait(false);
      }
    }
  }
}
