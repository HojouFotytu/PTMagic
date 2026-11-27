using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Explains normalized metric failures in everyday language in the end-of-raid
  /// summary. Requests remain grouped by MarketTrend/metric and never change
  /// the trigger's calculated value or three-valued decision.
  /// </summary>
  public sealed class RaidMetricWarnings
  {
    private const int MaxCallerExamples = 5;
    private const int MaxAssetReasonExamples = 3;

    private sealed class Caller
    {
      public string Source;
      public string Setting;
      public string Market;
      public string Relation;
      public string Trigger;
      public bool ImplicitPriceTrend;
    }

    private sealed class ReportedCause
    {
      public string Market;
      public string Reason;
      public string Relation;
      public Caller Caller;
    }

    private sealed class AssetFailure
    {
      public string Reason;
      public string ExampleMarket;
      public int Count;
      public bool Stale;
    }

    private sealed class Issue
    {
      public string Trend;
      public string Metric;
      public int FailedRequests;
      public readonly HashSet<string> Markets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      public readonly List<Caller> Callers = new List<Caller>();
      // For genuine WARN, retain only bounded caller examples. Exact
      // flat-path trigger DEBUG is emitted as the request is evaluated.
      public bool HasNonSmsCaller;
      // Full reason/market facts are needed to safely classify >5 repeated
      // SMS trigger requests; Callers remain bounded only for display.
      public readonly List<ReportedCause> ReportedCauses = new List<ReportedCause>();
      public readonly List<string> FailureReasons = new List<string>();
      public FeatureValueSnapshot GlobalFeature;
      public bool UnknownMetric;
      public int UnavailableAssets;
      public int EligibleAssets;
      public readonly List<AssetFailure> AssetFailures = new List<AssetFailure>();
      public int OtherAssetFailures;
    }

    private readonly Dictionary<string, Issue> _issues =
      new Dictionary<string, Issue>(StringComparer.OrdinalIgnoreCase);
    private string _globalDecision = "";
    private readonly Action<string> _inlineDebug;

    public RaidMetricWarnings(Action<string> inlineDebug = null)
    {
      _inlineDebug = inlineDebug;
    }

    public void Reset()
    {
      _issues.Clear();
      _globalDecision = "";
    }

    public void ReportUnavailable(
      string trendName, string metricName, string reason,
      string source = null, string settingName = null, string marketSymbol = null,
      string relation = null, string trigger = null)
    {
      // The calculator's exact verified NO_TRADES tag represents inactivity,
      // not a corrupt source. Do not suppress other missing-data failures.
      if (Contains(reason, "(NO_TRADES: source-verified")) return;
      Issue issue = GetIssue(Name(trendName, "(unknown trend)"), MarketTrendMetrics.Normalize(metricName));
      issue.FailedRequests++;
      string cleaned = Clean(reason);

      if (!issue.FailureReasons.Contains(cleaned, StringComparer.OrdinalIgnoreCase)
        && issue.FailureReasons.Count < MaxAssetReasonExamples)
      {
        issue.FailureReasons.Add(cleaned);
      }

      if (!String.IsNullOrWhiteSpace(marketSymbol))
      {
        issue.Markets.Add(marketSymbol.Trim());
      }

      Caller caller = new Caller
      {
        Source = Name(source, "trigger"),
        Setting = Name(settingName, "(unknown setting)"),
        Market = marketSymbol?.Trim() ?? "",
        Relation = relation?.Trim() ?? "",
        Trigger = trigger?.Trim() ?? "",
        ImplicitPriceTrend = String.IsNullOrWhiteSpace(metricName)
      };
      issue.ReportedCauses.Add(new ReportedCause
        { Market = caller.Market, Reason = cleaned, Relation = caller.Relation, Caller = caller });
      bool smsCaller = caller.Source.Equals("SMS activation", StringComparison.OrdinalIgnoreCase) ||
        caller.Source.Equals("SMS OffTrigger", StringComparison.OrdinalIgnoreCase);
      if (!smsCaller) issue.HasNonSmsCaller = true;
      if (smsCaller &&
          String.Equals(issue.Metric, MarketTrendMetrics.PricePathEfficiency, StringComparison.OrdinalIgnoreCase) &&
          String.Equals(NaturalReason(cleaned).TrimEnd('.'),
            PricePathEfficiencyEngine.FlatPathUnavailableReason.TrimEnd('.'),
            StringComparison.OrdinalIgnoreCase))
      {
        _inlineDebug?.Invoke("PricePathEfficiency unavailable during SMS evaluation: MarketTrend '" +
          issue.Trend + "', " + DescribeCaller(caller) + "; reason=" + NaturalReason(cleaned) + ".");
      }
      if (issue.Callers.Count < MaxCallerExamples &&
        !issue.Callers.Any(existing => SameCaller(existing, caller)))
      {
        issue.Callers.Add(caller);
      }
    }

    public void ReportGlobalDecision(string description)
    {
      _globalDecision = description?.Trim() ?? "";
    }

    public IReadOnlyList<string> BuildLines(IReadOnlyDictionary<string, MarketTrendAnalysisSnapshot> analyses,
      NewMarketHistoryClassification newMarkets = null,
      IReadOnlyDictionary<string, MarketTrendDeltaAnalysisSnapshot> deltas = null)
    {
      foreach (Issue issue in _issues.Values)
      {
        issue.GlobalFeature = null;
        issue.UnknownMetric = false;
        issue.UnavailableAssets = 0;
        issue.EligibleAssets = 0;
        issue.AssetFailures.Clear();
        issue.OtherAssetFailures = 0;
      }

      if (analyses != null)
      {
        foreach (MarketTrendAnalysisSnapshot analysis in analyses.Values.Where(item => item != null))
        {
          string trend = Name(analysis.MarketTrendName, "(unknown trend)");
          foreach (KeyValuePair<string, FeatureValueSnapshot> metric in analysis.MarketMetrics)
          {
            if (!IsUsable(metric.Value))
            {
              GetIssue(trend, metric.Key).GlobalFeature = metric.Value;
            }
          }

          foreach (string metric in analysis.EnabledMetrics)
          {
            if (!MarketTrendMetrics.TryGetScope(metric, out MarketTrendMetricScope scope))
            {
              GetIssue(trend, metric).UnknownMetric = true;
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
            List<AssetFailure> reasons = new List<AssetFailure>();
            int others = 0;
            foreach (string symbol in analysis.AssetSymbols)
            {
              FeatureValueSnapshot feature = null;
              if (analysis.AssetMetrics.TryGetValue(symbol, out IReadOnlyDictionary<string, FeatureValueSnapshot> metrics)
                && metrics != null)
              {
                metrics.TryGetValue(metric, out feature);
              }

              if (IsUsable(feature) || (feature?.IsNoTrades == true && !feature.IsStale) ||
                newMarkets?.IsReportedNewMarket(symbol) == true)
              {
                continue;
              }

              unavailable++;
              string reason = feature == null ? "No metric snapshot was published."
                : !String.IsNullOrWhiteSpace(feature.InvalidReason) ? feature.InvalidReason
                : feature.IsStale ? "The market data is stale." : "No valid metric value was available.";

              AssetFailure existing = reasons.FirstOrDefault(item =>
                item.Stale == (feature?.IsStale == true) &&
                String.Equals(item.Reason, reason, StringComparison.OrdinalIgnoreCase));
              if (existing != null)
              {
                existing.Count++;
              }
              else if (reasons.Count < MaxAssetReasonExamples)
              {
                reasons.Add(new AssetFailure {
                  Reason = reason, Count = 1, ExampleMarket = symbol, Stale = feature?.IsStale == true
                });
              }
              else
              {
                others++;
              }
            }

            if (unavailable > 0)
            {
              Issue issue = GetIssue(trend, metric);
              issue.UnavailableAssets = unavailable;
              issue.EligibleAssets = eligible;
              issue.AssetFailures.AddRange(reasons);
              issue.OtherAssetFailures = others;
            }
          }
        }
      }

      // The consolidated notice owns all market-specific warning reporting for
      // its members for this raid, regardless of metric, relation or cause.
      // Independent global failures remain reportable; original evidence remains intact.
      Issue[] reportable = _issues.Values.Select(issue => FilterReportedNewMarkets(issue, newMarkets))
        .Where(issue => (issue.FailedRequests > 0 || issue.UnavailableAssets > 0 ||
          issue.GlobalFeature != null || issue.UnknownMetric) &&
          !IsVerifiedFlatPath(issue) && !IsVerifiedHistoricalFlatPath(issue, deltas))
        .OrderBy(i => i.Trend, StringComparer.OrdinalIgnoreCase)
        .ThenBy(i => i.Metric, StringComparer.OrdinalIgnoreCase).ToArray();
      if (reportable.Length == 0 && String.IsNullOrWhiteSpace(_globalDecision))
        return Array.Empty<string>();

      int count = reportable.Length + (String.IsNullOrWhiteSpace(_globalDecision) ? 0 : 1);
      List<string> lines = new List<string> { "+ Metric warnings: " + count };
      foreach (Issue issue in reportable)
      {
        lines.Add("+   " + Explain(issue));
      }

      if (!String.IsNullOrWhiteSpace(_globalDecision))
      {
        lines.Add("+   " + ExplainGlobalDecision(_globalDecision));
      }

      return lines;
    }

    private static Issue FilterReportedNewMarkets(Issue original, NewMarketHistoryClassification newMarkets)
    {
      if (newMarkets == null || original.ReportedCauses.Count != original.FailedRequests)
        return original;
      var retained = original.ReportedCauses.Where(cause => !newMarkets.IsReportedNewMarket(cause.Market)).ToArray();
      if (retained.Length == original.FailedRequests) return original;
      var filtered = new Issue
      {
        Trend = original.Trend, Metric = original.Metric,
        FailedRequests = retained.Length, GlobalFeature = original.GlobalFeature,
        UnknownMetric = original.UnknownMetric, UnavailableAssets = original.UnavailableAssets,
        EligibleAssets = original.EligibleAssets, OtherAssetFailures = original.OtherAssetFailures
      };
      filtered.AssetFailures.AddRange(original.AssetFailures);
      foreach (var cause in retained)
      {
        filtered.ReportedCauses.Add(cause);
        if (!String.IsNullOrWhiteSpace(cause.Market)) filtered.Markets.Add(cause.Market);
        var caller = cause.Caller;
        if (caller != null)
        {
          if (!(caller.Source.Equals("SMS activation", StringComparison.OrdinalIgnoreCase) ||
                caller.Source.Equals("SMS OffTrigger", StringComparison.OrdinalIgnoreCase)))
            filtered.HasNonSmsCaller = true;
          if (filtered.Callers.Count < MaxCallerExamples &&
              !filtered.Callers.Any(existing => SameCaller(existing, caller)))
            filtered.Callers.Add(caller);
        }
        if (filtered.FailureReasons.Count < MaxAssetReasonExamples &&
            !filtered.FailureReasons.Contains(cause.Reason, StringComparer.OrdinalIgnoreCase))
          filtered.FailureReasons.Add(cause.Reason);
      }
      return filtered;
    }

    // This is intentionally narrow: only the *exact* mathematical 0/0
    // result from valid, nonstale asset price candles can become informational.
    // Any other unavailable reason, mixed cause, missing snapshot, global
    // failure or stale metric remains a normal WARN.
    private static bool IsVerifiedFlatPath(Issue issue)
    {
      if (issue == null ||
          !String.Equals(issue.Metric, MarketTrendMetrics.PricePathEfficiency, StringComparison.OrdinalIgnoreCase) ||
          issue.UnknownMetric || issue.GlobalFeature != null ||
          issue.UnavailableAssets <= 0 || issue.EligibleAssets <= 0 ||
          issue.OtherAssetFailures != 0 || issue.AssetFailures.Count == 0 ||
          issue.AssetFailures.Sum(failure => failure.Count) != issue.UnavailableAssets ||
          issue.AssetFailures.Any(failure => failure.Stale ||
            !String.Equals(Clean(failure.Reason).TrimEnd('.'),
              PricePathEfficiencyEngine.FlatPathUnavailableReason.TrimEnd('.'),
              StringComparison.OrdinalIgnoreCase)) ||
          issue.FailedRequests != issue.ReportedCauses.Count ||
          issue.ReportedCauses.Any(cause => String.IsNullOrWhiteSpace(cause.Market) ||
            !Contains(cause.Reason, PricePathEfficiencyEngine.FlatPathUnavailableReason)) ||
          issue.HasNonSmsCaller)
        return false;
      return true;
    }

    private static bool IsVerifiedHistoricalFlatPath(Issue issue,
      IReadOnlyDictionary<string, MarketTrendDeltaAnalysisSnapshot> deltas)
    {
      if (issue.UnknownMetric || issue.GlobalFeature != null || issue.HasNonSmsCaller ||
          issue.UnavailableAssets != 0 || issue.FailedRequests == 0 ||
          !String.Equals(issue.Metric, MarketTrendMetrics.PricePathEfficiency, StringComparison.OrdinalIgnoreCase) ||
          deltas == null || !deltas.TryGetValue(issue.Trend, out var delta))
        return false;
      return issue.ReportedCauses.All(cause =>
        MarketTrendDeltaSelection.IsDelta(cause.Relation) &&
        !String.IsNullOrWhiteSpace(cause.Market) &&
        Contains(cause.Reason, PricePathEfficiencyEngine.FlatPathUnavailableReason) &&
        delta.TryGet(issue.Metric, cause.Market, out var observation) &&
        observation?.Delta != null && !observation.Delta.IsValid && !observation.Delta.IsStale &&
        IsUsable(observation.Current) && observation.Previous != null &&
        !observation.Previous.IsValid && !observation.Previous.IsStale &&
        String.Equals(observation.Previous.InvalidReason, PricePathEfficiencyEngine.FlatPathUnavailableReason,
          StringComparison.OrdinalIgnoreCase));
    }

    // Call after BuildLines(analyses), which joins exact runtime snapshots
    // with recorded trigger requests. This changes only log classification.
    public IReadOnlyList<string> BuildFlatPathInfoLines(
      IReadOnlyDictionary<string, MarketTrendDeltaAnalysisSnapshot> deltas = null)
    {
      return _issues.Values
        .Where(issue => IsVerifiedFlatPath(issue) || IsVerifiedHistoricalFlatPath(issue, deltas))
        .OrderBy(issue => issue.Trend, StringComparer.OrdinalIgnoreCase)
        .ThenBy(issue => issue.Metric, StringComparer.OrdinalIgnoreCase)
        .Select(issue => "+ " + issue.Trend + " " + issue.Metric + ": " +
          (issue.UnavailableAssets > 0 ? issue.UnavailableAssets + "/" + issue.EligibleAssets
            : issue.Markets.Count + " historical") +
          " unavailable (flat price paths" +
          (issue.FailedRequests > 0 ? "; " + issue.FailedRequests + " SMS trigger evaluations" : "") + ").")
        .ToArray();
    }

    /// <summary>
    /// One concise INFO line per raid naming every verified new market whose
    /// pre-listing history makes one or more metrics unavailable.
    /// The underlying source-validity and exclusion decisions are unchanged.
    /// </summary>
    public IReadOnlyList<string> BuildNewMarketInfoLines(NewMarketHistoryClassification classification)
    {
      IReadOnlyDictionary<string, IReadOnlyCollection<string>> affected =
        (classification ?? new NewMarketHistoryClassification()).Affected;
      if (affected.Count == 0) return Array.Empty<string>();
      return new[] {
        "+ New markets with insufficient history, excluded from some metrics: " +
        String.Join(", ", affected.Keys.OrderBy(symbol => symbol, StringComparer.OrdinalIgnoreCase))
      };
    }

    /// <summary>
    /// Group verified inactivity at DEBUG without repeating a warning for
    /// each unavailable taker metric or SMS trigger on the same asset.
    /// </summary>
    public IReadOnlyList<string> BuildNoTradeDebugLines(
      IReadOnlyDictionary<string, MarketTrendAnalysisSnapshot> analyses)
    {
      var lines = new List<string>();
      if (analyses == null) return lines;
      foreach (MarketTrendAnalysisSnapshot analysis in analyses.Values
        .Where(item => item != null)
        .OrderBy(item => item.MarketTrendName, StringComparer.OrdinalIgnoreCase))
      {
        string[] inactive = analysis.AssetSymbols.Where(symbol =>
          analysis.AssetMetrics.TryGetValue(symbol, out var metrics) && metrics != null &&
          metrics.Values.Any(feature => feature?.IsNoTrades == true && !feature.IsStale))
          .Distinct(StringComparer.OrdinalIgnoreCase)
          .OrderBy(symbol => symbol, StringComparer.OrdinalIgnoreCase)
          .ToArray();
        if (inactive.Length == 0) continue;
        lines.Add("MarketTrend '" + analysis.MarketTrendName + "': " + inactive.Length +
          " verified NO_TRADES markets; per-asset taker ratios unavailable." +
          " Examples: " + String.Join(", ", inactive.Take(5)) +
          (inactive.Length > 5 ? ", ..." : "") + ".");
      }
      return lines;
    }

    private static string Explain(Issue issue)
    {
      Caller caller = issue.FailedRequests == 1 ? issue.Callers.FirstOrDefault() : null;
      string subject = caller == null ? "MarketTrend '" + issue.Trend + "'"
        : DescribeCaller(caller);
      string target = issue.Metric;
      if (caller != null && !String.IsNullOrWhiteSpace(caller.Market))
      {
        target = caller.Market + " " + target;
      }

      string reason = issue.FailureReasons.FirstOrDefault() ?? "";
      string assetReason = issue.AssetFailures.FirstOrDefault()?.Reason ?? "";
      string dataReason = !String.IsNullOrWhiteSpace(assetReason) ? assetReason : reason;
      string message;

      if (issue.UnknownMetric)
      {
        message = "MarketTrend '" + issue.Trend + "' lists unknown metric '" + issue.Metric +
          "'. Correct its name in Metrics or remove it.";
      }
      else if (Contains(reason, "outside the selected asset population"))
      {
        string filter = Contains(reason, "excluded by IgnoredMarkets") ? "IgnoredMarkets"
          : Contains(reason, "not in AllowedMarkets") ? "AllowedMarkets" : "";
        string explanation = filter == "IgnoredMarkets"
          ? "it is excluded by the MarketTrend's IgnoredMarkets list"
          : filter == "AllowedMarkets"
            ? "it is not included in the MarketTrend's AllowedMarkets list"
            : "it is outside the MarketTrend's selected asset population";
        string action = filter == "IgnoredMarkets"
          ? "Remove the market from IgnoredMarkets if this trigger should evaluate it."
          : filter == "AllowedMarkets"
            ? "Add the market to AllowedMarkets if this trigger should evaluate it."
            : "Check the MarketTrend's market filters and exchange eligibility.";
        message = subject + " cannot evaluate " + target + " from '" + issue.Trend +
          "' because " + explanation + ". " + action;
      }
      else if (Contains(reason, "requires a market-specific context") ||
        Contains(reason, "asset-only metric used globally"))
      {
        string alternative = issue.Metric == MarketTrendMetrics.ATRP
          ? " Consider MedianATRP if a market-wide volatility measurement is intended." : "";
        message = subject + " cannot evaluate " + issue.Metric + " from '" + issue.Trend +
          "' because this metric is available only for individual markets, not a GlobalSetting." + alternative;
      }
      else if (Contains(reason, "does not enable metric"))
      {
        string action = issue.Metric == MarketTrendMetrics.PriceTrend
          ? " Add PriceTrend to the MarketTrend's Metrics, or leave the trigger's Metric blank to use implicit PriceTrend."
          : " Add " + issue.Metric + " to the MarketTrend's Metrics if this trigger needs it.";
        message = subject + " explicitly requests " + target + " from '" + issue.Trend +
          "', but that metric is not enabled in the MarketTrend's Metrics." + action;
      }
      else if (TryDescribeHistory(dataReason, out string history))
      {
        string affected = issue.EligibleAssets > 0 && issue.UnavailableAssets > 0
          ? " for " + issue.UnavailableAssets + " of its " + issue.EligibleAssets + " markets" : "";
        message = "MarketTrend '" + issue.Trend + "' cannot calculate " + issue.Metric + affected +
          " because " + history + ". Check StoreDataMaxHours and whether the exchange supplied the required candles.";
      }
      else if (issue.GlobalFeature != null && !IsUsable(issue.GlobalFeature))
      {
        FeatureValueSnapshot feature = issue.GlobalFeature;
        int available = feature.SampleCount;
        int? eligible = feature.EligibleSampleCount;

        // Runtime snapshots provide structured coverage counts. Some callers and
        // older snapshots provide only the equivalent InvalidReason text. Use
        // that detail only when the structured eligible count is unavailable.
        if (!eligible.HasValue || eligible.Value <= 0)
        {
          Match coverage = Regex.Match(feature.InvalidReason ?? "",
            @"Insufficient market coverage:\s*(?<available>\d+)\s*/\s*(?<eligible>\d+)\s+valid\b",
            RegexOptions.IgnoreCase);
          if (coverage.Success
            && Int32.TryParse(coverage.Groups["available"].Value, NumberStyles.None,
              CultureInfo.InvariantCulture, out int parsedAvailable)
            && Int32.TryParse(coverage.Groups["eligible"].Value, NumberStyles.None,
              CultureInfo.InvariantCulture, out int parsedEligible)
            && parsedEligible > 0 && parsedAvailable <= parsedEligible)
          {
            available = parsedAvailable;
            eligible = parsedEligible;
          }
        }

        Match required = Regex.Match(feature.InvalidReason ?? "",
          @"(?:at least \d+% \((?<minimum>\d+) markets\)|(?<minimum2>\d+) required)",
          RegexOptions.IgnoreCase);
        string requirement = required.Success
          ? "; at least " + (required.Groups["minimum"].Success ? required.Groups["minimum"].Value : required.Groups["minimum2"].Value) + " are required"
          : "";
        if (eligible.HasValue && eligible.Value > 0)
        {
          message = "MarketTrend '" + issue.Trend + "' cannot calculate " + issue.Metric +
            " because only " + available + " of " + eligible.Value +
            " markets have valid data" + requirement + "." +
            (!Contains(feature.InvalidReason, "Insufficient market coverage")
              && !String.IsNullOrWhiteSpace(feature.InvalidReason)
                ? " Reason: " + NaturalReason(feature.InvalidReason) + "." : "") +
            " Check the underlying metric history and missing or stale candles.";
        }
        else
        {
          message = "MarketTrend '" + issue.Trend + "' cannot calculate " + issue.Metric +
            " because " + NaturalReason(feature.InvalidReason) +
            ". Check the available candles and metric requirements.";
        }
      }
      else if (issue.UnavailableAssets > 0)
      {
        message = "MarketTrend '" + issue.Trend + "' cannot calculate " + issue.Metric + " for " +
          issue.UnavailableAssets + " of its " + issue.EligibleAssets + " markets. " +
          String.Join(" ", issue.AssetFailures.Select(item =>
            item.ExampleMarket + ": " + NaturalReason(item.Reason) + " (" + item.Count + " affected).")) +
          (issue.OtherAssetFailures > 0 ? " More markets have other missing-data reasons." : "") +
          " Check candle availability and timestamps.";
      }
      else if (Contains(reason, "No normalized analysis snapshot"))
      {
        message = subject + " cannot evaluate " + target + " because '" + issue.Trend +
          "' has no normalized analysis snapshot. Check whether the MarketTrend was evaluated in this raid.";
      }
      else if (Contains(reason, "not published") || Contains(reason, "no calculated"))
      {
        message = subject + " cannot evaluate " + target + " from '" + issue.Trend +
          "' because its calculated value was not published. Check the requested metric, market filters and available candles.";
      }
      else
      {
        message = subject + " cannot evaluate " + target + " from '" + issue.Trend + "' because " +
          NaturalReason(reason) + ". Check the referenced MarketTrend and its data requirements.";
      }

      // Preserve the identity of each affected setting/trigger without repeating
      // the underlying resolver error on multiple long log lines.
      if (issue.FailedRequests > 1)
      {
        string examples = String.Join("; ", issue.Callers.Select(DescribeCaller));
        int hidden = Math.Max(0, issue.FailedRequests - issue.Callers.Count);
        message += " Affected trigger requests: " + issue.FailedRequests + " (" + examples +
          (hidden > 0 ? "; " + hidden + " more" : "") + ").";
      }

      // A single group can involve distinct causes. Never silently discard
      // the second cause merely because it shares a MarketTrend and metric.
      if (issue.FailureReasons.Count > 1)
      {
        string first = Classify(issue.FailureReasons[0]);
        string[] secondary = issue.FailureReasons.Skip(1).Select(Classify)
          .Where(item => !String.Equals(item, first, StringComparison.OrdinalIgnoreCase))
          .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        if (secondary.Length > 0)
        {
          message += " Other reported cause" + (secondary.Length > 1 ? "s" : "") +
            ": " + String.Join("; ", secondary) + ".";
        }
      }

      return message;
    }

    private static bool TryDescribeHistory(string reason, out string explanation)
    {
      explanation = "";
      Match matched = Regex.Match(reason ?? "",
        @"Insufficient complete (?<minutes>\d+)m [^:]*?:\s*(?<available>\d+)\s*/\s*(?<required>\d+)",
        RegexOptions.IgnoreCase);
      if (!matched.Success)
      {
        return false;
      }
      int minutes = Int32.Parse(matched.Groups["minutes"].Value, CultureInfo.InvariantCulture);
      string unit = minutes % 60 == 0 ? (minutes / 60).ToString(CultureInfo.InvariantCulture) + "-hour"
        : minutes.ToString(CultureInfo.InvariantCulture) + "-minute";
      explanation = "only " + matched.Groups["available"].Value + " of " +
        matched.Groups["required"].Value + " required " + unit + " periods are available";
      return true;
    }

    private static string DescribeCaller(Caller caller)
    {
      if (caller == null) return "an unspecified trigger";
      string setting = caller.Source.StartsWith("SMS", StringComparison.OrdinalIgnoreCase)
        ? "SMS '" + caller.Setting + "'" : caller.Source == "GlobalSetting"
          ? "GlobalSetting '" + caller.Setting + "'" : "Setting '" + caller.Setting + "'";
      string stage = caller.Source.Equals("SMS activation", StringComparison.OrdinalIgnoreCase)
        ? "activation" : caller.Source.Equals("SMS OffTrigger", StringComparison.OrdinalIgnoreCase)
          ? "OffTrigger" : "trigger";
      string id = caller.Trigger;
      if (id.StartsWith("tag=", StringComparison.OrdinalIgnoreCase)) id = id.Substring(4);
      string description = setting;
      if (caller.Source.Equals("SMS OffTrigger", StringComparison.OrdinalIgnoreCase)
        || caller.Source.Equals("SMS activation", StringComparison.OrdinalIgnoreCase))
      {
        description += " (" + stage + (String.IsNullOrWhiteSpace(id) ? "" : " " + id) + ")";
      }
      else if (!String.IsNullOrWhiteSpace(id) && id != "(untagged)")
      {
        description += " (trigger " + id + ")";
      }
      if (!String.IsNullOrWhiteSpace(caller.Market)) description += " for " + caller.Market;
      if (!String.IsNullOrWhiteSpace(caller.Relation)) description += " [" + caller.Relation + "]";
      if (caller.ImplicitPriceTrend) description += " [implicit PriceTrend]";
      return description;
    }

    private static string ExplainGlobalDecision(string decision)
    {
      Match retained = Regex.Match(decision,
        @"selection indeterminate;\s*retained\s*'(?<setting>[^']+)'", RegexOptions.IgnoreCase);
      if (retained.Success)
      {
        return "GlobalSetting selection was inconclusive because one or more required metrics were unavailable. " +
          "PTMagic retained '" + retained.Groups["setting"].Value + "' as the active setting.";
      }
      if (Contains(decision, "prior setting absent"))
      {
        return "GlobalSetting selection was inconclusive because required metrics were unavailable. " +
          "The previous setting could not be found, so the default setting was used.";
      }
      return "GlobalSetting decision: " + NaturalReason(decision) + ".";
    }

    private static string Classify(string reason)
    {
      if (Contains(reason, "outside the selected asset population")) return "the requested market is excluded from the trend";
      if (Contains(reason, "does not enable metric")) return "the metric is not enabled in the MarketTrend";
      if (Contains(reason, "requires a market-specific context") ||
        Contains(reason, "asset-only metric used globally")) return "the metric can only be used for individual markets";
      if (TryDescribeHistory(reason, out string explanation)) return explanation;
      if (Contains(reason, "No exact closed-candle endpoint")) return "the required closed candle was missing";
      if (Contains(reason, "Insufficient market coverage")) return "insufficient valid market coverage";
      return NaturalReason(reason);
    }

    private static string NaturalReason(string reason)
    {
      string text = Clean(reason);
      if (String.IsNullOrWhiteSpace(text)) return "the value is unavailable";
      if (TryDescribeHistory(text, out string explanation)) return explanation;
      int structured = text.IndexOf(" (MarketTrend=", StringComparison.OrdinalIgnoreCase);
      if (structured >= 0) text = text.Substring(0, structured);
      if (Contains(text, "No exact closed-candle endpoint"))
      {
        return "the required closed candle is missing";
      }
      if (Contains(text, "Metric '") && Contains(text, "' is unavailable for MarketTrend"))
      {
        int explanationStart = text.IndexOf("; reason=", StringComparison.OrdinalIgnoreCase);
        if (explanationStart >= 0) return Clean(text.Substring(explanationStart + 9)).TrimEnd('.');
      }
      return text.TrimEnd('.');
    }

    private static bool SameCaller(Caller a, Caller b)
    {
      return String.Equals(a.Source, b.Source, StringComparison.OrdinalIgnoreCase) &&
        String.Equals(a.Setting, b.Setting, StringComparison.OrdinalIgnoreCase) &&
        String.Equals(a.Market, b.Market, StringComparison.OrdinalIgnoreCase) &&
        String.Equals(a.Trigger, b.Trigger, StringComparison.OrdinalIgnoreCase) &&
        String.Equals(a.Relation, b.Relation, StringComparison.OrdinalIgnoreCase);
    }

    private Issue GetIssue(string trend, string metric)
    {
      string key = trend + "|" + metric;
      if (!_issues.TryGetValue(key, out Issue issue))
      {
        issue = new Issue { Trend = trend, Metric = metric };
        _issues[key] = issue;
      }
      return issue;
    }

    private static bool IsUsable(FeatureValueSnapshot value)
    {
      return value != null && value.IsValid && !value.IsStale
        && value.Value.HasValue && double.IsFinite(value.Value.Value);
    }

    private static bool Contains(string text, string target)
    {
      return !String.IsNullOrWhiteSpace(text) &&
        text.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string Name(string name, string fallback)
    {
      return String.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
    }

    private static string Clean(string text)
    {
      return String.IsNullOrWhiteSpace(text) ? "" : text.Trim().Replace('\r', ' ').Replace('\n', ' ');
    }
  }
}
