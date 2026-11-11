using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
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
      string exchange, string symbol, int candles, LogHelper log, CancellationToken ct, DateTimeOffset asOfUtc)
    {
      switch (exchange?.ToUpperInvariant())
      {
        case "BINANCE": return Binance.GetFiveMinuteMarketCandlesAsync(symbol, candles, log, ct, asOfUtc);
        case "BINANCEUS": return BinanceUS.GetFiveMinuteMarketCandlesAsync(symbol, candles, log, ct, asOfUtc);
        case "BINANCEFUTURES": return BinanceFutures.GetFiveMinuteMarketCandlesAsync(symbol, candles, log, ct, asOfUtc);
        default: throw new ArgumentException("Unsupported normalized Exchange.", nameof(exchange));
      }
    }

    private const int RefreshConcurrency = 2;
    private static readonly TimeSpan BaseInterval = TimeSpan.FromMinutes(5);
    private static readonly object SnapshotLock = new object();
    private static readonly MarketDataService DataServiceInstance = new MarketDataService(
      new MarketDataStore(2),
      new MarketDataWarmStartCache(Path.Combine(Environment.CurrentDirectory, "_data", "NormalizedOhlcvCache")));
    private static readonly FuturesOpenInterestDataService OpenInterestDataServiceInstance =
      new FuturesOpenInterestDataService(Path.Combine(Environment.CurrentDirectory, "_data", "DerivativesOpenInterestCache"));
    private static readonly FuturesFundingDataService FundingDataServiceInstance =
      new FuturesFundingDataService(Path.Combine(Environment.CurrentDirectory, "_data", "DerivativesFundingCache"));
    private static readonly FuturesLongShortRatioDataService LongShortRatioDataServiceInstance =
      new FuturesLongShortRatioDataService(Path.Combine(Environment.CurrentDirectory, "_data", "DerivativesLongShortRatioCache"));
    private static AnalyzerUniverseSnapshot _currentUniverse;
    private static MarketAnalysisSnapshot _currentAnalysis;
    private static IReadOnlyDictionary<string, MarketTrendAnalysisSnapshot> _currentTrendAnalyses =
      new ReadOnlyDictionary<string, MarketTrendAnalysisSnapshot>(new Dictionary<string, MarketTrendAnalysisSnapshot>(StringComparer.OrdinalIgnoreCase));
    private static IReadOnlyList<string> _cachedExchangeSymbols = Array.Empty<string>();
    private static string _activeExchange = "BinanceFutures";
    private static IReadOnlyList<MarketDataRefreshStatus> _lastRefreshStatuses = Array.Empty<MarketDataRefreshStatus>();
    private static DateTimeOffset? _currentRaidAsOfUtc;

    public static MarketDataService DataService => DataServiceInstance;
    public static FuturesOpenInterestDataService OpenInterestDataService => OpenInterestDataServiceInstance;
    public static FuturesFundingDataService FundingDataService => FundingDataServiceInstance;
    public static FuturesLongShortRatioDataService LongShortRatioDataService => LongShortRatioDataServiceInstance;

    public static DateTimeOffset? CurrentRaidAsOfUtc
    {
      get { lock (SnapshotLock) return _currentRaidAsOfUtc; }
    }

    public static bool IsDecisionFresh(PTMagicConfiguration configuration, out string reason,
      DateTimeOffset? decisionTimeUtc = null)
    {
      return AnalysisDecisionFreshness.IsFresh(CurrentRaidAsOfUtc,
        decisionTimeUtc ?? DateTimeOffset.UtcNow,
        configuration?.GeneralSettings?.Application?.MaxAnalysisAgeSeconds ?? 300, out reason);
    }

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
        _currentRaidAsOfUtc = null;
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

        log.DoLogWarn("BinanceFutures analytics - StoreDataMaxHours must be greater than zero; normalized metrics including PriceTrend are unavailable.");
        return;
      }

      int targetHistoryCandles = GetTargetHistoryCandles(storeDataMaxHours);
      DataServiceInstance.Store.SetMaxCandlesPerSeries(targetHistoryCandles);

      // For non-USD quote currencies, a fiat PriceTrend needs aligned
      // quote/USDT closes. Fetch the reference on the same exchange without
      // adding it to the analyzer's eligible asset/global populations.
      bool hasFiatTrends = (systemConfiguration.AnalyzerSettings?.MarketAnalyzer?.MarketTrends
        ?? new List<MarketTrend>()).Any(trend =>
          trend != null && String.Equals(trend.Platform, "Exchange", StringComparison.OrdinalIgnoreCase)
          && MarketTrendFiatConversion.IsFiat(trend.TrendCurrency));
      string fiatSymbol = hasFiatTrends
        ? MarketTrendFiatConversion.ConversionSymbol(mainMarket) : null;
      string[] refreshSymbols = !String.IsNullOrWhiteSpace(fiatSymbol)
        ? exchangeSymbols.Concat(new[] { fiatSymbol })
          .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        : exchangeSymbols;

      // Pin the last fully closed candle as of the beginning of this refresh.
      // A long cold fetch may span another close; the evaluation must not
      // demand a candle unavailable to symbols fetched before that boundary.
      DateTimeOffset refreshAsOfUtc = DateTimeOffset.UtcNow;
      IReadOnlyList<MarketDataRefreshStatus> statuses = await DataServiceInstance.RefreshAtAsync(
        refreshSymbols,
        BaseInterval,
        targetHistoryCandles,
        (symbol, count, logger, ct, asOfUtc) => GetFiveMinuteMarketCandlesAsync(exchangeName, symbol, count, logger, ct, asOfUtc),
        log,
        RefreshConcurrency,
        cancellationToken,
        refreshAsOfUtc,
        exchangeName).ConfigureAwait(false);

      MarketDataRefreshStatus[] statusSnapshot = statuses.ToArray();
      lock (SnapshotLock)
      {
        _lastRefreshStatuses = Array.AsReadOnly(statusSnapshot);
        _currentRaidAsOfUtc = refreshAsOfUtc;
      }

      int skipped = statusSnapshot.Count(status => status != null && status.Skipped);
      int refreshed = statusSnapshot.Count(status => status != null && status.Succeeded && !status.Skipped);
      int failed = statusSnapshot.Count(status => status == null || !status.Succeeded);
      int receivedCandles = statusSnapshot.Where(status => status != null).Sum(status => status.ReceivedCandleCount);

      if (refreshed == 0 && failed == 0)
      {
        log.DoLogInfo(
          "BinanceFutures analytics - normalized 5m data current; no kline requests required for " + skipped +
          " markets" + (refreshSymbols.Length > exchangeSymbols.Length ? " (including fiat conversion)" : "") +
          ". StoreDataMaxHours=" + storeDataMaxHours + ", per-series capacity=" + targetHistoryCandles +
          ", store series=" + DataServiceInstance.Store.SeriesCount + ".");
      }
      else
      {
        log.DoLogInfo(
          "BinanceFutures analytics - normalized 5m data refreshed for " + refreshed + "/" + refreshSymbols.Length +
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
    /// Optional Phase 2 acquisition after the frozen universe exists and
    /// before synchronous metric evaluation. Only explicit OI metrics cause
    /// requests. The runtime uses temporary internal service ceilings pending
    /// adaptive derivative acquisition.
    /// </summary>
    public static async Task RefreshOpenInterestAsync(
      PTMagicConfiguration configuration, LogHelper log,
      IReadOnlyDictionary<string, Market> recentMarkets = null,
      CancellationToken cancellationToken = default)
    {
      if (configuration == null) throw new ArgumentNullException(nameof(configuration));
      if (log == null) throw new ArgumentNullException(nameof(log));
      var trends = (configuration.AnalyzerSettings?.MarketAnalyzer?.MarketTrends
        ?? new List<MarketTrend>())
        .Where(t => t != null &&
          String.Equals(t.Platform, "Exchange", StringComparison.OrdinalIgnoreCase) &&
          FuturesOpenInterestFeatureEngine.HasMetrics(MarketTrendMetricSelection.Select(
            t, configuration.AnalyzerSettings))).ToArray();
      if (trends.Length == 0) return;
      string exchange = configuration.GeneralSettings?.Application?.Exchange ?? "";
      if (!exchange.Equals("BinanceFutures", StringComparison.OrdinalIgnoreCase))
      {
        log.DoLogWarn("Phase 2 OI metrics requested on " + exchange +
          "; these per-asset derivatives metrics are only available for Binance USD-M Futures.");
        return;
      }

      AnalyzerUniverseSnapshot universe;
      DateTimeOffset? pin;
      lock (SnapshotLock) { universe = _currentUniverse; pin = _currentRaidAsOfUtc; }
      if (universe == null || !pin.HasValue)
      {
        log.DoLogWarn("Phase 2 open-interest acquisition deferred: frozen universe or pinned raid time is unavailable.");
        return;
      }
      // Transitional internal safeguard; D-051 adaptive acquisition will replace this ceiling.
      int budget = FuturesOpenInterestDataService.DefaultRequestsPerRaid;

      var usable = trends.Where(t => t.TrendMinutes >= 5 && t.TrendMinutes % 5 == 0).ToArray();
      int maximumHorizon = FuturesOpenInterestDataService.RequiredHorizonMinutes(usable);
      if (maximumHorizon == 0)
      {
        log.DoLogWarn("Phase 2 open-interest requests skipped: no valid OI-enabled MarketTrend duration.");
        return;
      }
      var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var trend in usable)
      {
        MarketTrendSelectedPopulation selected = MarketTrendPopulationSelector.Select(
          trend, recentMarkets ?? new Dictionary<string, Market>(),
          universe, universe.MainMarket, DataServiceInstance.Store, BaseInterval, pin.Value);
        assets.UnionWith(selected.AssetSymbols);
      }
      OpenInterestRefreshReport report = await OpenInterestDataServiceInstance.RefreshAsync(
        assets, maximumHorizon, pin.Value, budget,
        (symbol, end, limit, ct) => BinanceFuturesOpenInterestClient.GetHistoryPageAsync(
          symbol, FuturesOpenInterest.SourcePeriod, end, limit, log, ct),
        log, cancellationToken).ConfigureAwait(false);
      log.DoLogInfo("Phase 2 OI: " + report.Requests + "/" + budget +
        " page requests; " + report.SuccessfulPages + " succeeded, " + report.FailedPages +
        " failed; " + report.AvailableSymbols + "/" + report.EligibleSymbols +
        " selected assets have exact " + maximumHorizon +
        "m endpoints (derived from enabled MarketTrends); " + report.RestoredSymbols + " restored from disk. " +
        "Missing/unpolled data remain unavailable.");
    }


    /// <summary>
    /// Opt-in and pinned Binance USD-M regular funding settlements. History
    /// requirement is the longest user-enabled funding TrendMinutes, not 8h
    /// or any independent cache retention setting.
    /// </summary>
    public static async Task RefreshFundingAsync(PTMagicConfiguration configuration, LogHelper log,
      IReadOnlyDictionary<string, Market> recentMarkets = null, CancellationToken cancellationToken = default)
    {
      if (configuration == null) throw new ArgumentNullException(nameof(configuration));
      if (log == null) throw new ArgumentNullException(nameof(log));
      var trends = (configuration.AnalyzerSettings?.MarketAnalyzer?.MarketTrends
        ?? new List<MarketTrend>()).Where(t => t != null &&
          String.Equals(t.Platform, "Exchange", StringComparison.OrdinalIgnoreCase) &&
          FuturesFundingFeatureEngine.HasMetrics(MarketTrendMetricSelection.Select(
            t, configuration.AnalyzerSettings))).ToArray();
      if (trends.Length == 0) return;
      if (!String.Equals(configuration.GeneralSettings?.Application?.Exchange, "BinanceFutures",
        StringComparison.OrdinalIgnoreCase))
      {
        log.DoLogWarn("Funding metrics are only supported on Binance USD-M Futures.");
        return;
      }

      AnalyzerUniverseSnapshot universe;
      DateTimeOffset? pin;
      lock (SnapshotLock) { universe = _currentUniverse; pin = _currentRaidAsOfUtc; }
      if (universe == null || !pin.HasValue)
      {
        log.DoLogWarn("Funding history unavailable: analyzer universe/raid pin missing.");
        return;
      }
      // Transitional internal safeguard; no user-configured request budget.
      int budget = FuturesFundingDataService.DefaultRequestsPerRaid;
      var enabled = trends.Where(t => t.TrendMinutes >= 5 && t.TrendMinutes % 5 == 0).ToArray();
      int horizon = FuturesFundingDataService.RequiredHorizonMinutes(enabled);
      if (horizon <= 0) return;
      var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var trend in enabled)
      {
        var population = MarketTrendPopulationSelector.Select(trend,
          recentMarkets ?? new Dictionary<string, Market>(),
          universe, universe.MainMarket, DataServiceInstance.Store, BaseInterval, pin.Value);
        assets.UnionWith(population.AssetSymbols);
      }

      FundingRefreshReport report = await FundingDataServiceInstance.RefreshAsync(
        assets, horizon, pin.Value, budget,
        (symbol, from, until, limit, ct) => BinanceFuturesFundingClient.GetHistoryPageAsync(
          symbol, from, until, limit, log, ct),
        log, cancellationToken).ConfigureAwait(false);
      log.DoLogInfo("Phase 2 funding: " + report.Requests + "/" + budget +
        " page requests; " + report.Failures + " failed; " +
        report.CompleteSymbols + "/" + report.SelectedSymbols + " selected markets have verified " +
        horizon + "m funding history. Missing/pending history is UNAVAILABLE.");
    }

    /// <summary>
    /// Optional public USD-M global-account long/short ratio acquisition.
    /// Uses frozen asset populations and the pinned raid boundary.
    /// </summary>
    public static async Task RefreshLongShortRatiosAsync(PTMagicConfiguration configuration,
      LogHelper log, IReadOnlyDictionary<string, Market> recentMarkets = null,
      CancellationToken cancellationToken = default)
    {
      if (configuration == null) throw new ArgumentNullException(nameof(configuration));
      if (log == null) throw new ArgumentNullException(nameof(log));
      var trends = (configuration.AnalyzerSettings?.MarketAnalyzer?.MarketTrends
        ?? new List<MarketTrend>())
        .Where(t => t != null && String.Equals(t.Platform, "Exchange", StringComparison.OrdinalIgnoreCase))
        .Select(t => new { Trend = t, Metrics = MarketTrendMetricSelection.Select(t, configuration.AnalyzerSettings)
          .Where(FuturesLongShortRatioFeatureEngine.IsMetric).ToArray() })
        .Where(x => x.Metrics.Length > 0).ToArray();
      if (trends.Length == 0) return;
      if (!String.Equals(configuration.GeneralSettings?.Application?.Exchange, "BinanceFutures",
        StringComparison.OrdinalIgnoreCase))
      {
        log.DoLogWarn("Long/short ratio metrics are supported only on Binance USD-M Futures.");
        return;
      }

      AnalyzerUniverseSnapshot universe;
      DateTimeOffset? pin;
      lock (SnapshotLock) { universe = _currentUniverse; pin = _currentRaidAsOfUtc; }
      if (universe == null || !pin.HasValue)
      {
        log.DoLogWarn("Long/short ratio acquisition deferred: frozen universe or pinned raid time is unavailable.");
        return;
      }

      // Transitional internal safeguard; no user-configured request budget.
      int budget = FuturesLongShortRatioDataService.DefaultRequestsPerRaid;

      var validTrends = trends.Where(x => x.Trend.TrendMinutes >= 5 &&
        x.Trend.TrendMinutes <= FuturesLongShortRatio.MaximumHistoryMinutes &&
        x.Trend.TrendMinutes % 5 == 0).ToArray();
      if (validTrends.Length == 0) return;
      int horizon = validTrends.Max(x => x.Trend.TrendMinutes);
      var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var item in validTrends)
      {
        MarketTrendSelectedPopulation population = MarketTrendPopulationSelector.Select(
          item.Trend, recentMarkets ?? new Dictionary<string, Market>(),
          universe, universe.MainMarket, DataServiceInstance.Store, BaseInterval, pin.Value);
        symbols.UnionWith(population.AssetSymbols);
      }

      var request = new FuturesLongShortRatioSeriesRequest
      {
        Source = FuturesLongShortRatioSource.GlobalAccount,
        HorizonMinutes = horizon,
        Symbols = symbols.ToArray()
      };
      FuturesLongShortRatioRefreshReport report = await LongShortRatioDataServiceInstance.RefreshAsync(
        new[] { request }, pin.Value, budget,
        (source, symbol, from, until, limit, ct) =>
          BinanceFuturesLongShortRatioClient.GetHistoryPageAsync(source, symbol, from, until,
            limit, log, ct),
        log, cancellationToken).ConfigureAwait(false);
      log.DoLogInfo("Phase 2 global long/short account ratio: " + report.Requests + "/" + budget +
        " page requests; " + report.Failures + " failed; " +
        report.CompleteSeries + "/" + report.SelectedSeries +
        " symbol histories fully verified. Missing or incomplete observations remain UNAVAILABLE.");
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
      DateTimeOffset? refreshAsOfUtc;
      lock (SnapshotLock)
      {
        universe = _currentUniverse;
        refreshAsOfUtc = _currentRaidAsOfUtc;
      }

      if (universe == null)
      {
        log.DoLogWarn("BinanceFutures analytics - configured MarketTrend metrics were not calculated because the analyzer universe is unavailable.");
        return;
      }

      Dictionary<string, MarketTrendAnalysisSnapshot> analyses =
        new Dictionary<string, MarketTrendAnalysisSnapshot>(StringComparer.OrdinalIgnoreCase);
      DateTimeOffset evaluationStartedUtc = DateTimeOffset.UtcNow;
      DateTimeOffset analysisAsOfUtc = refreshAsOfUtc ?? evaluationStartedUtc;
      log.DoLogInfo("BinanceFutures analytics - normalized MarketTrends evaluate as of " +
        analysisAsOfUtc.ToString("O", CultureInfo.InvariantCulture) +
        (refreshAsOfUtc.HasValue ? " (pinned to kline refresh start)." : " (no refresh timestamp available)."));

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
          universe, universe.MainMarket, DataServiceInstance.Store, BaseInterval, analysisAsOfUtc);
        string[] globalSymbols = population.GlobalSymbols.ToArray();
        string[] assetSymbols = population.AssetSymbols.ToArray();

        MarketTrendAnalysisSnapshot analysis = MarketTrendAnalyticsEngine.Calculate(
          marketTrend,
          globalSymbols,
          assetSymbols,
          DataServiceInstance.Store,
          BaseInterval,
          analysisAsOfUtc,
          enabledMetrics,
          "BTC" + universe.MainMarket,
          systemConfiguration.AnalyzerSettings?.MarketAnalyzer?.StoreDataMaxHours ?? 0,
          OpenInterestDataServiceInstance,
          systemConfiguration.GeneralSettings?.Application?.Exchange ?? "BinanceFutures",
          FundingDataServiceInstance,
          LongShortRatioDataServiceInstance);
        analyses[marketTrend.Name] = analysis;

        foreach (MarketTrendHistoryRequirement requirement in analysis.HistoryRequirements)
        {
          if (requirement.SourceDataFamily == "BinanceFuturesFunding")
          {
            log.DoLogDebug("Phase 2 funding history for '" + marketTrend.Name + "' / " +
              requirement.Metric + ": " + requirement.Basis + " " + requirement.Diagnostic);
            continue;
          }
          if (requirement.SourceDataFamily == "BinanceFuturesLongShortRatio")
          {
            log.DoLogDebug("Phase 2 long/short ratio history for '" + marketTrend.Name + "' / " +
              requirement.Metric + ": " + requirement.Basis);
            continue;
          }
          if (requirement.SourceDataFamily == "BinanceFuturesOpenInterest")
          {
            log.DoLogDebug("Phase 2 OI history for '" + marketTrend.Name + "' / " +
              requirement.Metric + ": " + requirement.Basis + " " + requirement.Diagnostic);
            continue;
          }
          string detail = "BinanceFutures analytics - history requirement for '" + marketTrend.Name +
            "' / " + requirement.Metric + ": " + requirement.MinimumClosedSourceCandles +
            " closed source candles minimum; " + requirement.RequiredStoredSourceCandles +
            " retained slots at this raid; recommended StoreDataMaxHours >= " +
            requirement.RecommendedStoreDataMaxHours + ". " + requirement.Basis;
          if (requirement.HasAllAlignmentCapacity == false)
            log.DoLogWarn(detail + " " + requirement.Diagnostic);
          else
            log.DoLogDebug(detail);
        }

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
