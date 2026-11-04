using System;
using System.Collections.Generic;
using System.Linq;
using Core.Helper;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer.MarketData
{
  public sealed class MarketTrendSelectedPopulation
  {
    public IReadOnlyList<string> GlobalSymbols { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AssetSymbols { get; init; } = Array.Empty<string>();
  }

  /// <summary>
  /// Builds normalized metric populations from the frozen Phase 0 market
  /// eligibility and the current 24h-volume market ordering, without
  /// invoking legacy historical-snapshot price-change calculations.
  /// </summary>
  public static class MarketTrendPopulationSelector
  {
    public static MarketTrendSelectedPopulation Select(
      MarketTrend trend,
      IReadOnlyDictionary<string, Market> recentMarkets,
      AnalyzerUniverseSnapshot frozenUniverse,
      string mainMarket,
      MarketDataStore normalizedStore,
      TimeSpan baseInterval,
      DateTimeOffset asOfUtc)
    {
      if (trend == null) throw new ArgumentNullException(nameof(trend));
      if (recentMarkets == null) throw new ArgumentNullException(nameof(recentMarkets));
      if (frozenUniverse == null) throw new ArgumentNullException(nameof(frozenUniverse));

      HashSet<string> eligible = frozenUniverse.Symbols.ToHashSet(StringComparer.OrdinalIgnoreCase);
      string quote = (mainMarket ?? frozenUniverse.MainMarket).Trim().ToUpperInvariant();
      HashSet<string> ignored = ParseMarketNames(trend.IgnoredMarkets, quote);
      HashSet<string> allowed = ParseMarketNames(trend.AllowedMarkets, quote);

      string[] selected = recentMarkets
        .Where(pair => pair.Value != null && !String.IsNullOrWhiteSpace(pair.Key))
        .Where(pair => eligible.Contains(pair.Key))
        .Where(pair =>
        {
          string shortName = ToBaseMarket(pair.Key, quote);
          return !ignored.Contains(shortName)
            && (allowed.Count == 0 || allowed.Contains(shortName));
        })
        .OrderByDescending(pair => pair.Value.Volume24h)
        .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
        .Select(pair => pair.Key.Trim().ToUpperInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

      // Preserve the existing MarketTrend rule: global MaxMarkets selects
      // the first N volume-ranked markets; the asset universe is not capped.
      string[] global = trend.MaxMarkets > 0
        ? selected.Take(trend.MaxMarkets).ToArray()
        : selected;

      // When TrendThreshold is explicitly configured, a return is needed
      // solely for outlier filtering. Calculate it with the normalized
      // engine, not the legacy snapshots. Invalid returns stay in the
      // eligible population so 90% coverage can fail closed.
      if (trend.TrendThreshold != 0 && normalizedStore != null && baseInterval > TimeSpan.Zero)
      {
        double threshold = Math.Abs((double)trend.TrendThreshold);
        global = global.Where(symbol =>
        {
          FeatureValueSnapshot value = MarketTrendAnalyticsEngine.CalculateReturn(
            normalizedStore.GetSnapshot(symbol, baseInterval, asOfUtc), trend.TrendMinutes, asOfUtc);
          return value == null || !value.IsValid || value.IsStale || !value.Value.HasValue
            || !double.IsFinite(value.Value.Value)
            || Math.Abs(value.Value.Value) <= threshold;
        }).ToArray();
      }

      return new MarketTrendSelectedPopulation
      {
        GlobalSymbols = Array.AsReadOnly(global),
        AssetSymbols = Array.AsReadOnly(selected)
      };
    }

    private static HashSet<string> ParseMarketNames(string tokens, string quote)
    {
      HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      if (String.IsNullOrWhiteSpace(tokens))
      {
        return result;
      }
      foreach (string token in SystemHelper.ConvertTokenStringToList(tokens, ","))
      {
        if (!String.IsNullOrWhiteSpace(token))
        {
          result.Add(ToBaseMarket(token.Trim().ToUpperInvariant(), quote));
        }
      }
      return result;
    }

    private static string ToBaseMarket(string market, string quote)
    {
      string normalized = (market ?? "").Trim().ToUpperInvariant();
      if (quote.Length > 0 && normalized.EndsWith(quote, StringComparison.OrdinalIgnoreCase))
      {
        return normalized.Substring(0, normalized.Length - quote.Length);
      }
      return normalized;
    }
  }
}
