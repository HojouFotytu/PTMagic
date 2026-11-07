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
      MarketTrendMetrics.Concentration,
      MarketTrendMetrics.MedianATRP,
      MarketTrendMetrics.VolatilityBreadth,
      MarketTrendMetrics.MedianVolumeExpansion,
      MarketTrendMetrics.VolumeExpansionBreadth,
      MarketTrendMetrics.StrongVolumeExpansionBreadth,
      MarketTrendMetrics.VolumeConcentration,
      MarketTrendMetrics.TopNVolumeShare,
      MarketTrendMetrics.BTCVolumeShare,
      MarketTrendMetrics.BTCETHVolumeShare,
      MarketTrendMetrics.MeanCorrelationToBTC,
      MarketTrendMetrics.MedianCorrelationToBTC
    };

    public IReadOnlyList<string> AssetMetricNames { get; } = new string[]
    {
      MarketTrendMetrics.RelativeStrengthMarket,
      MarketTrendMetrics.RelativeStrengthBTC,
      MarketTrendMetrics.ATRP,
      MarketTrendMetrics.VolumeExpansion,
      MarketTrendMetrics.CorrelationToBTC
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

      bool isCorrelation = metric.Equals(MarketTrendMetrics.CorrelationToBTC, StringComparison.OrdinalIgnoreCase)
        || metric.Equals(MarketTrendMetrics.MeanCorrelationToBTC, StringComparison.OrdinalIgnoreCase)
        || metric.Equals(MarketTrendMetrics.MedianCorrelationToBTC, StringComparison.OrdinalIgnoreCase);
      string format = isCorrelation ? "0.0000" : isReturnDifference ? "+#,#0.00;-#,#0.00;0.00" : "#,#0.00";
      string unit = MarketTrendMetricTriggerResolver.GetUnit(metric);
      return feature.Value.Value.ToString(format, new System.Globalization.CultureInfo("en-US")) + unit;
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
        MarketTrendMetrics.ATRP => "ATRP",
        MarketTrendMetrics.MedianATRP => "Median ATRP",
        MarketTrendMetrics.VolatilityBreadth => "Volatility Breadth",
        MarketTrendMetrics.VolumeExpansion => "Volume Expansion",
        MarketTrendMetrics.MedianVolumeExpansion => "Median Volume Expansion",
        MarketTrendMetrics.VolumeExpansionBreadth => "Volume Breadth",
        MarketTrendMetrics.StrongVolumeExpansionBreadth => "Strong Volume Breadth",
        MarketTrendMetrics.VolumeConcentration => "Volume HHI",
        MarketTrendMetrics.TopNVolumeShare => "Top N Volume Share",
        MarketTrendMetrics.BTCVolumeShare => "BTC Volume Share",
        MarketTrendMetrics.BTCETHVolumeShare => "BTC+ETH Volume Share",
        MarketTrendMetrics.CorrelationToBTC => "Correlation to BTC",
        MarketTrendMetrics.MeanCorrelationToBTC => "Mean BTC Correlation",
        MarketTrendMetrics.MedianCorrelationToBTC => "Median BTC Correlation",
        _ => metric
      };
    }

    public string MetricTooltip(string metric)
    {
      return metric switch
      {
        MarketTrendMetrics.PriceTrend => "Arithmetic mean percentage price change of valid markets over this MarketTrend timeframe, calculated from normalized closed-candle data. Blank Metrics uses normalized PriceTrend. TrendThreshold excludes price outliers from this mean only, not from the other metrics. Fiat mode converts quote-denominated returns using synchronized quote/USDT prices.",
        MarketTrendMetrics.MedianPriceTrend => "Median percentage price change of valid markets over this MarketTrend timeframe. The median is resistant to a small number of extreme movers.",
        MarketTrendMetrics.MeanMedianSpread => "Unfiltered arithmetic mean return minus MedianPriceTrend for the same market population and timeframe. Positive values mean upside outliers are pulling the mean above the median; negative values mean downside outliers are pulling it below the median. Displayed as a signed % for readability; mathematically this is an absolute difference between percentage returns.",
        MarketTrendMetrics.Breadth => "Percentage of valid markets with a price change greater than 0 over this MarketTrend timeframe.",
        MarketTrendMetrics.Dispersion => "Population standard deviation of valid market price changes over this MarketTrend timeframe. Displayed as a percentage magnitude.",
        MarketTrendMetrics.RelativeStrengthMarket => "Individual market price change minus MedianPriceTrend. Displayed as a signed % for readability; mathematically this is an absolute difference between percentage returns, not a relative percentage increase.",
        MarketTrendMetrics.RelativeStrengthBTC => "Individual market price change minus BTC price change over the same timeframe. Displayed as a signed % for readability; mathematically this is an absolute difference between percentage returns, not a relative percentage increase.",
        MarketTrendMetrics.Concentration => "Mean return of the Top10 markets ranked by trailing MarketTrend-duration quote volume minus the mean return of the remaining MarketTrend universe. Ranking and returns share the same normalized 5-minute endpoint; complete population coverage is required. Displayed as a signed %; mathematically this is an absolute difference between percentage returns.",
        MarketTrendMetrics.ATRP => "Wilder ATR over complete MarketTrend-duration candles divided by latest complete trend close, in percent.",
        MarketTrendMetrics.MedianATRP => "Median ATRP across global eligible markets; 90% coverage.",
        MarketTrendMetrics.VolatilityBreadth => "Percent of valid markets whose ATRP exceeds configured ATRPBreadthThreshold; 90% coverage.",
        MarketTrendMetrics.VolumeExpansion => "Latest complete MarketTrend-duration quote volume divided by the previous VolumeExpansionLookback periods average; ratio, 1.0x means normal.",
        MarketTrendMetrics.MedianVolumeExpansion => "Median VolumeExpansion ratio among valid global markets; 90% coverage.",
        MarketTrendMetrics.VolumeExpansionBreadth => "Percent of valid markets whose volume expansion exceeds VolumeExpansionBreadthThreshold.",
        MarketTrendMetrics.StrongVolumeExpansionBreadth => "Percent of valid markets whose volume expansion exceeds StrongVolumeExpansionThreshold.",
        MarketTrendMetrics.VolumeConcentration => "HHI of quote-volume shares in the latest complete trend period multiplied by 100; complete global population required.",
        MarketTrendMetrics.TopNVolumeShare => "TopNVolumeCount ranked-market share of latest complete MarketTrend-duration quote volume, percent; complete global population required.",
        MarketTrendMetrics.BTCVolumeShare => "BTC quote-volume share of the complete eligible global universe in latest complete trend period, percent.",
        MarketTrendMetrics.BTCETHVolumeShare => "Combined BTC and ETH quote-volume share of complete eligible global universe in latest complete trend period, percent.",
        MarketTrendMetrics.CorrelationToBTC => "Pearson correlation of this market and BTC across CorrelationLookback synchronized simple returns. Each return spans exactly TrendMinutes; missing, stale or flat series are unavailable. Unitless coefficient from -1 to 1.",
        MarketTrendMetrics.MeanCorrelationToBTC => "Arithmetic mean of valid BTC correlation coefficients across the selected global population; at least 90% coverage. BTC is included only when it belongs to that population. Unitless coefficient.",
        MarketTrendMetrics.MedianCorrelationToBTC => "Median of valid BTC correlation coefficients across the selected global population; at least 90% coverage. BTC is included only when it belongs to that population. Unitless coefficient.",
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
