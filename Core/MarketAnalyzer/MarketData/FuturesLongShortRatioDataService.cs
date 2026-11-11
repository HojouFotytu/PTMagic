using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Helper;

namespace Core.MarketAnalyzer.MarketData
{
  public sealed class FuturesLongShortRatioSeriesRequest
  {
    public FuturesLongShortRatioSource Source { get; init; }
    public int HorizonMinutes { get; init; }
    public IReadOnlyCollection<string> Symbols { get; init; } = Array.Empty<string>();
  }

  public delegate Task<IReadOnlyList<FuturesLongShortRatioObservation>> FuturesLongShortRatioHistoryLoader(
    FuturesLongShortRatioSource source, string symbol, DateTimeOffset startInclusiveUtc,
    DateTimeOffset endInclusiveUtc, int pageSize, CancellationToken cancellationToken);

  public sealed class FuturesLongShortRatioRefreshReport
  {
    public int SelectedSeries { get; init; }
    public int Requests { get; init; }
    public int Failures { get; init; }
    public int CompleteSeries { get; init; }
  }

  /// <summary>
  /// Bounded, incremental per-symbol 5m global-account ratio history.
  /// Exact samples are checked by the feature engine; missing periods are never interpolated.
  /// </summary>
  public sealed class FuturesLongShortRatioDataService
  {
    public const int DefaultRequestsPerRaid = 60;
    public const int MaximumRequestsPerRaid = 80;
    private const int PageSize = FuturesLongShortRatio.MaximumRowsPerRequest;
    private const int PagesPerSeriesPerRaid = 2;
    private static readonly long IntervalMilliseconds = (long)FuturesLongShortRatio.SourcePeriod.TotalMilliseconds;

    private sealed class Series
    {
      public readonly SortedDictionary<long, FuturesLongShortRatioObservation> Rows =
        new SortedDictionary<long, FuturesLongShortRatioObservation>();
      public readonly List<(long Start, long End)> Complete = new List<(long, long)>();
    }

    private sealed class Work
    {
      public FuturesLongShortRatioSource Source;
      public string Symbol = "";
      public int HorizonMinutes;
      public long Start;
      public long End;
      public int Pages;
    }

    private readonly FuturesLongShortRatioDiskCache _disk;
    private readonly HashSet<(FuturesLongShortRatioSource Source, string Symbol)> _restored =
      new HashSet<(FuturesLongShortRatioSource Source, string Symbol)>();
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

    public FuturesLongShortRatioDataService(string cacheDirectory = null)
    {
      _disk = String.IsNullOrWhiteSpace(cacheDirectory) ? null : new FuturesLongShortRatioDiskCache(cacheDirectory);
    }
    private readonly object _sync = new object();
    private readonly Dictionary<(FuturesLongShortRatioSource Source, string Symbol), Series> _series =
      new Dictionary<(FuturesLongShortRatioSource Source, string Symbol), Series>();
    private readonly Dictionary<(FuturesLongShortRatioSource Source, string Symbol), long> _served =
      new Dictionary<(FuturesLongShortRatioSource Source, string Symbol), long>();

    public static void ValidateBudget(int budget)
    {
      if (budget < 0 || budget > MaximumRequestsPerRaid)
        throw new ArgumentOutOfRangeException(nameof(budget),
          "LongShortRatioMaxRequestsPerRaid must be between 0 and " + MaximumRequestsPerRaid + ".");
    }

    public async Task<FuturesLongShortRatioRefreshReport> RefreshAsync(
      IEnumerable<FuturesLongShortRatioSeriesRequest> requests,
      DateTimeOffset pinnedAsOfUtc, int requestBudget,
      FuturesLongShortRatioHistoryLoader loader, LogHelper log,
      CancellationToken cancellationToken = default)
    {
      if (loader == null) throw new ArgumentNullException(nameof(loader));
      if (log == null) throw new ArgumentNullException(nameof(log));
      ValidateBudget(requestBudget);

      long end = AlignEnd(pinnedAsOfUtc);
      var work = new List<Work>();
      foreach (FuturesLongShortRatioSeriesRequest request in requests ?? Array.Empty<FuturesLongShortRatioSeriesRequest>())
      {
        if (request == null) continue;
        if (!Enum.IsDefined(request.Source)) throw new ArgumentOutOfRangeException(nameof(requests));
        if (request.HorizonMinutes < 5 || request.HorizonMinutes % 5 != 0)
          throw new ArgumentOutOfRangeException(nameof(requests), "Ratio history horizons must be positive 5m multiples.");
        if (request.HorizonMinutes > FuturesLongShortRatio.MaximumHistoryMinutes)
          continue;

        long start = end - (long)request.HorizonMinutes * 60000L + 1;
        if (start <= 0) continue;
        foreach (string symbol in (request.Symbols ?? Array.Empty<string>())
          .Where(value => !String.IsNullOrWhiteSpace(value))
          .Select(FuturesLongShortRatio.NormalizeSymbol)
          .Distinct(StringComparer.OrdinalIgnoreCase))
        {
          work.Add(new Work
          {
            Source = request.Source, Symbol = symbol,
            HorizonMinutes = request.HorizonMinutes, Start = start, End = end
          });
        }
      }

      if (work.Count == 0)
        return new FuturesLongShortRatioRefreshReport();

      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      {
        lock (_sync)
        {
          var active = new HashSet<(FuturesLongShortRatioSource Source, string Symbol)>(
            work.Select(item => (item.Source, item.Symbol)));
          foreach (var key in _series.Keys.Where(key => !active.Contains(key)).ToArray())
            _series.Remove(key);
          foreach (var key in _served.Keys.Where(key => !active.Contains(key)).ToArray())
            _served.Remove(key);
          _restored.RemoveWhere(key => !active.Contains(key));

          foreach (Work item in work)
          {
            Series state = Ensure((item.Source, item.Symbol));
            foreach (long timestamp in state.Rows.Keys.Where(value => value < item.Start).ToArray())
              state.Rows.Remove(timestamp);
            state.Complete.RemoveAll(range => range.End < item.Start);
          }
        }

        Work[] schedule;
        lock (_sync)
        {
          schedule = work.OrderBy(item =>
              _served.TryGetValue((item.Source, item.Symbol), out long last) ? last : long.MinValue)
            .ThenBy(item => item.Source)
            .ThenBy(item => item.Symbol, StringComparer.Ordinal)
            .ToArray();
        }

        int restored = 0;
        if (_disk != null)
        {
          foreach (Work item in work)
          {
            var key = (item.Source, item.Symbol);
            bool restore;
            lock (_sync) restore = _restored.Add(key);
            if (!restore) continue;
            FuturesLongShortRatioDiskSnapshot saved = _disk.TryRestore(item.Source, item.Symbol, pinnedAsOfUtc, log);
            lock (_sync)
            {
              Series state = Ensure(key);
              foreach (FuturesLongShortRatioObservation row in saved.Rows)
                state.Rows[row.TimestampUtc.ToUnixTimeMilliseconds()] = row;
              state.Complete.AddRange(saved.CompleteIntervals);
              foreach (long timestamp in state.Rows.Keys.Where(value => value < item.Start).ToArray()) state.Rows.Remove(timestamp);
              state.Complete.RemoveAll(value => value.End < item.Start);
            }
            if (saved.Rows.Count > 0 || saved.CompleteIntervals.Count > 0) restored++;
          }
        }

        int requestsMade = 0;
        int failures = 0;
        bool madeProgress = true;

        while (requestsMade < requestBudget && madeProgress)
        {
          madeProgress = false;
          foreach (Work item in schedule)
          {
            if (requestsMade >= requestBudget) break;
            if (item.Pages >= PagesPerSeriesPerRaid)
              continue;

            long from;
            lock (_sync)
            {
              Series state = Ensure((item.Source, item.Symbol));
              from = FirstUncovered(state, item.Start, item.End);
              if (from > item.End)
              {
                item.Pages = PagesPerSeriesPerRaid;
                continue;
              }
              _served[(item.Source, item.Symbol)] = item.End;
            }

            requestsMade++;
            item.Pages++;
            madeProgress = true;
            try
            {
              IReadOnlyList<FuturesLongShortRatioObservation> returned = await loader(
                item.Source, item.Symbol,
                DateTimeOffset.FromUnixTimeMilliseconds(from),
                DateTimeOffset.FromUnixTimeMilliseconds(item.End),
                PageSize, cancellationToken).ConfigureAwait(false);

              if (returned == null || returned.Count > PageSize)
                throw new InvalidOperationException("Invalid Binance long/short ratio history page.");

              var pending = new SortedDictionary<long, FuturesLongShortRatioObservation>();
              foreach (FuturesLongShortRatioObservation row in returned)
              {
                FuturesLongShortRatio.Validate(row, item.Source, item.Symbol);
                long timestamp = row.TimestampUtc.ToUnixTimeMilliseconds();
                if (timestamp < from || timestamp > item.End)
                  throw new InvalidOperationException("Long/short ratio page contains an observation outside the pinned request window.");
                if (pending.TryGetValue(timestamp, out FuturesLongShortRatioObservation previous) &&
                  !Same(previous, row))
                  throw new InvalidOperationException("Conflicting long/short ratio rows share a page timestamp.");
                pending[timestamp] = row;
              }

              long last = pending.Count == 0 ? from - 1 : pending.Keys.Last();
              bool exhausted = returned.Count < PageSize;
              if (!exhausted && last < from)
                throw new InvalidOperationException("Long/short ratio pagination made no progress.");

              lock (_sync)
              {
                Series state = Ensure((item.Source, item.Symbol));
                foreach (var entry in pending)
                {
                  if (state.Rows.TryGetValue(entry.Key, out FuturesLongShortRatioObservation previous) &&
                    !Same(previous, entry.Value))
                    throw new InvalidOperationException("Conflicting cached long/short ratio observations.");
                }
                foreach (var entry in pending) state.Rows[entry.Key] = entry.Value;
                if (exhausted) state.Complete.Add((from, item.End));
                else if (last >= from) state.Complete.Add((from, last));
              }

              if (_disk != null)
              {
                IReadOnlyList<FuturesLongShortRatioObservation> snapshot;
                (long Start, long End)[] coverage;
                lock (_sync)
                {
                  Series saved = Ensure((item.Source, item.Symbol));
                  snapshot = saved.Rows.Values.ToArray();
                  coverage = saved.Complete.ToArray();
                }
                _disk.TrySave(item.Source, item.Symbol, snapshot, coverage, pinnedAsOfUtc, log);
              }
              if (exhausted) item.Pages = PagesPerSeriesPerRaid;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
              throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
              failures++;
              item.Pages = PagesPerSeriesPerRaid;
              log.DoLogWarn("Long/short ratio history unavailable for '" + item.Symbol + "' / " +
                item.Source + " (" + ex.GetType().Name + "); affected metric remains unavailable.");
            }
          }
        }

        int complete;
        lock (_sync)
        {
          complete = work.Count(item =>
          {
            Series state = Ensure((item.Source, item.Symbol));
            return HasCompleteWindow(state, item.Start, item.End, item.HorizonMinutes);
          });
        }

        return new FuturesLongShortRatioRefreshReport
        {
          SelectedSeries = work.Count, Requests = requestsMade,
          Failures = failures,
          CompleteSeries = complete
        };
      }
      finally
      {
        _gate.Release();
      }
    }

    public IReadOnlyList<FuturesLongShortRatioObservation> Snapshot(
      FuturesLongShortRatioSource source, string symbol, int trendMinutes, DateTimeOffset asOfUtc)
    {
      if (trendMinutes < 5 || trendMinutes % 5 != 0) return Array.Empty<FuturesLongShortRatioObservation>();
      long end = AlignEnd(asOfUtc);
      long start = end - (long)trendMinutes * 60000L;
      lock (_sync)
      {
        if (!_series.TryGetValue((source, FuturesLongShortRatio.NormalizeSymbol(symbol)), out Series state))
          return Array.Empty<FuturesLongShortRatioObservation>();
        return state.Rows.Where(row => row.Key > start && row.Key <= end)
          .Select(row => row.Value).ToArray();
      }
    }

    public string ExplainUnavailable(string symbol, FuturesLongShortRatioSource source,
      int trendMinutes, DateTimeOffset asOfUtc)
    {
      if (trendMinutes > FuturesLongShortRatio.MaximumHistoryMinutes)
        return "Binance long/short ratio history is limited to the latest 30 days; this " +
          trendMinutes + " minute MarketTrend exceeds that source limit.";
      if (trendMinutes < 5 || trendMinutes % 5 != 0)
        return "Long/short ratio requires a positive 5-minute-aligned TrendMinutes window.";
      int actual = Snapshot(source, symbol, trendMinutes, asOfUtc).Count;
      int expected = trendMinutes / 5;
      return "Binance " + source + " history has " + actual + "/" + expected +
        " exact 5-minute observations in the configured window. Missing, unpolled or incomplete " +
        "source data remains UNAVAILABLE; no nearest-time substitution or interpolation is used.";
    }

    private Series Ensure((FuturesLongShortRatioSource Source, string Symbol) key)
    {
      if (!_series.TryGetValue(key, out Series state))
        _series[key] = state = new Series();
      return state;
    }

    private static long AlignEnd(DateTimeOffset asOfUtc)
    {
      long milliseconds = asOfUtc.ToUniversalTime().ToUnixTimeMilliseconds();
      return milliseconds - milliseconds % IntervalMilliseconds;
    }

    private static long FirstUncovered(Series state, long start, long end)
    {
      long covered = start - 1;
      foreach (var range in state.Complete.OrderBy(value => value.Start))
      {
        if (range.End < start) continue;
        if (range.Start > covered + 1) break;
        covered = Math.Max(covered, range.End);
        if (covered >= end) return end + 1;
      }
      return covered + 1;
    }

    private static bool HasCompleteWindow(Series state, long start, long end, int horizonMinutes)
    {
      if (FirstUncovered(state, start, end) <= end) return false;
      int expected = horizonMinutes / 5;
      long windowStart = end - (long)horizonMinutes * 60000L;
      for (int index = 1; index <= expected; index++)
      {
        long timestamp = windowStart + index * IntervalMilliseconds;
        if (!state.Rows.ContainsKey(timestamp)) return false;
      }
      return true;
    }

    private static bool Same(FuturesLongShortRatioObservation left, FuturesLongShortRatioObservation right) =>
      left.Source == right.Source && left.Symbol.Equals(right.Symbol, StringComparison.OrdinalIgnoreCase) &&
      left.LongShortRatio == right.LongShortRatio &&
      left.LongSideShare == right.LongSideShare &&
      left.ShortSideShare == right.ShortSideShare;

    private static bool Covers(Series state, long start, long end) =>
      FirstUncovered(state, start, end) > end;
  }
}
