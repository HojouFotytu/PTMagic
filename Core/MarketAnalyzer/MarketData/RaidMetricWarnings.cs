using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Collects failed normalized metric requests without writing one warning per
  /// market/trigger. Retains failure counts, caller examples and snapshot evidence.
  /// This collector never changes trigger or trading decisions.
  /// </summary>
  public sealed class RaidMetricWarnings
  {
    private const int MaxContextExamples = 5;
    private const int MaxReasonExamples = 3;
    private const int MaxReasonLength = 500;

    private sealed class Issue
    {
      public string Trend;
      public string Metric;
      public int FailedRequests;
      public readonly HashSet<string> Markets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      public readonly Dictionary<string, int> Sources = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      public readonly Dictionary<string, int> ReasonGroups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      public readonly List<string> Contexts = new List<string>();
      public int OtherReasonRequests;
      public string FirstReason;
      public string GlobalSnapshot;
      public string AssetSnapshot;
      public string Population;

      public Issue(string trend, string metric)
      {
        Trend = trend;
        Metric = metric;
      }
    }

    private readonly Dictionary<string, Issue> _issues = new Dictionary<string, Issue>(StringComparer.OrdinalIgnoreCase);
    private string _globalDecision = "";

    public void Reset()
    {
      _issues.Clear();
      _globalDecision = "";
    }

    /// <summary>
    /// Each failed request carries its origin. Source is GlobalSetting,
    /// SMS activation, or SMS OffTrigger; market is omitted for global triggers.
    /// Repeat failures are counted, but only a bounded set of examples is retained.
    /// </summary>
    public void ReportUnavailable(
      string trendName, string metricName, string reason,
      string source = null, string settingName = null, string marketSymbol = null,
      string relation = null, string trigger = null)
    {
      Issue issue = GetIssue(Name(trendName, "(unknown trend)"), MarketTrendMetrics.Normalize(metricName));
      issue.FailedRequests++;
      string normalizedReason = Clean(reason, "unavailable");
      if (issue.FirstReason == null)
      {
        issue.FirstReason = normalizedReason;
      }

      string reasonGroup = GroupReason(normalizedReason);
      if (issue.ReasonGroups.TryGetValue(reasonGroup, out int currentCount))
      {
        issue.ReasonGroups[reasonGroup] = currentCount + 1;
      }
      else if (issue.ReasonGroups.Count < MaxReasonExamples)
      {
        issue.ReasonGroups[reasonGroup] = 1;
      }
      else
      {
        issue.OtherReasonRequests++;
      }

      if (!String.IsNullOrWhiteSpace(marketSymbol))
      {
        issue.Markets.Add(marketSymbol.Trim());
      }
      string sourceLabel = Name(source, "unspecified caller");
      issue.Sources.TryGetValue(sourceLabel, out int sourceCount);
      issue.Sources[sourceLabel] = sourceCount + 1;

      string context = sourceLabel;
      if (!String.IsNullOrWhiteSpace(settingName)) context += " setting='" + Clean(settingName, "") + "'";
      if (!String.IsNullOrWhiteSpace(marketSymbol)) context += " market=" + Clean(marketSymbol, "");
      if (!String.IsNullOrWhiteSpace(trigger)) context += " trigger=" + Clean(trigger, "");
      if (!String.IsNullOrWhiteSpace(relation)) context += " relation=" + Clean(relation, "");
      context += String.IsNullOrWhiteSpace(metricName) ? " metric=implicit PriceTrend" : " metric=explicit " + Clean(metricName, "");
      context += " reason=" + ShortReason(normalizedReason);

      if (issue.Contexts.Count < MaxContextExamples && !issue.Contexts.Contains(context, StringComparer.OrdinalIgnoreCase))
      {
        issue.Contexts.Add(context);
      }
    }

    public void ReportGlobalDecision(string description)
    {
      _globalDecision = description?.Trim() ?? "";
    }

    public IReadOnlyList<string> BuildLines(IReadOnlyDictionary<string, MarketTrendAnalysisSnapshot> analyses)
    {
      foreach (Issue issue in _issues.Values)
      {
        issue.GlobalSnapshot = null;
        issue.AssetSnapshot = null;
        issue.Population = null;
      }

      if (analyses != null)
      {
        foreach (MarketTrendAnalysisSnapshot analysis in analyses.Values.Where(item => item != null))
        {
          string trend = Name(analysis.MarketTrendName, "(unknown trend)");
          string population = "TrendMinutes=" + analysis.TrendMinutes
            + "; global population=" + analysis.GlobalSymbols.Count
            + "; asset population=" + analysis.AssetSymbols.Count
            + "; expected endpoint=" + analysis.ExpectedClosedCandleTimeUtc.ToString("O", CultureInfo.InvariantCulture);

          foreach (KeyValuePair<string, FeatureValueSnapshot> metric in analysis.MarketMetrics)
          {
            if (!IsUsable(metric.Value))
            {
              Issue issue = GetIssue(trend, metric.Key);
              issue.GlobalSnapshot = "global snapshot " + FormatFeature(metric.Value);
              issue.Population = population;
            }
          }

          foreach (string metric in analysis.EnabledMetrics)
          {
            if (!MarketTrendMetrics.TryGetScope(metric, out MarketTrendMetricScope scope))
            {
              Issue issue = GetIssue(trend, metric);
              issue.GlobalSnapshot = "unknown configured metric '" + Clean(metric, "") + "'";
              issue.Population = population;
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
            Dictionary<string, (int Count, string SampleMarket)> examples =
              new Dictionary<string, (int Count, string SampleMarket)>(StringComparer.OrdinalIgnoreCase);
            int otherUnavailable = 0;
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
              string reason = Clean(feature?.InvalidReason, feature == null
                ? "No asset feature snapshot was published" : (feature.IsStale
                  ? "Stale asset feature" : "Invalid or missing value"));
              if (examples.TryGetValue(reason, out (int Count, string SampleMarket) count))
              {
                examples[reason] = (count.Count + 1, count.SampleMarket);
              }
              else if (examples.Count < MaxReasonExamples)
              {
                examples[reason] = (1, symbol);
              }
              else
              {
                otherUnavailable++;
              }
            }
            if (unavailable > 0)
            {
              Issue issue = GetIssue(trend, metric);
              issue.AssetSnapshot = unavailable + "/" + eligible + " markets unavailable; reasons: " +
                String.Join(" | ", examples.Select(pair => pair.Value.Count + "x " +
                  ShortReason(pair.Key) + " (example=" + pair.Value.SampleMarket + ")")) +
                (otherUnavailable > 0 ? " | other failures=" + otherUnavailable : "");
              issue.Population = population;
            }
          }
        }
      }

      if (_issues.Count == 0 && String.IsNullOrWhiteSpace(_globalDecision))
      {
        return Array.Empty<string>();
      }

      int groupCount = _issues.Count + (String.IsNullOrWhiteSpace(_globalDecision) ? 0 : 1);
      List<string> lines = new List<string> { "+ Metric warnings: " + groupCount };
      foreach (Issue issue in _issues.Values.OrderBy(item => item.Trend, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.Metric, StringComparer.OrdinalIgnoreCase))
      {
        // Prefer a diagnostic aggregate when the metric itself was invalid,
        // but keep resolver reasons and caller context alongside it.
        string primary = issue.GlobalSnapshot ?? issue.AssetSnapshot ?? issue.FirstReason ?? "unavailable";
        lines.Add("+   " + issue.Trend + " / " + issue.Metric + ": " + primary);

        if (issue.FailedRequests > 0)
        {
          string sources = String.Join(", ", issue.Sources.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key + "=" + pair.Value));
          string reasons = String.Join(" | ", issue.ReasonGroups.Select(pair =>
            pair.Value + "x " + ShortReason(pair.Key)));
          if (issue.OtherReasonRequests > 0) reasons += " | other reasons=" + issue.OtherReasonRequests;

          lines.Add("+     Failed trigger requests=" + issue.FailedRequests
            + "; distinct markets=" + issue.Markets.Count + "; sources=" + sources
            + "; reasons=" + reasons);
          lines.Add("+     Caller examples: " + String.Join(" | ", issue.Contexts)
            + (issue.FailedRequests > issue.Contexts.Count
              ? " (+" + (issue.FailedRequests - issue.Contexts.Count) + " additional requests)" : ""));
          if (issue.FirstReason != null && (issue.GlobalSnapshot != null || issue.AssetSnapshot != null))
          {
            lines.Add("+     First resolver error: " + ShortReason(issue.FirstReason));
          }
        }
        if (issue.AssetSnapshot != null && issue.GlobalSnapshot != null)
        {
          lines.Add("+     Asset diagnostics: " + issue.AssetSnapshot);
        }
        if (issue.Population != null)
        {
          lines.Add("+     Analytics: " + issue.Population);
        }
      }
      if (!String.IsNullOrWhiteSpace(_globalDecision))
      {
        lines.Add("+   GlobalSetting: " + ShortReason(_globalDecision));
      }
      return lines;
    }

    private Issue GetIssue(string trend, string metric)
    {
      string key = trend + "|" + metric;
      if (!_issues.TryGetValue(key, out Issue issue))
      {
        issue = new Issue(trend, metric);
        _issues.Add(key, issue);
      }
      return issue;
    }

    private static bool IsUsable(FeatureValueSnapshot value)
    {
      return value != null && value.IsValid && !value.IsStale
        && value.Value.HasValue && double.IsFinite(value.Value.Value);
    }

    private static string FormatFeature(FeatureValueSnapshot value)
    {
      if (value == null) return "missing";
      string description = "valid=" + value.IsValid + ", stale=" + value.IsStale
        + ", value=" + (value.Value.HasValue ? value.Value.Value.ToString("G", CultureInfo.InvariantCulture) : "null")
        + ", samples=" + value.SampleCount +
        (value.EligibleSampleCount.HasValue ? "/" + value.EligibleSampleCount.Value : "")
        + (value.CoveragePercent.HasValue
          ? ", coverage=" + value.CoveragePercent.Value.ToString("0.##", CultureInfo.InvariantCulture) + "%" : "")
        + (value.SourceDataTimestampUtc.HasValue ? ", endpoint=" +
          value.SourceDataTimestampUtc.Value.ToString("O", CultureInfo.InvariantCulture) : "");
      return description + ", reason=" + ShortReason(value.InvalidReason);
    }

    private static string GroupReason(string reason)
    {
      if (reason.IndexOf("outside the selected asset population", StringComparison.OrdinalIgnoreCase) >= 0)
      {
        return "Requested market excluded from the MarketTrend asset population";
      }
      if (reason.IndexOf("no calculated value", StringComparison.OrdinalIgnoreCase) >= 0)
      {
        return "No calculated metric value published";
      }
      return ShortReason(reason);
    }

    private static string Name(string value, string fallback)
    {
      return String.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string Clean(string text, string fallback)
    {
      return String.IsNullOrWhiteSpace(text) ? fallback : text.Trim().Replace('\r', ' ').Replace('\n', ' ');
    }

    private static string ShortReason(string text)
    {
      string reason = Clean(text, "unavailable");
      return reason.Length <= MaxReasonLength ? reason : reason.Substring(0, MaxReasonLength - 3) + "...";
    }
  }
}
