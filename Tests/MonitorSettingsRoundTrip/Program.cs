using System.Globalization;
using System.Xml.Linq;
using Core.Helper;
using Core.Main;
using Core.Main.DataObjects.PTMagicData;
using Monitor.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class Program
{
  private const string Global = "MarketAnalyzer_GlobalSetting_RiskOff|";
  private const string Single = "MarketAnalyzer_SingleMarketSetting_Protection|";
  private static int _passed;

  private static int Main()
  {
    try
    {
      Check("complete no-edit form roundtrip", () =>
      {
        var source = Baseline();
        Equal(source, AnalyzerSettingsForm.Apply(source, Form(source)));
      });
      Check("same-trend global row edits only its own metric", () =>
      {
        var source = Baseline(); var form = Form(source);
        Set(form, Global + "Trigger_i1|MinChange", "57.123456789012345");
        var saved = AnalyzerSettingsForm.Apply(source, form);
        Equal(source.GlobalSettings[0].Triggers[0], saved.GlobalSettings[0].Triggers[0]);
        Require(saved.GlobalSettings[0].Triggers[1].MinChange == Double.Parse("57.123456789012345", CultureInfo.InvariantCulture));
        Require(saved.GlobalSettings[0].Triggers[1].Tag == "B" && saved.GlobalSettings[0].Triggers[1].Metric == "Breadth");
      });
      Check("same-trend global delete uses row identity", () =>
      {
        var source = Baseline(); var form = Form(source);
        Set(form, Global + "TriggerOrder", "i1"); Set(form, Global + "TriggerConnection", "AND");
        var saved = AnalyzerSettingsForm.Apply(source, form);
        Require(saved.GlobalSettings[0].Triggers.Count == 1);
        Equal(source.GlobalSettings[0].Triggers[1], saved.GlobalSettings[0].Triggers[0]);
      });
      Check("trigger order is posted order", () =>
      {
        var source = Baseline(); var form = Form(source);
        form[Global + "TriggerOrder"] = new[] { "i1", "i0" };
        var saved = AnalyzerSettingsForm.Apply(source, form);
        Equal(source.GlobalSettings[0].Triggers[1], saved.GlobalSettings[0].Triggers[0]);
        Equal(source.GlobalSettings[0].Triggers[0], saved.GlobalSettings[0].Triggers[1]);
      });
      Check("multiple new rows on the same trend remain distinct", () =>
      {
        var source = Baseline(); var form = Form(source);
        form[Global + "TriggerOrder"] = new[] { "i0", "i1", "new_a", "new_b" };
        AddMetric(form, Global, "new_a", "PriceTrend", "C", "0.123456789012345", "");
        AddMetric(form, Global, "new_b", "Breadth", "D", "61.23456789012345", "");
        var saved = AnalyzerSettingsForm.Apply(source, form);
        Require(saved.GlobalSettings[0].Triggers.Count == 4);
        Require(saved.GlobalSettings[0].Triggers[2].Tag == "C" && saved.GlobalSettings[0].Triggers[3].Tag == "D");
      });
      Check("SMS activation rows retain interleaved order and all hidden fields", () =>
      {
        var source = Baseline();
        Equal(source.SingleMarketSettings[0].Triggers, AnalyzerSettingsForm.Apply(source, Form(source)).SingleMarketSettings[0].Triggers);
      });
      Check("age and volume tags are editable without losing inactive fields", () =>
      {
        var source = Baseline(); var form = Form(source);
        Set(form, Single + "Trigger_i1|Tag", "Young"); Set(form, Single + "Trigger_i2|Tag", "Liquid");
        Set(form, Single + "TriggerConnection", "(A && Young) || (Liquid && D)");
        var saved = AnalyzerSettingsForm.Apply(source, form);
        var expected = Clone(source.SingleMarketSettings[0].Triggers);
        expected[1].Tag = "Young"; expected[2].Tag = "Liquid";
        Equal(expected, saved.SingleMarketSettings[0].Triggers);
      });
      Check("OffTrigger metric and tag edits affect one same-trend row", () =>
      {
        var source = Baseline(); var form = Form(source);
        Set(form, Single + "OffTrigger_i3|Metric", "RelativeStrengthBTC");
        Set(form, Single + "OffTrigger_i3|Tag", "ExitRS");
        var saved = AnalyzerSettingsForm.Apply(source, form);
        var expected = Clone(source.SingleMarketSettings[0].OffTriggers);
        expected[3].Metric = "RelativeStrengthBTC"; expected[3].Tag = "ExitRS";
        Equal(expected, saved.SingleMarketSettings[0].OffTriggers);
      });
      Check("OffTrigger deletion and reordering preserve remaining timed and volume rows", () =>
      {
        var source = Baseline(); var form = Form(source);
        form[Single + "OffTriggerOrder"] = new[] { "i2", "i1", "i3" };
        var saved = AnalyzerSettingsForm.Apply(source, form);
        Equal(new[] { source.SingleMarketSettings[0].OffTriggers[2], source.SingleMarketSettings[0].OffTriggers[1], source.SingleMarketSettings[0].OffTriggers[3] }, saved.SingleMarketSettings[0].OffTriggers);
      });
      Check("remove every off-trigger is explicit", () =>
      {
        var source = Baseline(); var form = Form(source); form.Remove(Single + "OffTriggerOrder");
        Require(AnalyzerSettingsForm.Apply(source, form).SingleMarketSettings[0].OffTriggers.Count == 0);
      });
      Check("unrendered trigger lists and advanced settings survive", () =>
      {
        var source = Baseline(); var form = Form(source);
        form.Remove(Global + "TriggersRendered"); form.Remove(Single + "OffTriggersRendered");
        foreach (string key in form.Keys.Where(key => key.EndsWith("|CorrelationLookback") || key.EndsWith("|ATRPBreadthThreshold")).ToArray()) form.Remove(key);
        Equal(source, AnalyzerSettingsForm.Apply(source, form));
      });
      Check("checkbox false and true values are independent", () =>
      {
        var source = Baseline(); var form = Form(source);
        Set(form, Single + "RefreshOffTriggers", "false");
        form[Single + "StopProcessWhenTriggered"] = new[] { "false", "on" };
        var saved = AnalyzerSettingsForm.Apply(source, form).SingleMarketSettings[0];
        Require(!saved.RefreshOffTriggers && saved.StopProcessWhenTriggered);
      });
      Check("property strings numbers booleans formulas and suffixes roundtrip", () =>
      {
        var source = Baseline();
        Equal(source.GlobalSettings[0].PairsProperties, AnalyzerSettingsForm.Apply(source, Form(source)).GlobalSettings[0].PairsProperties);
      });
      Check("remove all properties retains unrelated sections", () =>
      {
        var source = Baseline(); var form = Form(source);
        foreach (string key in form.Keys.Where(key => key.StartsWith(Global + "PairsProperty_", StringComparison.Ordinal)).ToArray()) form.Remove(key);
        var saved = AnalyzerSettingsForm.Apply(source, form);
        Require(saved.GlobalSettings[0].PairsProperties.Count == 0);
        Equal(source.SingleMarketSettings, saved.SingleMarketSettings);
      });
      Check("raw names and reference spelling survive HTML options", () =>
      {
        var source = Baseline(); source.MarketAnalyzer.MarketTrends[0].Name = "Trend & 'quoted'";
        foreach (var trigger in source.GlobalSettings.SelectMany(setting => setting.Triggers)) trigger.MarketTrendName = "trend & 'quoted'";
        foreach (var trigger in source.SingleMarketSettings.SelectMany(setting => setting.Triggers).Where(trigger => trigger.AgeDaysLowerThan == 0 && trigger.Min24hVolume == 0)) trigger.MarketTrendName = "trend & 'quoted'";
        foreach (var trigger in source.SingleMarketSettings.SelectMany(setting => setting.OffTriggers).Where(trigger => trigger.HoursSinceTriggered == 0 && trigger.Min24hVolume == 0)) trigger.MarketTrendName = "trend & 'quoted'";
        Equal(source, AnalyzerSettingsForm.Apply(source, Form(source)));
      });
      Check("culture does not round or reinterpret numeric fields", () =>
      {
        CultureInfo prior = CultureInfo.CurrentCulture;
        try
        {
          CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
          var source = Baseline(); Equal(source, AnalyzerSettingsForm.Apply(source, Form(source)));
        }
        finally { CultureInfo.CurrentCulture = prior; }
      });
      Check("stale revision rejects without mutating source", () => Reject(form => Set(form, "AnalyzerSettingsRevision", "old")));
      Check("incomplete AJAX page rejects", () => Reject(form => Set(form, "AnalyzerSettingsReady", "false")));
      Check("duplicate row identity rejects", () => Reject(form => form[Global + "TriggerOrder"] = new[] { "i0", "i0" }));
      Check("noncanonical index alias rejects", () => Reject(form => form[Global + "TriggerOrder"] = new[] { "i00", "i1" }));
      Check("repeated scalar field rejects", () => Reject(form => form[Global + "Trigger_i0|Metric"] = new[] { "PriceTrend", "Breadth" }));
      Check("missing reference rejects atomically", () => Reject(form => Set(form, Global + "Trigger_i1|MarketTrendName", "DeletedTrend")));
      Check("malformed expression rejects atomically", () => Reject(form => Set(form, Single + "TriggerConnection", "A && (")));
      Check("new setting identities permit several additions", () =>
      {
        var source = Baseline(); var form = Form(source);
        Set(form, "MarketAnalyzer_SingleMarketSetting_new_one|SettingName", "FirstNew");
        Set(form, "MarketAnalyzer_SingleMarketSetting_new_two|SettingName", "SecondNew");
        var saved = AnalyzerSettingsForm.Apply(source, form);
        Require(saved.SingleMarketSettings.Count == 3 && saved.SingleMarketSettings[1].SettingName == "FirstNew" && saved.SingleMarketSettings[2].SettingName == "SecondNew");
      });
      Check("removing the only default fallback is rejected", () => Reject(form =>
      {
        foreach (string key in form.Keys.Where(key => key.StartsWith("MarketAnalyzer_GlobalSetting_Default|", StringComparison.Ordinal)).ToArray()) form.Remove(key);
      }));
      Check("unsupported timeframe is rejected", () => Reject(form => Set(form, "MarketAnalyzer_MarketTrend_SharedTrend|TrendMinutes", "7")));
      Check("atomic store saves and rejects the second same-revision post", () =>
      {
        WithFile((directory, source) =>
        {
          var form = Form(source); Set(form, Global + "Trigger_i1|MinChange", "59.123456789012345");
          AnalyzerSettingsStore.Save(directory, form);
          string path = Path.Combine(directory, "settings.analyzer.json"); string committed = File.ReadAllText(path);
          bool rejected = false;
          try { AnalyzerSettingsStore.Save(directory, form); } catch (InvalidOperationException) { rejected = true; }
          Require(rejected && File.ReadAllText(path) == committed && File.Exists(path + ".backup"));
          Require(source.GlobalSettings[0].Triggers[1].MinChange == Baseline().GlobalSettings[0].Triggers[1].MinChange);
        });
      });
      Check("failed file replacement leaves original file and source intact", () =>
      {
        WithFile((directory, source) =>
        {
          string path = Path.Combine(directory, "settings.analyzer.json"); string before = File.ReadAllText(path);
          Directory.CreateDirectory(path + ".backup"); // A backup directory cannot be replaced by a file.
          var form = Form(source); Set(form, Global + "Trigger_i1|MinChange", "59"); bool rejected = false;
          try { AnalyzerSettingsStore.Save(directory, form); }
          catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { rejected = true; }
          Require(rejected && File.ReadAllText(path) == before && !Directory.EnumerateFiles(directory, "*.tmp").Any());
          Equal(Baseline(), source);
        });
      });
      Console.WriteLine("Monitor production settings roundtrip checks passed: " + _passed + ".");
      return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine("Monitor settings roundtrip FAILED: " + ex); return 1; }
  }

  private static AnalyzerSettings Baseline() => new AnalyzerSettings
  {
    MarketAnalyzer = new MarketAnalyzer
    {
      StoreDataMaxHours = 240, IntervalMinutes = 5,
      IncludedUnderlyingTypes = new List<string> { "COIN", "equity" },
      MarketTrends = new List<MarketTrend> { new MarketTrend
      {
        Name = "SharedTrend", TrendMinutes = 60,
        Metrics = "PriceTrend,Breadth,RelativeStrengthBTC,CorrelationToBTC,MeanCorrelationToBTC,MedianCorrelationToBTC",
        CorrelationLookback = 37, ATRPBreadthThreshold = 0.12345678901234567,
        IgnoredMarkets = "TESTUSDT", DisplayGraph = false
      } }
    },
    GlobalSettings = new List<GlobalSetting>
    {
      new GlobalSetting
      {
        SettingName = "RiskOff", TriggerConnection = "A || B",
        Triggers = new List<Trigger>
        {
          new Trigger { MarketTrendName = "SharedTrend", Metric = "PriceTrend", Tag = "A", MinChange = -0.12345678901234567, MaxChange = 4.567890123456789, MarketTrendRelation = "Absolute" },
          new Trigger { MarketTrendName = "SharedTrend", Metric = "Breadth", Tag = "B", MinChange = 51.23456789012345 }
        },
        PairsProperties = new Dictionary<string, object>
        {
          ["NUMBER"] = 0.12345678901234567,
          ["LARGE"] = 9007199254740993L,
          ["BOOL"] = true,
          ["STRING_NUMBER"] = "001.250",
          ["FORMULA"] = "x > 1 ? 'quoted' : \"a&b\"",
          ["BUY_OFFSET_LITERAL"] = "keep_suffix_inside_name",
          ["BUY_offset"] = 0.0000000123456789
        }
      },
      new GlobalSetting { SettingName = "Default" }
    },
    SingleMarketSettings = new List<SingleMarketSetting>
    {
      new SingleMarketSetting
      {
        SettingName = "Protection", TriggerConnection = "(A && C) || (V && D)", OffTriggerConnection = "OR", RefreshOffTriggers = true,
        AllowedMarkets = "BTCUSDT,ETHUSDT", IgnoredGlobalSettings = "Disabled",
        Triggers = new List<Trigger>
        {
          new Trigger { MarketTrendName = "SharedTrend", Metric = "PriceTrend", Tag = "A", MinChange = -1.2345678901234567, MarketTrendRelation = "Absolute" },
          new Trigger { AgeDaysLowerThan = 7, Tag = "C", Metric = "kept_inactive_age_field", MaxChange = 8.765432109876543 },
          new Trigger { Min24hVolume = 100.12345678901234, Max24hVolume = 2000.9876543210987, Tag = "V", AgeDaysLowerThan = 4, Metric = "kept_inactive_volume_field" },
          new Trigger { MarketTrendName = "SharedTrend", Metric = "CorrelationToBTC", Tag = "D", MinChange = 0.12345678901234567 }
        },
        OffTriggers = new List<OffTrigger>
        {
          new OffTrigger { MarketTrendName = "SharedTrend", Metric = "PriceTrend", Tag = "ExitPrice", MinChange = 0.34567890123456789, MarketTrendRelation = "RelativeTrigger" },
          new OffTrigger { HoursSinceTriggered = 3, Tag = "Clock", Metric = "kept_inactive_time_field", Min24hVolume = 2.1234567890123456 },
          new OffTrigger { Min24hVolume = 33.123456789012345, Max24hVolume = 55.987654321098765, Tag = "Volume", Metric = "kept_inactive_volume_field", MinChange = -0.34567890123456789 },
          new OffTrigger { MarketTrendName = "SharedTrend", Metric = "CorrelationToBTC", Tag = "ExitCorrelation", MaxChange = -0.23456789012345678 }
        },
        PairsProperties = new Dictionary<string, object> { ["DEFAULT_sell_only_mode"] = true }
      }
    }
  };

  // Build browser form fields from the same production row projection used by
  // the AJAX pages, then call the exact transformer invoked by OnPost.
  private static Dictionary<string, string[]> Form(AnalyzerSettings source)
  {
    var form = new Dictionary<string, string[]>(StringComparer.Ordinal);
    Set(form, "AnalyzerSettingsFormVersion", AnalyzerSettingsForm.Version);
    Set(form, "AnalyzerSettingsRevision", AnalyzerSettingsForm.Revision(source));
    Set(form, "AnalyzerSettingsReady", "true");
    Fields(form, source.MarketAnalyzer, "MarketAnalyzer_", "StoreDataMaxHours", "IntervalMinutes", "ExcludeMainCurrency", "UseProfitTrailerEnabledPairs", "UseProfitTrailerAllowedUnderlyingTypes");
    Set(form, "MarketAnalyzer_IncludedUnderlyingTypes", String.Join(", ", source.MarketAnalyzer.IncludedUnderlyingTypes));
    foreach (var trend in source.MarketAnalyzer.MarketTrends)
      Fields(form, trend, "MarketAnalyzer_MarketTrend_" + Identity(trend.Name) + "|", "Name", "Platform", "TrendMinutes", "MaxMarkets", "TrendCurrency", "Metrics", "IgnoredMarkets", "AllowedMarkets", "TrendThreshold", "DisplayGraph", "DisplayOnMarketAnalyzerList", "ExcludeMainCurrency", "ATRPeriod", "VolumeExpansionLookback", "CorrelationLookback", "ATRPBreadthThreshold", "VolumeExpansionBreadthThreshold", "StrongVolumeExpansionThreshold", "TopNVolumeCount");
    foreach (var setting in source.GlobalSettings)
    {
      string identity = Identity(setting.SettingName); string parent = "MarketAnalyzer_GlobalSetting_" + identity + "|";
      Fields(form, setting, parent, "SettingName");
      if (!setting.SettingName.StartsWith("Default", StringComparison.OrdinalIgnoreCase))
      {
        Fields(form, setting, parent, "TriggerConnection");
        Rows(form, parent, "", AnalyzerSettingsEditor.TriggerRows(setting.Triggers, source.MarketAnalyzer.MarketTrends, "GlobalSetting", identity, "USDT"));
      }
      Properties(form, parent, "GlobalSetting", identity, "Pairs", setting.PairsProperties);
      Properties(form, parent, "GlobalSetting", identity, "DCA", setting.DCAProperties);
      Properties(form, parent, "GlobalSetting", identity, "Indicators", setting.IndicatorsProperties);
    }
    foreach (var setting in source.SingleMarketSettings)
    {
      string identity = Identity(setting.SettingName); string parent = "MarketAnalyzer_SingleMarketSetting_" + identity + "|";
      Fields(form, setting, parent, "SettingName", "TriggerConnection", "OffTriggerConnection", "IgnoredMarkets", "AllowedMarkets", "IgnoredGlobalSettings", "AllowedGlobalSettings", "StopProcessWhenTriggered", "RefreshOffTriggers");
      Rows(form, parent, "", AnalyzerSettingsEditor.TriggerRows(setting.Triggers, source.MarketAnalyzer.MarketTrends, "SingleMarketSetting", identity, "USDT"));
      Rows(form, parent, "Off", AnalyzerSettingsEditor.OffTriggerRows(setting.OffTriggers, source.MarketAnalyzer.MarketTrends, identity, "USDT"));
      Properties(form, parent, "SingleMarketSetting", identity, "Pairs", setting.PairsProperties);
      Properties(form, parent, "SingleMarketSetting", identity, "DCA", setting.DCAProperties);
      Properties(form, parent, "SingleMarketSetting", identity, "Indicators", setting.IndicatorsProperties);
    }
    return form;
  }

  private static void Rows(Dictionary<string, string[]> form, string parent, string off, object[] rows)
  {
    Set(form, parent + off + "TriggersRendered", "true");
    var data = JArray.FromObject(rows);
    form[parent + off + "TriggerOrder"] = data.Select(row => (string)row["rowId"]).ToArray();
    foreach (var row in data)
    {
      string prefix = parent + off + "Trigger_" + row["rowId"] + "|";
      string kind = (string)row["kind"];
      Set(form, prefix + "Kind", kind); Set(form, prefix + "Tag", (string)row["tag"]);
      if (kind == "metric")
      {
        Set(form, prefix + "MarketTrendName", Selected((string)row["marketTrends"]));
        Set(form, prefix + "Metric", (string)row["metric"]);
        Set(form, prefix + "MinChange", (string)row["minChange"]);
        Set(form, prefix + "MaxChange", (string)row["maxChange"]);
        if ((string)row["settingType"] == "SingleMarketSetting") Set(form, prefix + "MarketTrendRelation", Selected((string)row["marketTrendRelations"]));
      }
      else if (kind == "volume")
      {
        Set(form, prefix + "Min24hVolume", (string)row["min24hVolume"]);
        Set(form, prefix + "Max24hVolume", (string)row["max24hVolume"]);
      }
      else if (kind == "age") Set(form, prefix + "AgeDaysLowerThan", (string)row["coinAgeDays"]);
      else Set(form, prefix + "HoursSinceTriggered", (string)row["hoursSinceTriggered"]);
    }
  }

  private static void Properties(Dictionary<string, string[]> form, string parent, string type, string identity, string kind, Dictionary<string, object> properties)
  {
    Set(form, parent + kind + "PropertiesRendered", "true");
    foreach (var row in JArray.FromObject(AnalyzerSettingsEditor.PropertyRows(properties, type, identity, kind)))
    {
      string key = parent + kind + "Property_" + row["rowId"];
      Set(form, key, (string)row["propertyKeySimple"]);
      Set(form, key + "|Value", (string)row["value"]);
      Set(form, key + "|ValueMode", Selected((string)row["valueModes"]));
    }
  }

  private static void Fields(Dictionary<string, string[]> form, object source, string prefix, params string[] fields)
  {
    foreach (string field in fields)
    {
      object value = source.GetType().GetProperty(field).GetValue(source);
      if (value is bool flag) form[prefix + field] = flag ? new[] { "false", "on" } : new[] { "false" };
      else Set(form, prefix + field, AnalyzerSettingsEditor.PropertyValue(value));
    }
  }

  private static string Selected(string html)
  {
    var options = XElement.Parse("<select>" + html + "</select>").Elements("option").ToArray();
    return (string)(options.FirstOrDefault(option => option.Attribute("selected") != null) ?? options.First()).Attribute("value");
  }
  private static string Identity(string name) => SystemHelper.StripBadCode(name, Constants.WhiteListNames);
  private static void Set(Dictionary<string, string[]> form, string key, string value) => form[key] = new[] { value ?? "" };
  private static void AddMetric(Dictionary<string, string[]> form, string parent, string id, string metric, string tag, string min, string max)
  {
    string prefix = parent + "Trigger_" + id + "|";
    Set(form, prefix + "Kind", "metric"); Set(form, prefix + "MarketTrendName", "SharedTrend");
    Set(form, prefix + "Metric", metric); Set(form, prefix + "Tag", tag);
    Set(form, prefix + "MinChange", min); Set(form, prefix + "MaxChange", max);
  }
  private static void Reject(Action<Dictionary<string, string[]>> edit)
  {
    var source = Baseline(); string before = JsonConvert.SerializeObject(source); var form = Form(source); edit(form);
    bool rejected = false;
    try { AnalyzerSettingsForm.Apply(source, form); } catch (InvalidOperationException) { rejected = true; }
    Require(rejected && JsonConvert.SerializeObject(source) == before);
  }
  private static T Clone<T>(T value) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value));
  private static void WithFile(Action<string, AnalyzerSettings> test)
  {
    string directory = Path.Combine(Path.GetTempPath(), "PTMagic-MonitorRoundTrip-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
      var source = Baseline(); File.WriteAllText(Path.Combine(directory, "settings.analyzer.json"), JsonConvert.SerializeObject(new AnalyzerSettingsWrapper { AnalyzerSettings = source }));
      test(directory, source);
    }
    finally { Directory.Delete(directory, true); }
  }
  private static void Equal(object expected, object actual) => Require(JToken.DeepEquals(JToken.FromObject(expected), JToken.FromObject(actual)));
  private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
  private static void Check(string name, Action test) { test(); _passed++; Console.WriteLine("PASS " + name); }
}
