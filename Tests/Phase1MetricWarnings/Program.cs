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
