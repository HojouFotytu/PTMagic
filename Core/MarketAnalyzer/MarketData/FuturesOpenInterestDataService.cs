using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Helper;

namespace Core.MarketAnalyzer.MarketData
{
  public delegate Task<IReadOnlyList<FuturesOpenInterestObservation>> OpenInterestHistoryLoader(
    string symbol, DateTimeOffset requestEndUtc, int limit, CancellationToken cancellationToken);

  public sealed class OpenInterestRefreshReport
  {
    public int EligibleSymbols { get; init; }
    public int Requests { get; init; }
    public int SuccessfulPages { get; init; }
    public int FailedPages { get; init; }
    public int CurrentSymbols { get; init; }
    public int AvailableSymbols { get; init; }
    public int DeferredSymbols { get; init; }
    public int RestoredSymbols { get; init; }
  }

  /// <summary>
  /// Bounded, incremental, per-symbol Binance USD-M historical OI collection.
  /// Source is optional: only the explicit OI metric path calls RefreshAsync.
  /// Rows are stored separately from OHLCV and never count as PT acknowledgements.
  /// </summary>
  public sealed class FuturesOpenInterestDataService
  {
    public const int DefaultRetentionHours = 72;
    public const int MaximumRetentionHours = 672; // <= 28d, less than provider's ~1mo limit.
    public const int DefaultRequestsPerRaid = 60;
    public const int MaximumRequestsPerRaid = 400;
    private const int MaxPagesPerSymbolPerRaid = 2;
    private const int PageSize = 500;

    private readonly FuturesOpenInterestDiskCache _disk;
    private readonly Dictionary<string, SortedDictionary<long, FuturesOpenInterestObservation>> _cache =
      new Dictionary<string, SortedDictionary<long, FuturesOpenInterestObservation>>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _restored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _lastAttemptBucket =
      new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);
    private readonly object _sync = new object();

    public FuturesOpenInterestDataService(string cacheDirectory)
    {
      _disk = new FuturesOpenInterestDiskCache(cacheDirectory);
    }

    public IReadOnlyList<FuturesOpenInterestObservation> Snapshot(string symbol, DateTimeOffset asOfUtc,
      int retentionHours = DefaultRetentionHours)
    {
      ValidateRetention(retentionHours);
      string normalized = FuturesOpenInterest.NormalizeSymbol(symbol);
      lock (_sync)
      {
        if (!_cache.TryGetValue(normalized, out var rows))
          return Array.Empty<FuturesOpenInterestObservation>();
        long cutoff = asOfUtc.ToUniversalTime().AddHours(-retentionHours).ToUnixTimeMilliseconds()
          - (long)FuturesOpenInterest.SourcePeriod.TotalMilliseconds;
        long maximum = asOfUtc.ToUniversalTime().ToUnixTimeMilliseconds();
        return new ReadOnlyCollection<FuturesOpenInterestObservation>(rows
          .Where(pair => pair.Key >= cutoff && pair.Key <= maximum)
          .Select(pair => pair.Value).ToList());
      }
    }

    public static void ValidateRetention(int hours)
    {
      if (hours < 1 || hours > MaximumRetentionHours)
        throw new ArgumentOutOfRangeException(nameof(hours), "OI retention must be between 1 and 672 hours.");
    }

    public static void ValidateBudget(int requestsPerRaid)
    {
      if (requestsPerRaid < 0 || requestsPerRaid > MaximumRequestsPerRaid)
        throw new ArgumentOutOfRangeException(nameof(requestsPerRaid), "OI request budget must be between 0 and 400.");
    }

    /// <summary>
    /// Exposed for offline regression and network adapter injection. One HTTP
    /// invocation is one page; concurrency is deliberately serial, leaving
    /// the existing shared Binance limiter responsible for server-wide rates.
    /// </summary>
    public async Task<OpenInterestRefreshReport> RefreshAsync(
      IEnumerable<string> eligibleSymbols, int maximumTrendMinutes,
      DateTimeOffset pinnedAsOfUtc, int retentionHours, int requestsPerRaid,
      OpenInterestHistoryLoader loader, LogHelper log, CancellationToken cancellationToken = default)
    {
      if (loader == null) throw new ArgumentNullException(nameof(loader));
      if (log == null) throw new ArgumentNullException(nameof(log));
      ValidateBudget(requestsPerRaid);
      ValidateRetention(retentionHours);
      if (maximumTrendMinutes < 5 || maximumTrendMinutes % 5 != 0)
        throw new ArgumentOutOfRangeException(nameof(maximumTrendMinutes));
      string[] symbols = (eligibleSymbols ?? Array.Empty<string>())
        .Where(symbol => !String.IsNullOrWhiteSpace(symbol))
        .Select(FuturesOpenInterest.NormalizeSymbol)
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(symbol => symbol, StringComparer.Ordinal).ToArray();
      if (symbols.Length == 0 || requestsPerRaid == 0 || maximumTrendMinutes > retentionHours * 60)
        return new OpenInterestRefreshReport { EligibleSymbols = symbols.Length,
          DeferredSymbols = symbols.Length };

      await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      {
        // Keep in-memory state bounded to the current selected population and
        // configured historical horizon; persisted cache retains other symbols.
        HashSet<string> active = new HashSet<string>(symbols, StringComparer.OrdinalIgnoreCase);
        lock (_sync)
        {
          foreach (string symbol in _cache.Keys.Where(key => !active.Contains(key)).ToArray())
            _cache.Remove(symbol);
          _restored.RemoveWhere(symbol => !active.Contains(symbol));
          foreach (string symbol in _lastAttemptBucket.Keys.Where(key => !active.Contains(key)).ToArray())
            _lastAttemptBucket.Remove(symbol);
          long cutoff = pinnedAsOfUtc.ToUniversalTime().AddHours(-retentionHours)
            .ToUnixTimeMilliseconds() - (long)FuturesOpenInterest.SourcePeriod.TotalMilliseconds;
          foreach (var rows in _cache.Values)
            foreach (long time in rows.Keys.Where(time => time < cutoff).ToArray())
              rows.Remove(time);
        }

        long intervalMs = (long)FuturesOpenInterest.SourcePeriod.TotalMilliseconds;
        long pinMs = pinnedAsOfUtc.ToUniversalTime().ToUnixTimeMilliseconds();
        long endpointMs = pinMs - pinMs % intervalMs;
        DateTimeOffset endpoint = DateTimeOffset.FromUnixTimeMilliseconds(endpointMs);
        long requiredStart = endpointMs - (long)maximumTrendMinutes * 60000L;

        int restored = 0;
        foreach (string symbol in symbols)
        {
          cancellationToken.ThrowIfCancellationRequested();
          bool shouldRestore;
          lock (_sync) shouldRestore = _restored.Add(symbol);
          if (!shouldRestore) continue;
          IReadOnlyList<FuturesOpenInterestObservation> saved = _disk.TryRestore(symbol, pinnedAsOfUtc, log);
          if (saved.Count == 0) continue;
          Insert(symbol, saved, pinnedAsOfUtc, retentionHours);
          restored++;
        }

        // Fairness: least recently serviced symbols first, without relying on
        // the exchange's arbitrary enumeration order. Missing data never
        // counts as zero or inherits another market's OI history.
        string[] candidates;
        lock (_sync)
          candidates = symbols.OrderBy(symbol => _lastAttemptBucket.TryGetValue(symbol, out long last)
              ? last : long.MinValue)
            .ThenBy(symbol => symbol, StringComparer.Ordinal).ToArray();

        int requests = 0, succeeded = 0, failed = 0;
        foreach (string symbol in candidates)
        {
          if (requests >= requestsPerRaid) break;
          cancellationToken.ThrowIfCancellationRequested();
          var attemptedEnds = new HashSet<long>();
          for (int pageNumber = 0; pageNumber < MaxPagesPerSymbolPerRaid && requests < requestsPerRaid; pageNumber++)
          {
            (long end, int limit)? plan = Plan(symbol, endpointMs, requiredStart, maximumTrendMinutes);
            if (!plan.HasValue || !attemptedEnds.Add(plan.Value.end)) break;

            lock (_sync) _lastAttemptBucket[symbol] = endpointMs;
            requests++;
            try
            {
              DateTimeOffset requestEnd = DateTimeOffset.FromUnixTimeMilliseconds(plan.Value.end);
              IReadOnlyList<FuturesOpenInterestObservation> received =
                await loader(symbol, requestEnd, plan.Value.limit, cancellationToken).ConfigureAwait(false);
              if (received == null) throw new InvalidOperationException("OI provider returned no historical page.");
              // Honor both the raid pin and the individual pagination end.
              FuturesOpenInterestObservation[] accepted = received
                .Where(row => row != null && row.TimestampUtc.ToUnixTimeMilliseconds() <= plan.Value.end).ToArray();
              Insert(symbol, accepted, pinnedAsOfUtc, retentionHours);
              succeeded++;
              // No more data in this window: leave missing rows missing and
              // retry only on a different pinned close, not in a tight loop.
              if (accepted.Length == 0) break;
              _disk.TrySave(symbol, Snapshot(symbol, pinnedAsOfUtc, retentionHours), log);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
              throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
              failed++;
              log.DoLogWarn("Open-interest history unavailable for '" + symbol + "' (" +
                exception.GetType().Name + "); configured OI metrics may remain unavailable.");
              break;
            }
          }
        }

        int available = symbols.Count(symbol => HasExactEndpoints(symbol, endpointMs, requiredStart));
        int current = symbols.Count(symbol => HasTimestamp(symbol, endpointMs));
        return new OpenInterestRefreshReport
        {
          EligibleSymbols = symbols.Length, Requests = requests, SuccessfulPages = succeeded,
          FailedPages = failed, CurrentSymbols = current, AvailableSymbols = available,
          DeferredSymbols = symbols.Length - available, RestoredSymbols = restored
        };
      }
      finally { _refreshGate.Release(); }
    }

    public string ExplainUnavailable(string symbol, int trendMinutes, DateTimeOffset asOfUtc,
      int retentionHours = DefaultRetentionHours)
    {
      if (trendMinutes > retentionHours * 60)
        return "Open-interest horizon exceeds OpenInterestRetentionHours; increase the OI cache limit.";
      long ms = asOfUtc.ToUniversalTime().ToUnixTimeMilliseconds();
      long step = (long)FuturesOpenInterest.SourcePeriod.TotalMilliseconds;
      long end = ms - ms % step, start = end - trendMinutes * 60000L;
      return "Historical Binance futures OI has no exact " +
        (HasTimestamp(symbol, end) ? "reference" : "current") +
        " endpoint for this pinned trend. Source polling is budgeted; missing provider buckets are not interpolated.";
    }

    private (long end, int limit)? Plan(string symbol, long endpointMs, long referenceMs, int trendMinutes)
    {
      lock (_sync)
      {
        if (!_cache.TryGetValue(symbol, out var rows) || rows.Count == 0)
          return (endpointMs, Math.Min(PageSize, trendMinutes / 5 + 3));

        if (rows.ContainsKey(endpointMs) && rows.ContainsKey(referenceMs)) return null;
        if (!rows.ContainsKey(endpointMs))
        {
          long newest = rows.Last().Key;
          long gap = newest < endpointMs
            ? (endpointMs - newest) / (long)FuturesOpenInterest.SourcePeriod.TotalMilliseconds + 3 : 6;
          return (endpointMs, (int)Math.Min(PageSize, Math.Max(6, gap)));
        }

        // Already current but a reference is absent: ask for the older source
        // page. When the missing reference is an interior gap, request the
        // narrowly bounded page ending at that exact missing reference.
        long first = rows.First().Key;
        long end = first > referenceMs ? first - 1 : referenceMs;
        int limit = first > referenceMs
          ? (int)Math.Min(PageSize, Math.Max(6,
              (first - referenceMs) / (long)FuturesOpenInterest.SourcePeriod.TotalMilliseconds + 2))
          : 8;
        return (end, limit);
      }
    }

    private void Insert(string symbol, IReadOnlyList<FuturesOpenInterestObservation> observations,
      DateTimeOffset pinnedAsOfUtc, int retentionHours)
    {
      // Validate the complete page BEFORE applying any of its observations.
      // A malformed response must not partially advance the accepted history.
      long min = pinnedAsOfUtc.ToUniversalTime().AddHours(-retentionHours)
        .ToUnixTimeMilliseconds() - (long)FuturesOpenInterest.SourcePeriod.TotalMilliseconds;
      long max = pinnedAsOfUtc.ToUniversalTime().ToUnixTimeMilliseconds();
      long interval = (long)FuturesOpenInterest.SourcePeriod.TotalMilliseconds;
      var accepted = new SortedDictionary<long, FuturesOpenInterestObservation>();
      foreach (var row in observations)
      {
        if (row == null || !row.IsHistoricalStatistic ||
          row.Period != FuturesOpenInterest.SourcePeriod ||
          !String.Equals(row.Exchange, "BinanceFutures", StringComparison.OrdinalIgnoreCase) ||
          !String.Equals(row.Symbol, symbol, StringComparison.OrdinalIgnoreCase) ||
          row.NativeOpenInterest < 0 || !row.ReportedOpenInterestValue.HasValue ||
          row.ReportedOpenInterestValue.Value < 0)
          throw new InvalidOperationException("Mixed or invalid historical OI response.");
        long time = row.TimestampUtc.ToUnixTimeMilliseconds();
        if (time % interval != 0)
          throw new InvalidOperationException("Historical OI timestamps must be UTC-aligned to the provider interval.");
        if (time < min || time > max) continue;
        if (accepted.TryGetValue(time, out var duplicate) &&
          (duplicate.NativeOpenInterest != row.NativeOpenInterest ||
           duplicate.ReportedOpenInterestValue != row.ReportedOpenInterestValue))
          throw new InvalidOperationException("Conflicting duplicate historical OI observations.");
        accepted[time] = row;
      }
      lock (_sync)
      {
        if (!_cache.TryGetValue(symbol, out var rows))
          _cache[symbol] = rows = new SortedDictionary<long, FuturesOpenInterestObservation>();
        foreach (var row in accepted)
          if (rows.TryGetValue(row.Key, out var existing) &&
            (existing.NativeOpenInterest != row.Value.NativeOpenInterest ||
             existing.ReportedOpenInterestValue != row.Value.ReportedOpenInterestValue))
            throw new InvalidOperationException("Conflicting cached historical OI observation.");
        foreach (var row in accepted) rows[row.Key] = row.Value;
        foreach (long old in rows.Keys.Where(time => time < min || time > max).ToArray())
          rows.Remove(old);
      }
    }

    private bool HasExactEndpoints(string symbol, long end, long start) =>
      HasTimestamp(symbol, end) && HasTimestamp(symbol, start);

    private bool HasTimestamp(string symbol, long timestamp)
    {
      lock (_sync) return _cache.TryGetValue(symbol, out var rows) && rows.ContainsKey(timestamp);
    }
  }
}
