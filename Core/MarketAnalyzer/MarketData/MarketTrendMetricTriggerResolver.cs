using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Core.Main;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Resolves normalized metrics for all supported Binance exchanges.
  /// Omitted Trigger/OffTrigger Metric defaults to normalized PriceTrend.
  /// </summary>
  public static class MarketTrendMetricTriggerResolver
  {
    public static bool IsPriceTrend(string metric)
    {
      return MarketTrendMetrics.Normalize(metric).Equals(MarketTrendMetrics.PriceTrend, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// All supported Binance Exchange trends use the normalized engine, including
    /// omitted Metric values that default to PriceTrend.
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
        if (!BinanceFuturesAnalyticsRuntime.CurrentTrendAnalyses.TryGetValue(
          trend.Name, out MarketTrendAnalysisSnapshot analysis) ||
          !TryResolveSmsRelative(analysis, value, out double adjusted, out error))
        {
          if (String.IsNullOrWhiteSpace(error))
          {
            error = "No normalized SMS-relative PriceTrend reference is available for MarketTrend '" +
              trend.Name + "'.";
          }
          return false;
        }
        value = adjusted;
      }
      else if (String.Equals(relation, Constants.MarketTrendRelationRelativeTrigger, StringComparison.OrdinalIgnoreCase))
      {
        if (!double.IsFinite(currentPrice) || !double.IsFinite(activationPrice)
          || currentPrice <= 0 || activationPrice <= 0)
        {
          error = "Relative-to-trigger PriceTrend requires positive finite prices (market=" + marketSymbol +
            ", currentPrice=" + currentPrice.ToString("G", CultureInfo.InvariantCulture) +
            ", activationPrice=" + activationPrice.ToString("G", CultureInfo.InvariantCulture) + ").";
          return false;
        }
        value = (currentPrice / activationPrice - 1.0) * 100.0;
      }
      if (!double.IsFinite(value))
      {
        error = "PriceTrend relation '" + relation + "' produced a nonfinite result for market '" +
          marketSymbol + "'.";
        return false;
      }
      return true;
    }

    /// <summary>
    /// The legacy SMS-relative reference is the mean of ALL eligible asset
    /// PriceTrend values, not the MaxMarkets-capped GlobalSetting average.
    /// The feature already applies only PriceTrend's TrendThreshold and the
    /// normalized minimum-coverage policy.
    /// </summary>
    public static bool TryResolveSmsRelative(
      MarketTrendAnalysisSnapshot analysis, double assetPriceTrend,
      out double relativePriceTrend, out string error)
    {
      relativePriceTrend = 0;
      error = "";
      FeatureValueSnapshot reference = analysis?.SmsRelativePriceTrend;
      if (reference == null || !reference.IsValid || reference.IsStale ||
        !reference.Value.HasValue || !double.IsFinite(reference.Value.Value))
      {
        error = "The uncapped SMS Relative PriceTrend benchmark is unavailable for MarketTrend '" +
          (analysis?.MarketTrendName ?? "(unknown)") + "': " +
          (String.IsNullOrWhiteSpace(reference?.InvalidReason)
            ? "missing or stale asset returns." : reference.InvalidReason);
        return false;
      }

      relativePriceTrend = assetPriceTrend - reference.Value.Value;
      if (!double.IsFinite(relativePriceTrend))
      {
        error = "SMS Relative PriceTrend produced a nonfinite result.";
        return false;
      }
      return true;
    }

    public static string GetUnit(string metric)
    {
      string metricName = MarketTrendMetrics.Normalize(metric);
      if (TakerFlowFeatureEngine.IsMetric(metricName)) return "";
      if (metricName == MarketTrendMetrics.CorrelationToBTC ||
        metricName == MarketTrendMetrics.MeanCorrelationToBTC ||
        metricName == MarketTrendMetrics.MedianCorrelationToBTC)
      {
        return "";
      }
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
        error = "Metric must reference a supported Binance Exchange MarketTrend.";
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

      if (FuturesFundingFeatureEngine.HasAnyMetrics(new[] { normalizedMetric }) &&
        !String.Equals(configuration.GeneralSettings.Application.Exchange, "BinanceFutures", StringComparison.OrdinalIgnoreCase))
      {
        error = "Metric '" + normalizedMetric + "' requires BinanceFutures; funding is unavailable on Spot/BinanceUS.";
        return false;
      }

      if (!BinanceFuturesAnalyticsRuntime.ValidateMetricReference(marketTrend, metric, globalSettingContext, out error))
      {
        error += " (MarketTrend='" + marketTrend.Name + "', requested metric='" + normalizedMetric +
          "', context=" + (globalSettingContext ? "GlobalSetting" : "SMS") +
          ", requested market='" + (marketSymbol ?? "(global)") +
          "', configured Metrics='" + (marketTrend.Metrics ?? "") +
          "', TrendMinutes=" + marketTrend.TrendMinutes + ").";
        return false;
      }

      if (!BinanceFuturesAnalyticsRuntime.TryGetMetric(
        marketTrend.Name,
        normalizedMetric,
        marketSymbol,
        out FeatureValueSnapshot feature))
      {
        error = ExplainMissingMetric(configuration, marketTrend, normalizedMetric, marketSymbol);
        return false;
      }

      // Snapshot validity is assessed at its pinned time. A long refresh must
      // not turn that historical consistency into permission to act much later.
      if (!BinanceFuturesAnalyticsRuntime.IsDecisionFresh(configuration, out error))
      {
        return false;
      }

      if (feature == null || !feature.IsValid || feature.IsStale || !feature.Value.HasValue || !double.IsFinite(feature.Value.Value))
      {
        error = "Metric '" + normalizedMetric + "' is unavailable for MarketTrend '" + marketTrend.Name +
          "'" + (String.IsNullOrWhiteSpace(marketSymbol) ? " (global)" : " market='" + marketSymbol + "'") +
          ": valid=" + (feature?.IsValid.ToString() ?? "null") +
          ", stale=" + (feature?.IsStale.ToString() ?? "null") +
          ", value=" + (feature?.Value.HasValue == true ? feature.Value.Value.ToString("G", CultureInfo.InvariantCulture) : "null") +
          ", samples=" + (feature?.SampleCount.ToString(CultureInfo.InvariantCulture) ?? "null") +
          (feature?.EligibleSampleCount.HasValue == true ? "/" + feature.EligibleSampleCount.Value : "") +
          (feature?.CoveragePercent.HasValue == true ? ", coverage=" +
            feature.CoveragePercent.Value.ToString("0.##", CultureInfo.InvariantCulture) + "%" : "") +
          (feature?.SourceDataTimestampUtc.HasValue == true ? ", endpoint=" +
            feature.SourceDataTimestampUtc.Value.ToString("O", CultureInfo.InvariantCulture) : "") +
          (String.IsNullOrWhiteSpace(feature?.InvalidReason) ? "." : "; reason=" + feature.InvalidReason);
        return false;
      }

      value = feature.Value.Value;
      return true;
    }

    /// <summary>
    /// Distinguishes a missing MarketTrend evaluation from a request for an
    /// intentionally excluded asset. MaxMarkets affects global aggregation only.
    /// </summary>
    private static string ExplainMissingMetric(
      PTMagicConfiguration configuration, MarketTrend trend,
      string metric, string marketSymbol)
    {
      IReadOnlyDictionary<string, MarketTrendAnalysisSnapshot> analyses =
        BinanceFuturesAnalyticsRuntime.CurrentTrendAnalyses;
      if (analyses == null || !analyses.TryGetValue(trend.Name, out MarketTrendAnalysisSnapshot analysis))
      {
        return "No normalized analysis snapshot for MarketTrend '" + trend.Name + "'; metric='" + metric +
          "', requested market='" + (marketSymbol ?? "(global)") +
          "', available trends=" + (analyses == null ? 0 : analyses.Count) + ".";
      }

      string population = "global population=" + analysis.GlobalSymbols.Count +
        ", asset population=" + analysis.AssetSymbols.Count +
        ", MaxMarkets=" + trend.MaxMarkets +
        ", TrendThreshold=" + trend.TrendThreshold +
        ", AllowedMarkets='" + (trend.AllowedMarkets ?? "") +
        "', IgnoredMarkets='" + (trend.IgnoredMarkets ?? "") + "'";

      if (!String.IsNullOrWhiteSpace(marketSymbol) &&
        MarketTrendMetrics.TryGetScope(metric, out MarketTrendMetricScope scope) &&
        (scope == MarketTrendMetricScope.Asset || scope == MarketTrendMetricScope.Both))
      {
        if (!analysis.AssetSymbols.Contains(marketSymbol, StringComparer.OrdinalIgnoreCase))
        {
          string quote = BinanceFuturesAnalyticsRuntime.CurrentUniverse?.MainMarket ?? "";
          string shortSymbol = marketSymbol;
          if (quote.Length > 0 && shortSymbol.EndsWith(quote, StringComparison.OrdinalIgnoreCase))
          {
            shortSymbol = shortSymbol.Substring(0, shortSymbol.Length - quote.Length);
          }
          string excludedBy = MatchesFilter(trend.IgnoredMarkets, shortSymbol, marketSymbol)
            ? "excluded by IgnoredMarkets"
            : !String.IsNullOrWhiteSpace(trend.AllowedMarkets) &&
              !MatchesFilter(trend.AllowedMarkets, shortSymbol, marketSymbol)
              ? "not in AllowedMarkets"
              : "not in the current eligible/selected exchange population";

          return "Market '" + marketSymbol + "' is outside the selected asset population for MarketTrend '" +
            trend.Name + "' (" + excludedBy + "; " + population +
            "). MaxMarkets limits global aggregation only; no per-asset metric is available for this symbol.";
        }

        string available = analysis.AssetMetrics.TryGetValue(marketSymbol,
          out IReadOnlyDictionary<string, FeatureValueSnapshot> assetFeatures)
          ? String.Join(",", assetFeatures.Keys) : "(no asset snapshot)";
        return "Market '" + marketSymbol + "' is eligible for MarketTrend '" + trend.Name +
          "' but metric '" + metric + "' was not published; available asset metrics=" + available +
          "; " + population + ".";
      }

      return "Metric '" + metric + "' has no calculated global value for MarketTrend '" + trend.Name +
        "'; published global metrics=" + String.Join(",", analysis.MarketMetrics.Keys) +
        "; evaluated metrics=" + String.Join(",", analysis.EnabledMetrics) +
        "; " + population + ".";
    }

    private static bool MatchesFilter(string filter, string shortSymbol, string fullSymbol)
    {
      return !String.IsNullOrWhiteSpace(filter) && filter.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Any(token =>
        {
          string candidate = token.Trim();
          return candidate.Equals(shortSymbol, StringComparison.OrdinalIgnoreCase)
            || candidate.Equals(fullSymbol, StringComparison.OrdinalIgnoreCase);
        });
    }
  }
}
