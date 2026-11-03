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
      MarketTrendMetrics.PriceTrend,
      MarketTrendMetrics.MedianPriceTrend,
      MarketTrendMetrics.MeanMedianSpread,
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

      bool isReturnDifference = metric.Equals(MarketTrendMetrics.MeanMedianSpread, StringComparison.OrdinalIgnoreCase)
        || metric.Equals(MarketTrendMetrics.RelativeStrengthMarket, StringComparison.OrdinalIgnoreCase)
        || metric.Equals(MarketTrendMetrics.RelativeStrengthBTC, StringComparison.OrdinalIgnoreCase)
        || metric.Equals(MarketTrendMetrics.Concentration, StringComparison.OrdinalIgnoreCase);

      string format = isReturnDifference ? "+#,#0.00;-#,#0.00;0.00" : "#,#0.00";
      return feature.Value.Value.ToString(format, new System.Globalization.CultureInfo("en-US")) + "%";
    }

    public string FormatTimeframe(int trendMinutes)
    {
      if (trendMinutes <= 0)
      {
        return "0m";
      }

      int remainingMinutes = trendMinutes;
      int days = remainingMinutes / 1440;
      remainingMinutes %= 1440;
      int hours = remainingMinutes / 60;
      int minutes = remainingMinutes % 60;

      List<string> parts = new List<string>();
      if (days > 0)
      {
        parts.Add(days + "d");
      }
      if (hours > 0)
      {
        parts.Add(hours + "h");
      }
      if (minutes > 0)
      {
        parts.Add(minutes + "m");
      }

      return parts.Count > 0 ? String.Join(" ", parts) : "0m";
    }

    public string MetricTitle(string metric)
    {
      return metric switch
      {
        MarketTrendMetrics.PriceTrend => "Price Trend",
        MarketTrendMetrics.MedianPriceTrend => "Median Price Trend",
        MarketTrendMetrics.MeanMedianSpread => "Mean-Median Spread",
        MarketTrendMetrics.Breadth => "Positive Breadth",
        MarketTrendMetrics.Dispersion => "Dispersion",
        MarketTrendMetrics.RelativeStrengthMarket => "RS vs Market",
        MarketTrendMetrics.RelativeStrengthBTC => "RS vs BTC",
        MarketTrendMetrics.Concentration => "Top10 Return Advantage",
        _ => metric
      };
    }

    public string MetricTooltip(string metric)
    {
      return metric switch
      {
        MarketTrendMetrics.PriceTrend => "Arithmetic mean percentage price change of valid markets over this MarketTrend timeframe, calculated from normalized closed-candle data. This normalized PriceTrend is exposed only when PriceTrend is explicitly listed in Metrics; an empty Metrics setting retains PTMagic's legacy PriceTrend behavior.",
        MarketTrendMetrics.MedianPriceTrend => "Median percentage price change of valid markets over this MarketTrend timeframe. The median is resistant to a small number of extreme movers.",
        MarketTrendMetrics.MeanMedianSpread => "Normalized PriceTrend arithmetic mean minus MedianPriceTrend for the same market population and timeframe. Positive values mean upside outliers are pulling the mean above the median; negative values mean downside outliers are pulling it below the median. Displayed as a signed % for readability; mathematically this is an absolute difference between percentage returns.",
        MarketTrendMetrics.Breadth => "Percentage of valid markets with a price change greater than 0 over this MarketTrend timeframe.",
        MarketTrendMetrics.Dispersion => "Population standard deviation of valid market price changes over this MarketTrend timeframe. Displayed as a percentage magnitude.",
        MarketTrendMetrics.RelativeStrengthMarket => "Individual market price change minus MedianPriceTrend. Displayed as a signed % for readability; mathematically this is an absolute difference between percentage returns, not a relative percentage increase.",
        MarketTrendMetrics.RelativeStrengthBTC => "Individual market price change minus BTC price change over the same timeframe. Displayed as a signed % for readability; mathematically this is an absolute difference between percentage returns, not a relative percentage increase.",
        MarketTrendMetrics.Concentration => "Mean return of the Top10 markets by trailing 24-hour quote volume minus the mean return of the remaining MarketTrend universe. Displayed as a signed % for readability; mathematically this is an absolute difference between percentage returns, not a relative percentage increase.",
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
