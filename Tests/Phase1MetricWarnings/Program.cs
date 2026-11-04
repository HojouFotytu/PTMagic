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
      && lines[1].Contains("24h / ATRP: Insufficient history"), "Repeated SMS metric failure should be one warning.");
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
      && lines[1].Contains("8/10"), "Snapshot invalidity should override generic trigger error.");
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
    Expect(lines.Count >= 2 && lines[1].Contains("2/3 markets unavailable"),
      "Asset warnings must be grouped, including missing asset observations.");
    _passed++;
  }

  private static void TestUnknownMetric()
  {
    MarketTrendAnalysisSnapshot analysis = Trend("4h", new[] { "NotRegistered" },
      new Dictionary<string, FeatureValueSnapshot>());
    IReadOnlyList<string> lines = new RaidMetricWarnings().BuildLines(Analyses(analysis));
    Expect(lines.Count >= 2 && lines[1].Contains("unknown configured metric"), "Unknown configured metrics must be flagged.");
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
      Expect(lines.Any(line => line.Contains("4h / Metric" + index + ": Unavailable")),
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
    Expect(lines.Any(line => line.Contains("Failed trigger requests=8; distinct markets=8")),
      "Every failed request and market must be counted without flooding the logs.");
    Expect(lines.Any(line => line.Contains("SMS activation setting='SMS_0' market=0USDT trigger=#1 relation=Absolute")),
      "The first example must identify exact source, setting, symbol, trigger and relation.");
    Expect(lines.Any(line => line.Contains("(+3 additional requests)")),
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
    Expect(lines.Any(line => line.Contains("GlobalSetting=1") && line.Contains("SMS OffTrigger=1")),
      "Source-specific counts must both be retained.");
    Expect(lines.Any(line => line.Contains("Asset-only metric used globally") && line.Contains("Missing 2h candle")),
      "Distinct reasons for the same trend/metric must not overwrite one another.");
    Expect(lines.Any(line => line.Contains("setting='DIAG_SMS' market=ETHUSDT trigger=#2 relation=RelativeTrigger")),
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
      lines.Any(line => line.Contains("samples=3/10") && line.Contains("coverage=30%") &&
        line.Contains("stale=True") && line.Contains("Missing complete endpoint")),
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
      lines.Any(line => line.Contains("2/3 markets unavailable") &&
        line.Contains("Insufficient 2h history") && line.Contains("Stale 2h endpoint") &&
        line.Contains("example=ETHUSDT") && line.Contains("example=SOLUSDT")),
      "Mixed invalid asset reasons and sample markets must be visible within one summary group.");
    _passed++;
  }

  private static void TestOmittedMetricIsNamedPriceTrend()
  {
    RaidMetricWarnings warnings = new RaidMetricWarnings();
    warnings.ReportUnavailable("OldTrend", "", "No return", "SMS activation", "LegacySMS", "BTCUSDT");
    IReadOnlyList<string> lines = warnings.BuildLines(null!);
    Expect(lines[0] == "+ Metric warnings: 1" &&
      lines[1].Contains("OldTrend / PriceTrend") &&
      lines.Any(line => line.Contains("metric=implicit PriceTrend")),
      "Omitted trigger Metric must be identified as implicit PriceTrend.");
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
