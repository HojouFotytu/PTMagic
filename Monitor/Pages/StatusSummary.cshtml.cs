using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Core.Main;
using Core.Helper;
using Core.Main.DataObjects;
using Core.Main.DataObjects.PTMagicData;
using Core.MarketAnalyzer.MarketData;

namespace Monitor.Pages
{
  public class StatusSummaryModel : _Internal.BasePageModelSecure
  {
    public List<string> MarketsWithSingleSettings = new List<string>();
    public string SettingsDistribution24hChartDataJSON = "";
    public string SettingsDistribution3dChartDataJSON = "";
    private Dictionary<string, string> settingsChartColors = new Dictionary<string, string>();
    public ProfitTrailerData PTData = null;
    public MiscData MiscData { get; set; }

    public void OnGet()
    {
      base.Init();

      BindData();
    }

    private void BindData()
    {
      PTData = this.PtDataObject;
      MiscData = this.PTData.Misc;
      BuildMarketsWithSingleSettings();
      BuildChartColors();
      Build24hChartData();
      Build3dChartData();
    }

    public sealed class GlobalSettingLogMetricEntry
    {
      public string Text { get; set; } = "";
      public bool TriggerConditionTrue { get; set; } = false;
      public string Tooltip { get; set; } = "";
    }

    public List<GlobalSettingLogMetricEntry> GetGlobalSettingLogMetrics(GlobalSettingSummary summary)
    {
      List<GlobalSettingLogMetricEntry> entries = new List<GlobalSettingLogMetricEntry>();
      if (summary == null)
      {
        return entries;
      }

      List<GlobalSettingTriggerSummary> triggerDetails = (summary.TriggerDetails
        ?? new List<GlobalSettingTriggerSummary>())
        .Where(trigger => trigger != null)
        .ToList();
      List<GlobalSettingMetricSummary> metricSnapshots = (summary.MarketMetricSnapshots
        ?? new List<GlobalSettingMetricSummary>())
        .Where(metric => metric != null)
        .ToList();

      bool IsPriceTrend(string metric)
      {
        return String.Equals(MarketTrendMetrics.Normalize(metric),
          MarketTrendMetrics.PriceTrend, StringComparison.OrdinalIgnoreCase);
      }

      bool IsExplicitMetricTrigger(GlobalSettingTriggerSummary trigger)
      {
        return trigger.MetricWasExplicit
          || (!String.IsNullOrWhiteSpace(trigger.Metric) && !IsPriceTrend(trigger.Metric));
      }

      string MetricKey(string trendName, string metricName)
      {
        return (trendName ?? "").Trim().ToUpperInvariant() + "\u001f"
          + MarketTrendMetrics.Normalize(metricName).ToUpperInvariant();
      }

      bool hasExplicitMetricReferences = triggerDetails.Any(IsExplicitMetricTrigger)
        || metricSnapshots.Any(metric => metric.UsedInTriggerSet && !IsPriceTrend(metric.Metric));

      List<GlobalSettingTriggerSummary> explicitMetricTriggers = triggerDetails
        .Where(IsExplicitMetricTrigger)
        .ToList();
      List<GlobalSettingTriggerSummary> legacyTrendTriggers = triggerDetails
        .Where(trigger => !IsExplicitMetricTrigger(trigger)
          && !String.IsNullOrWhiteSpace(trigger.MarketTrendName))
        .ToList();

      HashSet<string> explicitPriceTrendNames = new HashSet<string>(
        explicitMetricTriggers
          .Where(trigger => IsPriceTrend(trigger.Metric))
          .Select(trigger => trigger.MarketTrendName),
        StringComparer.OrdinalIgnoreCase);

      HashSet<string> renderedMetricKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (GlobalSettingMetricSummary metric in metricSnapshots.Where(metric =>
        metric.UsedInTriggerSet
        && (!IsPriceTrend(metric.Metric)
          || explicitMetricTriggers.Any(trigger =>
            String.Equals(trigger.MarketTrendName, metric.MarketTrendName, StringComparison.OrdinalIgnoreCase)
            && IsPriceTrend(trigger.Metric)))))
      {
        string metricName = Core.Helper.SystemHelper.SplitCamelCase(metric.MarketTrendName)
          + " / " + Core.Helper.SystemHelper.SplitCamelCase(MarketTrendMetrics.Normalize(metric.Metric));
        string metricValue = metric.IsAssetMetric ? "" :
          metric.Value.HasValue
            ? ": " + metric.Value.Value.ToString("#,#0.00", System.Globalization.CultureInfo.InvariantCulture)
              + (String.IsNullOrWhiteSpace(metric.Unit) ? MarketTrendMetricTriggerResolver.GetUnit(metric.Metric) : metric.Unit)
            : ": UNAVAILABLE";

        entries.Add(new GlobalSettingLogMetricEntry
        {
          Text = metricName + metricValue,
          TriggerConditionTrue = metric.TriggerConditionTrue,
          Tooltip = metric.IsAssetMetric ? "Asset-level metric; no market-wide value" : metric.UnavailableReason
        });
        renderedMetricKeys.Add(MetricKey(metric.MarketTrendName, metric.Metric));
      }

      // Trigger details are sufficient to show explicitly configured metrics
      // for entries created before metric snapshots were persisted.
      foreach (IGrouping<string, GlobalSettingTriggerSummary> group in explicitMetricTriggers
        .GroupBy(trigger => MetricKey(trigger.MarketTrendName, trigger.Metric), StringComparer.OrdinalIgnoreCase))
      {
        if (renderedMetricKeys.Contains(group.Key))
        {
          continue;
        }

        GlobalSettingTriggerSummary detail = group.FirstOrDefault(trigger => trigger.Value.HasValue)
          ?? group.First();
        string unit = String.IsNullOrWhiteSpace(detail.Unit)
          ? MarketTrendMetricTriggerResolver.GetUnit(detail.Metric)
          : detail.Unit;
        string value = detail.Value.HasValue
          ? ": " + detail.Value.Value.ToString("#,#0.00", System.Globalization.CultureInfo.InvariantCulture) + unit
          : ": UNAVAILABLE";

        entries.Add(new GlobalSettingLogMetricEntry
        {
          Text = Core.Helper.SystemHelper.SplitCamelCase(detail.MarketTrendName)
            + " / " + Core.Helper.SystemHelper.SplitCamelCase(MarketTrendMetrics.Normalize(detail.Metric)) + value,
          TriggerConditionTrue = group.Any(trigger =>
            String.Equals(trigger.Result, "TRUE", StringComparison.OrdinalIgnoreCase)),
          Tooltip = detail.UnavailableReason
        });
      }

      // A legacy trend is included in metric mode only when that same activation
      // contains a legacy (implicit PriceTrend) trigger for it.
      IEnumerable<string> legacyTrendNames;
      if (triggerDetails.Count == 0 && !hasExplicitMetricReferences)
      {
        // Pre-metric history has no trigger details; MarketTrendChanges was
        // already scoped to the activated setting by the legacy writer.
        legacyTrendNames = (summary.MarketTrendChanges ?? new Dictionary<string, MarketTrendChange>()).Keys;
      }
      else
      {
        legacyTrendNames = legacyTrendTriggers
          .Where(trigger => !explicitPriceTrendNames.Contains(trigger.MarketTrendName))
          .Select(trigger => trigger.MarketTrendName)
          .Distinct(StringComparer.OrdinalIgnoreCase);
      }

      Dictionary<string, MarketTrendChange> legacyChanges = summary.MarketTrendChanges
        ?? new Dictionary<string, MarketTrendChange>();
      foreach (string trendName in legacyTrendNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
      {
        KeyValuePair<string, MarketTrendChange> changePair = legacyChanges.FirstOrDefault(pair =>
          String.Equals(pair.Key, trendName, StringComparison.OrdinalIgnoreCase));
        List<GlobalSettingTriggerSummary> matchingTriggers = legacyTrendTriggers
          .Where(trigger => String.Equals(trigger.MarketTrendName, trendName, StringComparison.OrdinalIgnoreCase))
          .ToList();
        bool wasTrue = matchingTriggers.Any(trigger =>
          String.Equals(trigger.Result, "TRUE", StringComparison.OrdinalIgnoreCase));

        if (changePair.Value != null)
        {
          entries.Add(new GlobalSettingLogMetricEntry
          {
            Text = Core.Helper.SystemHelper.SplitCamelCase(trendName) + ": "
              + changePair.Value.TrendChange.ToString("#,#0.00", System.Globalization.CultureInfo.InvariantCulture) + "%",
            TriggerConditionTrue = wasTrue,
            Tooltip = "Legacy MarketTrend value recorded at activation."
          });
        }
        else
        {
          GlobalSettingTriggerSummary detail = matchingTriggers.FirstOrDefault(trigger => trigger.Value.HasValue)
            ?? matchingTriggers.FirstOrDefault();
          if (detail != null)
          {
            string value = detail.Value.HasValue
              ? ": " + detail.Value.Value.ToString("#,#0.00", System.Globalization.CultureInfo.InvariantCulture)
                + (String.IsNullOrWhiteSpace(detail.Unit) ? "%" : detail.Unit)
              : ": UNAVAILABLE";
            entries.Add(new GlobalSettingLogMetricEntry
            {
              Text = Core.Helper.SystemHelper.SplitCamelCase(trendName) + value,
              TriggerConditionTrue = wasTrue,
              Tooltip = detail.UnavailableReason
            });
          }
        }
      }

      return entries;
    }

    private void BuildMarketsWithSingleSettings()
    {
      // Get markets with active single settings
      foreach (string key in Summary.MarketSummary.Keys)
      {
        if (Summary.MarketSummary[key].ActiveSingleSettings != null)
        {
          if (Summary.MarketSummary[key].ActiveSingleSettings.Count > 0)
          {
            MarketsWithSingleSettings.Add(key);
          }
        }
      }
      MarketsWithSingleSettings.Sort();
    }

    private void BuildChartColors()
    {
      int settingIndex = 0;
      foreach (GlobalSetting globalSetting in PTMagicConfiguration.AnalyzerSettings.GlobalSettings)
      {
        string chartColor = "";
        if (settingIndex < Constants.ChartLineColors.Length)
        {
          chartColor = Constants.ChartLineColors[settingIndex];
        }
        else
        {
          chartColor = Constants.ChartLineColors[settingIndex - 20];
        }

        settingsChartColors.Add(globalSetting.SettingName, chartColor);

        settingIndex++;
      }
    }

    private void Build24hChartData()
    {
      if (Summary.GlobalSettingSummary.Count > 0)
      {
        DateTime dateTime24hAgo = DateTime.UtcNow.AddHours(-24);
        List<GlobalSettingSummary> gsSummaries24h = Summary.GlobalSettingSummary.FindAll(gss => gss.SwitchDateTime >= dateTime24hAgo);
        IEnumerable<GlobalSettingSummary> gsNames24h = gsSummaries24h.GroupBy(gss => gss.SettingName).Select(group => group.First());

        if (Summary.GlobalSettingSummary.FindAll(gss => gss.SwitchDateTime <= dateTime24hAgo).Count > 0)
        {
          GlobalSettingSummary gsBefore24h = Summary.GlobalSettingSummary.FindAll(gss => gss.SwitchDateTime <= dateTime24hAgo).OrderByDescending(gss => gss.SwitchDateTime).First();
          if (gsBefore24h != null)
          {
            DateTime gsSwitchedOffDateTime = gsBefore24h.SwitchDateTime.AddSeconds(gsBefore24h.ActiveSeconds);
            if (gsSwitchedOffDateTime > dateTime24hAgo)
            {
              gsBefore24h.ActiveSeconds = (int)Math.Floor(gsSwitchedOffDateTime.Subtract(dateTime24hAgo).TotalSeconds);

              gsSummaries24h.Add(gsBefore24h);

              if (gsNames24h.Select(gss => gss.SettingName.Equals(gsBefore24h.SettingName)) == null)
              {
                gsNames24h.Append(gsBefore24h);
              }
            }
          }
        }

        if (gsNames24h.Count() > 0)
        {
          SettingsDistribution24hChartDataJSON = "[";
          int gssIndex = 0;
          double totalCoveredSeconds = gsSummaries24h.Sum(gs => gs.ActiveSeconds);
          foreach (GlobalSettingSummary gss in gsNames24h)
          {
            string lineColor = "";
            if (settingsChartColors.ContainsKey(gss.SettingName))
            {
              lineColor = settingsChartColors[gss.SettingName];
            }
            else
            {
              if (gssIndex < Constants.ChartLineColors.Length)
              {
                lineColor = Constants.ChartLineColors[gssIndex];
              }
              else
              {
                lineColor = Constants.ChartLineColors[gssIndex - 20];
              }
            }

            if (!SettingsDistribution24hChartDataJSON.Equals("[")) SettingsDistribution24hChartDataJSON += ",";

            double gsActiveSeconds = gsSummaries24h.FindAll(gs => gs.SettingName.Equals(gss.SettingName)).Sum(gs => gs.ActiveSeconds);
            double chartValue = gsActiveSeconds / totalCoveredSeconds * 100;

            SettingsDistribution24hChartDataJSON += "{";
            SettingsDistribution24hChartDataJSON += "label: '" + SystemHelper.SplitCamelCase(gss.SettingName) + "',";
            SettingsDistribution24hChartDataJSON += "color: '" + lineColor + "',";
            SettingsDistribution24hChartDataJSON += "value: " + chartValue.ToString("0.00", new System.Globalization.CultureInfo("en-US")) + "";
            SettingsDistribution24hChartDataJSON += "}";

            gssIndex++;
          }
          SettingsDistribution24hChartDataJSON += "]";
        }
      }
    }

    private void Build3dChartData()
    {
      if (Summary.GlobalSettingSummary.Count > 0)
      {
        DateTime dateTime3dAgo = DateTime.UtcNow.AddHours(-72);
        List<GlobalSettingSummary> gsSummaries3d = Summary.GlobalSettingSummary.FindAll(gss => gss.SwitchDateTime >= dateTime3dAgo);
        IEnumerable<GlobalSettingSummary> gsNames3d = gsSummaries3d.GroupBy(gss => gss.SettingName).Select(group => group.First());

        if (Summary.GlobalSettingSummary.FindAll(gss => gss.SwitchDateTime <= dateTime3dAgo).Count > 0)
        {
          GlobalSettingSummary gsBefore3d = Summary.GlobalSettingSummary.FindAll(gss => gss.SwitchDateTime <= dateTime3dAgo).OrderByDescending(gss => gss.SwitchDateTime).First();
          if (gsBefore3d != null)
          {
            DateTime gsSwitchedOffDateTime = gsBefore3d.SwitchDateTime.AddSeconds(gsBefore3d.ActiveSeconds);
            if (gsSwitchedOffDateTime > dateTime3dAgo)
            {
              gsBefore3d.ActiveSeconds = (int)Math.Floor(gsSwitchedOffDateTime.Subtract(dateTime3dAgo).TotalSeconds);

              gsSummaries3d.Add(gsBefore3d);

              if (gsNames3d.Select(gss => gss.SettingName.Equals(gsBefore3d.SettingName)) == null)
              {
                gsNames3d.Append(gsBefore3d);
              }
            }
          }
        }

        if (gsNames3d.Count() > 0)
        {
          SettingsDistribution3dChartDataJSON = "[";
          int gssIndex = 0;
          double totalCoveredSeconds = gsSummaries3d.Sum(gs => gs.ActiveSeconds);
          foreach (GlobalSettingSummary gss in gsNames3d)
          {
            string lineColor = "";
            if (settingsChartColors.ContainsKey(gss.SettingName))
            {
              lineColor = settingsChartColors[gss.SettingName];
            }
            else
            {
              if (gssIndex < Constants.ChartLineColors.Length)
              {
                lineColor = Constants.ChartLineColors[gssIndex];
              }
              else
              {
                lineColor = Constants.ChartLineColors[gssIndex - 20];
              }
            }

            if (!SettingsDistribution3dChartDataJSON.Equals("[")) SettingsDistribution3dChartDataJSON += ",";

            double gsActiveSeconds = gsSummaries3d.FindAll(gs => gs.SettingName.Equals(gss.SettingName)).Sum(gs => gs.ActiveSeconds);
            double chartValue = gsActiveSeconds / totalCoveredSeconds * 100;

            SettingsDistribution3dChartDataJSON += "{";
            SettingsDistribution3dChartDataJSON += "label: '" + SystemHelper.SplitCamelCase(gss.SettingName) + "',";
            SettingsDistribution3dChartDataJSON += "color: '" + lineColor + "',";
            SettingsDistribution3dChartDataJSON += "value: " + chartValue.ToString("0.00", new System.Globalization.CultureInfo("en-US")) + "";
            SettingsDistribution3dChartDataJSON += "}";

            gssIndex++;
          }
          SettingsDistribution3dChartDataJSON += "]";
        }
      }
    }
  }
}
