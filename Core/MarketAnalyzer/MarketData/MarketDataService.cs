using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Helper;

namespace Core.MarketAnalyzer.MarketData
{
  public delegate Task<List<MarketCandle>> MarketCandleLoader(string symbol, int candlesNeeded, LogHelper log, CancellationToken cancellationToken);

  /// <summary>
  /// Central refresh coordinator for normalized market data. Feature calculators consume
  /// MarketDataStore snapshots and never call an exchange directly.
  /// </summary>
  public sealed class MarketDataService
  {
    private const int DefaultRefreshConcurrency = 2;
    private const int RefreshOverlapCandles = 2;

    private readonly MarketDataStore _store;

    public MarketDataService(MarketDataStore store = null)
    {
      _store = store ?? new MarketDataStore();
    }

    public MarketDataStore Store => _store;

    public async Task<IReadOnlyList<MarketDataRefreshStatus>> RefreshAsync(IEnumerable<string> symbols, TimeSpan interval, int targetHistoryCandles, MarketCandleLoader loader, LogHelper log, int maxConcurrency = DefaultRefreshConcurrency, CancellationToken cancellationToken = default)
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

      DateTimeOffset refreshStartedUtc = DateTimeOffset.UtcNow;
      MarketDataRefreshStatus[] statuses = new MarketDataRefreshStatus[distinctSymbols.Length];

      await Parallel.ForEachAsync(Enumerable.Range(0, distinctSymbols.Length), new ParallelOptions
      {
        MaxDegreeOfParallelism = maxConcurrency,
        CancellationToken = cancellationToken
      }, async (index, ct) =>
      {
        string symbol = distinctSymbols[index];
        int requestedCandleCount = GetRequiredCandleCount(symbol, interval, targetHistoryCandles, refreshStartedUtc);

        try
        {
          List<MarketCandle> candles = await loader(symbol, requestedCandleCount, log, ct).ConfigureAwait(false);
          candles ??= new List<MarketCandle>();

          List<MarketCandle> matchingCandles = candles
            .Where(candle => candle != null
              && candle.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)
              && candle.Interval == interval)
            .ToList();

          _store.UpsertCandles(matchingCandles);

          statuses[index] = new MarketDataRefreshStatus
          {
            Symbol = symbol,
            RefreshedAtUtc = DateTimeOffset.UtcNow,
            RequestedCandleCount = requestedCandleCount,
            ReceivedCandleCount = matchingCandles.Count,
            Succeeded = matchingCandles.Count > 0,
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
            Error = ex.Message
          };

          log.DoLogWarn("MarketDataService - Unable to refresh normalized candles for '" + symbol + "': " + ex.Message);
        }
      }).ConfigureAwait(false);

      return Array.AsReadOnly(statuses);
    }

    private int GetRequiredCandleCount(string symbol, TimeSpan interval, int targetHistoryCandles, DateTimeOffset refreshStartedUtc)
    {
      if (!_store.TryGetLatestCandle(symbol, interval, out MarketCandle latestCandle))
      {
        return targetHistoryCandles;
      }

      TimeSpan age = refreshStartedUtc - latestCandle.OpenTimeUtc;
      if (age < TimeSpan.Zero)
      {
        return RefreshOverlapCandles;
      }

      int missingOrCurrentIntervals = (int)Math.Ceiling(age.TotalMilliseconds / interval.TotalMilliseconds) + 1;
      int requested = missingOrCurrentIntervals + RefreshOverlapCandles;
      return Math.Min(targetHistoryCandles, Math.Max(RefreshOverlapCandles, requested));
    }
  }
}
