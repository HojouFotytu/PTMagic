using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Helper;
using Core.Main;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Observation-only runtime for the Phase 1 Binance Futures analytics data foundation.
  /// It maintains normalized 5-minute OHLCV history independently from the legacy
  /// MarketData_*.json trend pipeline.
  /// </summary>
  public static class BinanceFuturesAnalyticsRuntime
  {
    // 360 x 5m = 30 hours. This covers all initial Phase 1 return horizons through 24h
    // plus headroom for ATRP and recent volume-expansion baselines.
    private const int BootstrapHistoryCandles = 360;
    private const int RefreshConcurrency = 2;
    private static readonly TimeSpan BaseInterval = TimeSpan.FromMinutes(5);
    private static readonly object SnapshotLock = new object();
    private static readonly MarketDataService DataServiceInstance = new MarketDataService(new MarketDataStore(500));
    private static AnalyzerUniverseSnapshot _currentUniverse;
    private static IReadOnlyList<MarketDataRefreshStatus> _lastRefreshStatuses = Array.Empty<MarketDataRefreshStatus>();

    public static MarketDataService DataService => DataServiceInstance;

    public static AnalyzerUniverseSnapshot CurrentUniverse
    {
      get
      {
        lock (SnapshotLock)
        {
          return _currentUniverse;
        }
      }
    }

    public static IReadOnlyList<MarketDataRefreshStatus> LastRefreshStatuses
    {
      get
      {
        lock (SnapshotLock)
        {
          return _lastRefreshStatuses;
        }
      }
    }

    public static async Task RefreshObservationDataAsync(IEnumerable<string> exchangeMarkets, string mainMarket, PTMagicConfiguration systemConfiguration, LogHelper log, CancellationToken cancellationToken = default)
    {
      if (exchangeMarkets == null)
      {
        throw new ArgumentNullException(nameof(exchangeMarkets));
      }
      if (systemConfiguration == null)
      {
        throw new ArgumentNullException(nameof(systemConfiguration));
      }
      if (log == null)
      {
        throw new ArgumentNullException(nameof(log));
      }

      Core.Main.DataObjects.PTMagicData.MarketAnalyzer settings = systemConfiguration.AnalyzerSettings?.MarketAnalyzer;
      if (settings?.UseProfitTrailerEnabledPairs == true)
      {
        // The exact PT-enabled population is not available until PTMagic has completed
        // BuildMarketList(). Do not create a falsely labelled analyzer universe here.
        lock (SnapshotLock)
        {
          _currentUniverse = null;
          _lastRefreshStatuses = Array.Empty<MarketDataRefreshStatus>();
        }

        log.DoLogDebug("BinanceFutures analytics - normalized observation refresh skipped because UseProfitTrailerEnabledPairs is enabled; exact universe is not frozen until after BuildMarketList().");
        return;
      }

      List<string> eligibleSymbols = BinanceFuturesMarketUniverse
        .FilterMarketNames(exchangeMarkets, systemConfiguration)
        .Where(symbol => !String.IsNullOrWhiteSpace(symbol))
        .Select(symbol => symbol.Trim().ToUpperInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(symbol => symbol, StringComparer.OrdinalIgnoreCase)
        .ToList();

      AnalyzerUniverseSnapshot universe = new AnalyzerUniverseSnapshot(
        DateTimeOffset.UtcNow,
        mainMarket,
        eligibleSymbols.Select(symbol => new AnalyzerUniverseMarket { Symbol = symbol }),
        "Binance Futures exchange universe filtered by the active analyzer underlying-type policy");

      if (universe.MarketCount == 0)
      {
        lock (SnapshotLock)
        {
          _currentUniverse = universe;
          _lastRefreshStatuses = Array.Empty<MarketDataRefreshStatus>();
        }

        log.DoLogWarn("BinanceFutures analytics - normalized analyzer universe is empty; no observation data was refreshed.");
        return;
      }

      IReadOnlyList<MarketDataRefreshStatus> statuses = await DataServiceInstance.RefreshAsync(
        universe.Symbols,
        BaseInterval,
        BootstrapHistoryCandles,
        BinanceFutures.GetFiveMinuteMarketCandlesAsync,
        log,
        RefreshConcurrency,
        cancellationToken).ConfigureAwait(false);

      MarketDataRefreshStatus[] statusSnapshot = statuses.ToArray();
      lock (SnapshotLock)
      {
        _currentUniverse = universe;
        _lastRefreshStatuses = Array.AsReadOnly(statusSnapshot);
      }

      int skipped = statusSnapshot.Count(status => status != null && status.Skipped);
      int refreshed = statusSnapshot.Count(status => status != null && status.Succeeded && !status.Skipped);
      int failed = statusSnapshot.Count(status => status == null || !status.Succeeded);
      int receivedCandles = statusSnapshot.Where(status => status != null).Sum(status => status.ReceivedCandleCount);

      if (refreshed == 0 && failed == 0)
      {
        log.DoLogInfo(
          "BinanceFutures analytics - normalized 5m data current; no kline requests required for " + skipped +
          " markets. Store series: " + DataServiceInstance.Store.SeriesCount + ".");
      }
      else
      {
        log.DoLogInfo(
          "BinanceFutures analytics - normalized 5m data refreshed for " + refreshed + "/" + universe.MarketCount +
          " markets (" + receivedCandles + " candles received, " + skipped + " current/skipped, " + failed +
          " failed). Store series: " + DataServiceInstance.Store.SeriesCount + ".");
      }
    }
  }
}
