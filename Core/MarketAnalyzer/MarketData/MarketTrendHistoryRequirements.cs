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
    // Settled funding is independent of normalized OHLCV store retention.
    public string SourceDataFamily { get; init; } = "NormalizedOHLCV";
    public string Basis { get; init; } = "";
    public long CalculationWindowMinutes { get; init; }
    public long? FundingWindowMinutes { get; init; }
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
      int? configuredStoreDataMaxHours = null,
      IReadOnlyCollection<string> deltaMetrics = null)
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
      HashSet<string> deltaNames = new HashSet<string>(deltaMetrics ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
      foreach (string metric in (metrics ?? MarketTrendMetrics.ParseEnabled(trend))
        .Select(MarketTrendMetrics.Normalize).Distinct(StringComparer.OrdinalIgnoreCase))
      {
        if (!MarketTrendMetrics.IsKnown(metric)) continue;
        bool projectedFunding = ContextualMarketMetrics.IsProjectedFunding(metric);
        long? fundingWindow = null;
        if (ContextualMarketMetrics.IsFunding(metric) || FuturesFundingFeatureEngine.IsMetric(metric) || FuturesFundingFeatureEngine.IsGlobalMetric(metric))
          fundingWindow = (long)(ContextualMarketMetrics.IsFunding(metric)
            ? ContextualMarketMetrics.FundingHistoryMinutes(trend.TrendMinutes, metric) : trend.TrendMinutes)
            + (deltaNames.Contains(metric) ? trend.TrendMinutes : 0);
        if (fundingWindow.HasValue && !projectedFunding)
        {
          result.Add(new MarketTrendHistoryRequirement
          {
            Metric = metric, SourceDataFamily = "BinanceFuturesFunding",
            Basis = ContextualMarketMetrics.IsFunding(metric)
              ? "Latest regular settlement daily-equivalent rate versus prior regular settlements with actual elapsed intervals. Verified funding history covers twice the trend window, plus one for Delta."
              : (FuturesFundingFeatureEngine.IsGlobalMetric(metric)
              ? "Global market aggregate of each selected market's latest Regular settlement; " +
                "at least 90% of the frozen global population requires valid latest rates. "
              : "") + "Fully paginated historical regular funding settlements from the exact " +
              trend.TrendMinutes + " minute MarketTrend window, with the provider's own " +
              "settlement timestamps and variable funding schedule. A complete provider " +
              "query is required; no hardcoded 8h interval or separate retention hours.",
            CalculationWindowMinutes = fundingWindow.Value, FundingWindowMinutes = fundingWindow,
            MinimumClosedSourceCandles = 0, RequiredStoredSourceCandles = 0,
            RequiredStoreDataMaxHours = 0, RecommendedStoreDataMaxHours = 0,
            ConfiguredStoreDataMaxHours = null, HasCurrentRaidCapacity = null,
            HasAllAlignmentCapacity = null, Diagnostic = ""
          });
          continue;
        }
        bool alignedTrendPeriods = false;
        long windowPeriods = 1;
        long minimumSourceCount = trendSourceCount + 1;
        string basis = "One TrendMinutes return and its exact reference close.";

        if (MarketLegMetrics.IsMetric(metric) && metric != MarketTrendMetrics.DrawdownSizeInATR && metric != MarketTrendMetrics.RallySizeInATR)
        {
          basis = "A common cohort with contiguous source closes throughout the rolling TrendMinutes window. "
            + "At least 90% coverage; the same assets and closed endpoints define the originating excursion and response.";
        }
        else if (metric == MarketTrendMetrics.Top50Breadth || metric == MarketTrendMetrics.Top75Breadth ||
          metric == MarketTrendMetrics.Top100Breadth)
        {
          basis = "Rolling return and exact reference close for the top N frozen eligible assets ranked by current exchange 24h turnover; 90% valid within the selected cohort.";
        }
        else if (metric == MarketTrendMetrics.PricePathEfficiency ||
          metric == MarketTrendMetrics.MedianPricePathEfficiency)
        {
          basis = "One pinned rolling TrendMinutes interval with N+1 exactly aligned, " +
            "fully closed normalized source prices, N=TrendMinutes/source interval. " +
            "Requires at least two close-to-close movements. A Fiat trend also " +
            "requires synchronized quote/USDT conversion closes; global median " +
            "requires at least 90% valid source-verified eligible assets.";
        }
        else if (MarketContextComparisons.IsMetric(metric))
        {
          MarketContextComparisons.Describe(trend, metric, out windowPeriods, out alignedTrendPeriods, out bool referenceClose);
          minimumSourceCount = windowPeriods * trendSourceCount + (referenceClose ? 1 : 0);
          basis = "Current observation ranked against 24 strictly preceding comparable periods, with the source metric lookback for each.";
        }
        else if (MarketTrendAnalyticsEngine.IsPreMoveRiskMetric(metric) || projectedFunding)
        {
          alignedTrendPeriods = true;
          // Current rolling return plus N+1 whole periods for ATR at its reference close.
          windowPeriods = (long)trend.ATRPeriod + (metric == MarketTrendMetrics.PreviousMedianReturnInPriorATR ? 3 : 2);
          minimumSourceCount = windowPeriods * trendSourceCount;
          basis = "Prior-range risk measurement with frozen membership, non-overlapping returns and ATR from before the measured move. "
            + "The prior Wilder ATR requires " + (trend.ATRPeriod + 1) + " complete UTC trend periods ending at or before the return reference close.";
          if (projectedFunding)
            basis = "Each contract requires positive ATRP as of the start of the horizon and verified, regular settled funding over "
              + fundingWindow + " minutes. The latest observed cadence and rate estimate the cost in the next horizon.";
        }
        else if (metric == MarketTrendMetrics.ATRP || metric == MarketTrendMetrics.MedianATRP ||
          metric == MarketTrendMetrics.VolatilityBreadth || metric == MarketTrendMetrics.MedianReturnInATR ||
          metric == MarketTrendMetrics.DrawdownSizeInATR || metric == MarketTrendMetrics.RallySizeInATR ||
          metric == MarketTrendMetrics.ReturnInATR || metric == MarketTrendMetrics.RelativeStrengthInATR)
        {
          alignedTrendPeriods = true;
          windowPeriods = (long)trend.ATRPeriod + 1;
          minimumSourceCount = windowPeriods * trendSourceCount;
          basis = "ATR(" + trend.ATRPeriod + ") needs " + windowPeriods + " complete UTC trend periods to seed Wilder smoothing.";
          if (MarketLegMetrics.IsMetric(metric))
            basis += " The numerator measures a directional excursion on the same complete constituent paths across a rolling TrendMinutes window.";
          else if (metric == MarketTrendMetrics.MedianReturnInATR || metric == MarketTrendMetrics.ReturnInATR || metric == MarketTrendMetrics.RelativeStrengthInATR)
            basis += " The signed rolling return requires the exact current and TrendMinutes-earlier source closes.";
        }
        else if (metric == MarketTrendMetrics.VolumeExpansion || metric == MarketTrendMetrics.MedianVolumeExpansion ||
          metric == ContextualMarketMetrics.MeanVolumeExpansionExcess || metric == MarketTrendMetrics.VolumeExpansionBreadth || metric == MarketTrendMetrics.StrongVolumeExpansionBreadth)
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
        else if (ContextualMarketMetrics.IsTaker(metric) || TakerFlowFeatureEngine.IsMetric(metric) ||
          TakerFlowFeatureEngine.IsGlobalMetric(metric))
        {
          alignedTrendPeriods = true;
          minimumSourceCount = trendSourceCount;
          basis = (TakerFlowFeatureEngine.IsGlobalMetric(metric)
            ? "Global metric across the frozen MaxMarkets cohort, requiring 90% verified per-asset taker coverage. "
            : "") + "One exact complete UTC MarketTrend period of source-verified taker-buy and total quote volume, with no missing 5m candles.";
        }
        else if (FlowPriceConfirmationEngine.IsAssetMetric(metric) ||
          FlowPriceConfirmationEngine.IsGlobalMetric(metric))
        {
          alignedTrendPeriods = true;
          minimumSourceCount = trendSourceCount;
          basis = (FlowPriceConfirmationEngine.IsGlobalMetric(metric)
            ? "At least 90% of the frozen eligible cohort must be observed for quote-volume-weighted net aggressive flow share; verified no-trade markets count as observed with zero volume, while missing markets remain excluded from the volume denominator. "
            : "Binary asset agreement/divergence flag. ") +
            "One exact UTC-completed TrendMinutes period of contiguous verified taker-buy and total quote-volume candles, " +
            "plus the first candle open and final candle close captured in the existing taker-flow pass. " +
            "No additional lookback or exchange endpoint.";
        }
        else if (metric == MarketTrendMetrics.Concentration)
        {
          basis = "Trailing TrendMinutes quote-volume ranking plus the same-window return and reference close; complete selected population required.";
        }

        if (deltaNames.Contains(metric))
        {
          // Historical comparison must retain one additional MarketTrend window.
          // Both current and prior values have their own source-valid history.
          minimumSourceCount = checked(minimumSourceCount + trendSourceCount);
          basis += " Delta compares against the same metric one TrendMinutes earlier.";
          if (metric == MarketTrendMetrics.FundingRate || metric == MarketTrendMetrics.FundingSum ||
              FuturesFundingFeatureEngine.IsGlobalMetric(metric))
            basis += " Funding is managed by its separate provider-backed history store.";
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
          Metric = metric, Basis = basis, FundingWindowMinutes = fundingWindow, CalculationWindowMinutes =
            checked(windowPeriods * trend.TrendMinutes + (deltaNames.Contains(metric) ? (long)trend.TrendMinutes : 0L)),
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
