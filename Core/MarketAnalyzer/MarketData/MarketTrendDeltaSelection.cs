using System;
using System.Collections.Generic;
using System.Linq;
using Core.Main;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Delta is a relation on an existing metric, not a new registry entry.
  /// Resolve only the metric dependencies actually referenced by numeric triggers.
  /// </summary>
  public static class MarketTrendDeltaSelection
  {
    public static bool IsDelta(string relation) =>
      String.Equals(relation?.Trim(), Constants.MarketTrendRelationDelta,
        StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> Select(
      MarketTrend trend, AnalyzerSettings settings)
    {
      if (trend == null || settings == null)
        return Array.Empty<string>();

      var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      bool Match(string trendName, string relation) =>
        IsDelta(relation) && String.Equals(trend.Name, trendName,
          StringComparison.OrdinalIgnoreCase);

      foreach (GlobalSetting setting in settings.GlobalSettings ?? new List<GlobalSetting>())
        foreach (Trigger trigger in setting?.Triggers ?? new List<Trigger>())
          if (trigger != null && Match(trigger.MarketTrendName, trigger.MarketTrendRelation))
            requested.Add(MarketTrendMetrics.Normalize(trigger.Metric));

      foreach (SingleMarketSetting setting in settings.SingleMarketSettings ?? new List<SingleMarketSetting>())
      {
        foreach (Trigger trigger in setting?.Triggers ?? new List<Trigger>())
          if (trigger != null && trigger.AgeDaysLowerThan <= 0 &&
            trigger.Min24hVolume <= 0 && trigger.Max24hVolume >= Constants.Max24hVolume &&
            Match(trigger.MarketTrendName, trigger.MarketTrendRelation))
            requested.Add(MarketTrendMetrics.Normalize(trigger.Metric));

        foreach (OffTrigger trigger in setting?.OffTriggers ?? new List<OffTrigger>())
          if (trigger != null && trigger.HoursSinceTriggered <= 0 &&
            trigger.Min24hVolume <= 0 && trigger.Max24hVolume >= Constants.Max24hVolume &&
            Match(trigger.MarketTrendName, trigger.MarketTrendRelation))
            requested.Add(MarketTrendMetrics.Normalize(trigger.Metric));
      }

      // Match the existing explicit-Metrics / omitted-PriceTrend contract.
      var enabled = new HashSet<string>(
        MarketTrendMetricSelection.Select(trend, settings), StringComparer.OrdinalIgnoreCase);
      return requested.Where(metric => MarketTrendMetrics.IsKnown(metric) && enabled.Contains(metric))
        .OrderBy(metric => metric, StringComparer.OrdinalIgnoreCase).ToArray();
    }
  }
}
