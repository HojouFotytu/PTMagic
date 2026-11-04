using System;
using System.Collections.Generic;
using System.Linq;
using Core.Main.DataObjects.PTMagicData;
using Core.MarketAnalyzer.MarketData;

internal static class Program
{
  private static int _passed;

  private static int Main()
  {
    try
    {
      CheckBlankMetricsUseNormalized();
      CheckImplicitMetricSelection();
      CheckSpotAndBinanceUsCompatibility();
      CheckPriceTrendScopeAndSelection();
      CheckNoImplicitPriceTrendForOtherMetrics();
      CheckExplicitNormalizedMetricsSkipLegacy();
      CheckExplicitPriceTrendOnlyUsesNormalized();
      CheckLegacyGlobalTriggerUsesNormalized();
      CheckNormalizedGlobalTriggerSkipsLegacy();
      CheckLegacySmsActivationUsesNormalized();
      CheckLegacyOffTriggerUsesNormalized();
      CheckNormalizedSmsActivationSkipsLegacy();
      CheckVolumeAndAgeTriggersDoNotNeedLegacy();
      CheckOtherExchangesRemainLegacy();
      CheckCmcTrendsRemainLegacy();
      CheckIndependentPopulationAndMaxMarkets();
      CheckPopulationAllowedAndIgnored();
      CheckZeroPopulationDoesNotInventLegacyValues();
      Console.WriteLine("Normalized PriceTrend compatibility tests passed: " + _passed + ".");
      return 0;
    }
    catch (Exception e)
    {
      Console.Error.WriteLine("Selective legacy market-trend tests FAILED: " + e);
      return 1;
    }
  }

  private static (GeneralSettings general, AnalyzerSettings analyzer, MarketTrend trend)
    Setup(string exchange = "BinanceFutures", string metrics = "MedianATRP")
  {
    MarketTrend trend = new MarketTrend
    {
      Name = "2h", Platform = "Exchange", TrendMinutes = 120,
      Metrics = metrics, MaxMarkets = 2
    };
    return (
      new GeneralSettings { Application = new Application { Exchange = exchange } },
      new AnalyzerSettings
      {
        MarketAnalyzer = new MarketAnalyzer { MarketTrends = new List<MarketTrend> { trend } },
        GlobalSettings = new List<GlobalSetting>(),
        SingleMarketSettings = new List<SingleMarketSetting>()
      },
      trend);
  }

  private static void CheckBlankMetricsUseNormalized()
  {
    var x = Setup(metrics: "");
    LegacyMarketTrendRequirements r = LegacyMarketTrendPlanner.Build(x.general, x.analyzer);
    Assert(!r.NeedsAssetPriceTrend(x.trend) && !r.NeedsGlobalPriceTrend(x.trend)
      && MarketTrendMetrics.ParseEnabled(x.trend).Contains(MarketTrendMetrics.PriceTrend),
      "Blank Metrics must default to normalized per-asset and global PriceTrend, without legacy calculations.");
    _passed++;
  }

  private static void CheckExplicitNormalizedMetricsSkipLegacy()
  {
    var x = Setup(metrics: "MedianATRP,VolumeExpansionBreadth");
    LegacyMarketTrendRequirements r = LegacyMarketTrendPlanner.Build(x.general, x.analyzer);
    Assert(!r.NeedsAssetPriceTrend(x.trend) && !r.NeedsGlobalPriceTrend(x.trend),
      "Metrics-only trend must not calculate old PriceTrend.");
    _passed++;
  }

  private static void CheckExplicitPriceTrendOnlyUsesNormalized()
  {
    var x = Setup(metrics: "PriceTrend");
    LegacyMarketTrendRequirements r = LegacyMarketTrendPlanner.Build(x.general, x.analyzer);
    Assert(!r.NeedsAssetPriceTrend(x.trend) && !r.NeedsGlobalPriceTrend(x.trend),
      "Explicit PriceTrend selects normalized mean without legacy duplicate.");
    _passed++;
  }

  private static void CheckLegacyGlobalTriggerUsesNormalized()
  {
    var x = Setup();
    x.analyzer.GlobalSettings.Add(new GlobalSetting
    {
      SettingName = "Legacy", Triggers = new List<Trigger>
      {
        new Trigger { MarketTrendName = "2h" }
      }
    });
    LegacyMarketTrendRequirements r = LegacyMarketTrendPlanner.Build(x.general, x.analyzer);
    Assert(!r.NeedsGlobalPriceTrend(x.trend) && !r.NeedsAssetPriceTrend(x.trend)
      && MarketTrendMetricSelection.Select(x.trend, x.analyzer).Contains(MarketTrendMetrics.PriceTrend),
      "Omitted GlobalSetting Metric must implicitly enable normalized PriceTrend.");
    _passed++;
  }

  private static void CheckNormalizedGlobalTriggerSkipsLegacy()
  {
    var x = Setup();
    x.analyzer.GlobalSettings.Add(new GlobalSetting
    {
      SettingName = "Normalized", Triggers = new List<Trigger>
      {
        new Trigger { MarketTrendName = "2h", Metric = "MedianATRP" }
      }
    });
    LegacyMarketTrendRequirements r = LegacyMarketTrendPlanner.Build(x.general, x.analyzer);
    Assert(!r.NeedsGlobalPriceTrend(x.trend) && !r.NeedsAssetPriceTrend(x.trend),
      "Normalized global metric reference must not force legacy mean.");
    _passed++;
  }

  private static void CheckLegacySmsActivationUsesNormalized()
  {
    var x = Setup();
    x.analyzer.SingleMarketSettings.Add(new SingleMarketSetting
    {
      SettingName = "LegacySMS", Triggers = new List<Trigger>
      {
        new Trigger { MarketTrendName = "2h", Metric = "" }
      }
    });
    LegacyMarketTrendRequirements r = LegacyMarketTrendPlanner.Build(x.general, x.analyzer);
    Assert(!r.NeedsAssetPriceTrend(x.trend) && !r.NeedsGlobalPriceTrend(x.trend)
      && MarketTrendMetricSelection.Select(x.trend, x.analyzer).Contains(MarketTrendMetrics.PriceTrend),
      "Omitted SMS trigger Metric must use normalized returns.");
    _passed++;
  }

  private static void CheckLegacyOffTriggerUsesNormalized()
  {
    var x = Setup();
    x.analyzer.SingleMarketSettings.Add(new SingleMarketSetting
    {
      SettingName = "LegacyOff", OffTriggers = new List<OffTrigger>
      {
        new OffTrigger { MarketTrendName = "2h", Metric = "" }
      }
    });
    LegacyMarketTrendRequirements r = LegacyMarketTrendPlanner.Build(x.general, x.analyzer);
    Assert(!r.NeedsAssetPriceTrend(x.trend) && !r.NeedsGlobalPriceTrend(x.trend)
      && MarketTrendMetricSelection.Select(x.trend, x.analyzer).Contains(MarketTrendMetrics.PriceTrend),
      "Omitted OffTrigger Metric must use normalized returns.");
    _passed++;
  }

  private static void CheckNormalizedSmsActivationSkipsLegacy()
  {
    var x = Setup(metrics: "ATRP,MedianATRP");
    x.analyzer.SingleMarketSettings.Add(new SingleMarketSetting
    {
      SettingName = "NormalizedSMS",
      Triggers = new List<Trigger> { new Trigger { MarketTrendName = "2h", Metric = "ATRP" } },
      OffTriggers = new List<OffTrigger> { new OffTrigger { MarketTrendName = "2h", Metric = "MedianATRP" } }
    });
    LegacyMarketTrendRequirements r = LegacyMarketTrendPlanner.Build(x.general, x.analyzer);
    Assert(!r.NeedsAssetPriceTrend(x.trend) && !r.NeedsGlobalPriceTrend(x.trend),
      "Normalized SMS and OffTrigger should not trigger legacy returns.");
    _passed++;
  }

  private static void CheckVolumeAndAgeTriggersDoNotNeedLegacy()
  {
    var x = Setup();
    x.analyzer.SingleMarketSettings.Add(new SingleMarketSetting
    {
      SettingName = "VolumeAge",
      Triggers = new List<Trigger>
      {
        new Trigger { Min24hVolume = 1000 },
        new Trigger { AgeDaysLowerThan = 1 }
      },
      OffTriggers = new List<OffTrigger>
      {
        new OffTrigger { HoursSinceTriggered = 2 },
        new OffTrigger { Max24hVolume = 1000000 }
      }
    });
    LegacyMarketTrendRequirements r = LegacyMarketTrendPlanner.Build(x.general, x.analyzer);
    Assert(!r.NeedsAssetPriceTrend(x.trend) && !r.NeedsGlobalPriceTrend(x.trend),
      "SMS volume, listing age and activation duration must use independent sources.");
    _passed++;
  }

  private static void CheckOtherExchangesRemainLegacy()
  {
    var x = Setup(exchange: "Poloniex", metrics: "MedianATRP");
    LegacyMarketTrendRequirements r = LegacyMarketTrendPlanner.Build(x.general, x.analyzer);
    Assert(r.NeedsAssetPriceTrend(x.trend) && r.NeedsGlobalPriceTrend(x.trend),
      "Non-Binance exchanges must retain original behavior.");
    _passed++;
  }

  private static void CheckCmcTrendsRemainLegacy()
  {
    var x = Setup();
    x.trend.Platform = "CoinMarketCap";
    LegacyMarketTrendRequirements r = LegacyMarketTrendPlanner.Build(x.general, x.analyzer);
    Assert(r.NeedsAssetPriceTrend(x.trend) && r.NeedsGlobalPriceTrend(x.trend),
      "CMC trend behavior is not migrated.");
    _passed++;
  }

  private static void CheckIndependentPopulationAndMaxMarkets()
  {
    var x = Setup();
    AnalyzerUniverseSnapshot universe = Universe("BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT");
    Dictionary<string, Market> recent = MarketMap(
      ("SOLUSDT", 400), ("BTCUSDT", 500), ("ETHUSDT", 300),
      ("XRPUSDT", 200), ("UNELIGIBLEUSDT", 999));
    MarketTrendSelectedPopulation result = MarketTrendPopulationSelector.Select(
      x.trend, recent, universe, "USDT", null, TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow);
    Assert(result.GlobalSymbols.SequenceEqual(new[] { "BTCUSDT", "SOLUSDT" }),
      "Global population must be capped and 24h volume ranked.");
    Assert(result.AssetSymbols.SequenceEqual(new[] { "BTCUSDT", "SOLUSDT", "ETHUSDT", "XRPUSDT" }),
      "Per-asset population must be eligible but not capped.");
    _passed++;
  }

  private static void CheckPopulationAllowedAndIgnored()
  {
    var x = Setup();
    x.trend.MaxMarkets = 0;
    x.trend.AllowedMarkets = "BTC,SOL,ETH";
    x.trend.IgnoredMarkets = "ETHUSDT";
    MarketTrendSelectedPopulation result = MarketTrendPopulationSelector.Select(
      x.trend,
      MarketMap(("BTCUSDT", 100), ("ETHUSDT", 500), ("SOLUSDT", 200), ("XRPUSDT", 300)),
      Universe("BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT"),
      "USDT", null, TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow);
    Assert(result.GlobalSymbols.SequenceEqual(new[] { "SOLUSDT", "BTCUSDT" })
      && result.AssetSymbols.SequenceEqual(new[] { "SOLUSDT", "BTCUSDT" }),
      "Allowed and ignored markets should apply to both normalized populations.");
    _passed++;
  }

  private static void CheckZeroPopulationDoesNotInventLegacyValues()
  {
    var x = Setup();
    MarketTrendSelectedPopulation result = MarketTrendPopulationSelector.Select(
      x.trend, MarketMap(("BTCUSDT", 100)), Universe("ETHUSDT"),
      "USDT", null, TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow);
    Assert(result.GlobalSymbols.Count == 0 && result.AssetSymbols.Count == 0,
      "Do not synthesize a fake price trend when the eligible universe is empty.");
    _passed++;
  }


  private static void CheckSpotAndBinanceUsCompatibility()
  {
    foreach (string exchange in new[] { "Binance", "BinanceUS", "BinanceFutures" })
    {
      var x = Setup(exchange: exchange, metrics: "");
      LegacyMarketTrendRequirements legacy = LegacyMarketTrendPlanner.Build(x.general, x.analyzer);
      Assert(!legacy.NeedsAssetPriceTrend(x.trend) && !legacy.NeedsGlobalPriceTrend(x.trend),
        exchange + " must use normalized PriceTrend by default");
      Assert(BinanceFuturesAnalyticsRuntime.IsSupportedExchange(exchange)
        && MarketTrendMetricTriggerResolver.UsesNormalizedMetric(x.trend, null, exchange),
        exchange + " must accept an omitted Metric trigger");
    }
    _passed++;
  }

  private static void CheckPriceTrendScopeAndSelection()
  {
    var x = Setup(metrics: "PriceTrend");
    Assert(MarketTrendMetrics.TryGetScope(MarketTrendMetrics.PriceTrend, out MarketTrendMetricScope scope)
      && scope == MarketTrendMetricScope.Both,
      "PriceTrend must provide both global average and per-asset returns");
    Assert(MarketTrendMetrics.ParseEnabled(x.trend).SequenceEqual(new[] { MarketTrendMetrics.PriceTrend }),
      "Explicit PriceTrend must remain selectable");
    _passed++;
  }

  private static void CheckImplicitMetricSelection()
  {
    var x = Setup(metrics: "MedianATRP");
    x.analyzer.GlobalSettings.Add(new GlobalSetting
    {
      SettingName = "DefaultPriceTrigger",
      Triggers = new List<Trigger> { new Trigger { MarketTrendName = "2h" } }
    });
    string[] enabled = MarketTrendMetricSelection.Select(x.trend, x.analyzer).ToArray();
    Assert(enabled.Contains(MarketTrendMetrics.PriceTrend)
      && enabled.Contains(MarketTrendMetrics.MedianATRP)
      && enabled.Length == 2,
      "Omitted GlobalSetting Metric must add PriceTrend without removing explicitly enabled metrics");
    _passed++;
  }

  private static void CheckNoImplicitPriceTrendForOtherMetrics()
  {
    var x = Setup(metrics: "MedianATRP,VolumeExpansionBreadth");
    x.analyzer.GlobalSettings.Add(new GlobalSetting
    {
      SettingName = "ATRPOnly",
      Triggers = new List<Trigger> { new Trigger { MarketTrendName = "2h", Metric = "MedianATRP" } }
    });
    x.analyzer.SingleMarketSettings.Add(new SingleMarketSetting
    {
      SettingName = "VolumeOnly",
      Triggers = new List<Trigger> { new Trigger { MarketTrendName = "2h", Min24hVolume = 10 } }
    });
    string[] enabled = MarketTrendMetricSelection.Select(x.trend, x.analyzer).ToArray();
    Assert(!enabled.Contains(MarketTrendMetrics.PriceTrend),
      "Explicit non-PriceTrend metrics and volume-only triggers must not force PriceTrend");
    _passed++;
  }

  private static AnalyzerUniverseSnapshot Universe(params string[] symbols)
  {
    return new AnalyzerUniverseSnapshot(DateTimeOffset.UtcNow, "USDT",
      symbols.Select(symbol => new AnalyzerUniverseMarket { Symbol = symbol, UnderlyingType = "COIN" }));
  }

  private static Dictionary<string, Market> MarketMap(params (string symbol, double volume)[] markets)
  {
    return markets.ToDictionary(
      pair => pair.symbol,
      pair => new Market
      {
        Name = pair.symbol, Symbol = pair.symbol, Price = 100.0, Volume24h = pair.volume
      }, StringComparer.OrdinalIgnoreCase);
  }

  private static void Assert(bool condition, string message)
  {
    if (!condition) throw new InvalidOperationException(message);
  }
}
