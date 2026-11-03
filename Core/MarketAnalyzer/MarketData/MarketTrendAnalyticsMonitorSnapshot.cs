using System;
using System.Collections.Generic;

namespace Core.MarketAnalyzer.MarketData
{
  public sealed class MarketTrendAnalyticsMonitorSnapshot
  {
    public DateTimeOffset TimestampUtc { get; set; }
    public Dictionary<string, MarketTrendAnalyticsMonitorTrend> Trends { get; set; } =
      new Dictionary<string, MarketTrendAnalyticsMonitorTrend>(StringComparer.OrdinalIgnoreCase);
  }

  public sealed class MarketTrendAnalyticsMonitorTrend
  {
    public string Name { get; set; } = "";
    public int TrendMinutes { get; set; }
    public List<string> GlobalSymbols { get; set; } = new List<string>();
    public List<string> AssetSymbols { get; set; } = new List<string>();
    public Dictionary<string, FeatureValueSnapshot> MarketMetrics { get; set; } =
      new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, FeatureValueSnapshot>> AssetMetrics { get; set; } =
      new Dictionary<string, Dictionary<string, FeatureValueSnapshot>>(StringComparer.OrdinalIgnoreCase);
  }
}
