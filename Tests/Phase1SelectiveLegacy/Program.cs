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
      CheckBlankMetricsKeepLegacy();
      CheckExplicitNormalizedMetricsSkipLegacy();
      CheckExplicitPriceTrendOnlyUsesNormalized();
      CheckLegacyGlobalTriggerKeepsBoth();
      CheckNormalizedGlobalTriggerSkipsLegacy();
      CheckLegacySmsActivationKeepsAssetOnly();
      CheckLegacyOffTriggerKeepsAssetOnly();
      CheckNormalizedSmsActivationSkipsLegacy();
      CheckVolumeAndAgeTriggersDoNotNeedLegacy();
      CheckOtherExchangesRemainLegacy();
      CheckCmcTrendsRemainLegacy();
      CheckIndependentPopulationAndMaxMarkets();
      CheckPopulationAllowedAndIgnored();
      CheckZeroPopulationDoesNotInventLegacyValues();
      Console.WriteLine("Selective legacy market-trend tests passed: " + _passed + ".");
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

  private static void CheckBlankMetricsKeepLegacy()
  {
    var x = Setup(metrics: "");
    LegacyMarketTrendRequirements r = LegacyMarketTrendPlanner.Build(x.general, x.analyzer);
    Assert(r.NeedsAssetPriceTrend(x.trend) && r.NeedsGlobalPriceTrend(x.trend),
      "Blank Metrics preserves old per-market and average PriceTrend.");
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

  private static void CheckLegacyGlobalTriggerKeepsBoth()
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
    Assert(r.NeedsGlobalPriceTrend(x.trend) && r.NeedsAssetPriceTrend(x.trend),
      "Omitted GlobalSetting trigger Metric retains legacy mean and asset returns.");
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

  private static void CheckLegacySmsActivationKeepsAssetOnly()
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
    Assert(r.NeedsAssetPriceTrend(x.trend) && !r.NeedsGlobalPriceTrend(x.trend),
      "Legacy SMS needs per-market returns, not a legacy global average.");
    _passed++;
  }

  private static void CheckLegacyOffTriggerKeepsAssetOnly()
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
    Assert(r.NeedsAssetPriceTrend(x.trend) && !r.NeedsGlobalPriceTrend(x.trend),
      "Legacy OffTrigger needs per-market returns.");
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
    var x = Setup(exchange: "Binance", metrics: "MedianATRP");
    LegacyMarketTrendRequirements r = LegacyMarketTrendPlanner.Build(x.general, x.analyzer);
    Assert(r.NeedsAssetPriceTrend(x.trend) && r.NeedsGlobalPriceTrend(x.trend),
      "Non-Futures exchanges must retain original behavior.");
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
