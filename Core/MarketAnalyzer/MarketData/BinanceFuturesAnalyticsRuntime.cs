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
    private static IReadOnlyList<string> _cachedExchangeSymbols = Array.Empty<string>();
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

      // Cache the exchange-qualified population before the final analyzer-universe policy
      // is frozen. ProfitTrailer allowed_underlying_type and enabled_pairs are resolved later
      // during BuildMarketList(), so applying those filters here would make the cache depend on
      // stale or not-yet-resolved PT settings.
      string[] exchangeSymbols = exchangeMarkets
        .Where(symbol => !String.IsNullOrWhiteSpace(symbol))
        .Select(symbol => symbol.Trim().ToUpperInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(symbol => symbol, StringComparer.OrdinalIgnoreCase)
        .ToArray();

      lock (SnapshotLock)
      {
        _cachedExchangeSymbols = Array.AsReadOnly(exchangeSymbols);
        _currentUniverse = null;
      }

      if (exchangeSymbols.Length == 0)
      {
        lock (SnapshotLock)
        {
          _lastRefreshStatuses = Array.Empty<MarketDataRefreshStatus>();
        }

        log.DoLogWarn("BinanceFutures analytics - exchange-qualified cache population is empty; no observation data was refreshed.");
        return;
      }

      IReadOnlyList<MarketDataRefreshStatus> statuses = await DataServiceInstance.RefreshAsync(
        exchangeSymbols,
        BaseInterval,
        BootstrapHistoryCandles,
        BinanceFutures.GetFiveMinuteMarketCandlesAsync,
        log,
        RefreshConcurrency,
        cancellationToken).ConfigureAwait(false);

      MarketDataRefreshStatus[] statusSnapshot = statuses.ToArray();
      lock (SnapshotLock)
      {
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
          "BinanceFutures analytics - normalized 5m data refreshed for " + refreshed + "/" + exchangeSymbols.Length +
          " markets (" + receivedCandles + " candles received, " + skipped + " current/skipped, " + failed +
          " failed). Store series: " + DataServiceInstance.Store.SeriesCount + ".");
      }
    }

    public static void FreezeAnalyzerUniverse(List<string> pairsLines, PTMagicConfiguration systemConfiguration, LogHelper log)
    {
      if (systemConfiguration == null)
      {
        throw new ArgumentNullException(nameof(systemConfiguration));
      }
      if (log == null)
      {
        throw new ArgumentNullException(nameof(log));
      }

      IReadOnlyList<string> cachedSymbols;
      lock (SnapshotLock)
      {
        cachedSymbols = _cachedExchangeSymbols;
      }

      List<string> eligibleSymbols = BinanceFuturesMarketUniverse
        .FilterMarketNames(cachedSymbols, systemConfiguration)
        .Where(symbol => !String.IsNullOrWhiteSpace(symbol))
        .Select(symbol => symbol.Trim().ToUpperInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

      Core.Main.DataObjects.PTMagicData.MarketAnalyzer settings = systemConfiguration.AnalyzerSettings?.MarketAnalyzer;
      bool usePtEnabledPairs = settings?.UseProfitTrailerEnabledPairs == true;
      string mainMarket = GetPropertyValue(pairsLines, "MARKET").Trim().ToUpperInvariant();
      string enabledPairs = GetPropertyValue(pairsLines, "enabled_pairs");

      if (usePtEnabledPairs && !IsAllPairsValue(enabledPairs))
      {
        HashSet<string> enabledSymbols = SystemHelper.ConvertTokenStringToList(enabledPairs, ",")
          .Where(symbol => !String.IsNullOrWhiteSpace(symbol))
          .Select(symbol => SystemHelper.GetFullMarketName(mainMarket, symbol.Trim(), "BinanceFutures").ToUpperInvariant())
          .ToHashSet(StringComparer.OrdinalIgnoreCase);

        eligibleSymbols = eligibleSymbols.Where(enabledSymbols.Contains).ToList();
      }

      List<AnalyzerUniverseMarket> markets = new List<AnalyzerUniverseMarket>(eligibleSymbols.Count);
      foreach (string symbol in eligibleSymbols.OrderBy(symbol => symbol, StringComparer.OrdinalIgnoreCase))
      {
        BinanceFuturesMarketMetadata metadata = BinanceFuturesMarketUniverse.GetMarketMetadataSnapshot(symbol);
        if (metadata == null)
        {
          continue;
        }

        markets.Add(new AnalyzerUniverseMarket
        {
          Symbol = symbol,
          ContractType = metadata.ContractType,
          UnderlyingType = metadata.UnderlyingType,
          UnderlyingSubTypes = metadata.UnderlyingSubTypes
        });
      }

      string selectionReason = usePtEnabledPairs
        ? "Binance Futures exchange population filtered by the active underlying-type policy and ProfitTrailer enabled_pairs"
        : "Binance Futures exchange population filtered by the active underlying-type policy";

      AnalyzerUniverseSnapshot universe = new AnalyzerUniverseSnapshot(
        DateTimeOffset.UtcNow,
        mainMarket,
        markets,
        selectionReason);

      lock (SnapshotLock)
      {
        _currentUniverse = universe;
      }

      string typeCounts = String.Join(", ", universe.Markets
        .GroupBy(market => String.IsNullOrWhiteSpace(market.UnderlyingType) ? "(blank)" : market.UnderlyingType, StringComparer.OrdinalIgnoreCase)
        .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.Key + "=" + group.Count()));

      log.DoLogInfo(
        "BinanceFutures analytics - analyzer universe frozen: " + universe.MarketCount + "/" + cachedSymbols.Count +
        " cached exchange markets eligible" + (String.IsNullOrWhiteSpace(typeCounts) ? "." : " (" + typeCounts + ")."));
    }

    private static string GetPropertyValue(List<string> lines, string propertyName)
    {
      if (lines == null)
      {
        return "";
      }

      foreach (string line in lines)
      {
        string trimmedLine = line?.Trim() ?? "";
        if (trimmedLine.Length == 0 || trimmedLine.StartsWith('#'))
        {
          continue;
        }

        int separatorIndex = trimmedLine.IndexOf('=');
        if (separatorIndex < 0)
        {
          continue;
        }

        string key = trimmedLine.Substring(0, separatorIndex).Trim();
        if (key.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
        {
          return trimmedLine.Substring(separatorIndex + 1).Trim();
        }
      }

      return "";
    }

    private static bool IsAllPairsValue(string value)
    {
      return String.IsNullOrWhiteSpace(value)
        || value.Equals("all", StringComparison.OrdinalIgnoreCase)
        || value.Equals("true", StringComparison.OrdinalIgnoreCase)
        || value.Equals("false", StringComparison.OrdinalIgnoreCase);
    }
  }
}
