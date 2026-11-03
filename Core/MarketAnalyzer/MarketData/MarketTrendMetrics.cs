using System;
using System.Collections.Generic;
using System.Linq;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer.MarketData
{
  public enum MarketTrendMetricScope
  {
    Global,
    Asset,
    Both
  }

  /// <summary>
  /// Canonical names and scope rules for metrics exposed through MarketTrend.Metrics
  /// and Trigger/OffTrigger.Metric. PriceTrend remains implicit and is always enabled.
  /// </summary>
  public static class MarketTrendMetrics
  {
    public const string PriceTrend = "PriceTrend";
    public const string MeanReturn = "MeanReturn";
    public const string MedianReturn = "MedianReturn";
    public const string Breadth = "Breadth";
    public const string Dispersion = "Dispersion";
    public const string RelativeStrengthMarket = "RelativeStrengthMarket";
    public const string RelativeStrengthBTC = "RelativeStrengthBTC";
    public const string Concentration = "Concentration";

    private static readonly Dictionary<string, MarketTrendMetricScope> KnownMetrics =
      new Dictionary<string, MarketTrendMetricScope>(StringComparer.OrdinalIgnoreCase)
      {
        [PriceTrend] = MarketTrendMetricScope.Both,
        [MeanReturn] = MarketTrendMetricScope.Global,
        [MedianReturn] = MarketTrendMetricScope.Global,
        [Breadth] = MarketTrendMetricScope.Global,
        [Dispersion] = MarketTrendMetricScope.Global,
        [RelativeStrengthMarket] = MarketTrendMetricScope.Asset,
        [RelativeStrengthBTC] = MarketTrendMetricScope.Asset,
        [Concentration] = MarketTrendMetricScope.Global
      };

    public static IReadOnlyCollection<string> Names => KnownMetrics.Keys.ToArray();

    public static string Normalize(string metric)
    {
      if (String.IsNullOrWhiteSpace(metric))
      {
        return PriceTrend;
      }

      string candidate = metric.Trim();
      foreach (string knownMetric in KnownMetrics.Keys)
      {
        if (knownMetric.Equals(candidate, StringComparison.OrdinalIgnoreCase))
        {
          return knownMetric;
        }
      }

      return candidate;
    }

    public static bool IsKnown(string metric)
    {
      return KnownMetrics.ContainsKey(Normalize(metric));
    }

    public static bool TryGetScope(string metric, out MarketTrendMetricScope scope)
    {
      return KnownMetrics.TryGetValue(Normalize(metric), out scope);
    }

    public static IReadOnlyCollection<string> ParseEnabled(MarketTrend marketTrend)
    {
      HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
      {
        PriceTrend
      };

      if (marketTrend == null || String.IsNullOrWhiteSpace(marketTrend.Metrics))
      {
        return result.ToArray();
      }

      foreach (string token in marketTrend.Metrics.Split(',', StringSplitOptions.RemoveEmptyEntries))
      {
        string metric = Normalize(token);
        if (!String.IsNullOrWhiteSpace(metric))
        {
          result.Add(metric);
        }
      }

      return result.ToArray();
    }

    public static bool IsEnabled(MarketTrend marketTrend, string metric)
    {
      string normalized = Normalize(metric);
      if (normalized.Equals(PriceTrend, StringComparison.OrdinalIgnoreCase))
      {
        return true;
      }

      return ParseEnabled(marketTrend).Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }
  }
}
