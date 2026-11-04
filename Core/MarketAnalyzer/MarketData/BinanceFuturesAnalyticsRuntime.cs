using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Helper;
using Core.Main;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Shared normalized analytics for Binance Futures, Binance Spot and Binance US.
  /// Omitted MarketTrend.Metrics defaults to normalized PriceTrend; other
  /// metrics are evaluated only when requested or needed by a trigger.
  /// </summary>
  public static class BinanceFuturesAnalyticsRuntime
  {
    // Historical class name retained for API compatibility; this is the
    // shared normalized runtime for Binance Futures, Spot and Binance US.
    public static bool IsSupportedExchange(string exchange)
    {
      return String.Equals(exchange, "BinanceFutures", StringComparison.OrdinalIgnoreCase)
        || String.Equals(exchange, "Binance", StringComparison.OrdinalIgnoreCase)
        || String.Equals(exchange, "BinanceUS", StringComparison.OrdinalIgnoreCase);
    }

    private static Task<List<MarketCandle>> GetFiveMinuteMarketCandlesAsync(
      string exchange, string symbol, int candles, LogHelper log, CancellationToken ct)
    {
      switch (exchange?.ToUpperInvariant())
      {
        case "BINANCE": return Binance.GetFiveMinuteMarketCandlesAsync(symbol, candles, log, ct);
        case "BINANCEUS": return BinanceUS.GetFiveMinuteMarketCandlesAsync(symbol, candles, log, ct);
        case "BINANCEFUTURES": return BinanceFutures.GetFiveMinuteMarketCandlesAsync(symbol, candles, log, ct);
        default: throw new ArgumentException("Unsupported normalized Exchange.", nameof(exchange));
      }
    }

    private const int RefreshConcurrency = 2;
    private static readonly TimeSpan BaseInterval = TimeSpan.FromMinutes(5);
    private static readonly object SnapshotLock = new object();
    private static readonly MarketDataService DataServiceInstance = new MarketDataService(new MarketDataStore(2));
    private static AnalyzerUniverseSnapshot _currentUniverse;
    private static MarketAnalysisSnapshot _currentAnalysis;
    private static IReadOnlyDictionary<string, MarketTrendAnalysisSnapshot> _currentTrendAnalyses =
      new ReadOnlyDictionary<string, MarketTrendAnalysisSnapshot>(new Dictionary<string, MarketTrendAnalysisSnapshot>(StringComparer.OrdinalIgnoreCase));
    private static IReadOnlyList<string> _cachedExchangeSymbols = Array.Empty<string>();
    private static string _activeExchange = "BinanceFutures";
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

    /// <summary>
    /// Compatibility property for the earlier fixed-horizon prototype. Production
    /// MarketTrend-driven analytics are exposed through CurrentTrendAnalyses.
    /// </summary>
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

    public static IReadOnlyDictionary<string, MarketTrendAnalysisSnapshot> CurrentTrendAnalyses
    {
      get
      {
        lock (SnapshotLock)
        {
          return _currentTrendAnalyses;
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

    public static async Task RefreshObservationDataAsync(
      IEnumerable<string> exchangeMarkets,
      string mainMarket,
      PTMagicConfiguration systemConfiguration,
      LogHelper log,
      CancellationToken cancellationToken = default,
      string exchangeName = "BinanceFutures")
    {
      if (!IsSupportedExchange(exchangeName))
      {
        throw new ArgumentException("Unsupported normalized Exchange '" + exchangeName + "'.", nameof(exchangeName));
      }
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
        if (!String.Equals(_activeExchange, exchangeName, StringComparison.OrdinalIgnoreCase))
        {
          DataServiceInstance.Store.Clear();
        }
        _activeExchange = exchangeName;
        _cachedExchangeSymbols = Array.AsReadOnly(exchangeSymbols);
        _currentUniverse = null;
        _currentAnalysis = null;
        _currentTrendAnalyses = EmptyTrendAnalyses();
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

      int storeDataMaxHours = systemConfiguration.AnalyzerSettings?.MarketAnalyzer?.StoreDataMaxHours ?? 0;
      if (storeDataMaxHours <= 0)
      {
        DataServiceInstance.Store.Clear();
        lock (SnapshotLock)
        {
          _lastRefreshStatuses = Array.Empty<MarketDataRefreshStatus>();
        }

        log.DoLogWarn("BinanceFutures analytics - StoreDataMaxHours must be greater than zero to calculate normalized MarketTrend metrics. Legacy PriceTrend analysis will continue.");
        return;
      }

      int targetHistoryCandles = GetTargetHistoryCandles(storeDataMaxHours);
      DataServiceInstance.Store.SetMaxCandlesPerSeries(targetHistoryCandles);

      IReadOnlyList<MarketDataRefreshStatus> statuses = await DataServiceInstance.RefreshAsync(
        exchangeSymbols,
        BaseInterval,
        targetHistoryCandles,
        (symbol, count, logger, ct) => GetFiveMinuteMarketCandlesAsync(exchangeName, symbol, count, logger, ct),
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
          " markets. StoreDataMaxHours=" + storeDataMaxHours + ", per-series capacity=" + targetHistoryCandles +
          ", store series=" + DataServiceInstance.Store.SeriesCount + ".");
      }
      else
      {
        log.DoLogInfo(
          "BinanceFutures analytics - normalized 5m data refreshed for " + refreshed + "/" + exchangeSymbols.Length +
          " markets (" + receivedCandles + " candles received, " + skipped + " current/skipped, " + failed +
          " failed). StoreDataMaxHours=" + storeDataMaxHours + ", per-series capacity=" + targetHistoryCandles +
          ", store series=" + DataServiceInstance.Store.SeriesCount + ".");
      }
    }

    /// <summary>
    /// Freezes the broad normalized analytics eligibility boundary after PT pair/type
    /// filters are known. It no longer calculates a fixed list of time horizons.
    /// </summary>
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

      string exchangeName;
      lock (SnapshotLock)
      {
        exchangeName = _activeExchange;
      }
      bool futures = exchangeName.Equals("BinanceFutures", StringComparison.OrdinalIgnoreCase);

      List<string> eligibleSymbols = (futures
          ? BinanceFuturesMarketUniverse.FilterMarketNames(cachedSymbols, systemConfiguration)
          : cachedSymbols)
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
        if (futures)
        {
          BinanceFuturesMarketMetadata metadata = BinanceFuturesMarketUniverse.GetMarketMetadataSnapshot(symbol);
          if (metadata == null) continue;
          markets.Add(new AnalyzerUniverseMarket
          {
            Symbol = symbol,
            ContractType = metadata.ContractType,
            UnderlyingType = metadata.UnderlyingType,
            UnderlyingSubTypes = metadata.UnderlyingSubTypes
          });
        }
        else
        {
          // Spot markets have no Futures contract/underlying type metadata.
          markets.Add(new AnalyzerUniverseMarket { Symbol = symbol });
        }
      }

      string selectionReason = futures
        ? (usePtEnabledPairs
          ? "Binance Futures active underlying-type policy and enabled/disabled PT pairs"
          : "Binance Futures active underlying-type policy")
        : (usePtEnabledPairs
          ? exchangeName + " exchange market universe filtered by enabled/disabled PT pairs"
          : exchangeName + " exchange market universe");

      AnalyzerUniverseSnapshot universe = new AnalyzerUniverseSnapshot(DateTimeOffset.UtcNow, mainMarket, markets, selectionReason);
      lock (SnapshotLock)
      {
        _currentUniverse = universe;
        _currentAnalysis = null;
        _currentTrendAnalyses = EmptyTrendAnalyses();
      }

      string typeCounts = String.Join(", ", universe.Markets
        .GroupBy(market => String.IsNullOrWhiteSpace(market.UnderlyingType) ? "(blank)" : market.UnderlyingType, StringComparer.OrdinalIgnoreCase)
        .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.Key + "=" + group.Count()));

      log.DoLogInfo(
        "BinanceFutures analytics - analyzer universe frozen: " + universe.MarketCount + "/" + cachedSymbols.Count +
        " cached exchange markets eligible" + (String.IsNullOrWhiteSpace(typeCounts) ? "." : " (" + typeCounts + ")."));
    }

    /// <summary>
    /// Calculates explicitly configured normalized metrics for Exchange MarketTrends after
    /// the legacy PTMagic trend pipeline has resolved each trend's market population.
    /// Empty Metrics selects normalized PriceTrend by default; omitted trigger
    /// Metric adds it implicitly on metrics-only trends when required.
    /// </summary>
    public static void EvaluateConfiguredMarketTrends(
      IReadOnlyDictionary<string, List<MarketTrendChange>> globalMarketTrendChanges,
      IReadOnlyDictionary<string, List<MarketTrendChange>> singleMarketTrendChanges,
      PTMagicConfiguration systemConfiguration,
      LogHelper log,
      IReadOnlyDictionary<string, Market> recentMarkets = null)
    {
      if (systemConfiguration == null)
      {
        throw new ArgumentNullException(nameof(systemConfiguration));
      }
      if (log == null)
      {
        throw new ArgumentNullException(nameof(log));
      }

      AnalyzerUniverseSnapshot universe;
      lock (SnapshotLock)
      {
        universe = _currentUniverse;
      }

      if (universe == null)
      {
        log.DoLogWarn("BinanceFutures analytics - configured MarketTrend metrics were not calculated because the analyzer universe is unavailable.");
        return;
      }

      Dictionary<string, MarketTrendAnalysisSnapshot> analyses =
        new Dictionary<string, MarketTrendAnalysisSnapshot>(StringComparer.OrdinalIgnoreCase);
      DateTimeOffset calculatedAtUtc = DateTimeOffset.UtcNow;

      IEnumerable<MarketTrend> configuredTrends = systemConfiguration.AnalyzerSettings?.MarketAnalyzer?.MarketTrends
        ?? Enumerable.Empty<MarketTrend>();

      foreach (MarketTrend marketTrend in configuredTrends)
      {
        if (marketTrend == null
          || !marketTrend.Platform.Equals("Exchange", StringComparison.OrdinalIgnoreCase)
          || String.IsNullOrWhiteSpace(marketTrend.Name))
        {
          continue;
        }

        IReadOnlyCollection<string> enabledMetrics = MarketTrendMetricSelection.Select(
          marketTrend, systemConfiguration.AnalyzerSettings);
        List<string> invalidMetrics = enabledMetrics
          .Where(metric => !MarketTrendMetrics.IsKnown(metric))
          .ToList();
        if (invalidMetrics.Count > 0)
        {
          log.DoLogError(
            "BinanceFutures analytics - MarketTrend '" + marketTrend.Name + "' contains unknown Metrics: " +
            String.Join(", ", invalidMetrics) + ". Unknown metrics will be ignored.");
        }

        bool hasKnownMetric = enabledMetrics.Any(MarketTrendMetrics.IsKnown);
        if (!hasKnownMetric)
        {
          continue;
        }

        // Normalized metrics use the selected market population, with current
        // exchange snapshot metadata retained for sorting and filtering.
        MarketTrendSelectedPopulation population = MarketTrendPopulationSelector.Select(
          marketTrend, recentMarkets ?? new Dictionary<string, Market>(),
          universe, universe.MainMarket, DataServiceInstance.Store, BaseInterval, calculatedAtUtc);
        string[] globalSymbols = population.GlobalSymbols.ToArray();
        string[] assetSymbols = population.AssetSymbols.ToArray();

        MarketTrendAnalysisSnapshot analysis = MarketTrendAnalyticsEngine.Calculate(
          marketTrend,
          globalSymbols,
          assetSymbols,
          DataServiceInstance.Store,
          BaseInterval,
          calculatedAtUtc,
          enabledMetrics,
          "BTC" + universe.MainMarket);
        analyses[marketTrend.Name] = analysis;

        LogTrendAnalysis(marketTrend, analysis, log);
      }

      lock (SnapshotLock)
      {
        _currentTrendAnalyses = new ReadOnlyDictionary<string, MarketTrendAnalysisSnapshot>(analyses);
      }
    }

    public static bool TryGetMetric(
      string marketTrendName,
      string metric,
      string marketSymbol,
      out FeatureValueSnapshot feature)
    {
      feature = null;
      string normalizedMetric = MarketTrendMetrics.Normalize(metric);
      MarketTrendAnalysisSnapshot analysis;

      lock (SnapshotLock)
      {
        if (!_currentTrendAnalyses.TryGetValue(marketTrendName ?? "", out analysis))
        {
          return false;
        }
      }

      if (!MarketTrendMetrics.TryGetScope(normalizedMetric, out MarketTrendMetricScope scope))
      {
        return false;
      }

      if ((scope == MarketTrendMetricScope.Asset || scope == MarketTrendMetricScope.Both)
        && !String.IsNullOrWhiteSpace(marketSymbol))
      {
        return analysis.AssetMetrics.TryGetValue(marketSymbol, out IReadOnlyDictionary<string, FeatureValueSnapshot> assetMetrics)
          && assetMetrics.TryGetValue(normalizedMetric, out feature);
      }

      if (scope == MarketTrendMetricScope.Global || scope == MarketTrendMetricScope.Both)
      {
        return analysis.MarketMetrics.TryGetValue(normalizedMetric, out feature);
      }

      return false;
    }

    public static bool ValidateMetricReference(
      MarketTrend marketTrend,
      string metric,
      bool globalSettingContext,
      out string error)
    {
      error = "";
      bool metricOmitted = String.IsNullOrWhiteSpace(metric);
      string normalizedMetric = MarketTrendMetrics.Normalize(metric);

      if (!MarketTrendMetrics.IsKnown(normalizedMetric))
      {
        error = "Unknown metric '" + normalizedMetric + "'.";
        return false;
      }

      if (marketTrend == null)
      {
        error = "The referenced MarketTrend does not exist.";
        return false;
      }

      if (!String.Equals(marketTrend.Platform, "Exchange", StringComparison.OrdinalIgnoreCase))
      {
        error = "Normalized metrics require an Exchange MarketTrend.";
        return false;
      }

      if (!metricOmitted && !MarketTrendMetrics.IsEnabled(marketTrend, normalizedMetric))
      {
        error = "MarketTrend '" + marketTrend.Name + "' does not enable metric '" + normalizedMetric + "'.";
        return false;
      }

      if (globalSettingContext
        && MarketTrendMetrics.TryGetScope(normalizedMetric, out MarketTrendMetricScope scope)
        && scope == MarketTrendMetricScope.Asset)
      {
        error = "Metric '" + normalizedMetric + "' requires a market-specific context and cannot be used by a GlobalSetting trigger.";
        return false;
      }

      return true;
    }

    private static int GetTargetHistoryCandles(int storeDataMaxHours)
    {
      double intervals = TimeSpan.FromHours(storeDataMaxHours).TotalMilliseconds / BaseInterval.TotalMilliseconds;
      // One boundary candle is required to measure a return across the full configured
      // duration, and one additional slot permits Binance's currently forming candle.
      return checked((int)Math.Ceiling(intervals) + 2);
    }

    private static void LogTrendAnalysis(MarketTrend marketTrend, MarketTrendAnalysisSnapshot analysis, LogHelper log)
    {
      List<string> marketValues = new List<string>();
      foreach (KeyValuePair<string, FeatureValueSnapshot> pair in analysis.MarketMetrics.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
      {
        marketValues.Add(FormatMetric(pair.Key, pair.Value));
      }

      int validAssetValues = analysis.AssetMetrics.Values.Sum(metrics =>
        metrics.Values.Count(feature => feature.IsValid && !feature.IsStale));
      int totalAssetValues = analysis.AssetMetrics.Values.Sum(metrics => metrics.Count);
      string globalCoverage = analysis.GlobalReturnsEvaluated
        ? "global returns=" + analysis.ValidGlobalReturnCount + "/" + analysis.GlobalSymbols.Count +
          " (" + (analysis.GlobalSymbols.Count > 0
            ? analysis.ValidGlobalReturnCount * 100.0 / analysis.GlobalSymbols.Count
            : 0.0).ToString("0.##", CultureInfo.InvariantCulture) + "%)"
        : "global returns=not requested";
      string assetCoverage = analysis.AssetReturnsEvaluated
        ? "asset returns=" + analysis.ValidAssetReturnCount + "/" + analysis.AssetSymbols.Count
        : "asset returns=not requested";

      log.DoLogInfo(
        "BinanceFutures analytics - MarketTrend '" + marketTrend.Name + "' (" + marketTrend.TrendMinutes +
        "m) metrics: " + (marketValues.Count == 0 ? "no global metrics" : String.Join(", ", marketValues)) +
        "; asset metrics valid=" + validAssetValues + "/" + totalAssetValues +
        "; " + globalCoverage + "; " + assetCoverage +
        "; expected closed endpoint=" + analysis.ExpectedClosedCandleTimeUtc.ToString("O", CultureInfo.InvariantCulture) +
        "; global invalid reasons=" + FormatIssueCounts(analysis.GlobalInvalidReturnReasons) +
        "; asset invalid reasons=" + FormatIssueCounts(analysis.AssetInvalidReturnReasons) +
        "; global universe=" + analysis.GlobalSymbols.Count + ", asset universe=" + analysis.AssetSymbols.Count + ".");
    }

    private static string FormatIssueCounts(IReadOnlyDictionary<string, int> reasons)
    {
      return reasons == null || reasons.Count == 0
        ? "none"
        : String.Join(", ", reasons.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
          .Select(pair => pair.Key + "=" + pair.Value));
    }

    private static string FormatMetric(string name, FeatureValueSnapshot feature)
    {
      string coverage = feature?.EligibleSampleCount is int eligible
        ? " [" + feature.SampleCount + "/" + eligible + " (" +
          (feature.CoveragePercent ?? 0.0).ToString("0.##", CultureInfo.InvariantCulture) + "%)]"
        : "";
      if (feature == null || !feature.IsValid || feature.IsStale || !feature.Value.HasValue)
      {
        return name + "=unavailable" + coverage +
          (String.IsNullOrWhiteSpace(feature?.InvalidReason) ? "" : " (" + feature.InvalidReason + ")");
      }

      return name + "=" + feature.Value.Value.ToString("0.########", CultureInfo.InvariantCulture) + coverage;
    }

    private static IReadOnlyDictionary<string, MarketTrendAnalysisSnapshot> EmptyTrendAnalyses()
    {
      return new ReadOnlyDictionary<string, MarketTrendAnalysisSnapshot>(
        new Dictionary<string, MarketTrendAnalysisSnapshot>(StringComparer.OrdinalIgnoreCase));
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

      return normalized + mainMarket;
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
