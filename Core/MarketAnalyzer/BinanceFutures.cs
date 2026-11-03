using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Helper;
using Core.Main;
using Core.Main.DataObjects.PTMagicData;
using Core.MarketAnalyzer.MarketData;

namespace Core.MarketAnalyzer
{
  public class BinanceFutures : BaseAnalyzer
  {
    private const string ExchangeName = "BinanceFutures";
    private const string ApiBase = "https://fapi.binance.com/fapi/v1";

    public static Task<double> GetMainCurrencyPriceAsync(string mainMarket, PTMagicConfiguration systemConfiguration, LogHelper log, CancellationToken cancellationToken = default)
    {
      return BinanceAnalyzerCommon.GetMainCurrencyPriceAsync(ExchangeName, ApiBase, mainMarket, log, false, cancellationToken);
    }

    public static async Task<List<string>> GetMarketDataAsync(string mainMarket, ConcurrentDictionary<string, MarketInfo> marketInfos, PTMagicConfiguration systemConfiguration, LogHelper log, CancellationToken cancellationToken = default)
    {
      await BinanceFuturesMarketUniverse.RefreshAsync(ApiBase, log, cancellationToken).ConfigureAwait(false);
      BinanceFuturesMarketUniverse.BeginRaidEligibilityCache();

      List<string> markets = await BinanceAnalyzerCommon.GetMarketDataAsync(ExchangeName, ApiBase, mainMarket, marketInfos, systemConfiguration, log, false, true, true, false, cancellationToken).ConfigureAwait(false);

      bool hasConfiguredAnalytics = false;
      IEnumerable<MarketTrend> configuredTrends = systemConfiguration?.AnalyzerSettings?.MarketAnalyzer?.MarketTrends
        ?? Enumerable.Empty<MarketTrend>();
      foreach (MarketTrend marketTrend in configuredTrends.Where(trend =>
        trend != null
        && String.Equals(trend.Platform, "Exchange", StringComparison.OrdinalIgnoreCase)
        && !String.IsNullOrWhiteSpace(trend.Metrics)))
      {
        IReadOnlyCollection<string> enabledMetrics = MarketTrendMetrics.ParseEnabled(marketTrend);
        string[] unknownMetrics = enabledMetrics.Where(metric => !MarketTrendMetrics.IsKnown(metric)).ToArray();
        if (unknownMetrics.Length > 0)
        {
          log.DoLogError(
            "BinanceFutures analytics - MarketTrend '" + marketTrend.Name + "' contains unknown Metrics: " +
            String.Join(", ", unknownMetrics) + ".");
        }

        if (enabledMetrics.Any(MarketTrendMetrics.IsKnown))
        {
          hasConfiguredAnalytics = true;
        }
      }

      if (markets == null)
      {
        MarketTrendAnalyticsMonitorExporter.ClearSnapshot(log);
        return null;
      }

      if (!hasConfiguredAnalytics)
      {
        BinanceFuturesAnalyticsRuntime.DataService.Store.Clear();
        MarketTrendAnalyticsMonitorExporter.ClearSnapshot(log);
        return markets;
      }

      // Only populate normalized observation data here. Metric evaluation is deliberately
      // deferred until PTMagic's Phase 0 pipeline has finalized the exact per-trend global
      // and single-market populations.
      await BinanceFuturesAnalyticsRuntime.RefreshObservationDataAsync(markets, mainMarket, systemConfiguration, log, cancellationToken).ConfigureAwait(false);

      return markets;
    }

    public static Task<List<MarketCandle>> GetOneMinuteMarketCandlesAsync(string marketName, int candlesNeeded, LogHelper log, CancellationToken cancellationToken = default)
    {
      return BinanceKlineClient.GetOneMinuteCandlesAsync(ExchangeName, ApiBase, marketName, candlesNeeded, log, cancellationToken);
    }

    public static Task<List<MarketCandle>> GetFiveMinuteMarketCandlesAsync(string marketName, int candlesNeeded, LogHelper log, CancellationToken cancellationToken = default)
    {
      return BinanceKlineClient.GetFiveMinuteCandlesAsync(ExchangeName, ApiBase, marketName, candlesNeeded, log, cancellationToken);
    }

    public static Task CheckFirstSeenDatesAsync(Dictionary<string, Market> markets, ConcurrentDictionary<string, MarketInfo> marketInfos, PTMagicConfiguration systemConfiguration, LogHelper log, CancellationToken cancellationToken = default)
    {
      return BinanceAnalyzerCommon.CheckFirstSeenDatesAsync(ExchangeName, ApiBase, markets, marketInfos, systemConfiguration, log, cancellationToken);
    }

    public static Task<DateTime> GetFirstSeenDateAsync(string marketName, PTMagicConfiguration systemConfiguration, LogHelper log, CancellationToken cancellationToken = default)
    {
      return BinanceAnalyzerCommon.GetFirstSeenDateAsync(ExchangeName, ApiBase, marketName, log, cancellationToken);
    }

    public static Task<List<MarketTick>> GetMarketTicksAsync(string marketName, int ticksNeeded, PTMagicConfiguration systemConfiguration, LogHelper log, CancellationToken cancellationToken = default)
    {
      return BinanceAnalyzerCommon.GetMarketTicksAsync(ExchangeName, ApiBase, marketName, ticksNeeded, log, false, cancellationToken);
    }

    public static Task CheckForMarketDataRecreationAsync(string mainMarket, Dictionary<string, Market> markets, PTMagicConfiguration systemConfiguration, LogHelper log, CancellationToken cancellationToken = default)
    {
      return BinanceAnalyzerCommon.CheckForMarketDataRecreationAsync(ExchangeName, ApiBase, mainMarket, markets, systemConfiguration, log, false, false, cancellationToken);
    }
  }
}
