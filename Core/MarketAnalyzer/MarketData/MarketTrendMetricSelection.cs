using System;
using System.Collections.Generic;
using System.Linq;
using Core.Main.DataObjects.PTMagicData;
using Core.Main;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Converts the per-trend metric configuration into the effective set.
  /// Omitted Metrics defaults to normalized PriceTrend, and an omitted
  /// trigger Metric implicitly requests PriceTrend even on an explicit
  /// metrics-only MarketTrend. All other dependencies remain internal.
  /// </summary>
  public static class MarketTrendMetricSelection
  {
    public static IReadOnlyCollection<string> Select(MarketTrend trend, AnalyzerSettings analyzer)
    {
      HashSet<string> metrics = new HashSet<string>(
        MarketTrendMetrics.ParseEnabled(trend), StringComparer.OrdinalIgnoreCase);
      if (trend == null || analyzer == null || metrics.Contains(MarketTrendMetrics.PriceTrend))
      {
        return metrics.ToArray();
      }

      if ((analyzer.GlobalSettings ?? new List<GlobalSetting>())
        .Where(setting => setting?.Triggers != null)
        .SelectMany(setting => setting.Triggers)
        .Any(t => NeedsImplicitPriceTrend(t?.MarketTrendName, t?.Metric, trend)))
      {
        metrics.Add(MarketTrendMetrics.PriceTrend);
      }

      foreach (SingleMarketSetting setting in analyzer.SingleMarketSettings
        ?? new List<SingleMarketSetting>())
      {
        if (setting == null) continue;
        if ((setting.Triggers ?? new List<Trigger>()).Any(t => t != null
            && t.AgeDaysLowerThan <= 0 && !IsVolumeOnly(t.Min24hVolume, t.Max24hVolume)
            && NeedsImplicitPriceTrend(t.MarketTrendName, t.Metric, trend))
          || (setting.OffTriggers ?? new List<OffTrigger>()).Any(t => t != null
            && t.HoursSinceTriggered <= 0 && !IsVolumeOnly(t.Min24hVolume, t.Max24hVolume)
            && NeedsImplicitPriceTrend(t.MarketTrendName, t.Metric, trend)))
        {
          metrics.Add(MarketTrendMetrics.PriceTrend);
          break;
        }
      }

      return metrics.ToArray();
    }

    private static bool NeedsImplicitPriceTrend(string trendName, string metric, MarketTrend trend)
    {
      return String.IsNullOrWhiteSpace(metric)
        && !String.IsNullOrWhiteSpace(trendName)
        && trendName.Equals(trend.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVolumeOnly(double minVolume, double maxVolume)
    {
      return minVolume > 0 || maxVolume < Constants.Max24hVolume;
    }
  }
}
