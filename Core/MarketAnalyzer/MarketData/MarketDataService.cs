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

    public async Task<IReadOnlyList<MarketDataRefreshStatus>> RefreshAtAsync(IEnumerable<string> symbols, TimeSpan interval, int targetHistoryCandles, AnchoredMarketCandleLoader loader, LogHelper log, int maxConcurrency = DefaultRefreshConcurrency, CancellationToken cancellationToken = default, DateTimeOffset? refreshAsOfUtc = null, string cacheExchange = null, IReadOnlyCollection<int> takerTrendMinutes = null, bool supportsHistoricalAnchors = false, Func<string, DateTimeOffset?> onboardDate = null)
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
        if (supportsHistoricalAnchors)
        {
          statuses[index] = await RefreshMissingRangesAsync(symbol, interval, targetHistoryCandles,
            loader, log, ct, refreshStartedUtc, cacheExchange, takerTrendMinutes,
            onboardDate?.Invoke(symbol)).ConfigureAwait(false);
          return;
        }
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

    private sealed class MissingRange
    {
      public long First;
      public long Last;
      public bool Historical;
    }

    // Production loaders support a historical end anchor. Legacy count-only
    // adapters retain their original contract and never receive old anchors.
    private async Task<MarketDataRefreshStatus> RefreshMissingRangesAsync(
      string symbol, TimeSpan interval, int target, AnchoredMarketCandleLoader loader,
      LogHelper log, CancellationToken ct, DateTimeOffset now, string cacheExchange,
      IReadOnlyCollection<int> takerTrendMinutes, DateTimeOffset? listed)
    {
      long step = checked((long)interval.TotalMilliseconds);
      long nowMs = now.ToUnixTimeMilliseconds();
      long forming = nowMs / step * step;
      long first = checked(forming - (long)(target - 1) * step);
      if (listed.HasValue && listed.Value > DateTimeOffset.UnixEpoch && listed.Value <= now)
      {
        long listingMs = listed.Value.ToUnixTimeMilliseconds();
        first = Math.Max(first, listingMs / step * step);
      }
      var snapshot = _store.GetSnapshot(symbol, interval, now);
      var cached = snapshot.Candles.Where(c => c.OpenTimeUtc.ToUnixTimeMilliseconds() >= first &&
        c.OpenTimeUtc.ToUnixTimeMilliseconds() <= forming).ToDictionary(c => c.OpenTimeUtc.ToUnixTimeMilliseconds());
      bool ClosedAt(long open) => cached.TryGetValue(open, out var c) && c.IsClosed &&
        c.CloseTimeUtc.ToUnixTimeMilliseconds() == open + step - 1;
      long latestClosed = cached.Keys.Where(ClosedAt).DefaultIfEmpty(first - step).Max();
      bool cold = latestClosed < first;
      var takerWindows = (takerTrendMinutes ?? Array.Empty<int>()).Where(m => m > 0)
        .Select(m => (long)m * 60000L).Where(ms => ms % step == 0)
        .Select(ms => (Start: nowMs / ms * ms - ms, End: nowMs / ms * ms))
        .Where(w => w.Start >= first).ToArray();
      bool NeedsRepair(long open) => !ClosedAt(open) ||
        (!cached[open].TakerBuyQuoteVolume.HasValue &&
          takerWindows.Any(w => open >= w.Start && open < w.End));
      var ranges = new List<MissingRange>();
      // Fetch recent candles (including forming) with the existing two-bar
      // overlap; repair older holes separately, without fetching intervening
      // verified history. A cached forming bar must be refreshed when it closes.
      long recentFirst = cold ? first : Math.Max(first, Math.Min(forming, latestClosed + step) - RefreshOverlapCandles * step);
      if (!cold && latestClosed == forming - step && cached.TryGetValue(forming, out var formingBar)
          && !formingBar.IsClosed)
        recentFirst = forming + step; // already pinned to this forming interval
      for (long open = first; open < Math.Min(recentFirst, forming); open += step)
      {
        if (!NeedsRepair(open)) continue;
        if (ranges.Count > 0 && ranges[ranges.Count - 1].Last + step == open)
          ranges[ranges.Count - 1].Last = open;
        else ranges.Add(new MissingRange { First = open, Last = open, Historical = true });
      }
      if (recentFirst <= forming)
        ranges.Add(new MissingRange { First = recentFirst, Last = forming, Historical = false });
      int requested = 0, received = 0, historicalRequested = 0, historicalReceived = 0;
      bool historicalProgress = false;
      string error = "";
      try
      {
        // Recent data first, then bounded historical pages. A completed page
        // is merged immediately; failure in a later page cannot discard it.
        foreach (var range in ranges.OrderBy(x => x.Historical))
        {
          for (long last = range.Last; last >= range.First;)
          {
            ct.ThrowIfCancellationRequested();
            int count = (int)Math.Min(1000L, (last - range.First) / step + 1);
            long start = last - (count - 1L) * step;
            bool historical = range.Historical || last < forming;
            var anchor = last == forming ? now : DateTimeOffset.FromUnixTimeMilliseconds(last + step - 1);
            requested += count;
            if (historical && !cold) historicalRequested += count;
            var rows = await loader(symbol, count, log, ct, anchor).ConfigureAwait(false);
            var matching = (rows ?? new List<MarketCandle>()).Where(c => c != null &&
              String.Equals(c.Symbol, symbol, StringComparison.OrdinalIgnoreCase) && c.Interval == interval &&
              c.OpenTimeUtc.ToUnixTimeMilliseconds() >= start && c.OpenTimeUtc.ToUnixTimeMilliseconds() <= last &&
              c.OpenTimeUtc.ToUnixTimeMilliseconds() % step == 0 &&
              c.CloseTimeUtc == c.OpenTimeUtc.Add(interval).AddMilliseconds(-1) &&
              (c.OpenTimeUtc.ToUnixTimeMilliseconds() == forming ? !c.IsClosed : c.IsClosed))
              .GroupBy(c => c.OpenTimeUtc).Select(g => g.Last()).ToList();
            foreach (var candle in matching) MarketDataStore.ValidateCandle(candle);
            _store.UpsertCandles(matching);
            received += matching.Count;
            if (historical && !cold)
            {
              historicalReceived += matching.Count;
              historicalProgress |= matching.Count > 0;
            }
            // Do not infer that a short provider response filled the range.
            // Continue other planned ranges; unfilled slots remain missing.
            last = start - step;
          }
        }
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
      catch (Exception ex)
      {
        error = ex.Message;
        log.DoLogWarn("MarketDataService - Unable to refresh normalized candles for '" + symbol + "': " + ex.Message);
      }
      finally
      {
        // Persist any completed historical work, even on cancellation or
        // partial failure, bypassing the ordinary checkpoint throttle.
        if (received > 0 && _warmStartCache != null && !String.IsNullOrWhiteSpace(cacheExchange))
          _warmStartCache.TrySave(cacheExchange, symbol, interval, now, _store, log,
            forceSave: historicalProgress || cold);
      }
      var final = _store.GetSnapshot(symbol, interval, now);
      var closed = final.ClosedCandles.Select(c => c.OpenTimeUtc.ToUnixTimeMilliseconds()).ToHashSet();
      bool complete = true;
      for (long open = first; open < forming; open += step)
        if (!closed.Contains(open)) { complete = false; break; }
      if (!complete && error.Length == 0) error = "Requested closed history remains incomplete.";
      if (received == 0 && ranges.Count > 0 && error.Length == 0) error = "No matching candles were returned.";
      return new MarketDataRefreshStatus
      {
        Symbol = symbol, RefreshedAtUtc = DateTimeOffset.UtcNow,
        RequestedCandleCount = requested, ReceivedCandleCount = received,
        FullHistoryRequest = cold && requested > 0,
        HistoricalBackfillRequestedCandleCount = historicalRequested,
        HistoricalBackfillReceivedCandleCount = historicalReceived,
        Succeeded = error.Length == 0, Skipped = ranges.Count == 0, Error = error
      };
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
