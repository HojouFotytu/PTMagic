using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Core.Helper;
using Core.Main;
using Core.Main.DataObjects.PTMagicData;
using Core.MarketAnalyzer;
using Newtonsoft.Json;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Builds an observation-only snapshot of explicitly configured MarketTrend analytics for the Monitor.
  /// This does not participate in GlobalSetting or SingleMarketSetting trigger evaluation.
  /// </summary>
  public static class MarketTrendAnalyticsMonitorExporter
  {
    private const string SnapshotFileName = "MarketTrendAnalytics.json";
    private static readonly TimeSpan BaseInterval = TimeSpan.FromMinutes(5);

    public static void BuildAndSave(
      IEnumerable<string> exchangeMarkets,
      string mainMarket,
      PTMagicConfiguration systemConfiguration,
      LogHelper log)
    {
      if (exchangeMarkets == null || systemConfiguration == null || log == null)
      {
        return;
      }

      Core.Main.DataObjects.PTMagicData.MarketAnalyzer analyzerSettings = systemConfiguration.AnalyzerSettings?.MarketAnalyzer;
      if (analyzerSettings?.MarketTrends == null || analyzerSettings.MarketTrends.Count == 0)
      {
        return;
      }

      // PT enabled_pairs/disabled_pairs are finalized later in the raid. Do not publish a
      // misleading monitor snapshot when the configured analytics universe depends on them.
      if (analyzerSettings.UseProfitTrailerEnabledPairs)
      {
        log.DoLogWarn("BinanceFutures analytics monitor snapshot skipped because UseProfitTrailerEnabledPairs is enabled; the exact PT pair universe is not finalized at this stage of the raid.");
        return;
      }

      try
      {
        string[] eligibleSymbols = BinanceFuturesMarketUniverse
          .FilterMarketNames(exchangeMarkets, systemConfiguration)
          .Where(symbol => !String.IsNullOrWhiteSpace(symbol))
          .Select(symbol => symbol.Trim().ToUpperInvariant())
          .Distinct(StringComparer.OrdinalIgnoreCase)
          .ToArray();

        if (eligibleSymbols.Length == 0)
        {
          return;
        }

        Dictionary<string, Market> recentMarkets = BaseAnalyzer.GetMarketDataFromFile(
          systemConfiguration,
          log,
          "Exchange",
          DateTime.UtcNow,
          "Recent");

        MarketTrendAnalyticsMonitorSnapshot monitorSnapshot = new MarketTrendAnalyticsMonitorSnapshot
        {
          TimestampUtc = DateTimeOffset.UtcNow
        };

        foreach (MarketTrend marketTrend in analyzerSettings.MarketTrends
          .Where(trend => trend != null && trend.Platform.Equals("Exchange", StringComparison.OrdinalIgnoreCase)))
        {
          if (String.IsNullOrWhiteSpace(marketTrend.Metrics))
          {
            continue;
          }

          IReadOnlyCollection<string> enabledMetrics = MarketTrendMetrics.ParseEnabled(marketTrend);
          if (!enabledMetrics.Any(MarketTrendMetrics.IsKnown))
          {
            continue;
          }

          string[] assetSymbols = ApplyTrendMarketFilters(
            eligibleSymbols,
            mainMarket,
            marketTrend);

          string[] volumeRankedSymbols = assetSymbols
            .Where(symbol => recentMarkets != null && recentMarkets.ContainsKey(symbol))
            .OrderByDescending(symbol => recentMarkets[symbol].Volume24h)
            .ThenBy(symbol => symbol, StringComparer.OrdinalIgnoreCase)
            .ToArray();

          if (marketTrend.MaxMarkets > 0)
          {
            volumeRankedSymbols = volumeRankedSymbols.Take(marketTrend.MaxMarkets).ToArray();
          }

          string[] globalSymbols = ApplyTrendThreshold(
            volumeRankedSymbols,
            marketTrend,
            monitorSnapshot.TimestampUtc);

          MarketTrendAnalysisSnapshot analysis = MarketTrendAnalyticsEngine.Calculate(
            marketTrend,
            globalSymbols,
            assetSymbols,
            BinanceFuturesAnalyticsRuntime.DataService.Store,
            BaseInterval,
            monitorSnapshot.TimestampUtc);

          monitorSnapshot.Trends[marketTrend.Name] = ToMonitorTrend(analysis);
        }

        string snapshotPath = Path.Combine(
          Directory.GetCurrentDirectory(),
          Constants.PTMagicPathData,
          SnapshotFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath));
        File.WriteAllText(snapshotPath, JsonConvert.SerializeObject(monitorSnapshot, Formatting.None));

        log.DoLogInfo("BinanceFutures analytics - Monitor snapshot saved for " + monitorSnapshot.Trends.Count + " configured MarketTrend(s).");
      }
      catch (Exception ex)
      {
        log.DoLogWarn("BinanceFutures analytics - unable to save Monitor metric snapshot. " + ex.Message);
      }
    }

    private static string[] ApplyTrendMarketFilters(
      IEnumerable<string> symbols,
      string mainMarket,
      MarketTrend marketTrend)
    {
      string normalizedMainMarket = mainMarket?.Trim().ToUpperInvariant() ?? "";
      HashSet<string> ignoredMarkets = NormalizeConfiguredMarkets(marketTrend.IgnoredMarkets, normalizedMainMarket);
      HashSet<string> allowedMarkets = NormalizeConfiguredMarkets(marketTrend.AllowedMarkets, normalizedMainMarket);

      return symbols
        .Where(symbol =>
        {
          string simpleSymbol = StripMainMarket(symbol, normalizedMainMarket);
          if (ignoredMarkets.Contains(simpleSymbol))
          {
            return false;
          }

          return allowedMarkets.Count == 0 || allowedMarkets.Contains(simpleSymbol);
        })
        .ToArray();
    }

    private static string[] ApplyTrendThreshold(
      IEnumerable<string> symbols,
      MarketTrend marketTrend,
      DateTimeOffset calculatedAtUtc)
    {
      string[] rankedSymbols = symbols.ToArray();
      if (marketTrend.TrendThreshold == 0)
      {
        return rankedSymbols;
      }

      double threshold = Math.Abs(marketTrend.TrendThreshold);
      List<string> filtered = new List<string>(rankedSymbols.Length);
      foreach (string symbol in rankedSymbols)
      {
        MarketDataSeriesSnapshot series = BinanceFuturesAnalyticsRuntime.DataService.Store.GetSnapshot(
          symbol,
          BaseInterval,
          calculatedAtUtc);
        FeatureValueSnapshot feature = MarketTrendAnalyticsEngine.CalculateReturn(
          series,
          marketTrend.TrendMinutes,
          calculatedAtUtc);

        if (feature.IsValid
          && !feature.IsStale
          && feature.Value.HasValue
          && double.IsFinite(feature.Value.Value)
          && feature.Value.Value <= threshold
          && feature.Value.Value >= -threshold)
        {
          filtered.Add(symbol);
        }
      }

      return filtered.ToArray();
    }

    private static MarketTrendAnalyticsMonitorTrend ToMonitorTrend(MarketTrendAnalysisSnapshot analysis)
    {
      MarketTrendAnalyticsMonitorTrend result = new MarketTrendAnalyticsMonitorTrend
      {
        Name = analysis.MarketTrendName,
        TrendMinutes = analysis.TrendMinutes,
        GlobalSymbols = analysis.GlobalSymbols.ToList(),
        AssetSymbols = analysis.AssetSymbols.ToList(),
        MarketMetrics = analysis.MarketMetrics.ToDictionary(
          pair => pair.Key,
          pair => pair.Value,
          StringComparer.OrdinalIgnoreCase)
      };

      foreach (KeyValuePair<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> asset in analysis.AssetMetrics)
      {
        result.AssetMetrics[asset.Key] = asset.Value.ToDictionary(
          pair => pair.Key,
          pair => pair.Value,
          StringComparer.OrdinalIgnoreCase);
      }

      return result;
    }

    private static HashSet<string> NormalizeConfiguredMarkets(string configuredMarkets, string mainMarket)
    {
      return new HashSet<string>(
        SystemHelper.ConvertTokenStringToList(configuredMarkets ?? "", ",")
          .Where(value => !String.IsNullOrWhiteSpace(value))
          .Select(value => StripMainMarket(value.Trim().ToUpperInvariant(), mainMarket)),
        StringComparer.OrdinalIgnoreCase);
    }

    private static string StripMainMarket(string symbol, string mainMarket)
    {
      string normalized = symbol?.Trim().ToUpperInvariant() ?? "";
      if (!String.IsNullOrWhiteSpace(mainMarket)
        && normalized.EndsWith(mainMarket, StringComparison.OrdinalIgnoreCase))
      {
        return normalized.Substring(0, normalized.Length - mainMarket.Length);
      }

      return normalized.Replace("-", "").Replace("_", "");
    }
  }
}
