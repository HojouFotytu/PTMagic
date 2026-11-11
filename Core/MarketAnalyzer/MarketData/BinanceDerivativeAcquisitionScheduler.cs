using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.MarketAnalyzer;

namespace Core.MarketAnalyzer.MarketData
{
  public enum FuturesDerivativeFamily
  {
    OpenInterest = 0,
    Funding = 1,
    GlobalAccountRatio = 2
  }

  /// <summary>
  /// Fair per-page admission for an analysis raid. No fixed request count:
  /// the shared Binance limiter owns IP/provider capacity, and the existing
  /// analysis-freshness deadline bounds acquisition time. A blocked family
  /// waits asynchronously for capacity that can return before the deadline.
  /// If capacity cannot return in time, its missing history is deferred.
  /// Waiting for one family must not prevent other families from acquiring.
  /// </summary>
  public sealed class BinanceDerivativeAcquisitionScheduler
  {
    private static readonly Uri[] Endpoints =
    {
      new Uri("https://fapi.binance.com/futures/data/openInterestHist"),
      new Uri("https://fapi.binance.com/fapi/v1/fundingRate"),
      new Uri("https://fapi.binance.com/futures/data/globalLongShortAccountRatio")
    };

    private sealed class Pending
    {
      public CancellationToken CancellationToken;
      public TaskCompletionSource<bool> Completion =
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _sync = new object();
    private readonly Queue<Pending>[] _pending =
    {
      new Queue<Pending>(), new Queue<Pending>(), new Queue<Pending>()
    };
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<Uri, TimeSpan> _estimatedDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _waitAsync;
    private int _nextFamily;
    private bool _dispatching;

    public DateTimeOffset DeadlineUtc { get; }

    public BinanceDerivativeAcquisitionScheduler(DateTimeOffset deadlineUtc,
      Func<Uri, TimeSpan> estimatedDelay = null,
      Func<DateTimeOffset> utcNow = null,
      Func<TimeSpan, CancellationToken, Task> waitAsync = null)
    {
      DeadlineUtc = deadlineUtc.ToUniversalTime();
      _estimatedDelay = estimatedDelay ?? BinanceRateLimiter.EstimatedDelay;
      _now = utcNow ?? (() => DateTimeOffset.UtcNow);
      _waitAsync = waitAsync ?? ((delay, ct) => Task.Delay(delay, ct));
    }

    public Task<bool> TryPermitAsync(FuturesDerivativeFamily family,
      CancellationToken cancellationToken = default)
    {
      if (!Enum.IsDefined(family)) throw new ArgumentOutOfRangeException(nameof(family));
      if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<bool>(cancellationToken);
      var item = new Pending { CancellationToken = cancellationToken };
      lock (_sync)
      {
        _pending[(int)family].Enqueue(item);
        if (!_dispatching)
        {
          _dispatching = true;
          _ = DispatchAsync();
        }
      }
      return item.Completion.Task;
    }

    private async Task DispatchAsync()
    {
      // Give simultaneously enabled families a chance to join before a grant.
      await Task.Yield();
      while (true)
      {
        Pending selected = null;
        int family = -1;
        lock (_sync)
        {
          for (int offset = 0; offset < _pending.Length; offset++)
          {
            int index = (_nextFamily + offset) % _pending.Length;
            if (_pending[index].Count == 0) continue;
            selected = _pending[index].Dequeue();
            family = index;
            _nextFamily = (index + 1) % _pending.Length;
            break;
          }
          if (selected == null)
          {
            _dispatching = false;
            return;
          }
        }

        // A family waiting for its quota must not hold up ready families.
        _ = CompleteWhenReadyAsync(selected, family);
        await Task.Yield();
      }
    }

    private async Task CompleteWhenReadyAsync(Pending selected, int family)
    {
      try
      {
        while (true)
        {
          selected.CancellationToken.ThrowIfCancellationRequested();
          DateTimeOffset now = _now().ToUniversalTime();
          TimeSpan remaining = DeadlineUtc - now;
          if (remaining <= TimeSpan.Zero)
          {
            selected.Completion.TrySetResult(false);
            return;
          }

          TimeSpan delay = _estimatedDelay(Endpoints[family]);
          if (delay <= TimeSpan.Zero)
          {
            selected.Completion.TrySetResult(true);
            return;
          }

          // Do not wait past the existing raid freshness cutoff.
          if (delay >= remaining)
          {
            selected.Completion.TrySetResult(false);
            return;
          }

          // Re-estimate after every wait: other traffic can consume the
          // newly released quota before this family gets to use it.
          await _waitAsync(delay, selected.CancellationToken).ConfigureAwait(false);
        }
      }
      catch (OperationCanceledException) when (selected.CancellationToken.IsCancellationRequested)
      {
        selected.Completion.TrySetCanceled(selected.CancellationToken);
      }
      catch (Exception exception)
      {
        selected.Completion.TrySetException(exception);
      }
    }
  }
}
