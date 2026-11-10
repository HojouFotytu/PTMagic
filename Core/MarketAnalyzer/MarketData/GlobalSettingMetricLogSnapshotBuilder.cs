using System;
using System.Collections.Generic;
using System.Linq;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Copies the global metrics of every configured trend at the setting-selection
  /// boundary. The log must never re-evaluate a historical activation using
  /// today's MarketTrend configuration or a later analytics snapshot.
  /// </summary>
  public static class GlobalSettingMetricLogSnapshotBuilder
  {
    public static List<GlobalSettingMetricSummary> Capture(
      IEnumerable<MarketTrend> configuredTrends,
      IReadOnlyDictionary<string, MarketTrendAnalysisSnapshot> analyses,
      IEnumerable<GlobalSettingTriggerSummary> selectedTriggers)
    {
      List<GlobalSettingMetricSummary> result = new List<GlobalSettingMetricSummary>();
      List<GlobalSettingTriggerSummary> references = (selectedTriggers ??
        Enumerable.Empty<GlobalSettingTriggerSummary>()).Where(trigger => trigger != null).ToList();

      foreach (MarketTrend trend in configuredTrends ?? Enumerable.Empty<MarketTrend>())
      {
        if (trend == null || String.IsNullOrWhiteSpace(trend.Name))
        {
          continue;
        }

        MarketTrendAnalysisSnapshot analysis = null;
        if (analyses != null)
        {
          analyses.TryGetValue(trend.Name, out analysis);
        }

        // Include declared metrics, computed global metrics (including implicit
        // PriceTrend dependencies), and selected trigger references. An enabled
        // but unavailable metric is shown as UNAVAILABLE, never fabricated as zero.
        HashSet<string> metrics = new HashSet<string>(
          MarketTrendMetrics.ParseEnabled(trend), StringComparer.OrdinalIgnoreCase);
        if (analysis?.MarketMetrics != null)
        {
          foreach (string metricName in analysis.MarketMetrics.Keys)
          {
            metrics.Add(MarketTrendMetrics.Normalize(metricName));
          }
        }

        foreach (GlobalSettingTriggerSummary trigger in references)
        {
          if (String.Equals(trigger.MarketTrendName, trend.Name, StringComparison.OrdinalIgnoreCase))
          {
            metrics.Add(MarketTrendMetrics.Normalize(trigger.Metric));
          }
        }

        foreach (string metric in metrics
          .OrderBy(name => String.Equals(name, MarketTrendMetrics.PriceTrend, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
          .ThenBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
          // Asset-scoped metrics belong in the complete metric list too.
          // They have no single global numerical value and must not be
          // presented as unavailable global aggregates.
          bool isAssetMetric = MarketTrendMetrics.TryGetScope(metric, out MarketTrendMetricScope scope)
            && scope == MarketTrendMetricScope.Asset;

          FeatureValueSnapshot feature = null;
          if (analysis?.MarketMetrics != null)
          {
            analysis.MarketMetrics.TryGetValue(metric, out feature);
          }

          bool available = feature != null && feature.IsValid && !feature.IsStale
            && feature.Value.HasValue && double.IsFinite(feature.Value.Value);
          bool referenced = references.Any(trigger =>
            String.Equals(trigger.MarketTrendName, trend.Name, StringComparison.OrdinalIgnoreCase)
            && String.Equals(MarketTrendMetrics.Normalize(trigger.Metric), metric, StringComparison.OrdinalIgnoreCase));
          bool matched = references.Any(trigger =>
            String.Equals(trigger.MarketTrendName, trend.Name, StringComparison.OrdinalIgnoreCase)
            && String.Equals(MarketTrendMetrics.Normalize(trigger.Metric), metric, StringComparison.OrdinalIgnoreCase)
            && String.Equals(trigger.Result, "TRUE", StringComparison.OrdinalIgnoreCase));

          string reason = "";
          if (!available && !isAssetMetric)
          {
            reason = !String.IsNullOrWhiteSpace(feature?.InvalidReason)
              ? feature.InvalidReason
              : feature?.IsStale == true ? "Metric was stale at activation."
              : analysis == null ? "No analysis snapshot was available at activation."
              : "Metric was unavailable at activation.";
          }

          result.Add(new GlobalSettingMetricSummary
          {
            MarketTrendName = trend.Name,
            Metric = metric,
            Value = !isAssetMetric && available ? feature.Value : (double?)null,
            Unit = MarketTrendMetricTriggerResolver.GetUnit(metric),
            UsedInTriggerSet = referenced,
            TriggerConditionTrue = matched,
            IsAssetMetric = isAssetMetric,
            UnavailableReason = reason
          });
        }
      }

      return result;
    }
  }
}
