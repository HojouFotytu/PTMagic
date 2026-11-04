using System;
using Core.Main;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Resolves explicitly configured normalized trigger metrics while preserving the
  /// legacy PriceTrend path when Trigger/OffTrigger.Metric is omitted or the referenced
  /// MarketTrend has no normalized Metrics configured.
  /// </summary>
  public static class MarketTrendMetricTriggerResolver
  {
    public static bool IsPriceTrend(string metric)
    {
      return MarketTrendMetrics.Normalize(metric).Equals(MarketTrendMetrics.PriceTrend, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Only an explicit metric selects the normalized engine. An omitted Metric,
    /// or PriceTrend on a legacy-only MarketTrend, preserves the old trigger path.
    /// </summary>
    public static bool UsesNormalizedMetric(MarketTrend marketTrend, string metric, string exchange = "BinanceFutures")
    {
      return marketTrend != null
        && String.Equals(marketTrend.Platform, "Exchange", StringComparison.OrdinalIgnoreCase)
        && BinanceFuturesAnalyticsRuntime.IsSupportedExchange(exchange);
    }

    public static bool TryResolveGlobal(
      PTMagicConfiguration configuration,
      MarketTrend marketTrend,
      string metric,
      out double value,
      out string error)
    {
      return TryResolve(configuration, marketTrend, metric, null, true, out value, out error);
    }

    public static bool TryResolveSingle(
      PTMagicConfiguration configuration,
      MarketTrend marketTrend,
      string metric,
      string marketSymbol,
      out double value,
      out string error)
    {
      return TryResolve(configuration, marketTrend, metric, marketSymbol, false, out value, out error);
    }

    /// <summary>
    /// Uses normalized returns while preserving the original SMS
    /// relative-to-market and relative-to-activation price comparisons.
    /// </summary>
    public static bool TryResolveSingleWithRelation(
      PTMagicConfiguration configuration, MarketTrend trend, string metric,
      string marketSymbol, string relation, double currentPrice, double activationPrice,
      out double value, out string error)
    {
      if (!TryResolveSingle(configuration, trend, metric, marketSymbol, out value, out error))
      {
        return false;
      }
      if (!IsPriceTrend(metric))
      {
        return true;
      }
      if (String.Equals(relation, Constants.MarketTrendRelationRelative, StringComparison.OrdinalIgnoreCase))
      {
        if (!TryResolveGlobal(configuration, trend, metric, out double global, out error))
        {
          return false;
        }
        value -= global;
      }
      else if (String.Equals(relation, Constants.MarketTrendRelationRelativeTrigger, StringComparison.OrdinalIgnoreCase))
      {
        if (!double.IsFinite(currentPrice) || !double.IsFinite(activationPrice)
          || currentPrice <= 0 || activationPrice <= 0)
        {
          error = "Relative-to-trigger PriceTrend requires valid current and saved activation prices.";
          return false;
        }
        value = (currentPrice / activationPrice - 1.0) * 100.0;
      }
      return double.IsFinite(value);
    }

    public static string GetUnit(string metric)
    {
      string metricName = MarketTrendMetrics.Normalize(metric);
      if (metricName == MarketTrendMetrics.VolumeExpansion || metricName == MarketTrendMetrics.MedianVolumeExpansion)
      {
        return "x";
      }
      return metricName == MarketTrendMetrics.VolumeConcentration ? " index" : "%";
    }

    private static bool TryResolve(
      PTMagicConfiguration configuration,
      MarketTrend marketTrend,
      string metric,
      string marketSymbol,
      bool globalSettingContext,
      out double value,
      out string error)
    {
      value = 0;
      error = "";

      string normalizedMetric = MarketTrendMetrics.Normalize(metric);

      // Every supported Binance Exchange trigger resolves normalized metrics.
      // Omitted Metric maps to PriceTrend, even with a nonempty Metrics list.
      if (!UsesNormalizedMetric(marketTrend, metric,
        configuration?.GeneralSettings?.Application?.Exchange))
      {
        error = "PriceTrend is resolved by PTMagic's legacy trend path.";
        return false;
      }

      if (configuration == null)
      {
        error = "PTMagic configuration is unavailable.";
        return false;
      }

      if (!BinanceFuturesAnalyticsRuntime.IsSupportedExchange(configuration.GeneralSettings?.Application?.Exchange))
      {
        error = "Normalized MarketTrend metrics require Binance, BinanceUS or BinanceFutures.";
        return false;
      }

      if (!BinanceFuturesAnalyticsRuntime.ValidateMetricReference(marketTrend, metric, globalSettingContext, out error))
      {
        return false;
      }

      if (!BinanceFuturesAnalyticsRuntime.TryGetMetric(
        marketTrend.Name,
        normalizedMetric,
        marketSymbol,
        out FeatureValueSnapshot feature))
      {
        error = "Metric '" + normalizedMetric + "' has no calculated value for MarketTrend '" + marketTrend.Name + "'.";
        return false;
      }

      if (feature == null || !feature.IsValid || feature.IsStale || !feature.Value.HasValue || !double.IsFinite(feature.Value.Value))
      {
        error = "Metric '" + normalizedMetric + "' is unavailable for MarketTrend '" + marketTrend.Name + "'"
          + (String.IsNullOrWhiteSpace(feature?.InvalidReason) ? "." : ": " + feature.InvalidReason);
        return false;
      }

      value = feature.Value.Value;
      return true;
    }
  }
}
