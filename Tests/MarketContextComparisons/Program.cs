using Core.Main.DataObjects.PTMagicData;
using Core.MarketAnalyzer.MarketData;

internal static class Program
{
  private static readonly TimeSpan Step = TimeSpan.FromMinutes(5);
  private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;
  private static readonly string[] Symbols = { "BTCUSDT", "AUSDT", "BUSDT", "CUSDT" };
  private static readonly string[] GlobalMetrics = {
    "BreadthChangePercentile", "VolumeExpansionBreadthChangePercentile", "MedianVolumeExpansionPercentile",
    "MedianCorrelationToBTCPercentile", "MedianCorrelationToBTCChangePercentile", "MedianPricePathEfficiencyPercentile",
    "MedianPricePathEfficiencyChangePercentile", "BuyerFlowPriceConfirmationVolumeSharePercentile",
    "SellerFlowPriceConfirmationVolumeSharePercentile"
  };
  private static readonly string[] AssetMetrics = {
    "TakerImbalancePercentile", "TakerImbalanceChangePercentile", "PricePathEfficiencyChangePercentile"
  };
  private static string Source(string metric) => metric.Replace("ChangePercentile", "").Replace("Percentile", "");
  private static void Require(bool ok, string description)
  {
    if (!ok) throw new InvalidOperationException(description);
  }

  private static MarketDataStore Sample(IEnumerable<string> symbols, int count, bool constantFlow = false)
  {
    var store = new MarketDataStore(count + 4);
    int index = 0;
    foreach (string symbol in symbols)
    {
      var rng = new Random(++index);
      double price = 100;
      for (int i = 0; i < count; i++)
      {
        var open = Start.AddMinutes(i * 5);
        double close = price * (1 + 0.004 * Math.Sin(i * 0.8) + 0.012 * (rng.NextDouble() - 0.5));
        double volume = 100 + rng.NextDouble() * 100;
        store.UpsertCandle(new MarketCandle {
          Symbol = symbol, Interval = Step, OpenTimeUtc = open, CloseTimeUtc = open.Add(Step).AddMilliseconds(-1),
          Open = price, High = Math.Max(price, close), Low = Math.Min(price, close), Close = close, BaseVolume = volume / close,
          QuoteVolume = volume, TakerBuyQuoteVolume = volume * (constantFlow ? 0.5 : rng.NextDouble()),
          TradeCount = 2, IsClosed = true
        });
        price = close;
      }
    }
    return store;
  }

  public static void Main()
  {
    const int count = 300;
    // Deliberately not a UTC 15-minute boundary: price-path and breadth classifiers read the
    // latest five-minute endpoint while flow, volume and BTC correlation use complete 15m bars.
    var at = Start.AddMinutes((count - 1) * 5 + 2);
    var trend = new MarketTrend { Name = "ContextParity", TrendMinutes = 15, TrendCurrency = "Market",
      CorrelationLookback = 6, VolumeExpansionLookback = 4, VolumeExpansionBreadthThreshold = 1 };
    var requested = GlobalMetrics.Concat(AssetMetrics).ToArray();
    var underlying = requested.Select(Source).Distinct().ToArray();
    var store = Sample(Symbols, count);
    var actual = MarketContextComparisons.Calculate(trend, requested, Symbols, Symbols, store, Step, at, "BTCUSDT", false);
    // The reference deliberately re-evaluates each original native primitive at every historical end.
    // It is an independent correctness oracle. Production never makes these 26 complete analytics calls.
    var windows = Enumerable.Range(0, 26).Select(k => MarketTrendAnalyticsEngine.Calculate(trend,
      Symbols, Symbols, store, Step, at.AddMinutes(-k * trend.TrendMinutes), underlying)).ToArray();
    int checks = 0;
    void Compare(string metric, string? symbol)
    {
      string source = Source(metric);
      var x = windows.Select(w => (symbol == null ? w.MarketMetrics[source] : w.AssetMetrics[symbol][source]).Value!.Value).ToArray();
      if (metric.Contains("ChangePercentile")) x = x.Zip(x.Skip(1), (current, previous) => current - previous).ToArray();
      var prior = x.Skip(1).Take(24).ToArray();
      double expected = 100.0 * (prior.Count(v => v < x[0]) + prior.Count(v => v == x[0]) * 0.5) / 24;
      var found = symbol == null ? actual.GlobalMetrics[metric] : actual.AssetMetrics[symbol][metric];
      Require(found.IsValid && found.Value.HasValue && Math.Abs(found.Value.Value - expected) < 1e-8,
        $"{metric} {symbol} expected {expected}, got {found.Value}, {found.InvalidReason}");
      checks++;
    }
    foreach (var key in GlobalMetrics) Compare(key, null);
    foreach (var symbol in Symbols) foreach (var key in AssetMetrics) Compare(key, symbol);
    var sparse = MarketContextComparisons.Calculate(trend, requested, Symbols, Symbols,
      Sample(Symbols, 60), Step, Start.AddHours(5), "BTCUSDT", false);
    Require(!sparse.GlobalMetrics.Values.Any(v => v.IsValid) && !sparse.AssetMetrics.Values.Any(a => a.Values.Any(v => v.IsValid)),
      "Missing historical measurement must not be filled with zero");
    var flat = MarketContextComparisons.Calculate(trend, AssetMetrics, Symbols, Symbols, Sample(Symbols, count, true), Step, at, "BTCUSDT", false);
    foreach (var symbol in Symbols) Require(flat.AssetMetrics[symbol]["TakerImbalancePercentile"].Value == 50, "Tie midrank");
    var missing = MarketContextComparisons.Calculate(trend, GlobalMetrics, Symbols.Concat(new[] { "MISSING" }).ToArray(),
      Array.Empty<string>(), store, Step, at, "BTCUSDT", false);
    Require(missing.GlobalMetrics.Values.All(v => !v.IsValid && v.EligibleSampleCount == 5), "Retain missing symbol in coverage denominator");
    var after = Sample(Symbols, count + 40);
    var frozen = MarketContextComparisons.Calculate(trend, requested, Symbols, Symbols, after, Step, at, "BTCUSDT", false);
    Require(GlobalMetrics.All(k => actual.GlobalMetrics[k].Value == frozen.GlobalMetrics[k].Value)
      && Symbols.All(s => AssetMetrics.All(k => actual.AssetMetrics[s][k].Value == frozen.AssetMetrics[s][k].Value)),
      "Future candle append changes historical classification");
    var endpointsOnly = new MarketDataStore(count);
    foreach (string symbol in Symbols)
    {
      var all = store.GetSnapshot(symbol, Step, at).ClosedCandles;
      endpointsOnly.UpsertCandles(all.Where(c => ((long)(c.OpenTimeUtc - Start).TotalMinutes / 5) % 3 == (count - 2) % 3));
    }
    var gaps = MarketContextComparisons.Calculate(trend, GlobalMetrics, Symbols, Symbols, endpointsOnly, Step, at, "BTCUSDT", false);
    Require(gaps.GlobalMetrics["BreadthChangePercentile"].Value == actual.GlobalMetrics["BreadthChangePercentile"].Value
      && !gaps.GlobalMetrics["MedianPricePathEfficiencyPercentile"].IsValid, "Sparse exact endpoints support returns only");
    var fiat = new MarketTrend { Name = "FiatParity", TrendMinutes = 15, TrendCurrency = "Fiat",
      CorrelationLookback = 6, VolumeExpansionLookback = 4 };
    string[] nonUsd = { "BTCBNB", "ABNB", "BBNB", "CBNB" };
    var fiatStore = Sample(nonUsd.Append("BNBUSDT"), count);
    string[] fiatMetrics = { "BreadthChangePercentile", "MedianPricePathEfficiencyPercentile",
      "MedianCorrelationToBTCPercentile", "BuyerFlowPriceConfirmationVolumeSharePercentile" };
    var fiatRanks = MarketContextComparisons.Calculate(fiat, fiatMetrics, nonUsd, Array.Empty<string>(),
      fiatStore, Step, at, "BTCBNB", true);
    var fiatWindows = Enumerable.Range(0, 26).Select(k => MarketTrendAnalyticsEngine.Calculate(fiat,
      nonUsd, Array.Empty<string>(), fiatStore, Step, at.AddMinutes(-k * fiat.TrendMinutes),
      fiatMetrics.Select(Source).ToArray(), "BTCBNB")).ToArray();
    foreach (string metric in fiatMetrics)
    {
      double[] sequence = fiatWindows.Select(x => x.MarketMetrics[Source(metric)].Value!.Value).ToArray();
      if (metric.Contains("ChangePercentile"))
        sequence = sequence.Zip(sequence.Skip(1), (current, prior) => current - prior).ToArray();
      var reference = sequence.Skip(1).Take(24).ToArray();
      double percentile = 100.0 * (reference.Count(x => x < sequence[0]) + 0.5 * reference.Count(x => x == sequence[0])) / 24;
      Require(fiatRanks.GlobalMetrics[metric].Value == percentile, "Fiat context " + metric);
      checks++;
    }
    var noFx = MarketContextComparisons.Calculate(fiat, fiatMetrics, nonUsd, Array.Empty<string>(),
      Sample(nonUsd, count), Step, at, "BTCBNB", true);
    Require(!noFx.GlobalMetrics["MedianPricePathEfficiencyPercentile"].IsValid
      && !noFx.GlobalMetrics["MedianCorrelationToBTCPercentile"].IsValid
      && noFx.GlobalMetrics["BuyerFlowPriceConfirmationVolumeSharePercentile"].IsValid,
      "Missing conversion must affect only price-derived inputs");
    Console.WriteLine($"PASS, {checks} native reference comparisons, missing-history, coverage, tie, no-lookahead checks");
  }
}
