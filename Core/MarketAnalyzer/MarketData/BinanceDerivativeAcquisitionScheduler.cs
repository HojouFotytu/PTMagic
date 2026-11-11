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
  /// defers its remaining history to a future raid rather than waiting through
  /// an entire exchange quota window and making the current decision stale.
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
    private int _nextFamily;
    private bool _dispatching;

    public DateTimeOffset DeadlineUtc { get; }

    public BinanceDerivativeAcquisitionScheduler(DateTimeOffset deadlineUtc,
      Func<Uri, TimeSpan> estimatedDelay = null,
      Func<DateTimeOffset> utcNow = null)
    {
      DeadlineUtc = deadlineUtc.ToUniversalTime();
      _estimatedDelay = estimatedDelay ?? BinanceRateLimiter.EstimatedDelay;
      _now = utcNow ?? (() => DateTimeOffset.UtcNow);
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

        try
        {
          if (selected.CancellationToken.IsCancellationRequested)
            selected.Completion.TrySetCanceled(selected.CancellationToken);
          else
            selected.Completion.TrySetResult(_now().ToUniversalTime() < DeadlineUtc &&
              _estimatedDelay(Endpoints[family]) <= TimeSpan.Zero);
        }
        catch (Exception exception)
        {
          selected.Completion.TrySetException(exception);
        }
        await Task.Yield();
      }
    }
  }
}
