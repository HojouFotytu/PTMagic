using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  public sealed class AnalyzerUniverseMarket
  {
    public string Symbol { get; init; } = "";
    public string ContractType { get; init; } = "";
    public string UnderlyingType { get; init; } = "";
    public IReadOnlyList<string> UnderlyingSubTypes { get; init; } = Array.Empty<string>();
  }

  /// <summary>
  /// Immutable definition of the exact market population used by one analytics cycle.
  /// </summary>
  public sealed class AnalyzerUniverseSnapshot
  {
    public AnalyzerUniverseSnapshot(DateTimeOffset timestampUtc, string mainMarket, IEnumerable<AnalyzerUniverseMarket> markets, string selectionReason = "")
    {
      if (String.IsNullOrWhiteSpace(mainMarket))
      {
        throw new ArgumentException("The analyzer universe main market is required.", nameof(mainMarket));
      }
      if (markets == null)
      {
        throw new ArgumentNullException(nameof(markets));
      }

      TimestampUtc = timestampUtc.ToUniversalTime();
      MainMarket = mainMarket.Trim().ToUpperInvariant();
      SelectionReason = selectionReason ?? "";

      AnalyzerUniverseMarket[] normalizedMarkets = markets
        .Where(market => market != null && !String.IsNullOrWhiteSpace(market.Symbol))
        .GroupBy(market => market.Symbol, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.Last())
        .OrderBy(market => market.Symbol, StringComparer.OrdinalIgnoreCase)
        .ToArray();

      Markets = new ReadOnlyCollection<AnalyzerUniverseMarket>(normalizedMarkets);
      Symbols = new ReadOnlyCollection<string>(normalizedMarkets.Select(market => market.Symbol.Trim().ToUpperInvariant()).ToArray());
    }

    public DateTimeOffset TimestampUtc { get; }
    public string MainMarket { get; }
    public string SelectionReason { get; }
    public IReadOnlyList<AnalyzerUniverseMarket> Markets { get; }
    public IReadOnlyList<string> Symbols { get; }
    public int MarketCount => Markets.Count;
  }
}
