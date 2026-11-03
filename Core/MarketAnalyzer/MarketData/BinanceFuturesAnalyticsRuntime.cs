using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Helper;
using Core.Main;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Observation-only runtime for the Phase 1 Binance Futures analytics pipeline.
  /// It maintains normalized 5-minute OHLCV history independently from the legacy
  /// MarketData_*.json trend pipeline and exposes immutable analytics snapshots.
  /// </summary>
  public static class BinanceFuturesAnalyticsRuntime
  {
    // 900 x 5m = 75 hours. This covers the Phase 1B return horizons through 72h
    // with 3 hours of headroom. Binance serves at most 500 klines per request, so a
    // cold bootstrap requires two paged kline requests per symbol.
    private const int BootstrapHistoryCandles = 900;
    private const int RefreshConcurrency = 2;
    private static readonly TimeSpan BaseInterval = TimeSpan.FromMinutes(5);
    private static readonly object SnapshotLock = new object();
    private static readonly MarketDataService DataServiceInstance = new MarketDataService(new MarketDataStore(1000));
    private static AnalyzerUniverseSnapshot _currentUniverse;
    private static MarketAnalysisSnapshot _currentAnalysis;
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

    public static MarketAnalysisSnapshot CurrentAnalysis
    {
      get
      {
        lock (SnapshotLock)
        {
          return _currentAnalysis;
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
        _currentAnalysis = null;
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
      string disabledPairs = GetPropertyValue(pairsLines, "disabled_pairs");

      if (usePtEnabledPairs && !IsAllPairsValue(enabledPairs))
      {
        HashSet<string> enabledSymbols = SystemHelper.ConvertTokenStringToList(enabledPairs, ",")
          .Where(symbol => !String.IsNullOrWhiteSpace(symbol))
          .Select(symbol => ToFullMarketSymbol(mainMarket, symbol))
          .ToHashSet(StringComparer.OrdinalIgnoreCase);

        eligibleSymbols = eligibleSymbols.Where(enabledSymbols.Contains).ToList();
      }

      if (usePtEnabledPairs && !String.IsNullOrWhiteSpace(disabledPairs) && !disabledPairs.Equals("false", StringComparison.OrdinalIgnoreCase))
      {
        HashSet<string> disabledSymbols = SystemHelper.ConvertTokenStringToList(disabledPairs, ",")
          .Where(symbol => !String.IsNullOrWhiteSpace(symbol))
          .Select(symbol => ToFullMarketSymbol(mainMarket, symbol))
          .ToHashSet(StringComparer.OrdinalIgnoreCase);

        eligibleSymbols = eligibleSymbols.Where(symbol => !disabledSymbols.Contains(symbol)).ToList();
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
        ? "Binance Futures exchange population filtered by the active underlying-type policy and ProfitTrailer enabled_pairs/disabled_pairs"
        : "Binance Futures exchange population filtered by the active underlying-type policy";

      DateTimeOffset calculatedAtUtc = DateTimeOffset.UtcNow;
      AnalyzerUniverseSnapshot universe = new AnalyzerUniverseSnapshot(calculatedAtUtc, mainMarket, markets, selectionReason);
      MarketAnalysisSnapshot analysis = BuildPriceReturnAnalysis(universe, calculatedAtUtc);

      lock (SnapshotLock)
      {
        _currentUniverse = universe;
        _currentAnalysis = analysis;
      }

      string typeCounts = String.Join(", ", universe.Markets
        .GroupBy(market => String.IsNullOrWhiteSpace(market.UnderlyingType) ? "(blank)" : market.UnderlyingType, StringComparer.OrdinalIgnoreCase)
        .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.Key + "=" + group.Count()));

      log.DoLogInfo(
        "BinanceFutures analytics - analyzer universe frozen: " + universe.MarketCount + "/" + cachedSymbols.Count +
        " cached exchange markets eligible" + (String.IsNullOrWhiteSpace(typeCounts) ? "." : " (" + typeCounts + ")."));

      int validAssets = analysis.Assets.Values.Count(asset => asset.IsValid);
      int valid72hAssets = analysis.Assets.Values.Count(asset => asset.Features.TryGetValue("Return72h", out FeatureValueSnapshot feature) && feature.IsValid);
      log.DoLogInfo(
        "BinanceFutures analytics - Phase 1B returns calculated for " + validAssets + "/" + universe.MarketCount +
        " assets; " + valid72hAssets + " have valid 72h returns.");

      int validAggregateHorizons = PriceReturnCalculator.Horizons.Count(horizon =>
        analysis.MarketFeatures.TryGetValue("MeanReturn" + horizon.DisplayName, out FeatureValueSnapshot mean) && mean.IsValid
        && analysis.MarketFeatures.TryGetValue("MedianReturn" + horizon.DisplayName, out FeatureValueSnapshot median) && median.IsValid
        && analysis.MarketFeatures.TryGetValue("PositiveBreadth" + horizon.DisplayName, out FeatureValueSnapshot breadth) && breadth.IsValid
        && analysis.MarketFeatures.TryGetValue("ReturnStdDev" + horizon.DisplayName, out FeatureValueSnapshot dispersion) && dispersion.IsValid);
      log.DoLogInfo(
        "BinanceFutures analytics - Phase 1B market mean/median/breadth/dispersion calculated for " +
        validAggregateHorizons + "/" + PriceReturnCalculator.Horizons.Count + " horizons.");

      IReadOnlyList<MarketReturnValidationSnapshot> validations = MarketReturnValidator.Validate(
        universe,
        DataServiceInstance.Store,
        BaseInterval,
        analysis,
        calculatedAtUtc);

      foreach (MarketReturnValidationSnapshot validation in validations)
      {
        log.DoLogInfo("BinanceFutures analytics - " + MarketReturnValidator.FormatLogLine(validation));
      }

      int validValidationHorizons = validations.Count(validation => validation.IsValid);
      if (validValidationHorizons == validations.Count)
      {
        log.DoLogInfo(
          "BinanceFutures analytics - Phase 1 independent validation passed for " + validValidationHorizons +
          "/" + validations.Count + " horizons.");
      }
      else
      {
        log.DoLogWarn(
          "BinanceFutures analytics - Phase 1 independent validation passed for " + validValidationHorizons +
          "/" + validations.Count + " horizons; inspect mismatch counts above.");
      }
    }

    private static MarketAnalysisSnapshot BuildPriceReturnAnalysis(AnalyzerUniverseSnapshot universe, DateTimeOffset calculatedAtUtc)
    {
      Dictionary<string, AssetFeatureSnapshot> assets = new Dictionary<string, AssetFeatureSnapshot>(StringComparer.OrdinalIgnoreCase);

      foreach (AnalyzerUniverseMarket market in universe.Markets)
      {
        MarketDataSeriesSnapshot series = DataServiceInstance.Store.GetSnapshot(market.Symbol, BaseInterval, calculatedAtUtc);
        assets[market.Symbol] = PriceReturnCalculator.Calculate(series);
      }

      IReadOnlyDictionary<string, FeatureValueSnapshot> marketFeatures = MarketReturnAggregator.Calculate(assets, calculatedAtUtc);

      return new MarketAnalysisSnapshot
      {
        TimestampUtc = calculatedAtUtc,
        Universe = universe,
        MarketFeatures = marketFeatures,
        Assets = new ReadOnlyDictionary<string, AssetFeatureSnapshot>(assets)
      };
    }

    private static string ToFullMarketSymbol(string mainMarket, string symbol)
    {
      string normalized = symbol?.Trim().ToUpperInvariant() ?? "";
      if (normalized.Length == 0 || mainMarket.Length == 0)
      {
        return normalized;
      }

      if (normalized.EndsWith(mainMarket, StringComparison.OrdinalIgnoreCase))
      {
        return normalized;
      }

      return SystemHelper.GetFullMarketName(mainMarket, normalized, "BinanceFutures").ToUpperInvariant();
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
