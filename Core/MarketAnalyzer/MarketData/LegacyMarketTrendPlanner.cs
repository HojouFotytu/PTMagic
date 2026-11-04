using System;
using System.Collections.Generic;
using System.Linq;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Determines which configured MarketTrends still require pre-2.10
  /// price-return calculations. An explicit normalized Metrics list does
  /// not, by itself, require legacy per-market changes or the legacy mean.
  /// </summary>
  public sealed class LegacyMarketTrendRequirements
  {
    public HashSet<string> AssetPriceTrends { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> GlobalPriceTrends { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool NeedsAssetPriceTrend(MarketTrend trend)
    {
      return trend != null && AssetPriceTrends.Contains(trend.Name ?? "");
    }

    public bool NeedsGlobalPriceTrend(MarketTrend trend)
    {
      return trend != null && GlobalPriceTrends.Contains(trend.Name ?? "");
    }
  }

  public static class LegacyMarketTrendPlanner
  {
    public static LegacyMarketTrendRequirements Build(PTMagicConfiguration configuration)
    {
      LegacyMarketTrendRequirements required = new LegacyMarketTrendRequirements();
      if (configuration?.AnalyzerSettings?.MarketAnalyzer?.MarketTrends == null)
      {
        return required;
      }

      bool isBinanceFutures = String.Equals(
        configuration.GeneralSettings?.Application?.Exchange, "BinanceFutures",
        StringComparison.OrdinalIgnoreCase);

      foreach (MarketTrend trend in configuration.AnalyzerSettings.MarketAnalyzer.MarketTrends)
      {
        if (trend == null || String.IsNullOrWhiteSpace(trend.Name))
        {
          continue;
        }

        // No migration of legacy/CMC/other-exchange behavior. Only explicit
        // Binance Futures Exchange Metrics are eligible for selective routing.
        bool legacyOnly = !isBinanceFutures
          || !String.Equals(trend.Platform, "Exchange", StringComparison.OrdinalIgnoreCase)
          || String.IsNullOrWhiteSpace(trend.Metrics);
        bool needsGlobal = legacyOnly || IsNeededByGlobalTrigger(configuration, trend);
        bool needsAsset = needsGlobal || IsNeededBySingleMarketTrigger(configuration, trend);

        if (needsGlobal)
        {
          required.GlobalPriceTrends.Add(trend.Name);
        }
        if (needsAsset)
        {
          required.AssetPriceTrends.Add(trend.Name);
        }
      }

      return required;
    }

    private static bool IsNeededByGlobalTrigger(PTMagicConfiguration configuration, MarketTrend trend)
    {
      return (configuration.AnalyzerSettings.GlobalSettings ?? new List<GlobalSetting>())
        .Where(setting => setting?.Triggers != null)
        .SelectMany(setting => setting.Triggers)
        .Any(trigger => trigger != null
          && SameTrend(trigger.MarketTrendName, trend.Name)
          && !MarketTrendMetricTriggerResolver.UsesNormalizedMetric(trend, trigger.Metric));
    }

    private static bool IsNeededBySingleMarketTrigger(PTMagicConfiguration configuration, MarketTrend trend)
    {
      foreach (SingleMarketSetting setting in configuration.AnalyzerSettings.SingleMarketSettings
        ?? new List<SingleMarketSetting>())
      {
        if (setting == null)
        {
          continue;
        }

        if ((setting.Triggers ?? new List<Trigger>()).Any(trigger => trigger != null
          && SameTrend(trigger.MarketTrendName, trend.Name)
          && trigger.AgeDaysLowerThan <= 0
          && !IsVolumeCondition(trigger.Min24hVolume, trigger.Max24hVolume)
          && !MarketTrendMetricTriggerResolver.UsesNormalizedMetric(trend, trigger.Metric)))
        {
          return true;
        }

        if ((setting.OffTriggers ?? new List<OffTrigger>()).Any(trigger => trigger != null
          && SameTrend(trigger.MarketTrendName, trend.Name)
          && trigger.HoursSinceTriggered <= 0
          && !IsVolumeCondition(trigger.Min24hVolume, trigger.Max24hVolume)
          && !MarketTrendMetricTriggerResolver.UsesNormalizedMetric(trend, trigger.Metric)))
        {
          return true;
        }
      }
      return false;
    }

    private static bool SameTrend(string left, string right)
    {
      return !String.IsNullOrWhiteSpace(left)
        && String.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVolumeCondition(double min24hVolume, double max24hVolume)
    {
      return min24hVolume > 0 || max24hVolume < Constants.Max24hVolume;
    }
  }
}
