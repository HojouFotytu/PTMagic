using System;
using System.Collections.Generic;
using System.Linq;
using Core.MarketAnalyzer.MarketData;

internal static class Program
{
  private static int _passed;

  private static int Main()
  {
    try
    {
      TestNoWarnings();
      TestDeduplicationAndReset();
      TestGlobalCoverage();
      TestGroupedAssetWarnings();
      TestUnknownMetric();
      TestGlobalSettingDecision();
      TestAllWarningsDisplayed();
      TestCallerContextAndBoundedExamples();
      TestMultipleSourcesAndReasons();
      TestInvalidGlobalSnapshotMetadata();
      TestMixedAssetFailureReasons();
      TestOmittedMetricIsNamedPriceTrend();
      TestPlainLanguageExcludedMarket();
      TestPlainLanguageInvalidScope();
      TestPlainLanguageMissingHistory();
      TestPlainLanguageCoverage();
      TestPlainLanguageDisabledMetric();
      TestPlainLanguageGroupedCallers();
      TestVerifiedNoTradesWarnSuppression();
      TestVerifiedFlatPricePathInfoAndCompleteDebug();
      TestHistoricalFlatPath();
      TestMixedFlatPathAndSourceFaultRemainWarnings();
      TestVerifiedNewMarketHistoryClassification();
      TestNewMarketWarningsAndNoticeLifecycle();
      Console.WriteLine("Phase 1 metric-warning summary tests passed: " + _passed + ".");
      return 0;
    }
    catch (Exception error)
    {
      Console.Error.WriteLine("Phase 1 metric-warning tests FAILED: " + error);
      return 1;
    }
  }

  private static void TestNoWarnings()
  {
    RaidMetricWarnings warnings = new RaidMetricWarnings();
    Expect(warnings.BuildLines(null!).Count == 0, "No metrics should produce no warning lines.");
    Expect(warnings.BuildLines(Analyses(
      Trend("4h", new[] { MarketTrendMetrics.Breadth }, 
        new Dictionary<string, FeatureValueSnapshot>
        {
          [MarketTrendMetrics.Breadth] = Valid(60)
        }))).Count == 0, "Valid metrics must not be warned.");
    _passed++;
  }

  private static void TestDeduplicationAndReset()
  {
    RaidMetricWarnings warnings = new RaidMetricWarnings();
    warnings.ReportUnavailable("24h", "ATRP", "Insufficient history");
    warnings.ReportUnavailable("24h", "ATRP", "Insufficient history");
    IReadOnlyList<string> lines = warnings.BuildLines(null!);
    Expect(lines.Count >= 2 && lines[0] == "+ Metric warnings: 1"
      && lines[1].Contains("24h") && lines[1].Contains("ATRP") && lines[1].Contains("Insufficient history"), "Repeated SMS metric failure should be one warning.");
    warnings.Reset();
    Expect(warnings.BuildLines(null!).Count == 0, "A new raid must clear prior warnings.");
    _passed++;
  }

  private static void TestGlobalCoverage()
  {
    RaidMetricWarnings warnings = new RaidMetricWarnings();
    warnings.ReportUnavailable("4h", MarketTrendMetrics.Breadth, "Generic trigger error");
    MarketTrendAnalysisSnapshot analysis = Trend(
      "4h", new[] { MarketTrendMetrics.Breadth },
      new Dictionary<string, FeatureValueSnapshot>
      {
        [MarketTrendMetrics.Breadth] = Invalid("Insufficient market coverage: 8/10 valid; 9 required.")
      });
    IReadOnlyList<string> lines = warnings.BuildLines(Analyses(analysis));
    Expect(lines.Count >= 2 && lines[0] == "+ Metric warnings: 1"
      && lines[1].Contains("8 of 10"), "Snapshot invalidity should override generic trigger error.");
    _passed++;
  }

  private static void TestGroupedAssetWarnings()
  {
    MarketTrendAnalysisSnapshot analysis = new MarketTrendAnalysisSnapshot
    {
      MarketTrendName = "4h",
      EnabledMetrics = new[] { MarketTrendMetrics.RelativeStrengthBTC },
      AssetSymbols = new[] { "AAAUSDT", "BBBUSDT", "CCCUSDT" },
      AssetMetrics = new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>
      {
        ["AAAUSDT"] = new Dictionary<string, FeatureValueSnapshot> { [MarketTrendMetrics.RelativeStrengthBTC] = Valid(2) },
        ["BBBUSDT"] = new Dictionary<string, FeatureValueSnapshot> { [MarketTrendMetrics.RelativeStrengthBTC] = Invalid("Missing 4h candle.") }
        // CCCUSDT intentionally lacks an asset snapshot.
      }
    };
    IReadOnlyList<string> lines = new RaidMetricWarnings().BuildLines(Analyses(analysis));
    Expect(lines.Count >= 2 && lines[1].Contains("2 of its 3 markets"),
      "Asset warnings must be grouped, including missing asset observations.");
    _passed++;
  }

  private static void TestUnknownMetric()
  {
    MarketTrendAnalysisSnapshot analysis = Trend("4h", new[] { "NotRegistered" },
      new Dictionary<string, FeatureValueSnapshot>());
    IReadOnlyList<string> lines = new RaidMetricWarnings().BuildLines(Analyses(analysis));
    Expect(lines.Count >= 2 && lines[1].Contains("unknown metric"), "Unknown configured metrics must be flagged.");
    _passed++;
  }

  private static void TestGlobalSettingDecision()
  {
    RaidMetricWarnings warnings = new RaidMetricWarnings();
    warnings.ReportGlobalDecision("selection indeterminate; retained 'Bear'.");
    IReadOnlyList<string> lines = warnings.BuildLines(null!);
    Expect(lines.Count == 2 && lines[1].Contains("retained 'Bear'"), "Active-setting hold belongs in the raid summary.");
    _passed++;
  }

  private static void TestAllWarningsDisplayed()
  {
    RaidMetricWarnings warnings = new RaidMetricWarnings();
    for (int index = 0; index < 9; index++)
    {
      warnings.ReportUnavailable("4h", "Metric" + index, "Unavailable");
    }
    IReadOnlyList<string> lines = warnings.BuildLines(null!);
    Expect(lines.Count >= 10 && lines[0] == "+ Metric warnings: 9",
      "Summary must include every distinct metric warning.");
    for (int index = 0; index < 9; index++)
    {
      Expect(lines.Any(line => line.Contains("4h") && line.Contains("Metric" + index) && line.Contains("Unavailable")),
        "Missing warning for Metric" + index + ".");
    }
    _passed++;
  }

  private static void TestCallerContextAndBoundedExamples()
  {
    RaidMetricWarnings warnings = new RaidMetricWarnings();
    for (int index = 0; index < 8; index++)
    {
      warnings.ReportUnavailable("FILTERED_1h", "PriceTrend",
        "Market '" + index + "USDT' is outside the selected asset population",
        "SMS activation", "SMS_" + index, index + "USDT", "Absolute", "#" + (index + 1));
    }
    IReadOnlyList<string> lines = warnings.BuildLines(null!);
    Expect(lines[0] == "+ Metric warnings: 1", "A group remains a single metric warning.");
    Expect(lines.Any(line => line.Contains("Affected trigger requests: 8")),
      "Every failed request and market must be counted without flooding the logs.");
    Expect(lines.Any(line => line.Contains("SMS_0") && line.Contains("0USDT") && line.Contains("Absolute")),
      "The first example must identify exact source, setting, symbol, trigger and relation.");
    Expect(lines.Any(line => line.Contains("3 more")),
      "Example collection must be bounded at five while retaining the remaining count.");
    _passed++;
  }

  private static void TestMultipleSourcesAndReasons()
  {
    RaidMetricWarnings warnings = new RaidMetricWarnings();
    warnings.ReportUnavailable("FULL_2h", "ATRP", "Asset-only metric used globally",
      "GlobalSetting", "DIAG_Global", null, "Relative", "tag=A");
    warnings.ReportUnavailable("FULL_2h", "ATRP", "Missing 2h candle",
      "SMS OffTrigger", "DIAG_SMS", "ETHUSDT", "RelativeTrigger", "#2");
    IReadOnlyList<string> lines = warnings.BuildLines(null!);
    Expect(lines[0] == "+ Metric warnings: 1", "Different callers must not duplicate the metric group.");
    Expect(lines.Any(line => line.Contains("DIAG_Global") && line.Contains("DIAG_SMS")),
      "Source-specific counts must both be retained.");
    Expect(lines.Any(line => line.Contains("individual markets") && line.Contains("Missing 2h candle")),
      "Distinct reasons for the same trend/metric must not overwrite one another.");
    Expect(lines.Any(line => line.Contains("DIAG_SMS") && line.Contains("ETHUSDT") && line.Contains("RelativeTrigger")),
      "An OffTrigger failure must identify the setting, market, trigger and relation.");
    _passed++;
  }

  private static void TestInvalidGlobalSnapshotMetadata()
  {
    RaidMetricWarnings warnings = new RaidMetricWarnings();
    FeatureValueSnapshot invalid = new FeatureValueSnapshot
    {
      IsValid = false, IsStale = true, SampleCount = 3, EligibleSampleCount = 10,
      CoveragePercent = 30, InvalidReason = "Missing complete endpoint"
    };
    var analysis = Trend("2h", new[] { MarketTrendMetrics.Breadth },
      new Dictionary<string, FeatureValueSnapshot> { [MarketTrendMetrics.Breadth] = invalid });
    IReadOnlyList<string> lines = warnings.BuildLines(Analyses(analysis));
    Expect(lines[0] == "+ Metric warnings: 1" &&
      lines.Any(line => line.Contains("3 of 10") && line.Contains("Missing complete endpoint")),
      "Global invalidity must include validity flags, sample counts, coverage and actual reason.");
    _passed++;
  }

  private static void TestMixedAssetFailureReasons()
  {
    MarketTrendAnalysisSnapshot analysis = new MarketTrendAnalysisSnapshot
    {
      MarketTrendName = "2h",
      EnabledMetrics = new[] { MarketTrendMetrics.ATRP },
      AssetSymbols = new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT" },
      AssetMetrics = new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>
      {
        ["BTCUSDT"] = new Dictionary<string, FeatureValueSnapshot> { [MarketTrendMetrics.ATRP] = Valid(2) },
        ["ETHUSDT"] = new Dictionary<string, FeatureValueSnapshot> { [MarketTrendMetrics.ATRP] = Invalid("Insufficient 2h history") },
        ["SOLUSDT"] = new Dictionary<string, FeatureValueSnapshot> { [MarketTrendMetrics.ATRP] = Invalid("Stale 2h endpoint") }
      }
    };
    IReadOnlyList<string> lines = new RaidMetricWarnings().BuildLines(Analyses(analysis));
    Expect(lines[0] == "+ Metric warnings: 1" &&
      lines.Any(line => line.Contains("2 of its 3 markets") &&
        line.Contains("Insufficient 2h history") && line.Contains("Stale 2h endpoint") &&
        line.Contains("ETHUSDT") && line.Contains("SOLUSDT")),
      "Mixed invalid asset reasons and sample markets must be visible within one summary group.");
    _passed++;
  }

  private static void TestOmittedMetricIsNamedPriceTrend()
  {
    RaidMetricWarnings warnings = new RaidMetricWarnings();
    warnings.ReportUnavailable("OldTrend", "", "No return", "SMS activation", "LegacySMS", "BTCUSDT");
    IReadOnlyList<string> lines = warnings.BuildLines(null!);
    Expect(lines[0] == "+ Metric warnings: 1" &&
      lines[1].Contains("OldTrend") && lines[1].Contains("PriceTrend") &&
      lines.Any(line => line.Contains("implicit PriceTrend")),
      "Omitted trigger Metric must be identified as implicit PriceTrend.");
    _passed++;
  }


  private static void TestPlainLanguageExcludedMarket()
  {
    RaidMetricWarnings warnings = new RaidMetricWarnings();
    warnings.ReportUnavailable("FILTERED_1h", "PriceTrend",
      "Market 'SOLUSDT' is outside the selected asset population for MarketTrend 'FILTERED_1h' (excluded by IgnoredMarkets; global population=1, asset population=2).",
      "SMS activation", "DIAG_SMS_SOL_FilteredOut", "SOLUSDT", "Absolute", "#1");
    var lines = warnings.BuildLines(null!);
    Expect(lines.Count == 2 && lines[1].Contains("DIAG_SMS_SOL_FilteredOut")
      && lines[1].Contains("SOLUSDT") && lines[1].Contains("IgnoredMarkets")
      && lines[1].Contains("Remove the market"),
      "Excluded market warning must identify SMS caller, excluded market, and resolution.");
    _passed++;
  }

  private static void TestPlainLanguageInvalidScope()
  {
    RaidMetricWarnings warnings = new RaidMetricWarnings();
    warnings.ReportUnavailable("FULL_2h", "ATRP",
      "Metric 'ATRP' requires a market-specific context and cannot be used by a GlobalSetting trigger.",
      "GlobalSetting", "DIAG_Global_RejectAssetScope", null, "Relative", "(untagged)");
    var lines = warnings.BuildLines(null!);
    Expect(lines.Count == 2 && lines[1].Contains("DIAG_Global_RejectAssetScope")
      && lines[1].Contains("individual markets") && lines[1].Contains("MedianATRP"),
      "Asset-only global metric warning should identify GlobalSetting and a valid alternative.");
    _passed++;
  }

  private static void TestPlainLanguageMissingHistory()
  {
    RaidMetricWarnings warnings = new RaidMetricWarnings();
    warnings.ReportUnavailable("INSUFFICIENT_8h", "ATRP",
      "Metric 'ATRP' is unavailable for MarketTrend 'INSUFFICIENT_8h' market='BTCUSDT': valid=False, samples=11; reason=Insufficient complete 480m candles for ATRP(14): 11/15.",
      "SMS activation", "DIAG_SMS", "BTCUSDT", "Relative", "#1");
    var analysis = new MarketTrendAnalysisSnapshot
    {
      MarketTrendName = "INSUFFICIENT_8h",
      EnabledMetrics = new[] { "ATRP" },
      AssetSymbols = new[] { "BTCUSDT", "ETHUSDT" },
      AssetMetrics = new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>
      {
        ["BTCUSDT"] = new Dictionary<string, FeatureValueSnapshot>
        { ["ATRP"] = Invalid("Insufficient complete 480m candles for ATRP(14): 11/15.") },
        ["ETHUSDT"] = new Dictionary<string, FeatureValueSnapshot>
        { ["ATRP"] = Invalid("Insufficient complete 480m candles for ATRP(14): 11/15.") }
      }
    };
    var lines = warnings.BuildLines(Analyses(analysis));
    Expect(lines[1].Contains("11 of 15") && lines[1].Contains("8-hour")
      && lines[1].Contains("2 of its 2 markets") && lines[1].Contains("StoreDataMaxHours"),
      "Insufficient candle history should explain missing periods, scope, and remedy.");
    _passed++;
  }

  private static void TestPlainLanguageCoverage()
  {
    var trend = Trend("FULL_2h", new[] { MarketTrendMetrics.MedianATRP },
      new Dictionary<string, FeatureValueSnapshot>
      {
        ["MedianATRP"] = new FeatureValueSnapshot
        {
          IsValid = false, SampleCount = 0, EligibleSampleCount = 250,
          InvalidReason = "Insufficient market coverage: 0/250 valid (0%); at least 90% (225 markets) required."
        }
      });
    var lines = new RaidMetricWarnings().BuildLines(Analyses(trend));
    Expect(lines.Count == 2 && lines[1].Contains("only 0 of 250")
      && lines[1].Contains("225") && lines[1].Contains("MedianATRP"),
      "Coverage warning should show available markets and required threshold in readable prose.");
    _passed++;
  }

  private static void TestPlainLanguageDisabledMetric()
  {
    RaidMetricWarnings warnings = new RaidMetricWarnings();
    warnings.ReportUnavailable("IMPLICIT_1h", "PriceTrend",
      "MarketTrend 'IMPLICIT_1h' does not enable metric 'PriceTrend'.",
      "SMS activation", "DIAG_SMS_BTC_ExplicitDisabledPriceTrend", "BTCUSDT", "Absolute", "#1");
    var lines = warnings.BuildLines(null!);
    Expect(lines.Count == 2 && lines[1].Contains("DIAG_SMS_BTC_ExplicitDisabledPriceTrend")
      && lines[1].Contains("not enabled") && lines[1].Contains("leave the trigger's Metric blank"),
      "Disabled explicit PriceTrend warning must explain backward-compatible fix.");
    _passed++;
  }

  private static void TestPlainLanguageGroupedCallers()
  {
    RaidMetricWarnings warnings = new RaidMetricWarnings();
    warnings.ReportUnavailable("INSUFFICIENT_8h", "ATRP",
      "Insufficient complete 480m candles for ATRP(14): 11/15.",
      "SMS activation", "SMS_BTC", "BTCUSDT", "Relative", "tag=A");
    warnings.ReportUnavailable("INSUFFICIENT_8h", "ATRP",
      "Insufficient complete 480m candles for ATRP(14): 11/15.",
      "SMS OffTrigger", "SMS_ETH", "ETHUSDT", "Relative", "#2");
    var lines = warnings.BuildLines(null!);
    Expect(lines.Count == 2 && lines[0] == "+ Metric warnings: 1" &&
      lines[1].Contains("2") && lines[1].Contains("SMS_BTC") &&
      lines[1].Contains("SMS_ETH") && lines[1].Contains("11 of 15"),
      "A grouped warning should remain one readable line while identifying distinct callers.");
    _passed++;
  }

  private static void TestVerifiedNoTradesWarnSuppression()
  {
    var warnings = new RaidMetricWarnings();
    const string trendName = "M15";
    string reason = TakerFlowFeatureEngine.VerifiedNoTradesReason;
    warnings.ReportUnavailable(trendName, MarketTrendMetrics.TakerBuyRatio,
      "Metric unavailable; reason=" + reason,
      "SMS activation", "DIAG_SMS01", "SNOWUSDT", "Absolute", "#1");
    var inactive = new FeatureValueSnapshot
    {
      IsNoTrades = true, InvalidReason = reason, SampleCount = 3,
      SourceDataTimestampUtc = new DateTimeOffset(2026, 11, 17, 14, 15, 0, TimeSpan.Zero)
    };
    var analysis = new MarketTrendAnalysisSnapshot
    {
      MarketTrendName = trendName,
      EnabledMetrics = new[] { MarketTrendMetrics.TakerBuyRatio, MarketTrendMetrics.TakerImbalance },
      AssetSymbols = new[] { "SNOWUSDT", "BTCUSDT" },
      AssetMetrics = new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>
      {
        ["SNOWUSDT"] = new Dictionary<string, FeatureValueSnapshot>
        {
          [MarketTrendMetrics.TakerBuyRatio] = inactive,
          [MarketTrendMetrics.TakerImbalance] = inactive
        },
        ["BTCUSDT"] = new Dictionary<string, FeatureValueSnapshot>
        {
          [MarketTrendMetrics.TakerBuyRatio] = Valid(0.6),
          [MarketTrendMetrics.TakerImbalance] = Valid(0.2)
        }
      }
    };
    var source = Analyses(analysis);
    Expect(warnings.BuildLines(source).Count == 0,
      "Verified inactivity must not cause WARN-level missing-data summaries.");
    var debug = warnings.BuildNoTradeDebugLines(source);
    Expect(debug.Count == 1 && debug[0].Contains("1 verified NO_TRADES") &&
      debug[0].Contains("SNOWUSDT"),
      "Identical per-asset inactive conditions must produce one DEBUG summary.");

    warnings.ReportUnavailable(trendName, MarketTrendMetrics.TakerBuyRatio,
      "The required source candle is missing", "SMS activation",
      "DIAG_SMS01", "BTCUSDT", "Absolute", "#1");
    var mixed = new MarketTrendAnalysisSnapshot
    {
      MarketTrendName = trendName, EnabledMetrics = analysis.EnabledMetrics,
      AssetSymbols = analysis.AssetSymbols,
      AssetMetrics = new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>
      {
        ["SNOWUSDT"] = analysis.AssetMetrics["SNOWUSDT"],
        ["BTCUSDT"] = new Dictionary<string, FeatureValueSnapshot>
        {
          [MarketTrendMetrics.TakerBuyRatio] = Invalid("The required source candle is missing"),
          [MarketTrendMetrics.TakerImbalance] = Valid(0.2)
        }
      }
    };
    var trueWarnings = warnings.BuildLines(Analyses(mixed));
    Expect(trueWarnings.Count == 2 && trueWarnings[1].Contains("BTCUSDT"),
      "Genuinely missing market data must still WARN.");
    warnings.ReportGlobalDecision("selection indeterminate; retained 'DEFAULT'.");
    Expect(warnings.BuildLines(source).Any(line => line.Contains("retained 'DEFAULT'")),
      "NO_TRADES suppression must not suppress unrelated GlobalSetting safety warnings.");
    _passed++;
  }

  private static readonly DateTimeOffset Listing =
    new DateTimeOffset(2026, 11, 18, 9, 30, 0, TimeSpan.Zero);
  private static readonly DateTimeOffset Raid =
    new DateTimeOffset(2026, 11, 19, 9, 50, 0, TimeSpan.Zero);

  private static MarketDataStore ListingStore(bool gap = false, int delayedStartMinutes = 0,
    params string[] extraSymbols)
  {
    var store = new MarketDataStore(2000);
    foreach (string symbol in new[] { "SOXXUSDT" }.Concat(extraSymbols))
    {
      for (DateTimeOffset open = Listing.AddMinutes(delayedStartMinutes);
           open < Raid; open = open.AddMinutes(5))
      {
        if (gap && open == Listing.AddHours(6)) continue;
        store.UpsertCandle(new MarketCandle
        {
          Symbol = symbol, Interval = TimeSpan.FromMinutes(5),
          OpenTimeUtc = open, CloseTimeUtc = open.AddMinutes(5).AddMilliseconds(-1),
          IsClosed = true, Open = 100, High = 102, Low = 99, Close = 101,
          BaseVolume = 2, QuoteVolume = 202, TakerBuyQuoteVolume = 120, TradeCount = 15
        });
      }
    }
    return store;
  }

  private static MarketTrendHistoryRequirement Needed(string name, int count) =>
    new MarketTrendHistoryRequirement { Metric = name, MinimumClosedSourceCandles = count };

  private static MarketTrendAnalysisSnapshot ListingTrend(bool invalidTaker = false)
  {
    var features = new Dictionary<string, FeatureValueSnapshot>
    {
      [MarketTrendMetrics.PriceTrend] =
        Invalid("No exact closed-candle reference exists 2880 minutes before the current normalized endpoint. StoreDataMaxHours may be insufficient or TrendMinutes may not align with the normalized interval."),
      [MarketTrendMetrics.RelativeStrengthMarket] =
        Invalid("Asset return or MedianPriceTrend is unavailable for MarketTrend 'H48'."),
      [MarketTrendMetrics.RelativeStrengthBTC] =
        Invalid("Asset return or BTC return is unavailable for MarketTrend 'H48'."),
      [MarketTrendMetrics.ATRP] =
        Invalid("Insufficient complete 2880m candles for ATRP(14): 0/15. StoreDataMaxHours may be insufficient, or source candles are missing."),
      [MarketTrendMetrics.VolumeExpansion] =
        Invalid("Insufficient complete 2880m quote-volume periods for VolumeExpansion(20): 0/21. StoreDataMaxHours may be insufficient, or source candles are missing."),
      [MarketTrendMetrics.TakerBuyRatio] =
        Invalid("Missing normalized source candle in the latest complete MarketTrend period."),
      [MarketTrendMetrics.TakerImbalance] =
        Invalid(invalidTaker
          ? "Invalid taker-buy quote-volume value in a source candle (negative, nonfinite or greater than total)."
          : "Missing normalized source candle in the latest complete MarketTrend period."),
      [MarketTrendMetrics.CorrelationToBTC] =
        Invalid("Asset return vector unavailable: Missing or invalid exact closed source candle for 'SOXXUSDT' at 2026-11-18T08:59:59.9990000+00:00. Correlation needs all 16 returns and their reference endpoint.")
    };
    return new MarketTrendAnalysisSnapshot
    {
      MarketTrendName = "H48", TrendMinutes = 2880, CalculatedAtUtc = Raid,
      ExpectedClosedCandleTimeUtc = Raid.AddMilliseconds(-1),
      AssetSymbols = new[] { "SOXXUSDT" }, EnabledMetrics = features.Keys.ToArray(),
      HistoryRequirements = new[]
      {
        Needed(MarketTrendMetrics.PriceTrend, 577),
        Needed(MarketTrendMetrics.RelativeStrengthMarket, 577),
        Needed(MarketTrendMetrics.RelativeStrengthBTC, 577),
        Needed(MarketTrendMetrics.ATRP, 15 * 576),
        Needed(MarketTrendMetrics.VolumeExpansion, 21 * 576),
        Needed(MarketTrendMetrics.TakerBuyRatio, 576),
        Needed(MarketTrendMetrics.TakerImbalance, 576),
        Needed(MarketTrendMetrics.CorrelationToBTC, 16 * 576 + 1)
      },
      MarketMetrics = new Dictionary<string, FeatureValueSnapshot>
      { [MarketTrendMetrics.MedianPriceTrend] = Valid(1) },
      AssetMetrics = new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>
      {
        ["SOXXUSDT"] = features,
        ["BTCUSDT"] = new Dictionary<string, FeatureValueSnapshot>
        { [MarketTrendMetrics.PriceTrend] = Valid(1.2) }
      }
    };
  }

  private static void TestVerifiedNewMarketHistoryClassification()
  {
    var analysis = Analyses(ListingTrend());
    var result = NewMarketHistoryClassification.Analyze(analysis, ListingStore(),
      symbol => symbol == "SOXXUSDT" ? Listing : null);
    Expect(result.MarketCount == 1 && result.Affected["SOXXUSDT"].Count == 8,
      "All eight pre-listing-only metric shortages must consolidate under one symbol.");
    Expect(result.IsExpected("SOXXUSDT", "H48", MarketTrendMetrics.CorrelationToBTC)
      && result.IsExpected("SOXXUSDT", "H48", MarketTrendMetrics.RelativeStrengthBTC),
      "Correlation and dependent relative-strength shortages must be recognized.");

    Expect(NewMarketHistoryClassification.Analyze(analysis, ListingStore(),
      _ => null).MarketCount == 0, "Unknown listing must remain a warning.");
    Expect(NewMarketHistoryClassification.Analyze(analysis, ListingStore(gap: true),
      _ => Listing).MarketCount == 0, "Post-listing candle gap must remain a warning.");
    Expect(NewMarketHistoryClassification.Analyze(analysis, ListingStore(delayedStartMinutes: 60),
      _ => Listing).MarketCount == 0, "Truncated histories must remain warnings.");
    Expect(NewMarketHistoryClassification.Analyze(analysis, ListingStore(),
      _ => Listing.AddDays(-100)).MarketCount == 0,
      "Older listings with retained-window gaps must remain warnings.");
    var current = ListingTrend();
    var missingBenchmark = new MarketTrendAnalysisSnapshot
    {
      MarketTrendName = current.MarketTrendName, TrendMinutes = current.TrendMinutes,
      CalculatedAtUtc = current.CalculatedAtUtc,
      ExpectedClosedCandleTimeUtc = current.ExpectedClosedCandleTimeUtc,
      AssetSymbols = current.AssetSymbols, EnabledMetrics = current.EnabledMetrics,
      HistoryRequirements = current.HistoryRequirements,
      MarketMetrics = current.MarketMetrics,
      AssetMetrics = new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>
      { ["SOXXUSDT"] = current.AssetMetrics["SOXXUSDT"] }
    };
    var partial = NewMarketHistoryClassification.Analyze(Analyses(missingBenchmark),
      ListingStore(), _ => Listing);
    Expect(partial.MarketCount == 1 && partial.Affected["SOXXUSDT"].Count == 7 &&
      !partial.IsExpected("SOXXUSDT", "H48", MarketTrendMetrics.RelativeStrengthBTC),
      "Unverified BTC benchmark must not hide a dependent relative-strength warning.");
    _passed++;
  }

  private static void TestVerifiedFlatPricePathInfoAndCompleteDebug()
  {
    var inlineDebug = new List<string>();
    var warnings = new RaidMetricWarnings(line => inlineDebug.Add(line));
    string[] invalidMarkets = { "FRHCUSDT", "VKTXUSDT", "GAPUSDT", "IDLEUSDT" };
    string[] universe = invalidMarkets.Concat(Enumerable.Range(0, 725)
      .Select(index => "ACTIVE" + index.ToString("D3") + "USDT")).ToArray();
    var marketMetrics = universe.ToDictionary(symbol => symbol,
      symbol => (IReadOnlyDictionary<string, FeatureValueSnapshot>)
        new Dictionary<string, FeatureValueSnapshot>
        {
          [MarketTrendMetrics.PricePathEfficiency] = invalidMarkets.Contains(symbol)
            ? Invalid(PricePathEfficiencyEngine.FlatPathUnavailableReason) : Valid(0.4)
        }, StringComparer.OrdinalIgnoreCase);
    var analysis = new MarketTrendAnalysisSnapshot
    {
      MarketTrendName = "H1",
      EnabledMetrics = new[] { MarketTrendMetrics.PricePathEfficiency },
      AssetSymbols = universe,
      AssetMetrics = marketMetrics
    };
    for (int index = 0; index < 10; index++)
    {
      string symbol = invalidMarkets[index % invalidMarkets.Length];
      warnings.ReportUnavailable("H1", MarketTrendMetrics.PricePathEfficiency,
        "Metric 'PricePathEfficiency' is unavailable for MarketTrend 'H1' market='" + symbol +
        "'; reason=" + PricePathEfficiencyEngine.FlatPathUnavailableReason,
        "SMS activation", "SMS_" + index, symbol, "Absolute", "tag=E" + index);
    }

    Expect(inlineDebug.Count == 10 &&
      invalidMarkets.All(symbol => inlineDebug.Any(line => line.Contains(symbol))) &&
      Enumerable.Range(0, 10).All(index => inlineDebug.Any(line =>
        line.Contains("SMS_" + index + "'") && line.Contains("E" + index))),
      "All ten DEBUG callers must be emitted before the raid summary is constructed.");
    var source = Analyses(analysis);
    Expect(warnings.BuildLines(source).Count == 0,
      "Verified flat 0/0 paths alone must not be Metric Warnings.");
    var info = warnings.BuildFlatPathInfoLines();
    Expect(info.Count == 1 &&
      info[0] == "+ H1 PricePathEfficiency: 4/729 unavailable (flat price paths; 10 SMS trigger evaluations).",
      "One compact INFO line must preserve counts and not repeat caller lists.");
    Expect(inlineDebug.Count == 10,
      "Building summary INFO must not replay additional DEBUG lines.");

    warnings.Reset();
    Expect(warnings.BuildLines(null!).Count == 0 &&
      warnings.BuildFlatPathInfoLines().Count == 0,
      "A new raid cannot inherit old flat-path reporting.");
    _passed++;
  }

  private static void TestHistoricalFlatPath()
  {
    var warnings = new RaidMetricWarnings();
    const string metric = MarketTrendMetrics.PricePathEfficiency;
    var current = Valid(0.4);
    var prior = Invalid(PricePathEfficiencyEngine.FlatPathUnavailableReason);
    var observation = MarketTrendDeltaEngine.Compare(current, prior, Raid);
    var analysis = new MarketTrendAnalysisSnapshot
    {
      MarketTrendName = "H1", EnabledMetrics = new[] { metric },
      AssetSymbols = new[] { "SNPSUSDT" },
      AssetMetrics = new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>
      { ["SNPSUSDT"] = new Dictionary<string, FeatureValueSnapshot> { [metric] = current } }
    };
    var deltas = new Dictionary<string, MarketTrendDeltaAnalysisSnapshot>
    {
      ["H1"] = new MarketTrendDeltaAnalysisSnapshot
      {
        MarketTrendName = "H1",
        AssetMetrics = new Dictionary<string, IReadOnlyDictionary<string, MetricDeltaObservation>>
        { ["SNPSUSDT"] = new Dictionary<string, MetricDeltaObservation> { [metric] = observation } }
      }
    };
    warnings.ReportUnavailable("H1", metric, "Delta unavailable: " + observation.Delta.InvalidReason,
      "SMS activation", "SMS", "SNPSUSDT", "Delta", "A");
    Expect(warnings.BuildLines(Analyses(analysis), deltas: deltas).Count == 0,
      "Verified historical flat paths alone must be informational.");
    Expect(warnings.BuildFlatPathInfoLines(deltas).Count == 1, "Historical flat path INFO missing.");
    Expect(warnings.BuildLines(Analyses(analysis)).Count > 0, "Missing Delta evidence must remain WARN.");
    _passed++;
  }

  private static void TestMixedFlatPathAndSourceFaultRemainWarnings()
  {
    var warnings = new RaidMetricWarnings();
    var markets = new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>
    {
      ["FLATUSDT"] = new Dictionary<string, FeatureValueSnapshot>
      {
        [MarketTrendMetrics.PricePathEfficiency] = Invalid(PricePathEfficiencyEngine.FlatPathUnavailableReason)
      },
      ["STALEUSDT"] = new Dictionary<string, FeatureValueSnapshot>
      {
        [MarketTrendMetrics.PricePathEfficiency] = Invalid("Normalized price-path source candles are stale.")
      },
      ["GOODUSDT"] = new Dictionary<string, FeatureValueSnapshot>
      {
        [MarketTrendMetrics.PricePathEfficiency] = Valid(0.4)
      }
    };
    var analysis = new MarketTrendAnalysisSnapshot
    {
      MarketTrendName = "H1",
      EnabledMetrics = new[] { MarketTrendMetrics.PricePathEfficiency },
      AssetSymbols = markets.Keys.ToArray(),
      AssetMetrics = markets
    };
    warnings.ReportUnavailable("H1", MarketTrendMetrics.PricePathEfficiency,
      PricePathEfficiencyEngine.FlatPathUnavailableReason,
      "SMS activation", "SMS_FLAT", "FLATUSDT", "Absolute", "tag=A");
    warnings.ReportUnavailable("H1", MarketTrendMetrics.PricePathEfficiency,
      "Normalized price-path source candles are stale.",
      "SMS activation", "SMS_STALE", "STALEUSDT", "Absolute", "tag=B");
    var lines = warnings.BuildLines(Analyses(analysis));
    Expect(lines.Count == 2 && lines[0] == "+ Metric warnings: 1" &&
      lines[1].Contains("STALEUSDT") &&
      warnings.BuildFlatPathInfoLines().Count == 0,
      "Mixed flat paths and stale/missing sources must remain WARN.");

    // The same flat reason must not conceal a stale snapshot even if its
    // InvalidReason string was preserved from a prior source observation.
    warnings.Reset();
    var staleFlat = new MarketTrendAnalysisSnapshot
    {
      MarketTrendName = "H1",
      EnabledMetrics = new[] { MarketTrendMetrics.PricePathEfficiency },
      AssetSymbols = new[] { "STALEUSDT" },
      AssetMetrics = new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>
      {
        ["STALEUSDT"] = new Dictionary<string, FeatureValueSnapshot>
        {
          [MarketTrendMetrics.PricePathEfficiency] = new FeatureValueSnapshot
          {
            InvalidReason = PricePathEfficiencyEngine.FlatPathUnavailableReason,
            IsValid = false, IsStale = true
          }
        }
      }
    };
    warnings.ReportUnavailable("H1", MarketTrendMetrics.PricePathEfficiency,
      PricePathEfficiencyEngine.FlatPathUnavailableReason,
      "SMS activation", "SMS_STALE", "STALEUSDT", "Absolute", "tag=S");
    Expect(warnings.BuildLines(Analyses(staleFlat)).Count == 2 &&
      warnings.BuildFlatPathInfoLines().Count == 0,
      "Stale assets must remain WARN even if they carry the flat-path reason.");
    _passed++;
  }

  private static void TestNewMarketWarningsAndNoticeLifecycle()
  {
    var analysis = Analyses(ListingTrend());
    var classification = NewMarketHistoryClassification.Analyze(analysis,
      ListingStore(), _ => Listing);
    var warnings = new RaidMetricWarnings();
    warnings.ReportUnavailable("H48", MarketTrendMetrics.PriceTrend,
      "Asset metric unavailable", "SMS activation", "SMS48", "SOXXUSDT", "Absolute", "#1");
    Expect(warnings.BuildLines(analysis, classification).Count == 0,
      "Expected history shortage must not be reported as a Metric Warning.");
    for (int index = 0; index < 9; index++)
      warnings.ReportUnavailable("H48", MarketTrendMetrics.ATRP,
        "Metric 'ATRP' is unavailable; reason=Insufficient complete 2880m candles for ATRP(14): 0/15.",
        "SMS activation", "SMS_" + index, "SOXXUSDT", "Absolute", "#" + index);
    Expect(warnings.BuildLines(analysis, classification).Count == 0,
      "More than five affected SMS callers must still be safely reclassified.");
    var first = warnings.BuildNewMarketInfoLines(classification);
    Expect(first.Count == 1 &&
      first[0] == "+ New markets with insufficient history, excluded from some metrics: SOXXUSDT",
      "New markets must produce one concise INFO line, with no per-market details.");

    warnings.Reset();
    var repeated = warnings.BuildNewMarketInfoLines(classification);
    Expect(repeated.Count == 1 && repeated[0] == first[0],
      "Each raid must report only the same concise list, without detail lines.");
    // The single summary names every affected market, even when there are more than five.
    var extraSymbols = new[] { "IGVUSDT", "ARTYUSDT", "IBITUSDT", "CCJUSDT", "ETNUSDT" };
    var baseTrend = ListingTrend();
    var multiAssetMetrics = new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(
      baseTrend.AssetMetrics, StringComparer.OrdinalIgnoreCase);
    foreach (string symbol in extraSymbols)
      multiAssetMetrics[symbol] = new Dictionary<string, FeatureValueSnapshot>
      { [MarketTrendMetrics.PriceTrend] = baseTrend.AssetMetrics["SOXXUSDT"][MarketTrendMetrics.PriceTrend] };
    var multiTrend = new MarketTrendAnalysisSnapshot
    {
      MarketTrendName = baseTrend.MarketTrendName, TrendMinutes = baseTrend.TrendMinutes,
      CalculatedAtUtc = baseTrend.CalculatedAtUtc,
      ExpectedClosedCandleTimeUtc = baseTrend.ExpectedClosedCandleTimeUtc,
      AssetSymbols = new[] { "SOXXUSDT" }.Concat(extraSymbols).ToArray(),
      EnabledMetrics = baseTrend.EnabledMetrics,
      HistoryRequirements = baseTrend.HistoryRequirements,
      MarketMetrics = baseTrend.MarketMetrics,
      AssetMetrics = multiAssetMetrics
    };
    var multiClassification = NewMarketHistoryClassification.Analyze(
      Analyses(multiTrend), ListingStore(extraSymbols: extraSymbols), _ => Listing);
    Expect(multiClassification.MarketCount == 6,
      "All six verified new listings must be classified.");
    string expectedSummary = "+ New markets with insufficient history, excluded from some metrics: ARTYUSDT, CCJUSDT, ETNUSDT, IBITUSDT, IGVUSDT, SOXXUSDT";
    var multiWarnings = new RaidMetricWarnings();
    var multiLines = multiWarnings.BuildNewMarketInfoLines(multiClassification);
    Expect(multiLines.Count == 1 && multiLines[0] == expectedSummary,
      "The complete six-market list must fit in one INFO line, without individual notices.");
    multiWarnings.Reset();
    var multiRepeated = multiWarnings.BuildNewMarketInfoLines(multiClassification);
    Expect(multiRepeated.Count == 1 && multiRepeated[0] == expectedSummary,
      "Later raids must retain the same concise, complete market list.");

    var mixed = Analyses(ListingTrend(invalidTaker: true));
    var filtered = NewMarketHistoryClassification.Analyze(mixed, ListingStore(), _ => Listing);
    Expect(filtered.Affected["SOXXUSDT"].Count == 7, "Invalid taker value must not be suppressed.");
    Expect(warnings.BuildLines(mixed, filtered).Any(line =>
      line.Contains("TakerImbalance")), "A genuine invalid value must remain WARN.");

    warnings.ReportGlobalDecision("selection indeterminate; retained 'DEFAULT'.");
    Expect(warnings.BuildLines(analysis, classification).Any(line =>
      line.Contains("retained 'DEFAULT'")), "Global decision failure must remain WARN.");
    Expect(warnings.BuildNewMarketInfoLines(new NewMarketHistoryClassification()).Count == 0,
      "When no new markets are excluded, no extra recovery or detail lines are required.");
    _passed++;
  }

  private static MarketTrendAnalysisSnapshot Trend(
    string name, IReadOnlyCollection<string> metrics, IReadOnlyDictionary<string, FeatureValueSnapshot> marketFeatures)
  {
    return new MarketTrendAnalysisSnapshot
    {
      MarketTrendName = name,
      EnabledMetrics = metrics,
      MarketMetrics = marketFeatures
    };
  }

  private static IReadOnlyDictionary<string, MarketTrendAnalysisSnapshot> Analyses(params MarketTrendAnalysisSnapshot[] items)
  {
    return items.ToDictionary(item => item.MarketTrendName, StringComparer.OrdinalIgnoreCase);
  }

  private static FeatureValueSnapshot Valid(double value)
  {
    return new FeatureValueSnapshot { Value = value, IsValid = true };
  }

  private static FeatureValueSnapshot Invalid(string reason)
  {
    return new FeatureValueSnapshot { IsValid = false, InvalidReason = reason };
  }

  private static void Expect(bool condition, string message)
  {
    if (!condition)
    {
      throw new InvalidOperationException(message);
    }
  }
}
