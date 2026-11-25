using Core.Helper;
using Core.Main;
using Core.Main.DataObjects.PTMagicData;
using Core.MarketAnalyzer.MarketData;

internal static class Program
{
  private static int _passed;

  private static int Main()
  {
    try
    {
      Valid("Global Delta relation uses an existing metric", settings => settings.GlobalSettings[0].Triggers[0].MarketTrendRelation = "Delta");
      Valid("SMS Delta relation uses an existing metric", settings => settings.SingleMarketSettings[0].Triggers[0].MarketTrendRelation = "Delta");
      Valid("SMS OffTrigger Delta uses an existing metric", settings => settings.SingleMarketSettings[0].OffTriggers[0].MarketTrendRelation = "Delta");
      Valid("simple AND permits omitted tags", settings => { settings.GlobalSettings[0].Triggers[0].Tag = ""; settings.GlobalSettings[0].TriggerConnection = "AND"; });
      Valid("simple OR is case insensitive and trims whitespace", settings => { settings.SingleMarketSettings[0].TriggerConnection = " or "; settings.SingleMarketSettings[0].OffTriggerConnection = " and "; });
      Valid("trend references are case insensitive", settings => settings.GlobalSettings[0].Triggers[0].MarketTrendName = "SHAREDTREND");
      Invalid("missing global trend", settings => settings.GlobalSettings[0].Triggers[0].MarketTrendName = "Gone", "missing MarketTrend");
      Invalid("missing SMS activation trend", settings => settings.SingleMarketSettings[0].Triggers[0].MarketTrendName = "Gone", "missing MarketTrend");
      Invalid("missing SMS off-trigger trend", settings => settings.SingleMarketSettings[0].OffTriggers[0].MarketTrendName = "Gone", "missing MarketTrend");
      Invalid("blank trend reference", settings => settings.SingleMarketSettings[0].OffTriggers[0].MarketTrendName = " ", "missing MarketTrend");
      Invalid("duplicate trend identity", settings => settings.MarketAnalyzer.MarketTrends.Add(new MarketTrend { Name = "sharedtrend", TrendMinutes = 5 }), "Duplicate MarketTrend");
      Invalid("blank trend identity", settings => settings.MarketAnalyzer.MarketTrends[0].Name = " ", "nonempty Name");
      Invalid("unsupported Exchange timeframe", settings => settings.MarketAnalyzer.MarketTrends[0].TrendMinutes = 7, "positive multiple of 5");
      Invalid("missing default fallback", settings => settings.GlobalSettings.RemoveAll(setting => setting.SettingName == "Default"), "default fallback");
      Invalid("duplicate global setting identity", settings => settings.GlobalSettings.Add(new GlobalSetting { SettingName = "riskoff" }), "Duplicate GlobalSetting");
      Invalid("duplicate SMS identity", settings => settings.SingleMarketSettings.Add(new SingleMarketSetting { SettingName = "protection" }), "Duplicate SingleMarketSetting");
      Invalid("blank setting identity", settings => settings.SingleMarketSettings[0].SettingName = "", "nonempty SettingName");
      Invalid("duplicate tags even on simple AND", settings =>
      {
        settings.GlobalSettings[0].TriggerConnection = "AND";
        settings.GlobalSettings[0].Triggers.Add(new Trigger { MarketTrendName = "SharedTrend", Tag = "a" });
      }, "duplicates Tag");
      Invalid("custom expression requires every tag", settings =>
      {
        settings.GlobalSettings[0].Triggers.Add(new Trigger { MarketTrendName = "SharedTrend" });
      }, "nonempty Tag");
      Invalid("custom expression unknown tag", settings => settings.GlobalSettings[0].TriggerConnection = "A && Missing", "TriggerConnection is invalid");
      Invalid("custom tag must be a variable identifier", settings => settings.GlobalSettings[0].Triggers[0].Tag = "bad tag", "Boolean variable identifier");
      Invalid("custom expression malformed branch", settings => settings.GlobalSettings[0].TriggerConnection = "A || (", "TriggerConnection is invalid");
      Invalid("unsupported custom method expression", settings => settings.GlobalSettings[0].TriggerConnection = "A.ToString() == \"True\"", "TriggerConnection is invalid");
      Valid("nested custom expression accepts indeterminate inputs", settings =>
      {
        settings.GlobalSettings[0].Triggers.Add(new Trigger { MarketTrendName = "SharedTrend", Tag = "AA" });
        settings.GlobalSettings[0].TriggerConnection = "(A && !AA) || (A == AA)";
      });
      Invalid("off-trigger expression keeps AND OR contract", settings => settings.SingleMarketSettings[0].OffTriggerConnection = "Exit || Clock", "OffTriggerConnection must be AND or OR");
      Valid("age and volume activation triggers need no trend", settings =>
      {
        settings.SingleMarketSettings[0].Triggers = new List<Trigger>
        {
          new Trigger { Tag = "Young", AgeDaysLowerThan = 7 },
          new Trigger { Tag = "Liquid", Min24hVolume = 100, AgeDaysLowerThan = 3 }
        };
        settings.SingleMarketSettings[0].TriggerConnection = "Young || Liquid";
      });
      Valid("timed and volume off-triggers need no trend", settings =>
      {
        settings.SingleMarketSettings[0].OffTriggers = new List<OffTrigger>
        {
          new OffTrigger { HoursSinceTriggered = 3, Min24hVolume = 1 },
          new OffTrigger { Max24hVolume = 1_000 }
        };
      });
      // Subtracting 1 from the double.MaxValue sentinel rounds back to it.
      // Its adjacent representable value and the sentinel must take different paths.
      Valid("volume off-trigger just below the unbounded sentinel needs no trend", settings =>
      {
        settings.SingleMarketSettings[0].OffTriggers = new List<OffTrigger>
        {
          new OffTrigger { Max24hVolume = Math.BitDecrement(Constants.Max24hVolume) }
        };
      });
      Invalid("unbounded off-trigger without time or volume still requires a trend", settings =>
      {
        settings.SingleMarketSettings[0].OffTriggers = new List<OffTrigger>
        {
          new OffTrigger { Max24hVolume = Constants.Max24hVolume }
        };
      }, "OffTrigger #1 references missing MarketTrend");
      Valid("null OffTriggers means no persistent condition", settings => settings.SingleMarketSettings[0].OffTriggers = null);
      Warning("unknown metric remains diagnostic unavailable", settings => settings.GlobalSettings[0].Triggers[0].Metric = "UnknownMetric", "Unknown metric");
      Warning("disabled metric remains diagnostic unavailable", settings => settings.GlobalSettings[0].Triggers[0].Metric = "MedianATRP", "does not enable");
      Warning("asset metric in global context remains diagnostic unavailable", settings => settings.GlobalSettings[0].Triggers[0].Metric = "RelativeStrengthBTC", "market-specific context");
      Warning("invalid SMS metric remains diagnostic unavailable", settings => settings.SingleMarketSettings[0].Triggers[0].Metric = "NotAMetric", "Unknown metric");
      Warning("invalid off-trigger metric remains diagnostic unavailable", settings => settings.SingleMarketSettings[0].OffTriggers[0].Metric = "NotAMetric", "Unknown metric");
      Invalid("active correlation lookback minimum", settings => { settings.MarketAnalyzer.MarketTrends[0].Metrics += ",CorrelationToBTC"; settings.MarketAnalyzer.MarketTrends[0].CorrelationLookback = 2; }, "CorrelationLookback");
      Invalid("active correlation lookback maximum", settings => { settings.MarketAnalyzer.MarketTrends[0].Metrics += ",MedianCorrelationToBTC"; settings.MarketAnalyzer.MarketTrends[0].CorrelationLookback = 1001; }, "CorrelationLookback");
      Invalid("active ATR period range", settings => { settings.MarketAnalyzer.MarketTrends[0].Metrics += ",MedianATRP"; settings.MarketAnalyzer.MarketTrends[0].ATRPeriod = 0; }, "ATRPeriod");
      Invalid("active volume expansion lookback range", settings => { settings.MarketAnalyzer.MarketTrends[0].Metrics += ",VolumeExpansion"; settings.MarketAnalyzer.MarketTrends[0].VolumeExpansionLookback = 1001; }, "VolumeExpansionLookback");
      Invalid("active TopN count range", settings => { settings.MarketAnalyzer.MarketTrends[0].Metrics += ",TopNVolumeShare"; settings.MarketAnalyzer.MarketTrends[0].TopNVolumeCount = 0; }, "TopNVolumeCount");
      Invalid("active threshold must be finite", settings => { settings.MarketAnalyzer.MarketTrends[0].Metrics += ",VolatilityBreadth"; settings.MarketAnalyzer.MarketTrends[0].ATRPBreadthThreshold = Double.NaN; }, "ATRPBreadthThreshold");
      Invalid("active expansion threshold must be nonnegative", settings => { settings.MarketAnalyzer.MarketTrends[0].Metrics += ",VolumeExpansionBreadth"; settings.MarketAnalyzer.MarketTrends[0].VolumeExpansionBreadthThreshold = -1; }, "VolumeExpansionBreadthThreshold");
      Valid("unused parameters do not invalidate legacy configuration", settings => { settings.MarketAnalyzer.MarketTrends[0].CorrelationLookback = 1; settings.MarketAnalyzer.MarketTrends[0].ATRPeriod = 0; });
      TerminalFallbackTests();
      HeldDefenseTests();
      Console.WriteLine("Trigger configuration validation checks passed: " + _passed + ".");
      return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine("Trigger configuration validation FAILED: " + ex); return 1; }
  }

  private static void TerminalFallbackTests()
  {
    static void Pair(AnalyzerSettings settings)
    {
      var stronger = settings.SingleMarketSettings[0];
      stronger.StopProcessWhenTriggered = true;
      stronger.OffTriggers.Clear();
      stronger.IndeterminateFallbackSetting = "Early";
      settings.SingleMarketSettings.Add(new SingleMarketSetting
      {
        SettingName = "Early", StopProcessWhenTriggered = true,
        Triggers = new List<Trigger> { new Trigger { MarketTrendName = "SharedTrend", Tag = "Early" } }
      });
    }
    Valid("same-scope terminal SMS fallback admitted", Pair);
    Invalid("fallback cannot cross a Global allowlist", s => { Pair(s); s.SingleMarketSettings[1].AllowedGlobalSettings = "RiskOff"; }, "IndeterminateFallbackSetting");
    Invalid("fallback cannot skip rows", s => { Pair(s); s.SingleMarketSettings[0].IndeterminateFallbackSetting = "Elsewhere"; }, "IndeterminateFallbackSetting");
    Invalid("fallback cannot persist via a separate off-trigger", s => { Pair(s); s.SingleMarketSettings[1].OffTriggers = new List<OffTrigger> { new OffTrigger { HoursSinceTriggered = 2 } }; }, "IndeterminateFallbackSetting");
    Invalid("projected carry requires a valid ATR period", s => { s.MarketAnalyzer.MarketTrends[0].Metrics += ",ProjectedFundingCostInPriorATR"; s.MarketAnalyzer.MarketTrends[0].ATRPeriod = 0; }, "ATRPeriod");
  }

  private static void HeldDefenseTests()
  {
    const string target = "GLOBAL_Z_FORMULA";
    static void WithDefense(AnalyzerSettings settings)
    {
      settings.HeldMarketDefenseProperty = target;
      settings.HeldMarketDefense = new List<HeldMarketDefenseRule> { new HeldMarketDefenseRule
      {
        Level = 1, TriggerConnection = "A", Triggers = new List<Trigger> {
          new Trigger { Tag = "A", MarketTrendName = "SharedTrend", Metric = "Breadth", MinChange = 60 }
        }
      } };
    }
    Valid("defense overlay is optional and accepts measured Global evidence", WithDefense);
    Invalid("missing defense target", s => { WithDefense(s); s.HeldMarketDefenseProperty = null; }, "HeldMarketDefenseProperty");
    Invalid("defense does not write arbitrary PT properties", s => { WithDefense(s); s.HeldMarketDefenseProperty = "DEFAULT_sell_strategy"; }, "HeldMarketDefenseProperty");
    Invalid("defense level requires evidence", s => { WithDefense(s); s.HeldMarketDefense[0].Triggers.Clear(); }, "requires market evidence");
    Invalid("duplicate defense levels", s => { WithDefense(s); s.HeldMarketDefense.Add(s.HeldMarketDefense[0]); }, "distinct levels");
    Invalid("defense expression uses actual trigger references", s => { WithDefense(s); s.HeldMarketDefense[0].TriggerConnection = "Missing"; }, "invalid");
    Warning("defense unavailable metric diagnosed", s => { WithDefense(s); s.HeldMarketDefense[0].Triggers[0].Metric = "MissingMetric"; }, "Unknown metric");
    foreach (var metric in new[] { "ReturnInATR", "MedianReturnInATR", "MedianReturnInPriorATR", "RelativeStrengthInATR", "RallySizeInATR", "DrawdownSizeInATR" })
      Invalid(metric + " validates ATRPeriod", s => { s.MarketAnalyzer.MarketTrends[0].Metrics = metric; s.MarketAnalyzer.MarketTrends[0].ATRPeriod = 0; }, "ATRPeriod");

    var settings = Baseline();
    WithDefense(settings);
    var trend = settings.MarketAnalyzer.MarketTrends[0];
    settings.GlobalSettings.Clear();
    settings.SingleMarketSettings.Clear();
    trend.Metrics = "Breadth";
    settings.HeldMarketDefense[0].Triggers[0].Metric = "";
    settings.HeldMarketDefense[0].Triggers[0].MarketTrendRelation = "Delta";
    if (!MarketTrendDeltaSelection.Select(trend, settings).Contains("PriceTrend"))
      throw new InvalidOperationException("Overlay-only omitted PriceTrend/Delta must be scheduled");
    Pass("implicit PriceTrend and Delta selected for overlay-only reference");

    var t = TriggerTruthValue.True; var f = TriggerTruthValue.False; var u = TriggerTruthValue.Unavailable;
    void Check(int floor, int? prior, TriggerTruthValue one, TriggerTruthValue two, int expected)
    {
      var lines = new List<string> { "# unchanged", target + " = " + floor, "GLOBAL_F_FORMULA = 32" };
      int got = HeldMarketDefensePolicy.Apply(lines, target, prior, new[] { (1, one), (2, two) });
      if (got != expected || lines[1] != target + " = " + expected || lines[2] != "GLOBAL_F_FORMULA = 32")
        throw new InvalidOperationException($"Defense floor={floor} prior={prior} L1={one} L2={two}: {got} expected {expected}");
    }
    Check(0, null, u, u, 0);
    Check(0, null, t, u, 1);
    Check(0, null, f, t, 2);
    Check(0, 2, t, u, 2);
    Check(0, 2, u, f, 1);
    Check(0, 1, f, f, 0);
    Check(1, 2, f, f, 1);
    Check(2, 0, f, f, 2);
    Pass("independent defense, confirmed retention, downgrade and market mask invariants");
    foreach (var assignments in new[] { Array.Empty<string>(), new[] { target + "=1", target + "=0" },
      new[] { target + "=NaN" }, new[] { target + "=3" } })
    {
      var lines = assignments.ToList();
      bool rejected = false;
      try { HeldMarketDefensePolicy.Apply(lines, target, 2, new[] { (1, t) }); }
      catch (InvalidOperationException) { rejected = true; }
      if (!rejected || !lines.SequenceEqual(assignments)) throw new InvalidOperationException("Malformed defense baseline");
    }
    Pass("malformed and ambiguous defense targets rejected without mutation");
  }

  private static AnalyzerSettings Baseline() => new AnalyzerSettings
  {
    MarketAnalyzer = new MarketAnalyzer
    {
      StoreDataMaxHours = 240,
      MarketTrends = new List<MarketTrend> { new MarketTrend { Name = "SharedTrend", TrendMinutes = 60, Metrics = "PriceTrend,Breadth,RelativeStrengthBTC" } }
    },
    GlobalSettings = new List<GlobalSetting>
    {
      new GlobalSetting { SettingName = "RiskOff", TriggerConnection = "A", Triggers = new List<Trigger> { new Trigger { MarketTrendName = "SharedTrend", Tag = "A", Metric = "Breadth", MinChange = 50 } } },
      new GlobalSetting { SettingName = "Default" }
    },
    SingleMarketSettings = new List<SingleMarketSetting>
    {
      new SingleMarketSetting
      {
        SettingName = "Protection",
        Triggers = new List<Trigger> { new Trigger { MarketTrendName = "SharedTrend", Tag = "Entry", MinChange = 1 } },
        OffTriggers = new List<OffTrigger> { new OffTrigger { MarketTrendName = "SharedTrend", Tag = "Exit", MaxChange = -1 } }
      }
    }
  };

  private static void Valid(string name, Action<AnalyzerSettings> change)
  {
    var settings = Baseline(); change(settings); TriggerConfigurationValidator.ThrowIfInvalid(settings); Pass(name);
  }
  private static void Invalid(string name, Action<AnalyzerSettings> change, string fragment)
  {
    var settings = Baseline(); change(settings);
    if (!TriggerConfigurationValidator.Validate(settings).Any(error => error.Contains(fragment, StringComparison.Ordinal)))
      throw new InvalidOperationException(name + ": expected diagnostic '" + fragment + "'.");
    bool threw = false;
    try { TriggerConfigurationValidator.ThrowIfInvalid(settings); } catch (InvalidOperationException) { threw = true; }
    if (!threw) throw new InvalidOperationException(name + ": startup/reload guard did not reject configuration.");
    Pass(name);
  }
  private static void Warning(string name, Action<AnalyzerSettings> change, string fragment)
  {
    var settings = Baseline(); change(settings); TriggerConfigurationValidator.ThrowIfInvalid(settings);
    if (!TriggerConfigurationValidator.GetMetricWarnings(settings).Any(warning => warning.Contains(fragment, StringComparison.Ordinal)))
      throw new InvalidOperationException(name + ": expected metric warning '" + fragment + "'.");
    Pass(name);
  }
  private static void Pass(string name) { _passed++; Console.WriteLine("PASS " + name); }
}
