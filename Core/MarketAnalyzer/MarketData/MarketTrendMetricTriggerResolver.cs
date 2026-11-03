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

    public static string GetUnit(string metric)
    {
      return "%";
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

      bool metricOmitted = String.IsNullOrWhiteSpace(metric);
      string normalizedMetric = MarketTrendMetrics.Normalize(metric);

      // Omitted Metric preserves the pre-Phase-1 PriceTrend trigger path. An explicitly
      // named PriceTrend also remains legacy when the MarketTrend has no Metrics configured.
      if (metricOmitted
        || (IsPriceTrend(normalizedMetric) && String.IsNullOrWhiteSpace(marketTrend?.Metrics)))
      {
        error = "PriceTrend is resolved by PTMagic's legacy trend path.";
        return false;
      }

      if (configuration == null)
      {
        error = "PTMagic configuration is unavailable.";
        return false;
      }

      if (!configuration.GeneralSettings.Application.Exchange.Equals("BinanceFutures", StringComparison.OrdinalIgnoreCase))
      {
        error = "Normalized MarketTrend metrics are currently available only for BinanceFutures.";
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
