using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Core.Helper;
using Core.Main;
using Newtonsoft.Json;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Persists the exact post-Phase-0 MarketTrend analytics runtime snapshot for the Monitor.
  /// Metric calculation and universe selection are completed before this exporter is called.
  /// </summary>
  public static class MarketTrendAnalyticsMonitorExporter
  {
    private const string SnapshotFileName = "MarketTrendAnalytics.json";

    public static void ClearSnapshot(LogHelper log)
    {
      try
      {
        string snapshotPath = GetSnapshotPath();
        if (File.Exists(snapshotPath))
        {
          File.Delete(snapshotPath);
        }
      }
      catch (Exception ex)
      {
        log?.DoLogWarn("BinanceFutures analytics - unable to clear stale Monitor metric snapshot. " + ex.Message);
      }
    }

    public static void SaveCurrentRuntimeSnapshot(LogHelper log)
    {
      Save(BinanceFuturesAnalyticsRuntime.CurrentTrendAnalyses, log);
    }

    public static void Save(
      IReadOnlyDictionary<string, MarketTrendAnalysisSnapshot> analyses,
      LogHelper log)
    {
      if (log == null)
      {
        return;
      }

      if (analyses == null || analyses.Count == 0)
      {
        ClearSnapshot(log);
        return;
      }

      try
      {
        MarketTrendAnalysisSnapshot[] validAnalyses = analyses.Values
          .Where(analysis => analysis != null)
          .ToArray();

        if (validAnalyses.Length == 0)
        {
          ClearSnapshot(log);
          return;
        }

        MarketTrendAnalyticsMonitorSnapshot monitorSnapshot = new MarketTrendAnalyticsMonitorSnapshot
        {
          TimestampUtc = validAnalyses.Max(analysis => analysis.CalculatedAtUtc)
        };

        foreach (MarketTrendAnalysisSnapshot analysis in validAnalyses
          .OrderBy(analysis => analysis.MarketTrendName, StringComparer.OrdinalIgnoreCase))
        {
          monitorSnapshot.Trends[analysis.MarketTrendName] = ToMonitorTrend(analysis);
        }

        string snapshotPath = GetSnapshotPath();
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath));
        File.WriteAllText(snapshotPath, JsonConvert.SerializeObject(monitorSnapshot, Formatting.None));

        log.DoLogInfo(
          "BinanceFutures analytics - exact Monitor snapshot saved for " +
          monitorSnapshot.Trends.Count + " configured MarketTrend(s).");
      }
      catch (Exception ex)
      {
        ClearSnapshot(log);
        log.DoLogWarn("BinanceFutures analytics - unable to save Monitor metric snapshot. " + ex.Message);
      }
    }

    private static string GetSnapshotPath()
    {
      return Path.Combine(
        Directory.GetCurrentDirectory(),
        Constants.PTMagicPathData,
        SnapshotFileName);
    }

    private static MarketTrendAnalyticsMonitorTrend ToMonitorTrend(MarketTrendAnalysisSnapshot analysis)
    {
      MarketTrendAnalyticsMonitorTrend result = new MarketTrendAnalyticsMonitorTrend
      {
        Name = analysis.MarketTrendName,
        TrendMinutes = analysis.TrendMinutes,
        HistoryRequirements = analysis.HistoryRequirements.ToList(),
        ExpectedClosedCandleTimeUtc = analysis.ExpectedClosedCandleTimeUtc,
        GlobalReturnsEvaluated = analysis.GlobalReturnsEvaluated,
        AssetReturnsEvaluated = analysis.AssetReturnsEvaluated,
        ValidGlobalReturnCount = analysis.ValidGlobalReturnCount,
        ValidAssetReturnCount = analysis.ValidAssetReturnCount,
        GlobalInvalidReturnReasons = analysis.GlobalInvalidReturnReasons.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
        AssetInvalidReturnReasons = analysis.AssetInvalidReturnReasons.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
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
  }
}
