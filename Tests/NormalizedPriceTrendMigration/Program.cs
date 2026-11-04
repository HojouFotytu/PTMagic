using System;
using System.Collections.Generic;
using System.Linq;
using Core.Main.DataObjects.PTMagicData;
using Core.MarketAnalyzer.MarketData;

internal static class Program
{
  private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
  private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
  private static readonly DateTimeOffset AsOf = Start.AddMinutes(65).AddSeconds(1);
  private static int passed;

  private static int Main()
  {
    try
    {
      TestBlankDefault();
      TestExplicitPriceTrend();
      TestExplicitOtherMetrics();
      TestImplicitGlobal();
      TestImplicitSms();
      TestInsufficientCoverage();
      TestIncompleteCandle();
      TestIndependentPopulations();
      TestUsdBenchmark();
      TestBothScope();
      TestAllBinanceExchanges();
      TestImplicitMetricValidation();
      Console.WriteLine("Normalized PriceTrend migration tests passed: " + passed + ".");
      return 0;
    }
    catch (Exception e)
    {
      Console.Error.WriteLine("Normalized PriceTrend migration tests FAILED: " + e);
      return 1;
    }
  }

  private static MarketTrend Trend(string metrics = "") =>
    new MarketTrend { Name = "1h", Platform = "Exchange", TrendMinutes = 60, Metrics = metrics };

  private static MarketDataStore Store(string quote = "USDT", bool omitBtcFirst = false, bool incomplete = false)
  {
    MarketDataStore store = new MarketDataStore(200);
    for (int i = 0; i <= 12; i++)
    {
      if (i != 0 || !omitBtcFirst) Add(store, "BTC" + quote, i, 100 + 10.0*i/12, true);
      Add(store, "ETH" + quote, i, 100 + 20.0*i/12, true);
    }
    if (incomplete)
    {
      Add(store, "BTC" + quote, 13, 1000, false);
      Add(store, "ETH" + quote, 13, 1000, false);
    }
    return store;
  }

  private static void Add(MarketDataStore store, string symbol, int index, double price, bool closed)
  {
    DateTimeOffset open = Start.AddMinutes(index*5);
    store.UpsertCandle(new MarketCandle
    {
      Symbol = symbol, Interval = Interval, OpenTimeUtc = open,
      CloseTimeUtc = open.Add(Interval).AddMilliseconds(-1), Open = price,
      High = price, Low = price, Close = price, QuoteVolume = 1000,
      BaseVolume = 10, TradeCount = 10, IsClosed = closed
    });
  }

  private static MarketTrendAnalysisSnapshot Calc(MarketTrend trend, MarketDataStore store,
    string[] global = null, string[] assets = null,
    IReadOnlyCollection<string> selected = null, string benchmark = "BTCUSDT")
  {
    global ??= new[] { "BTCUSDT", "ETHUSDT" };
    assets ??= global;
    return MarketTrendAnalyticsEngine.Calculate(
      trend, global, assets, store, Interval, AsOf, selected, benchmark);
  }

  private static void TestBlankDefault()
  {
    var result = Calc(Trend(), Store());
    Near(result.MarketMetrics["PriceTrend"], 15, "default global");
    Near(result.AssetMetrics["BTCUSDT"]["PriceTrend"], 10, "default BTC");
    Near(result.AssetMetrics["ETHUSDT"]["PriceTrend"], 20, "default ETH");
    Assert(result.EnabledMetrics.SequenceEqual(new[] { "PriceTrend" }), "Blank Metrics enables only PriceTrend.");
    passed++;
  }

  private static void TestExplicitPriceTrend()
  {
    var result = Calc(Trend("PriceTrend"), Store());
    Near(result.MarketMetrics["PriceTrend"], 15, "explicit global");
    Near(result.AssetMetrics["ETHUSDT"]["PriceTrend"], 20, "explicit asset");
    passed++;
  }

  private static void TestExplicitOtherMetrics()
  {
    var result = Calc(Trend("MedianATRP"), Store());
    Assert(!result.MarketMetrics.ContainsKey("PriceTrend")
      && result.AssetMetrics.Values.All(value => !value.ContainsKey("PriceTrend")),
      "Explicit non-price metrics must not publish PriceTrend.");
    passed++;
  }

  private static AnalyzerSettings Settings(MarketTrend trend) =>
    new AnalyzerSettings
    {
      MarketAnalyzer = new MarketAnalyzer { MarketTrends = new List<MarketTrend> { trend } },
      GlobalSettings = new List<GlobalSetting>(), SingleMarketSettings = new List<SingleMarketSetting>()
    };

  private static void TestImplicitGlobal()
  {
    var trend = Trend("MedianATRP"); var settings = Settings(trend);
    settings.GlobalSettings.Add(new GlobalSetting
    {
      SettingName = "Implicit", Triggers = new List<Trigger> { new Trigger { MarketTrendName = "1h" } }
    });
    var selected = MarketTrendMetricSelection.Select(trend, settings);
    Assert(selected.Contains("PriceTrend") && selected.Contains("MedianATRP"), "Missing implicit global dependency.");
    Near(Calc(trend, Store(), selected: selected).MarketMetrics["PriceTrend"], 15, "implicit global mean");
    passed++;
  }

  private static void TestImplicitSms()
  {
    var trend = Trend("ATRP"); var settings = Settings(trend);
    settings.SingleMarketSettings.Add(new SingleMarketSetting
    {
      SettingName = "Implicit", Triggers = new List<Trigger> { new Trigger { MarketTrendName = "1h" } }
    });
    var selected = MarketTrendMetricSelection.Select(trend, settings);
    Near(Calc(trend, Store(), selected: selected).AssetMetrics["ETHUSDT"]["PriceTrend"], 20, "implicit SMS");
    passed++;
  }

  private static void TestInsufficientCoverage()
  {
    var result = Calc(Trend(), Store(omitBtcFirst: true));
    Assert(!result.AssetMetrics["BTCUSDT"]["PriceTrend"].IsValid, "BTC missing 60m reference must be invalid.");
    Near(result.AssetMetrics["ETHUSDT"]["PriceTrend"], 20, "valid ETH");
    Assert(!result.MarketMetrics["PriceTrend"].IsValid, "1/2 coverage must not pass global gate.");
    passed++;
  }

  private static void TestIncompleteCandle()
  {
    Near(Calc(Trend(), Store(incomplete: true)).MarketMetrics["PriceTrend"],
      15, "Incomplete current candle cannot alter PriceTrend.");
    passed++;
  }

  private static void TestIndependentPopulations()
  {
    var result = Calc(Trend(), Store(), new[] { "BTCUSDT" }, new[] { "BTCUSDT", "ETHUSDT" });
    Near(result.MarketMetrics["PriceTrend"], 10, "capped global mean");
    Near(result.AssetMetrics["ETHUSDT"]["PriceTrend"], 20, "uncapped asset return");
    passed++;
  }

  private static void TestUsdBenchmark()
  {
    var symbols = new[] { "BTCUSD", "ETHUSD" };
    var result = Calc(Trend("PriceTrend,RelativeStrengthBTC"),
      Store(quote: "USD"), symbols, symbols, benchmark: "BTCUSD");
    Near(result.MarketMetrics["PriceTrend"], 15, "USD quote mean");
    Near(result.AssetMetrics["ETHUSD"]["RelativeStrengthBTC"], 10, "BTCUSD reference");
    passed++;
  }

  private static void TestBothScope()
  {
    Assert(MarketTrendMetrics.TryGetScope("PriceTrend", out var scope)
      && scope == MarketTrendMetricScope.Both, "PriceTrend must be both asset and global.");
    passed++;
  }

  private static void TestAllBinanceExchanges()
  {
    foreach (string exchange in new[] { "Binance", "BinanceUS", "BinanceFutures" })
    {
      Assert(BinanceFuturesAnalyticsRuntime.IsSupportedExchange(exchange)
        && MarketTrendMetricTriggerResolver.UsesNormalizedMetric(Trend(), null, exchange),
        exchange + " must route omitted metric to normalized PriceTrend.");
    }
    Assert(!MarketTrendMetricTriggerResolver.UsesNormalizedMetric(Trend(), null, "Other"),
      "Only the supported Binance exchanges use normalized routing.");
    passed++;
  }

  private static void TestImplicitMetricValidation()
  {
    var trend = Trend("MedianATRP");
    Assert(BinanceFuturesAnalyticsRuntime.ValidateMetricReference(trend, "", true, out var globalError),
      "An omitted GlobalSetting Metric must resolve normalized PriceTrend: " + globalError);
    Assert(BinanceFuturesAnalyticsRuntime.ValidateMetricReference(trend, null, false, out var smsError),
      "An omitted SMS Metric must resolve normalized PriceTrend: " + smsError);
    Assert(!BinanceFuturesAnalyticsRuntime.ValidateMetricReference(trend, "PriceTrend", true, out _),
      "Explicit PriceTrend must be enabled on the MarketTrend.");
    Assert(!BinanceFuturesAnalyticsRuntime.ValidateMetricReference(
      new MarketTrend { Name = "OldSource", Platform = "Other", Metrics = "" }, "", true, out _),
      "A non-Exchange MarketTrend must not pass implicit PriceTrend validation.");
    passed++;
  }

  private static void Near(FeatureValueSnapshot actual, double expected, string label)
  {
    Assert(actual != null && actual.IsValid && !actual.IsStale && actual.Value.HasValue,
      label + " unavailable: " + actual?.InvalidReason);
    Assert(Math.Abs(actual.Value.Value - expected) < 1e-7,
      label + ": expected " + expected + ", got " + actual.Value.Value);
  }
  private static void Assert(bool ok, string reason)
  {
    if (!ok) throw new InvalidOperationException(reason);
  }
}
