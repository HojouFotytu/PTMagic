using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Helper;

namespace Core.MarketAnalyzer.MarketData
{
  public delegate Task<List<MarketCandle>> MarketCandleLoader(string symbol, int candlesNeeded, LogHelper log, CancellationToken cancellationToken);
  public delegate Task<List<MarketCandle>> AnchoredMarketCandleLoader(string symbol, int candlesNeeded, LogHelper log, CancellationToken cancellationToken, DateTimeOffset asOfUtc);

  /// <summary>
  /// Central refresh coordinator for normalized market data. Feature calculators consume
  /// MarketDataStore snapshots and never call an exchange directly.
  /// </summary>
  public sealed class MarketDataService
  {
    private const int DefaultRefreshConcurrency = 2;
    private const int RefreshOverlapCandles = 2;

    private readonly MarketDataStore _store;
    private readonly MarketDataWarmStartCache _warmStartCache;

    public MarketDataService(MarketDataStore store = null, MarketDataWarmStartCache warmStartCache = null)
    {
      _store = store ?? new MarketDataStore();
      _warmStartCache = warmStartCache;
    }

    public MarketDataStore Store => _store;

    public Task<IReadOnlyList<MarketDataRefreshStatus>> RefreshAsync(IEnumerable<string> symbols, TimeSpan interval, int targetHistoryCandles, MarketCandleLoader loader, LogHelper log, int maxConcurrency = DefaultRefreshConcurrency, CancellationToken cancellationToken = default, DateTimeOffset? refreshAsOfUtc = null)
    {
      if (loader == null) throw new ArgumentNullException(nameof(loader));
      // Compatibility adapter for independent/in-memory loaders. Production
      // exchange clients use RefreshAtAsync and receive the fixed boundary.
      return RefreshAtAsync(symbols, interval, targetHistoryCandles,
        (symbol, count, logger, ct, _) => loader(symbol, count, logger, ct),
        log, maxConcurrency, cancellationToken, refreshAsOfUtc);
    }

    public async Task<IReadOnlyList<MarketDataRefreshStatus>> RefreshAtAsync(IEnumerable<string> symbols, TimeSpan interval, int targetHistoryCandles, AnchoredMarketCandleLoader loader, LogHelper log, int maxConcurrency = DefaultRefreshConcurrency, CancellationToken cancellationToken = default, DateTimeOffset? refreshAsOfUtc = null, string cacheExchange = null, IReadOnlyCollection<int> takerTrendMinutes = null)
    {
      if (symbols == null)
      {
        throw new ArgumentNullException(nameof(symbols));
      }
      if (interval <= TimeSpan.Zero)
      {
        throw new ArgumentOutOfRangeException(nameof(interval));
      }
      if (targetHistoryCandles <= 0)
      {
        throw new ArgumentOutOfRangeException(nameof(targetHistoryCandles));
      }
      if (loader == null)
      {
        throw new ArgumentNullException(nameof(loader));
      }
      if (log == null)
      {
        throw new ArgumentNullException(nameof(log));
      }
      if (maxConcurrency <= 0)
      {
        throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
      }

      string[] distinctSymbols = symbols
        .Where(symbol => !String.IsNullOrWhiteSpace(symbol))
        .Select(symbol => symbol.Trim().ToUpperInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

      // All refresh requests and the consuming MarketTrend evaluation must use
      // one raid-wide as-of time. Fetching hundreds of symbols can cross a
      // five-minute candle boundary during cold startup.
      DateTimeOffset refreshStartedUtc = (refreshAsOfUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
      MarketDataRefreshStatus[] statuses = new MarketDataRefreshStatus[distinctSymbols.Length];

      // The cache is only an optional seed. All restored series still pass the
      // normal pinned-window completeness and freshness checks below.
      HashSet<string> diskRestoredSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      if (_warmStartCache != null && !String.IsNullOrWhiteSpace(cacheExchange))
      {
        int restoredSeries = 0;
        long restoredCandles = 0;
        foreach (string symbol in distinctSymbols)
        {
          int count = _warmStartCache.TryRestore(cacheExchange, symbol, interval,
            refreshStartedUtc, _store, log);
          if (count > 0)
          {
            diskRestoredSymbols.Add(symbol);
            restoredSeries++;
            restoredCandles += count;
          }
        }
        if (restoredSeries > 0)
          log.DoLogInfo("Normalized warm-start restored " + restoredCandles +
            " validated closed candles across " + restoredSeries + " " + cacheExchange +
            " markets. Refreshing only missing, stale or incomplete windows.");
      }

      await Parallel.ForEachAsync(Enumerable.Range(0, distinctSymbols.Length), new ParallelOptions
      {
        MaxDegreeOfParallelism = maxConcurrency,
        CancellationToken = cancellationToken
      }, async (index, ct) =>
      {
        string symbol = distinctSymbols[index];
        // Only a missing/null taker field INSIDE already cached closed
        // history requires a taker-specific repair. Candles newer than the
        // latest cached close are handled by normal incremental refresh.
        int takerRepairCount = RequiredTakerRepairCount(symbol, interval,
          refreshStartedUtc, takerTrendMinutes);
        int requestedCandleCount = GetRequiredCandleCount(symbol, interval,
          targetHistoryCandles, refreshStartedUtc, diskRestoredSymbols.Contains(symbol));
        if (takerRepairCount > 0 && takerRepairCount <= targetHistoryCandles)
          requestedCandleCount = Math.Max(requestedCandleCount, takerRepairCount);

        if (requestedCandleCount == 0)
        {
          statuses[index] = new MarketDataRefreshStatus
          {
            Symbol = symbol,
            RefreshedAtUtc = refreshStartedUtc,
            RequestedCandleCount = 0,
            ReceivedCandleCount = 0,
            Succeeded = true,
            Skipped = true
          };
          return;
        }

        try
        {
          List<MarketCandle> candles = await loader(symbol, requestedCandleCount, log, ct, refreshStartedUtc).ConfigureAwait(false);
          candles ??= new List<MarketCandle>();

          List<MarketCandle> matchingCandles = candles
            .Where(candle => candle != null
              && candle.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)
              && candle.Interval == interval
              && candle.OpenTimeUtc <= refreshStartedUtc)
            .ToList();

          _store.UpsertCandles(matchingCandles);

          // Saving never changes fetch success or configuration decisions.
          // Per-market compressed files are atomically replaced and throttled.
          if (matchingCandles.Count > 0 && _warmStartCache != null
            && !String.IsNullOrWhiteSpace(cacheExchange))
          {
            bool repairCompleted = takerRepairCount > 0 &&
              RequiredTakerRepairCount(symbol, interval, refreshStartedUtc,
                takerTrendMinutes) == 0;
            _warmStartCache.TrySave(cacheExchange, symbol, interval,
              refreshStartedUtc, _store, log, forceSave: repairCompleted);
          }

          statuses[index] = new MarketDataRefreshStatus
          {
            Symbol = symbol,
            RefreshedAtUtc = DateTimeOffset.UtcNow,
            RequestedCandleCount = requestedCandleCount,
            ReceivedCandleCount = matchingCandles.Count,
            Succeeded = matchingCandles.Count > 0,
            Skipped = false,
            Error = matchingCandles.Count > 0 ? "" : "No matching candles were returned."
          };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
          throw;
        }
        catch (Exception ex)
        {
          statuses[index] = new MarketDataRefreshStatus
          {
            Symbol = symbol,
            RefreshedAtUtc = DateTimeOffset.UtcNow,
            RequestedCandleCount = requestedCandleCount,
            ReceivedCandleCount = 0,
            Succeeded = false,
            Skipped = false,
            Error = ex.Message
          };

          log.DoLogWarn("MarketDataService - Unable to refresh normalized candles for '" + symbol + "': " + ex.Message);
        }
      }).ConfigureAwait(false);

      return Array.AsReadOnly(statuses);
    }

    // Find the oldest deficient source field within a cached completed taker
    // window. In particular, a newly closed candle not yet acquired must
    // not trigger a H48-sized refetch every 15 minutes.
    private int RequiredTakerRepairCount(string symbol, TimeSpan interval,
      DateTimeOffset asOfUtc, IReadOnlyCollection<int> takerTrendMinutes)
    {
      if (takerTrendMinutes == null || takerTrendMinutes.Count == 0) return 0;
      MarketDataSeriesSnapshot snapshot = _store.GetSnapshot(symbol, interval, asOfUtc);
      IReadOnlyList<MarketCandle> closed = snapshot.ClosedCandles;
      if (closed == null || closed.Count == 0) return 0; // Standard cold fetch.
      long intervalMs = checked((long)interval.TotalMilliseconds);
      long asOfMs = asOfUtc.ToUnixTimeMilliseconds();
      long latestCachedOpen = closed[closed.Count - 1].OpenTimeUtc.ToUnixTimeMilliseconds();
      long earliestMissingOpen = long.MaxValue;

      foreach (int minutes in takerTrendMinutes)
      {
        if (minutes <= 0) continue;
        long trendMs = checked((long)TimeSpan.FromMinutes(minutes).TotalMilliseconds);
        if (trendMs % intervalMs != 0) continue; // Validated separately.
        long firstOpen = asOfMs / trendMs * trendMs - trendMs;
        for (long open = firstOpen; open < firstOpen + trendMs; open += intervalMs)
        {
          if (open > latestCachedOpen) continue; // Normal incremental work.
          if (!TryFindClosedCandle(closed, open, out MarketCandle candle) ||
              !candle.TakerBuyQuoteVolume.HasValue)
            earliestMissingOpen = Math.Min(earliestMissingOpen, open);
        }
      }
      if (earliestMissingOpen == long.MaxValue) return 0;
      // Anchor the repair to the oldest truly missing history point; preserve
      // two-candle overlap and the forming-candle allowance from the old path.
      long required = (asOfMs - earliestMissingOpen) / intervalMs +
        RefreshOverlapCandles + 1;
      return required >= int.MaxValue ? int.MaxValue : (int)required;
    }

    private static bool TryFindClosedCandle(IReadOnlyList<MarketCandle> candles,
      long expectedOpenMilliseconds, out MarketCandle candle)
    {
      int left = 0, right = candles.Count - 1;
      while (left <= right)
      {
        int middle = left + (right - left) / 2;
        MarketCandle current = candles[middle];
        long open = current.OpenTimeUtc.ToUnixTimeMilliseconds();
        if (open == expectedOpenMilliseconds)
        {
          candle = current;
          return true;
        }
        if (open < expectedOpenMilliseconds) left = middle + 1;
        else right = middle - 1;
      }
      candle = null;
      return false;
    }

    private int GetRequiredCandleCount(string symbol, TimeSpan interval, int targetHistoryCandles, DateTimeOffset refreshStartedUtc, bool restoredFromDisk)
    {
      MarketDataSeriesSnapshot snapshot = _store.GetSnapshot(symbol, interval, refreshStartedUtc);
      if (!snapshot.IsAvailable)
      {
        return targetHistoryCandles;
      }

      // A gap within retained history still requires a complete backfill.
      // A disk snapshot intentionally stores only closed candles, so it can
      // hold one fewer than the live target (which includes the forming bar).
      // Count the bars missing since its last close before deciding whether
      // the *historical* window is insufficient.
      if (snapshot.HasDetectedGaps)
        return targetHistoryCandles;

      // Keep the established conservative full-backfill rule for ordinary
      // incomplete in-memory series. Only a validated disk restoration may
      // legitimately omit the currently forming source candle.
      if (!restoredFromDisk && snapshot.SampleCount < targetHistoryCandles)
        return targetHistoryCandles;

      MarketCandle latestCandle = snapshot.Candles[snapshot.Candles.Count - 1];
      if (latestCandle.CloseTimeUtc > refreshStartedUtc)
        return snapshot.SampleCount >= targetHistoryCandles ? 0 : targetHistoryCandles;

      TimeSpan ageSinceClose = refreshStartedUtc - latestCandle.CloseTimeUtc;
      int missingIntervals = (int)Math.Min(targetHistoryCandles,
        Math.Max(1, Math.Floor(ageSinceClose.TotalMilliseconds / interval.TotalMilliseconds) + 1));
      if ((long)snapshot.SampleCount + missingIntervals < targetHistoryCandles)
        return targetHistoryCandles;

      int requested = missingIntervals + RefreshOverlapCandles;
      return Math.Min(targetHistoryCandles, Math.Max(RefreshOverlapCandles, requested));
    }
  }
}
