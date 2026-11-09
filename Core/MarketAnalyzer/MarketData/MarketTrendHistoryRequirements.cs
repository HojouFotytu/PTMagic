using System;
using System.Collections.Generic;
using System.Linq;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Retention capacity is a prerequisite, not evidence that exchange history
  /// actually arrived. Calculators still validate every required observation.
  /// </summary>
  public sealed class MarketTrendHistoryRequirement
  {
    public string Metric { get; init; } = "";
    // Derivatives are not constrained by the separate normalized OHLCV store.
    public string SourceDataFamily { get; init; } = "NormalizedOHLCV";
    public string Basis { get; init; } = "";
    public long CalculationWindowMinutes { get; init; }
    public long MinimumClosedSourceCandles { get; init; }
    public long RequiredStoredSourceCandles { get; init; }
    public int RequiredStoreDataMaxHours { get; init; }
    public int RecommendedStoreDataMaxHours { get; init; }
    public int? ConfiguredStoreDataMaxHours { get; init; }
    public long? ConfiguredSourceCapacity { get; init; }
    public bool? HasCurrentRaidCapacity { get; init; }
    public bool? HasAllAlignmentCapacity { get; init; }
    public string Diagnostic { get; init; } = "";
  }

  public static class MarketTrendHistoryRequirements
  {
    public static IReadOnlyList<MarketTrendHistoryRequirement> Describe(MarketTrend trend,
      IReadOnlyCollection<string> metrics, TimeSpan sourceInterval, DateTimeOffset asOfUtc,
      int? configuredStoreDataMaxHours = null, int? openInterestRetentionHours = null)
    {
      if (trend == null) throw new ArgumentNullException(nameof(trend));
      MarketTrendTimeframe.Validate(trend, sourceInterval);
      long trendSourceCount = TimeSpan.FromMinutes(trend.TrendMinutes).Ticks / sourceInterval.Ticks;
      long sourceMs = checked((long)sourceInterval.TotalMilliseconds);
      long trendMs = checked((long)TimeSpan.FromMinutes(trend.TrendMinutes).TotalMilliseconds);
      long nowMs = asOfUtc.ToUniversalTime().ToUnixTimeMilliseconds();
      long incompleteTrendSourceCount = (nowMs % trendMs) / sourceMs;
      long? capacity = configuredStoreDataMaxHours.HasValue
        ? configuredStoreDataMaxHours.Value > 0
          ? (long)Math.Ceiling(TimeSpan.FromHours(configuredStoreDataMaxHours.Value).TotalMilliseconds / sourceMs) + 2
          : 0
        : null;

      List<MarketTrendHistoryRequirement> result = new List<MarketTrendHistoryRequirement>();
      foreach (string metric in (metrics ?? MarketTrendMetrics.ParseEnabled(trend))
        .Select(MarketTrendMetrics.Normalize).Distinct(StringComparer.OrdinalIgnoreCase))
      {
        if (!MarketTrendMetrics.IsKnown(metric)) continue;
        if (FuturesOpenInterestFeatureEngine.IsMetric(metric))
        {
          int oiHours = openInterestRetentionHours ?? FuturesOpenInterestDataService.DefaultRetentionHours;
          bool enough = oiHours >= 1 && oiHours <= FuturesOpenInterestDataService.MaximumRetentionHours
            && (long)trend.TrendMinutes <= (long)oiHours * 60;
          result.Add(new MarketTrendHistoryRequirement
          {
            Metric = metric, SourceDataFamily = "BinanceFuturesOpenInterest",
            Basis = "Independent Binance USD-M 5m open-interest history; two exact provider endpoints; " +
              "OpenInterestRetentionHours=" + oiHours +
              ", independent of StoreDataMaxHours and OHLCV candle retention.",
            CalculationWindowMinutes = trend.TrendMinutes, MinimumClosedSourceCandles = 2,
            RequiredStoredSourceCandles = 2,
            RequiredStoreDataMaxHours = 0, RecommendedStoreDataMaxHours = 0,
            Diagnostic = enough ? "" : "OI TrendMinutes exceeds the configured OpenInterestRetentionHours."
          });
          continue;
        }
        bool alignedTrendPeriods = false;
        long windowPeriods = 1;
        long minimumSourceCount = trendSourceCount + 1;
        string basis = "One TrendMinutes return and its exact reference close.";

        if (metric == MarketTrendMetrics.ATRP || metric == MarketTrendMetrics.MedianATRP ||
          metric == MarketTrendMetrics.VolatilityBreadth)
        {
          alignedTrendPeriods = true;
          windowPeriods = (long)trend.ATRPeriod + 1;
          minimumSourceCount = windowPeriods * trendSourceCount;
          basis = "ATR(" + trend.ATRPeriod + ") needs " + windowPeriods + " complete UTC trend periods to seed Wilder smoothing.";
        }
        else if (metric == MarketTrendMetrics.VolumeExpansion || metric == MarketTrendMetrics.MedianVolumeExpansion ||
          metric == MarketTrendMetrics.VolumeExpansionBreadth || metric == MarketTrendMetrics.StrongVolumeExpansionBreadth)
        {
          alignedTrendPeriods = true;
          windowPeriods = (long)trend.VolumeExpansionLookback + 1;
          minimumSourceCount = windowPeriods * trendSourceCount;
          basis = "Current complete trend period plus " + trend.VolumeExpansionLookback + " prior complete volume periods.";
        }
        else if (metric == MarketTrendMetrics.CorrelationToBTC || metric == MarketTrendMetrics.MeanCorrelationToBTC ||
          metric == MarketTrendMetrics.MedianCorrelationToBTC)
        {
          alignedTrendPeriods = true;
          windowPeriods = trend.CorrelationLookback;
          minimumSourceCount = windowPeriods * trendSourceCount + 1;
          basis = trend.CorrelationLookback + " synchronized simple returns require " +
            ((long)trend.CorrelationLookback + 1) + " close endpoints and every source candle between them, for asset and BTC.";
        }
        else if (metric == MarketTrendMetrics.VolumeConcentration || metric == MarketTrendMetrics.TopNVolumeShare ||
          metric == MarketTrendMetrics.BTCVolumeShare || metric == MarketTrendMetrics.BTCETHVolumeShare)
        {
          alignedTrendPeriods = true;
          minimumSourceCount = trendSourceCount;
          basis = "One complete UTC trend period of quote volume for every selected global market.";
        }
        else if (metric == MarketTrendMetrics.Concentration)
        {
          basis = "Trailing TrendMinutes quote-volume ranking plus the same-window return and reference close; complete selected population required.";
        }

        // One slot is reserved for the forming source candle returned by the
        // anchored loader. Partial trend periods consume retention but cannot
        // supply completed trend bars or correlation endpoints.
        long required = minimumSourceCount + (alignedTrendPeriods ? incompleteTrendSourceCount : 0) + 1;
        long worstAlignment = minimumSourceCount + (alignedTrendPeriods ? trendSourceCount - 1 : 0) + 1;
        int requiredHours = HoursForCapacity(required, sourceInterval);
        int recommendedHours = HoursForCapacity(worstAlignment, sourceInterval);
        bool? currentCapacity = capacity.HasValue ? capacity.Value >= required : null;
        bool? allCapacity = capacity.HasValue ? capacity.Value >= worstAlignment : null;
        string diagnostic = "";
        if (currentCapacity == false)
        {
          diagnostic = "StoreDataMaxHours=" + configuredStoreDataMaxHours + " provides " + capacity +
            " source slots; " + required + " are required at this raid boundary. Metric remains unavailable if history is missing. " +
            "Use at least " + recommendedHours + " hours to cover every UTC alignment.";
        }
        else if (allCapacity == false)
        {
          diagnostic = "Current raid fits configured retention, but a later partial UTC trend period can evict required history. " +
            "Use at least " + recommendedHours + " StoreDataMaxHours to cover every UTC alignment.";
        }

        result.Add(new MarketTrendHistoryRequirement
        {
          Metric = metric, Basis = basis, CalculationWindowMinutes = windowPeriods * trend.TrendMinutes,
          MinimumClosedSourceCandles = minimumSourceCount, RequiredStoredSourceCandles = required,
          RequiredStoreDataMaxHours = requiredHours, RecommendedStoreDataMaxHours = recommendedHours,
          ConfiguredStoreDataMaxHours = configuredStoreDataMaxHours, ConfiguredSourceCapacity = capacity,
          HasCurrentRaidCapacity = currentCapacity, HasAllAlignmentCapacity = allCapacity, Diagnostic = diagnostic
        });
      }
      return result.AsReadOnly();
    }

    private static int HoursForCapacity(long required, TimeSpan interval)
    {
      // Runtime retention uses ceil(StoreDataMaxHours/sourceInterval) + 2.
      // Current production source intervals divide an hour exactly.
      return checked((int)Math.Max(1, Math.Ceiling(Math.Max(0, required - 2) * interval.TotalHours)));
    }
  }
}
