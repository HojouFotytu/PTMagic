using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Core.Main;
using Core.MarketAnalyzer.MarketData;
using Newtonsoft.Json;

namespace Monitor.Pages
{
  public class MarketMetricsModel : _Internal.BasePageModelSecure
  {
    private const string SnapshotFileName = "MarketTrendAnalytics.json";

    public MarketTrendAnalyticsMonitorSnapshot Analytics { get; set; } = new MarketTrendAnalyticsMonitorSnapshot();
    public string LoadError { get; set; } = "";

    public IReadOnlyList<string> GlobalMetricNames { get; } = new string[]
    {
      MarketTrendMetrics.MeanReturn,
      MarketTrendMetrics.MedianReturn,
      MarketTrendMetrics.Breadth,
      MarketTrendMetrics.Dispersion,
      MarketTrendMetrics.Concentration
    };

    public IReadOnlyList<string> AssetMetricNames { get; } = new string[]
    {
      MarketTrendMetrics.RelativeStrengthMarket,
      MarketTrendMetrics.RelativeStrengthBTC
    };

    public void OnGet()
    {
      base.Init();
      LoadAnalytics();
    }

    public IEnumerable<MarketTrendAnalyticsMonitorTrend> OrderedTrends()
    {
      return Analytics?.Trends?.Values
        .OrderBy(trend => trend.TrendMinutes)
        .ThenBy(trend => trend.Name, StringComparer.OrdinalIgnoreCase)
        ?? Enumerable.Empty<MarketTrendAnalyticsMonitorTrend>();
    }

    public IEnumerable<string> OrderedMarkets()
    {
      if (Analytics?.Trends == null)
      {
        return Enumerable.Empty<string>();
      }

      return Analytics.Trends.Values
        .SelectMany(trend => trend.AssetMetrics?.Keys ?? Enumerable.Empty<string>())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(symbol => symbol, StringComparer.OrdinalIgnoreCase);
    }

    public FeatureValueSnapshot GetGlobalMetric(MarketTrendAnalyticsMonitorTrend trend, string metric)
    {
      if (trend?.MarketMetrics == null)
      {
        return null;
      }

      trend.MarketMetrics.TryGetValue(metric, out FeatureValueSnapshot feature);
      return feature;
    }

    public FeatureValueSnapshot GetAssetMetric(MarketTrendAnalyticsMonitorTrend trend, string market, string metric)
    {
      if (trend?.AssetMetrics == null
        || !trend.AssetMetrics.TryGetValue(market, out Dictionary<string, FeatureValueSnapshot> metrics)
        || metrics == null)
      {
        return null;
      }

      metrics.TryGetValue(metric, out FeatureValueSnapshot feature);
      return feature;
    }

    public string FormatMetric(string metric, FeatureValueSnapshot feature)
    {
      if (feature == null || !feature.IsValid || feature.IsStale || !feature.Value.HasValue)
      {
        return "--";
      }

      string suffix = metric.Equals(MarketTrendMetrics.RelativeStrengthMarket, StringComparison.OrdinalIgnoreCase)
        || metric.Equals(MarketTrendMetrics.RelativeStrengthBTC, StringComparison.OrdinalIgnoreCase)
        || metric.Equals(MarketTrendMetrics.Dispersion, StringComparison.OrdinalIgnoreCase)
        || metric.Equals(MarketTrendMetrics.Concentration, StringComparison.OrdinalIgnoreCase)
          ? " pp"
          : "%";

      return feature.Value.Value.ToString("#,#0.00", new System.Globalization.CultureInfo("en-US")) + suffix;
    }

    public string MetricTitle(string metric)
    {
      return metric switch
      {
        MarketTrendMetrics.MeanReturn => "Mean Return",
        MarketTrendMetrics.MedianReturn => "Median Return",
        MarketTrendMetrics.Breadth => "Positive Breadth",
        MarketTrendMetrics.Dispersion => "Dispersion",
        MarketTrendMetrics.RelativeStrengthMarket => "RS vs Market",
        MarketTrendMetrics.RelativeStrengthBTC => "RS vs BTC",
        MarketTrendMetrics.Concentration => "Top10 Advantage",
        _ => metric
      };
    }

    public string FeatureTooltip(FeatureValueSnapshot feature)
    {
      if (feature == null)
      {
        return "Metric is not enabled for this MarketTrend.";
      }
      if (!feature.IsValid)
      {
        return String.IsNullOrWhiteSpace(feature.InvalidReason) ? "Metric is unavailable." : feature.InvalidReason;
      }
      if (feature.IsStale)
      {
        return "Metric is stale.";
      }

      return "Sample count: " + feature.SampleCount +
        (feature.SourceDataTimestampUtc.HasValue
          ? ". Source: " + feature.SourceDataTimestampUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
          : "");
    }

    private void LoadAnalytics()
    {
      string path = Path.Combine(Directory.GetCurrentDirectory(), Constants.PTMagicPathData, SnapshotFileName);
      if (!System.IO.File.Exists(path))
      {
        LoadError = "No MarketTrend analytics snapshot has been written yet. Run the Analyzer with one or more MarketTrend Metrics enabled.";
        return;
      }

      try
      {
        MarketTrendAnalyticsMonitorSnapshot snapshot = JsonConvert.DeserializeObject<MarketTrendAnalyticsMonitorSnapshot>(System.IO.File.ReadAllText(path));
        if (snapshot != null)
        {
          Analytics = snapshot;
        }
      }
      catch (Exception ex)
      {
        LoadError = "Unable to read MarketTrendAnalytics.json: " + ex.Message;
      }
    }
  }
}
