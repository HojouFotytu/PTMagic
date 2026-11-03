using System;
using Core.Main;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Resolves configured trigger metrics without changing the legacy PriceTrend path.
  /// Non-PriceTrend metrics are compared directly to MinChange/MaxChange; the legacy
  /// MarketTrendRelation setting is intentionally not applied to them.
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
      string normalized = MarketTrendMetrics.Normalize(metric);
      if (normalized.Equals(MarketTrendMetrics.RelativeStrengthMarket, StringComparison.OrdinalIgnoreCase)
        || normalized.Equals(MarketTrendMetrics.RelativeStrengthBTC, StringComparison.OrdinalIgnoreCase)
        || normalized.Equals(MarketTrendMetrics.Concentration, StringComparison.OrdinalIgnoreCase))
      {
        return " pp";
      }

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

      string normalizedMetric = MarketTrendMetrics.Normalize(metric);
      if (IsPriceTrend(normalizedMetric))
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
        error = "Additional MarketTrend metrics are currently available only for BinanceFutures.";
        return false;
      }

      if (!BinanceFuturesAnalyticsRuntime.ValidateMetricReference(marketTrend, normalizedMetric, globalSettingContext, out error))
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
