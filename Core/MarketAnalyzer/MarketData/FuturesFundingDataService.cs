using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Helper;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer.MarketData
{
  public delegate Task<IReadOnlyList<FuturesFundingObservation>> FundingHistoryLoader(
    string symbol, DateTimeOffset startInclusive, DateTimeOffset endInclusive,
    int pageSize, CancellationToken cancellationToken);
  public delegate Task<IReadOnlyList<FuturesFundingObservation>> FundingBulkHistoryLoader(
    DateTimeOffset startInclusive, DateTimeOffset endInclusive,
    int pageSize, CancellationToken cancellationToken);

  public sealed class FundingRefreshReport
  {
    public int SelectedSymbols { get; init; }
    public int Requests { get; init; }
    public int Failures { get; init; }
    public int CompleteSymbols { get; init; }
    public int BulkRequests { get; init; }
    public int SymbolRequests { get; init; }
  }

  /// <summary>
  /// Only explicitly requested MarketTrend funding metrics cause collection.
  /// A verified coverage interval denotes an exhausted provider query, not a
  /// guessed funding cadence. No fixed hours or settlement periodicity.
  /// </summary>
  public sealed class FuturesFundingDataService
  {
    public const int MaximumRequestsPerRaid = 400;
    private const int PageSize = 1000;
    private const int PagesPerSymbol = 2;
    private readonly FuturesFundingDiskCache _disk;
    private readonly HashSet<string> _restored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

    public FuturesFundingDataService(string cacheDirectory = null)
    {
      _disk = String.IsNullOrWhiteSpace(cacheDirectory) ? null : new FuturesFundingDiskCache(cacheDirectory);
    }
    private readonly object _sync = new object();
    private sealed class Series
    {
      public readonly SortedDictionary<(long At, string Kind), FuturesFundingObservation> Rows =
        new SortedDictionary<(long At, string Kind), FuturesFundingObservation>();
      public readonly List<(long Start, long End)> Complete = new List<(long, long)>();
    }
    private readonly Dictionary<string, Series> _series = new Dictionary<string, Series>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _served = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    public static int RequiredHorizonMinutes(IEnumerable<MarketTrend> trends)
    {
      int[] values = (trends ?? Enumerable.Empty<MarketTrend>())
        .Where(trend => trend != null && String.Equals(trend.Platform, "Exchange", StringComparison.OrdinalIgnoreCase)
          && FuturesFundingFeatureEngine.HasAnyMetrics(MarketTrendMetrics.ParseEnabled(trend))
          && trend.TrendMinutes >= 5 && trend.TrendMinutes % 5 == 0)
        .Select(trend => trend.TrendMinutes).ToArray();
      return values.Length == 0 ? 0 : values.Max();
    }

    public static void ValidateBudget(int budget)
    {
      if (budget != int.MaxValue && (budget < 0 || budget > MaximumRequestsPerRaid))
        throw new ArgumentOutOfRangeException(nameof(budget));
    }


    /// <summary>
    /// Verify shared funding-history windows with symbol-omitted pages first.
    /// A short provider page proves the entire requested interval for every
    /// selected symbol (including absence of settlements). A full page proves
    /// only times strictly before its final timestamp: the boundary is fetched
    /// again, so simultaneous symbols or Regular/Special rows cannot be lost.
    /// If an entire page shares that timestamp, fall back to symbol queries.
    /// Existing per-symbol histories, disk format and strict metric semantics
    /// remain unchanged.
    /// </summary>
    public async Task<FundingRefreshReport> RefreshWithBulkAsync(
      IEnumerable<string> eligible, int horizonMinutes, DateTimeOffset asOfUtc,
      int requestBudget, FundingHistoryLoader symbolLoader,
      FundingBulkHistoryLoader bulkLoader, LogHelper log, CancellationToken ct = default,
      Func<CancellationToken, Task<bool>> requestPermit = null)
    {
      if (symbolLoader == null) throw new ArgumentNullException(nameof(symbolLoader));
      if (bulkLoader == null) throw new ArgumentNullException(nameof(bulkLoader));
      if (log == null) throw new ArgumentNullException(nameof(log));
      ValidateBudget(requestBudget);
      if (horizonMinutes < 5 || horizonMinutes % 5 != 0)
        throw new ArgumentOutOfRangeException(nameof(horizonMinutes));

      string[] symbols = (eligible ?? Array.Empty<string>())
        .Where(symbol => !String.IsNullOrWhiteSpace(symbol))
        .Select(FuturesFunding.NormalizeSymbol)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(symbol => symbol, StringComparer.Ordinal).ToArray();

      // Restore and trim existing per-symbol state without making requests.
      FundingRefreshReport initial = await RefreshAsync(symbols, horizonMinutes, asOfUtc,
        0, symbolLoader, log, ct).ConfigureAwait(false);
      if (symbols.Length == 0 || requestBudget == 0) return initial;

      long end = asOfUtc.ToUniversalTime().ToUnixTimeMilliseconds();
      long start = end - (long)horizonMinutes * 60000L + 1;
      if (start <= 0) return initial;

      long cursor = end + 1;
      lock (_sync)
      {
        foreach (string symbol in symbols)
          cursor = Math.Min(cursor, FirstUncovered(Ensure(symbol), start, end));
      }
      if (cursor > end) return initial;

      int bulkRequests = 0, bulkFailures = 0;
      bool permitDeferred = false, dirty = false;
      var selected = new HashSet<string>(symbols, StringComparer.OrdinalIgnoreCase);
      await _gate.WaitAsync(ct).ConfigureAwait(false);
      try
      {
        while (cursor <= end && bulkRequests < requestBudget)
        {
          ct.ThrowIfCancellationRequested();
          if (requestPermit != null && !await requestPermit(ct).ConfigureAwait(false))
          {
            permitDeferred = true;
            break;
          }

          bulkRequests++;
          try
          {
            IReadOnlyList<FuturesFundingObservation> returned = await bulkLoader(
              DateTimeOffset.FromUnixTimeMilliseconds(cursor),
              DateTimeOffset.FromUnixTimeMilliseconds(end), PageSize, ct).ConfigureAwait(false);
            if (returned == null || returned.Count > PageSize)
              throw new InvalidDataException("Invalid bulk funding history page size.");

            // Validate all source rows before recording any values or coverage.
            long previousTime = cursor - 1;
            var unique = new Dictionary<(string Symbol, long At, string Type), FuturesFundingObservation>();
            foreach (FuturesFundingObservation row in returned)
            {
              FuturesFunding.Validate(row, row?.Symbol);
              string symbol = FuturesFunding.NormalizeSymbol(row.Symbol);
              long at = row.FundingTimeUtc.ToUnixTimeMilliseconds();
              if (at < cursor || at > end || at < previousTime)
                throw new InvalidDataException("Bulk funding page contains an unordered or unpinned settlement.");
              previousTime = at;
              var key = (symbol, at, row.RateType.ToUpperInvariant());
              if (unique.TryGetValue(key, out var prior) &&
                  (prior.NativeRate != row.NativeRate || prior.MarkPrice != row.MarkPrice))
                throw new InvalidDataException("Conflicting bulk funding rows share symbol/time/type.");
              unique[key] = row;
            }

            bool full = returned.Count == PageSize;
            long last = returned.Count == 0 ? cursor - 1 : previousTime;
            if (full && last <= cursor)
            {
              // Binance cannot page within one shared timestamp by startTime.
              // Do not certify this timestamp or invent missing symbol rows.
              log.DoLogWarn("Bulk funding page is saturated at one settlement timestamp; " +
                "remaining symbols will use individually verified history.");
              break;
            }

            long verifiedThrough = full ? last - 1 : end;
            lock (_sync)
            {
              // Check conflicts across every selected series atomically.
              foreach (var entry in unique)
              {
                if (entry.Key.At > verifiedThrough || !selected.Contains(entry.Key.Symbol)) continue;
                Series state = Ensure(entry.Key.Symbol);
                var key = (entry.Key.At, entry.Key.Type);
                if (state.Rows.TryGetValue(key, out var prior) &&
                    (prior.NativeRate != entry.Value.NativeRate || prior.MarkPrice != entry.Value.MarkPrice))
                  throw new InvalidDataException("Bulk funding conflicts with previously verified history.");
              }
              foreach (var entry in unique)
              {
                if (entry.Key.At > verifiedThrough || !selected.Contains(entry.Key.Symbol)) continue;
                Ensure(entry.Key.Symbol).Rows[(entry.Key.At, entry.Key.Type)] = entry.Value;
              }
              foreach (string symbol in symbols)
                Ensure(symbol).Complete.Add((cursor, verifiedThrough));
            }
            dirty = true;

            // A full page's final timestamp must be requested inclusively.
            // Otherwise all selected symbols have a verified exhausted window.
            cursor = full ? last : end + 1;
          }
          catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
          catch (Exception exception) when (exception is not OutOfMemoryException)
          {
            bulkFailures++;
            log.DoLogWarn("Bulk funding history unavailable (" +
              exception.GetType().Name + "); retaining safe per-symbol fallback.");
            break;
          }
        }

        if (dirty && _disk != null)
        {
          foreach (string symbol in symbols)
          {
            IReadOnlyList<FuturesFundingObservation> snapshot;
            (long Start, long End)[] coverage;
            lock (_sync)
            {
              Series saved = Ensure(symbol);
              snapshot = saved.Rows.Values.ToArray();
              coverage = saved.Complete.ToArray();
            }
            _disk.TrySave(symbol, snapshot, coverage, asOfUtc, log);
          }
        }
      }
      finally { _gate.Release(); }

      // Only unresolved history needs symbol-specific requests. If provider
      // capacity already deferred the bulk pass, do not seek a second permit.
      // Preserve the unbounded runtime sentinel after bulk requests. Subtracting
      // bulkRequests from int.MaxValue produces a large finite value that
      // ValidateBudget rejects instead of allowing the per-symbol fallback.
      int fallbackBudget = permitDeferred ? 0 :
        requestBudget == int.MaxValue ? int.MaxValue : requestBudget - bulkRequests;
      FundingRefreshReport fallback = await RefreshAsync(symbols, horizonMinutes,
        asOfUtc, fallbackBudget, symbolLoader, log, ct, requestPermit).ConfigureAwait(false);
      return new FundingRefreshReport
      {
        SelectedSymbols = symbols.Length,
        Requests = bulkRequests + fallback.Requests,
        BulkRequests = bulkRequests,
        SymbolRequests = fallback.Requests,
        Failures = bulkFailures + fallback.Failures,
        CompleteSymbols = fallback.CompleteSymbols
      };
    }

    public async Task<FundingRefreshReport> RefreshAsync(IEnumerable<string> eligible,
      int horizonMinutes, DateTimeOffset asOfUtc, int requestBudget,
      FundingHistoryLoader loader, LogHelper log, CancellationToken ct = default,
      Func<CancellationToken, Task<bool>> requestPermit = null)
    {
      if (loader == null) throw new ArgumentNullException(nameof(loader));
      if (log == null) throw new ArgumentNullException(nameof(log));
      if (horizonMinutes < 5 || horizonMinutes % 5 != 0) throw new ArgumentOutOfRangeException(nameof(horizonMinutes));
      ValidateBudget(requestBudget);
      string[] symbols = (eligible ?? Array.Empty<string>()).Where(s => !String.IsNullOrWhiteSpace(s))
        .Select(FuturesFunding.NormalizeSymbol).Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(s => s, StringComparer.Ordinal).ToArray();
      if (symbols.Length == 0)
        return new FundingRefreshReport { SelectedSymbols = 0 };

      long end = asOfUtc.ToUniversalTime().ToUnixTimeMilliseconds();
      long start = end - (long)horizonMinutes * 60000L + 1;
      if (start <= 0) return new FundingRefreshReport { SelectedSymbols = symbols.Length };

      await _gate.WaitAsync(ct).ConfigureAwait(false);
      try
      {
        string[] schedule;
        lock (_sync)
        {
          var active = new HashSet<string>(symbols, StringComparer.OrdinalIgnoreCase);
          foreach (string symbol in _series.Keys.Where(x => !active.Contains(x)).ToArray()) _series.Remove(symbol);
          foreach (string symbol in _served.Keys.Where(x => !active.Contains(x)).ToArray()) _served.Remove(symbol);
          _restored.RemoveWhere(symbol => !active.Contains(symbol));
          foreach (Series state in _series.Values)
          {
            foreach (var key in state.Rows.Keys.Where(x => x.At < start).ToArray()) state.Rows.Remove(key);
            state.Complete.RemoveAll(x => x.End < start);
          }
          schedule = symbols.OrderBy(s => _served.TryGetValue(s, out long last) ? last : long.MinValue)
            .ThenBy(s => s, StringComparer.Ordinal).ToArray();
        }

        int restored = 0;
        if (_disk != null)
        {
          foreach (string symbol in symbols)
          {
            bool restore;
            lock (_sync) restore = _restored.Add(symbol);
            if (!restore) continue;
            FuturesFundingDiskSnapshot saved = _disk.TryRestore(symbol, asOfUtc, log);
            lock (_sync)
            {
              Series state = Ensure(symbol);
              foreach (FuturesFundingObservation row in saved.Rows)
              {
                var key = (row.FundingTimeUtc.ToUnixTimeMilliseconds(), row.RateType.ToUpperInvariant());
                state.Rows[key] = row;
              }
              state.Complete.AddRange(saved.CompleteIntervals);
              foreach (var key in state.Rows.Keys.Where(value => value.At < start).ToArray()) state.Rows.Remove(key);
              state.Complete.RemoveAll(value => value.End < start);
            }
            if (saved.Rows.Count > 0 || saved.CompleteIntervals.Count > 0) restored++;
          }
        }

        int requests = 0, failures = 0;
        bool deferRemaining = false;
        foreach (string symbol in schedule)
        {
          if (deferRemaining) break;
          for (int page = 0; page < PagesPerSymbol && requests < requestBudget; page++)
          {
            ct.ThrowIfCancellationRequested();
            long from;
            lock (_sync)
            {
              Series state = Ensure(symbol);
              if (Covers(state, start, end)) break;
              from = FirstUncovered(state, start, end);
              if (from > end) break;
              _served[symbol] = end;
            }

            if (requestPermit != null && !await requestPermit(ct).ConfigureAwait(false))
            {
              deferRemaining = true;
              break;
            }
            requests++;
            try
            {
              var returned = await loader(symbol,
                DateTimeOffset.FromUnixTimeMilliseconds(from),
                DateTimeOffset.FromUnixTimeMilliseconds(end), PageSize, ct).ConfigureAwait(false);
              if (returned == null || returned.Count > PageSize)
                throw new InvalidOperationException("Invalid funding history page.");

              // Validate the entire page before touching any shared state.
              var pending = new SortedDictionary<(long At, string Kind), FuturesFundingObservation>();
              foreach (FuturesFundingObservation row in returned)
              {
                FuturesFunding.Validate(row, symbol);
                long at = row.FundingTimeUtc.ToUnixTimeMilliseconds();
                if (at < from || at > end) throw new InvalidOperationException("Unpinned funding history timestamp.");
                var key = (at, row.RateType.ToUpperInvariant());
                if (pending.TryGetValue(key, out var old) &&
                  (old.NativeRate != row.NativeRate || old.MarkPrice != row.MarkPrice))
                  throw new InvalidOperationException("Conflicting funding history page rows.");
                pending[key] = row;
              }
              long last = pending.Count == 0 ? from - 1 : pending.Last().Key.At;
              bool exhausted = returned.Count < PageSize;
              if (!exhausted && last < from) throw new InvalidOperationException("Invalid full funding page.");
              // The final timestamp of a full page may have additional
              // Regular/Special rows on the next page. Only certify history
              // strictly BEFORE that timestamp, then query again inclusively.
              // If a provider never makes progress, the bounded page budget
              // keeps the series UNAVAILABLE rather than skipping events.

              lock (_sync)
              {
                Series state = Ensure(symbol);
                foreach (var entry in pending)
                {
                  if (state.Rows.TryGetValue(entry.Key, out var old) &&
                    (old.NativeRate != entry.Value.NativeRate || old.MarkPrice != entry.Value.MarkPrice))
                    throw new InvalidOperationException("Conflicting cached funding history.");
                }
                foreach (var entry in pending) state.Rows[entry.Key] = entry.Value;
                if (exhausted) state.Complete.Add((from, end));
                else if (last > from) state.Complete.Add((from, last - 1));
              }
              if (_disk != null)
              {
                IReadOnlyList<FuturesFundingObservation> snapshot;
                (long Start, long End)[] coverage;
                lock (_sync)
                {
                  Series saved = Ensure(symbol);
                  snapshot = saved.Rows.Values.ToArray();
                  coverage = saved.Complete.ToArray();
                }
                _disk.TrySave(symbol, snapshot, coverage, asOfUtc, log);
              }
              if (exhausted) break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
              failures++;
              log.DoLogWarn("Funding history unavailable for '" + symbol + "' (" +
                ex.GetType().Name + "); affected metric remains unavailable.");
              break;
            }
          }
        }

        int complete;
        lock (_sync) complete = symbols.Count(symbol =>
          _series.TryGetValue(symbol, out var state) && Covers(state, start, end));
        return new FundingRefreshReport
        {
          SelectedSymbols = symbols.Length, Requests = requests,
          Failures = failures, CompleteSymbols = complete
        };
      }
      finally { _gate.Release(); }
    }

    public bool TrySnapshot(string symbol, int trendMinutes, DateTimeOffset asOfUtc,
      out IReadOnlyList<FuturesFundingObservation> history)
    {
      history = Array.Empty<FuturesFundingObservation>();
      if (trendMinutes < 5 || trendMinutes % 5 != 0) return false;
      long end = asOfUtc.ToUniversalTime().ToUnixTimeMilliseconds();
      long start = end - (long)trendMinutes * 60000L + 1;
      lock (_sync)
      {
        if (!_series.TryGetValue(FuturesFunding.NormalizeSymbol(symbol), out var state) ||
          !Covers(state, start, end)) return false;
        history = state.Rows.Where(row => row.Key.At >= start && row.Key.At <= end)
          .Select(row => row.Value).ToArray();
        return true;
      }
    }

    private Series Ensure(string symbol)
    {
      if (!_series.TryGetValue(symbol, out var state))
        _series[symbol] = state = new Series();
      return state;
    }

    private static long FirstUncovered(Series state, long start, long end)
    {
      long covered = start - 1;
      foreach (var part in state.Complete.OrderBy(x => x.Start))
      {
        if (part.Start > covered + 1) break;
        covered = Math.Max(covered, part.End);
        if (covered >= end) return end + 1;
      }
      return covered + 1;
    }

    private static bool Covers(Series state, long start, long end) =>
      FirstUncovered(state, start, end) > end;
  }
}
