using System;
using System.Collections.Generic;
using System.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Gathers normalized metric failures for a short end-of-raid summary.
  /// No trigger or trading behavior is changed by this diagnostics collector.
  /// </summary>
  public sealed class RaidMetricWarnings
  {
    private const int MaxDetails = 5;
    private const int MaxReasonLength = 105;
    private readonly Dictionary<string, string> _resolutionIssues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private string _globalDecision = "";

    public void Reset()
    {
      _resolutionIssues.Clear();
      _globalDecision = "";
    }

    public void ReportUnavailable(string trendName, string metricName, string reason)
    {
      string trend = Name(trendName, "(unknown trend)");
      string metric = Name(metricName, "(unnamed metric)");
      string key = trend + "|" + metric;
      if (!_resolutionIssues.ContainsKey(key))
      {
        _resolutionIssues.Add(key, trend + " / " + metric + ": " + ShortReason(reason));
      }
    }

    public void ReportGlobalDecision(string description)
    {
      _globalDecision = description?.Trim() ?? "";
    }

    public IReadOnlyList<string> BuildLines(IReadOnlyDictionary<string, MarketTrendAnalysisSnapshot> analyses)
    {
      SortedDictionary<string, string> issues = new SortedDictionary<string, string>(_resolutionIssues, StringComparer.OrdinalIgnoreCase);
      if (analyses != null)
      {
        foreach (MarketTrendAnalysisSnapshot analysis in analyses.Values.Where(item => item != null))
        {
          string trend = Name(analysis.MarketTrendName, "(unknown trend)");
          foreach (KeyValuePair<string, FeatureValueSnapshot> metric in analysis.MarketMetrics)
          {
            if (!IsUsable(metric.Value))
            {
              issues[trend + "|" + metric.Key] = trend + " / " + metric.Key + ": " + ShortReason(metric.Value?.InvalidReason);
            }
          }

          foreach (string metric in analysis.EnabledMetrics)
          {
            if (!MarketTrendMetrics.TryGetScope(metric, out MarketTrendMetricScope scope))
            {
              issues[trend + "|" + metric] = trend + " / " + metric + ": unknown metric";
              continue;
            }

            if (scope != MarketTrendMetricScope.Asset && scope != MarketTrendMetricScope.Both)
            {
              continue;
            }

            int eligible = analysis.AssetSymbols.Count;
            if (eligible == 0)
            {
              continue;
            }

            int unavailable = 0;
            string firstReason = null;
            bool uniformReason = true;
            foreach (string symbol in analysis.AssetSymbols)
            {
              FeatureValueSnapshot feature = null;
              if (analysis.AssetMetrics.TryGetValue(symbol, out IReadOnlyDictionary<string, FeatureValueSnapshot> assetMetrics)
                && assetMetrics != null)
              {
                assetMetrics.TryGetValue(metric, out feature);
              }

              if (IsUsable(feature))
              {
                continue;
              }

              unavailable++;
              string reason = feature?.InvalidReason ?? "";
              if (firstReason == null)
              {
                firstReason = reason;
              }
              else if (!String.Equals(firstReason, reason, StringComparison.OrdinalIgnoreCase))
              {
                uniformReason = false;
              }
            }

            if (unavailable > 0)
            {
              string description = unavailable + "/" + eligible + " markets unavailable";
              if (uniformReason && !String.IsNullOrWhiteSpace(firstReason))
              {
                description += " (" + ShortReason(firstReason) + ")";
              }

              issues[trend + "|" + metric] = trend + " / " + metric + ": " + description;
            }
          }
        }
      }

      if (!String.IsNullOrWhiteSpace(_globalDecision))
      {
        issues["~GlobalDecision"] = "GlobalSetting: " + ShortReason(_globalDecision);
      }

      if (issues.Count == 0)
      {
        return Array.Empty<string>();
      }

      List<string> lines = new List<string> { "+ Metric warnings: " + issues.Count };
      lines.AddRange(issues.Values.Take(MaxDetails).Select(detail => "+   " + detail));
      if (issues.Count > MaxDetails)
      {
        lines.Add("+   ... and " + (issues.Count - MaxDetails) + " more");
      }

      return lines;
    }

    private static bool IsUsable(FeatureValueSnapshot value)
    {
      return value != null && value.IsValid && !value.IsStale
        && value.Value.HasValue && double.IsFinite(value.Value.Value);
    }

    private static string Name(string value, string fallback)
    {
      return String.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string ShortReason(string text)
    {
      string reason = String.IsNullOrWhiteSpace(text) ? "unavailable" : text.Trim();
      reason = reason.Replace('\r', ' ').Replace('\n', ' ');
      return reason.Length <= MaxReasonLength ? reason : reason.Substring(0, MaxReasonLength - 3) + "...";
    }
  }
}
