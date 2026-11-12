using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Core.Main;
using Core.Helper;
using Core.Main.DataObjects;
using Core.Main.DataObjects.PTMagicData;
using Core.MarketAnalyzer;
using Core.MarketAnalyzer.MarketData;
using Newtonsoft.Json;
using System.Globalization;

namespace Monitor.Pages
{
  public class MarketAnalyzerModel : _Internal.BasePageModelSecure
  {
    public List<MarketTrend> MarketTrends { get; set; } = new List<MarketTrend>();
    public ProfitTrailerData PTData = null;
    public MiscData MiscData { get; set; }
    public string TrendChartDataJSON = "";
    public double DataHours { get; set; }
    public Dictionary<string, string> MarketUnderlyingTypes { get; set; } = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

    private const string MarketTrendAnalyticsSnapshotFileName = "MarketTrendAnalytics.json";

    public MarketTrendAnalyticsMonitorSnapshot Analytics { get; set; } = new MarketTrendAnalyticsMonitorSnapshot();
    public string AnalyticsLoadError { get; set; } = "";

    // The inline rows use the existing analytics export; no separate metric source or page.
    public sealed class InlineAssetMetricCell
    {
      public string Value { get; set; } = "";
      public string Tooltip { get; set; } = "";
      public string ColorClass { get; set; } = "text-muted";
    }

    public sealed class InlineAssetMetricRow
    {
      public string Label { get; set; } = "";
      public string Description { get; set; } = "";
      public List<InlineAssetMetricCell> Cells { get; set; } = new List<InlineAssetMetricCell>();
    }

    public Dictionary<string, List<InlineAssetMetricRow>> InlineAssetMetricRows { get; } =
      new Dictionary<string, List<InlineAssetMetricRow>>(StringComparer.OrdinalIgnoreCase);
    public string InlineAssetMetricRowsJson { get; private set; } = "{}";

    public bool HasAssetMetricsForMarket(string market) =>
      !String.IsNullOrWhiteSpace(market) && InlineAssetMetricRows.ContainsKey(market);

    public bool HasConfiguredDisplayAssetMetrics =>
      MarketTrends != null && MarketTrends.Any(trend =>
        trend != null
        && String.Equals(trend.Platform, "Exchange", StringComparison.OrdinalIgnoreCase)
        && trend.DisplayOnMarketAnalyzerList
        && MarketTrendMetrics.ParseEnabled(trend).Any(metric =>
          !String.Equals(metric, MarketTrendMetrics.PriceTrend, StringComparison.OrdinalIgnoreCase)
          && MarketTrendMetrics.TryGetScope(metric, out MarketTrendMetricScope scope)
          && (scope == MarketTrendMetricScope.Asset || scope == MarketTrendMetricScope.Both)));

    // Only explicit global/both-scope Metrics entries create this panel. A blank Metrics
    // field retains legacy PriceTrend behavior but does not count as a configured metrics view.
    public bool HasConfiguredMarketWideMetrics
    {
      get
      {
        return MarketTrends != null && MarketTrends.Any(trend =>
          trend != null
          && !String.IsNullOrWhiteSpace(trend.Metrics)
          && trend.Metrics.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(MarketTrendMetrics.Normalize)
            .Any(metric => MarketTrendMetrics.TryGetScope(metric, out MarketTrendMetricScope scope)
              && (scope == MarketTrendMetricScope.Global || scope == MarketTrendMetricScope.Both)));
      }
    }

    public sealed class GlobalMetricGroup
    {
      public string Heading { get; }
      public IReadOnlyList<string> Metrics { get; }

      public GlobalMetricGroup(string heading, params string[] metrics)
      {
        Heading = heading;
        Metrics = metrics;
      }
    }

    public IReadOnlyList<GlobalMetricGroup> GlobalMetricGroups { get; } = new[]
    {
      new GlobalMetricGroup("Price Direction",
        MarketTrendMetrics.PriceTrend, MarketTrendMetrics.MedianPriceTrend,
        MarketTrendMetrics.MeanMedianSpread),
      new GlobalMetricGroup("Market Structure",
        MarketTrendMetrics.Breadth, MarketTrendMetrics.Dispersion,
        MarketTrendMetrics.Concentration),
      new GlobalMetricGroup("Volatility",
        MarketTrendMetrics.MedianATRP, MarketTrendMetrics.VolatilityBreadth),
      new GlobalMetricGroup("Volume Expansion",
        MarketTrendMetrics.MedianVolumeExpansion, MarketTrendMetrics.VolumeExpansionBreadth,
        MarketTrendMetrics.StrongVolumeExpansionBreadth),
      new GlobalMetricGroup("Volume Concentration",
        MarketTrendMetrics.VolumeConcentration, MarketTrendMetrics.TopNVolumeShare),
      new GlobalMetricGroup("BTC / ETH Volume Share",
        MarketTrendMetrics.BTCVolumeShare, MarketTrendMetrics.BTCETHVolumeShare),
      new GlobalMetricGroup("BTC Correlation",
        MarketTrendMetrics.MeanCorrelationToBTC, MarketTrendMetrics.MedianCorrelationToBTC)
    };

    public IEnumerable<MarketTrendAnalyticsMonitorTrend> OrderedAnalyticsTrends()
    {
      return Analytics?.Trends?.Values
        .OrderBy(trend => trend.TrendMinutes)
        .ThenBy(trend => trend.Name, StringComparer.OrdinalIgnoreCase)
        ?? Enumerable.Empty<MarketTrendAnalyticsMonitorTrend>();
    }

    private void BuildInlineAssetMetricRows()
    {
      InlineAssetMetricRows.Clear();
      InlineAssetMetricRowsJson = "{}";
      if (Analytics?.Trends == null || Analytics.Trends.Count == 0 || Summary?.MarketSummary == null)
      {
        return;
      }

      // These columns must match the existing Absolute / Relative Trend tables exactly.
      MarketTrend[] displayedTrends = MarketTrends
        .Where(trend => trend != null
          && String.Equals(trend.Platform, "Exchange", StringComparison.OrdinalIgnoreCase)
          && trend.DisplayOnMarketAnalyzerList)
        .ToArray();
      if (displayedTrends.Length == 0)
      {
        return;
      }

      string[][] configuredAssetMetrics = displayedTrends
        .Select(trend => MarketTrendMetrics.ParseEnabled(trend)
          .Where(metric =>
            !String.Equals(metric, MarketTrendMetrics.PriceTrend, StringComparison.OrdinalIgnoreCase)
            && MarketTrendMetrics.TryGetScope(metric, out MarketTrendMetricScope scope)
            && (scope == MarketTrendMetricScope.Asset || scope == MarketTrendMetricScope.Both))
          .ToArray())
        .ToArray();
      string[] metrics = configuredAssetMetrics.SelectMany(items => items)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
      if (metrics.Length == 0)
      {
        return;
      }

      MarketTrendAnalyticsMonitorTrend[] snapshots = displayedTrends.Select(trend =>
      {
        Analytics.Trends.TryGetValue(trend.Name, out MarketTrendAnalyticsMonitorTrend snapshot);
        return snapshot;
      }).ToArray();
      HashSet<string>[] eligibleSymbols = snapshots.Select(snapshot =>
        new HashSet<string>(snapshot?.AssetSymbols ?? new List<string>(), StringComparer.OrdinalIgnoreCase))
        .ToArray();

      foreach (string market in Summary.MarketSummary.Keys)
      {
        List<InlineAssetMetricRow> rows = new List<InlineAssetMetricRow>();
        foreach (string metric in metrics)
        {
          InlineAssetMetricRow row = new InlineAssetMetricRow
          {
            Label = MetricTitle(metric),
            Description = MetricTooltip(metric)
          };
          bool hasApplicableTrend = false;

          for (int index = 0; index < displayedTrends.Length; index++)
          {
            MarketTrendAnalyticsMonitorTrend snapshot = snapshots[index];
            if (!configuredAssetMetrics[index].Contains(metric, StringComparer.OrdinalIgnoreCase)
              || snapshot == null || !eligibleSymbols[index].Contains(market))
            {
              row.Cells.Add(null);
              continue;
            }

            hasApplicableTrend = true;
            FeatureValueSnapshot feature = null;
            if (snapshot.AssetMetrics != null
              && snapshot.AssetMetrics.TryGetValue(market, out Dictionary<string, FeatureValueSnapshot> marketMetrics)
              && marketMetrics != null)
            {
              marketMetrics.TryGetValue(metric, out feature);
            }

            row.Cells.Add(new InlineAssetMetricCell
            {
              Value = FormatInlineAssetMetric(metric, feature),
              ColorClass = InlineAssetMetricColor(metric, feature),
              Tooltip = feature == null
                ? "Metric was configured but no value was published for this MarketTrend."
                : FeatureTooltip(feature)
            });
          }

          if (hasApplicableTrend)
          {
            rows.Add(row);
          }
        }

        if (rows.Count > 0)
        {
          InlineAssetMetricRows[market] = rows;
        }
      }

      // Escapes HTML-sensitive characters when embedded inside the Razor script tag.
      InlineAssetMetricRowsJson = JsonConvert.SerializeObject(InlineAssetMetricRows,
        new JsonSerializerSettings { StringEscapeHandling = StringEscapeHandling.EscapeHtml });
    }

    public string InlineAssetMetricColor(string metric, FeatureValueSnapshot feature)
    {
      if (feature == null || !feature.IsValid || feature.IsStale || !feature.Value.HasValue
        || !Double.IsFinite(feature.Value.Value))
      {
        return "text-muted";
      }

      // Volume-expansion multiples are neutral at 1.0; signed metrics at zero.
      double neutralValue = metric == MarketTrendMetrics.VolumeExpansion ? 1.0
        : metric == MarketTrendMetrics.TakerBuyRatio ? 0.5 : 0.0;
      return feature.Value.Value > neutralValue ? "text-success"
        : feature.Value.Value < neutralValue ? "text-danger" : "text-muted";
    }

    public string FormatInlineAssetMetric(string metric, FeatureValueSnapshot feature)
    {
      if (feature == null || !feature.IsValid || feature.IsStale || !feature.Value.HasValue
        || !Double.IsFinite(feature.Value.Value))
      {
        return "--";
      }

      double value = feature.Value.Value;
      if (metric == MarketTrendMetrics.CorrelationToBTC || TakerFlowFeatureEngine.IsMetric(metric))
      {
        return value.ToString("0.0000", CultureInfo.InvariantCulture);
      }
      if (metric == MarketTrendMetrics.FundingRate || metric == MarketTrendMetrics.FundingSum)
      {
        return value.ToString("+0.000000;-0.000000;0.000000", CultureInfo.InvariantCulture) + "%";
      }
      return FormatMetric(metric, feature);
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

    public string FormatMetric(string metric, FeatureValueSnapshot feature)
    {
      if (feature == null || !feature.IsValid || feature.IsStale || !feature.Value.HasValue)
      {
        return "--";
      }

      bool isReturnDifference = metric.Equals(MarketTrendMetrics.MeanMedianSpread, StringComparison.OrdinalIgnoreCase)
        || metric.Equals(MarketTrendMetrics.Concentration, StringComparison.OrdinalIgnoreCase);
      bool isCorrelation = metric.Equals(MarketTrendMetrics.MeanCorrelationToBTC, StringComparison.OrdinalIgnoreCase)
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
        MarketTrendMetrics.Concentration => "Top10 Return Advantage",
        MarketTrendMetrics.MedianATRP => "Median ATRP",
        MarketTrendMetrics.VolatilityBreadth => "Volatility Breadth",
        MarketTrendMetrics.MedianVolumeExpansion => "Median Volume Expansion",
        MarketTrendMetrics.VolumeExpansionBreadth => "Volume Breadth",
        MarketTrendMetrics.StrongVolumeExpansionBreadth => "Strong Volume Breadth",
        MarketTrendMetrics.VolumeConcentration => "Volume HHI",
        MarketTrendMetrics.TopNVolumeShare => "Top N Volume Share",
        MarketTrendMetrics.BTCVolumeShare => "BTC Volume Share",
        MarketTrendMetrics.BTCETHVolumeShare => "BTC+ETH Volume Share",
        MarketTrendMetrics.MeanCorrelationToBTC => "Mean BTC Correlation",
        MarketTrendMetrics.MedianCorrelationToBTC => "Median BTC Correlation",
        MarketTrendMetrics.RelativeStrengthMarket => "Strength vs Market",
        MarketTrendMetrics.RelativeStrengthBTC => "Strength vs BTC",
        MarketTrendMetrics.ATRP => "ATRP",
        MarketTrendMetrics.VolumeExpansion => "Volume Expansion",
        MarketTrendMetrics.CorrelationToBTC => "BTC Correlation",
        MarketTrendMetrics.FundingRate => "Funding Rate",
        MarketTrendMetrics.FundingSum => "Funding Sum",
        MarketTrendMetrics.TakerBuyRatio => "Taker Buy Ratio",
        MarketTrendMetrics.TakerImbalance => "Taker Imbalance",
        _ => metric
      };
    }

    public string GlobalMetricSubheading(string metric)
    {
      return metric switch
      {
        MarketTrendMetrics.Breadth => "Breadth",
        MarketTrendMetrics.Concentration => "Concentration",
        MarketTrendMetrics.MedianVolumeExpansion => "Median Expansion",
        MarketTrendMetrics.VolumeExpansionBreadth => "Expansion Breadth",
        MarketTrendMetrics.StrongVolumeExpansionBreadth => "Strong Expansion Breadth",
        MarketTrendMetrics.MeanCorrelationToBTC => "Mean Correlation",
        MarketTrendMetrics.MedianCorrelationToBTC => "Median Correlation",
        _ => MetricTitle(metric)
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
        MarketTrendMetrics.Concentration => "Mean return of the Top10 markets ranked by trailing MarketTrend-duration quote volume minus the mean return of the remaining MarketTrend universe. Ranking and returns share the same normalized 5-minute endpoint; complete population coverage is required. Displayed as a signed %; mathematically this is an absolute difference between percentage returns.",
        MarketTrendMetrics.MedianATRP => "Median ATRP across global eligible markets; 90% coverage.",
        MarketTrendMetrics.VolatilityBreadth => "Percent of valid markets whose ATRP exceeds configured ATRPBreadthThreshold; 90% coverage.",
        MarketTrendMetrics.MedianVolumeExpansion => "Median VolumeExpansion ratio among valid global markets; 90% coverage.",
        MarketTrendMetrics.VolumeExpansionBreadth => "Percent of valid markets whose volume expansion exceeds VolumeExpansionBreadthThreshold.",
        MarketTrendMetrics.StrongVolumeExpansionBreadth => "Percent of valid markets whose volume expansion exceeds StrongVolumeExpansionThreshold.",
        MarketTrendMetrics.VolumeConcentration => "HHI of quote-volume shares in the latest complete trend period multiplied by 100; complete global population required.",
        MarketTrendMetrics.TopNVolumeShare => "TopNVolumeCount ranked-market share of latest complete MarketTrend-duration quote volume, percent; complete global population required.",
        MarketTrendMetrics.BTCVolumeShare => "BTC quote-volume share of the complete eligible global universe in latest complete trend period, percent.",
        MarketTrendMetrics.BTCETHVolumeShare => "Combined BTC and ETH quote-volume share of complete eligible global universe in latest complete trend period, percent.",
        MarketTrendMetrics.MeanCorrelationToBTC => "Arithmetic mean of valid BTC correlation coefficients across the selected global population; at least 90% coverage. BTC is included only when it belongs to that population. Unitless coefficient.",
        MarketTrendMetrics.MedianCorrelationToBTC => "Median of valid BTC correlation coefficients across the selected global population; at least 90% coverage. BTC is included only when it belongs to that population. Unitless coefficient.",
        MarketTrendMetrics.FundingRate => "Latest regular funding settlement in the configured MarketTrend window, as a percentage. Missing regular settlements remain unavailable.",
        MarketTrendMetrics.FundingSum => "Sum of regular funding settlements in the configured MarketTrend window, as a percentage. Unverified history remains unavailable.",
        MarketTrendMetrics.TakerBuyRatio => "Taker-buy quote volume divided by total quote volume summed over the latest exact complete MarketTrend-duration UTC period. Range 0–1; 0.5 is balanced. Missing or invalid history remains unavailable.",
        MarketTrendMetrics.TakerImbalance => "Twice TakerBuyRatio minus one, using exactly the same volume-weighted closed MarketTrend period. Range -1 to +1; zero is balanced. Not an independent data source from TakerBuyRatio.",
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

    private void LoadMarketTrendAnalytics()
    {
      string path = Path.Combine(Directory.GetCurrentDirectory(), Constants.PTMagicPathData, MarketTrendAnalyticsSnapshotFileName);
      if (!System.IO.File.Exists(path))
      {
        AnalyticsLoadError = "No MarketTrend analytics snapshot has been written yet. Run the Analyzer with one or more MarketTrend Metrics enabled.";
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
        AnalyticsLoadError = "Unable to read MarketTrendAnalytics.json: " + ex.Message;
      }
    }


    public void OnGet()
    {
      base.Init();
      BindData();
    }

    private void BindData()
    {
      PTData = this.PtDataObject;
      MiscData = this.PTData.Misc;  
      MarketTrends = PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.MarketTrends.OrderBy(mt => mt.TrendMinutes).ThenByDescending(mt => mt.Platform).ToList();

      if (PTMagicConfiguration.GeneralSettings.Application.Exchange.Equals("BinanceFutures", StringComparison.InvariantCultureIgnoreCase)
        && Summary?.MarketSummary != null)
      {
        Summary.MarketSummary = Summary.MarketSummary
          .Where(market => BinanceFuturesMarketUniverse.IsMarketIncluded(market.Key, PTMagicConfiguration))
          .ToDictionary(market => market.Key, market => market.Value, StringComparer.InvariantCultureIgnoreCase);

        LoadMarketUnderlyingTypes();
      }

      if (HasConfiguredMarketWideMetrics || HasConfiguredDisplayAssetMetrics)
      {
        LoadMarketTrendAnalytics();
      }

      BuildInlineAssetMetricRows();
      BuildMarketTrendChartData();
    }

    public string GetMarketUnderlyingType(string market)
    {
      if (String.IsNullOrWhiteSpace(market))
      {
        return "";
      }

      return MarketUnderlyingTypes.TryGetValue(market, out string underlyingType) ? underlyingType : "";
    }

    private void LoadMarketUnderlyingTypes()
    {
      string metadataPath = Path.Combine(Directory.GetCurrentDirectory(), Constants.PTMagicPathData, Constants.PTMagicPathExchange, "BinanceFuturesMarketMetadata.json");
      if (!System.IO.File.Exists(metadataPath))
      {
        return;
      }

      try
      {
        List<BinanceFuturesMarketMetadata> metadata = JsonConvert.DeserializeObject<List<BinanceFuturesMarketMetadata>>(System.IO.File.ReadAllText(metadataPath));
        if (metadata == null)
        {
          return;
        }

        MarketUnderlyingTypes = metadata
          .Where(item => item != null && !String.IsNullOrWhiteSpace(item.Symbol))
          .GroupBy(item => item.Symbol, StringComparer.InvariantCultureIgnoreCase)
          .ToDictionary(group => group.Key, group => group.Last().UnderlyingType ?? "", StringComparer.InvariantCultureIgnoreCase);
      }
      catch
      {
        MarketUnderlyingTypes = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
      }
    }

    private void BuildMarketTrendChartData()
    {
        List<string> trendChartData = new List<string>();
        if (MarketTrends.Count > 0)
        {
        
            int mtIndex = 0;
            foreach (MarketTrend mt in MarketTrends)
            {
                if (mt.DisplayGraph)
                {
                    string lineColor = mtIndex < Constants.ChartLineColors.Length
                        ? Constants.ChartLineColors[mtIndex]
                        : Constants.ChartLineColors[mtIndex - 20];

                    if (Summary.MarketTrendChanges.ContainsKey(mt.Name))
                    {
                        List<MarketTrendChange> marketTrendChangeSummaries = Summary.MarketTrendChanges[mt.Name];

                        if (marketTrendChangeSummaries.Count > 0)
                        {
                            List<string> trendValues = new List<string>();

                            // Sort marketTrendChangeSummaries by TrendDateTime
                            marketTrendChangeSummaries = marketTrendChangeSummaries.OrderBy(m => m.TrendDateTime).ToList();

                            // Get trend ticks for chart
                            TimeSpan offset;
                            bool isNegative = MiscData.TimeZoneOffset.StartsWith("-");
                            string offsetWithoutSign = MiscData.TimeZoneOffset.TrimStart('+', '-');

                            if (!TimeSpan.TryParse(offsetWithoutSign, out offset))
                            {
                                offset = TimeSpan.Zero; // If offset is invalid, set it to zero
                            }

                            DateTime currentDateTime = DateTime.UtcNow;
                            DateTime startDateTime = currentDateTime.AddHours(-PTMagicConfiguration.GeneralSettings.Monitor.GraphMaxTimeframeHours);
                            DateTime endDateTime = currentDateTime;

                            // Ensure startDateTime doesn't exceed the available data
                            DateTime earliestTrendDateTime = marketTrendChangeSummaries.Min(mtc => mtc.TrendDateTime);
                            startDateTime = startDateTime > earliestTrendDateTime ? startDateTime : earliestTrendDateTime;
                            DataHours = (currentDateTime - earliestTrendDateTime).TotalHours;

                            // Cache the result of SplitCamelCase(mt.Name)
                            string splitCamelCaseName = SystemHelper.SplitCamelCase(mt.Name);

                            for (DateTime tickTime = startDateTime; tickTime <= endDateTime; tickTime = tickTime.AddMinutes(PTMagicConfiguration.GeneralSettings.Monitor.GraphIntervalMinutes))
                            {
                                // Use binary search to find the range of items that match the condition
                                int index = marketTrendChangeSummaries.BinarySearch(new MarketTrendChange { TrendDateTime = tickTime }, Comparer<MarketTrendChange>.Create((x, y) => x.TrendDateTime.CompareTo(y.TrendDateTime)));
                                if (index < 0) index = ~index;
                                if (index < marketTrendChangeSummaries.Count)
                                {
                                    MarketTrendChange mtc = marketTrendChangeSummaries[index];
                                    if (Double.IsInfinity(mtc.TrendChange)) mtc.TrendChange = 0;

                                    // Adjust tickTime to the desired timezone before converting to string
                                    DateTime adjustedTickTime = tickTime.Add(isNegative ? -offset : offset);
                                    trendValues.Add("{ x: new Date('" + adjustedTickTime.ToString("yyyy-MM-ddTHH:mm:ss").Replace(".", ":") + "'), y: " + mtc.TrendChange.ToString("0.00", CultureInfo.InvariantCulture) + "}");
                                }
                            }

                            // Add most recent tick
                            MarketTrendChange latestMtc = marketTrendChangeSummaries.Last();
                            if (Double.IsInfinity(latestMtc.TrendChange)) latestMtc.TrendChange = 0;

                            // Adjust latestMtc.TrendDateTime to the desired timezone before converting to string
                            DateTime adjustedLatestTrendDateTime = latestMtc.TrendDateTime.Add(isNegative ? -offset : offset);
                            trendValues.Add("{ x: new Date('" + adjustedLatestTrendDateTime.ToString("yyyy-MM-ddTHH:mm:ss").Replace(".", ":") + "'), y: " + latestMtc.TrendChange.ToString("0.00", CultureInfo.InvariantCulture) + "}");

                            // Use cached splitCamelCaseName
                            trendChartData.Add("{ key: '" + splitCamelCaseName + "', color: '" + lineColor + "', values: [" + string.Join(",\n", trendValues) + "] }");
                            mtIndex++;
                        }
                    }
                }
            }
        }
        TrendChartDataJSON = "[" + string.Join(",", trendChartData) + "]";
    }
  }
}
