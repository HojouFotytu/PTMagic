using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Core.Helper;
using Core.Main;
using Core.Main.DataObjects.PTMagicData;
using Newtonsoft.Json;

namespace Monitor.Configuration
{
  /// <summary>
  /// The analyzer editor's production form transformation. Row tokens identify
  /// original trigger indices within a revision-checked configuration, never a
  /// MarketTrend name. Edits are applied to a detached copy before validation.
  /// </summary>
  public static class AnalyzerSettingsForm
  {
    public const string Version = "2";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Revision(AnalyzerSettings settings) =>
      Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(settings))));

    public static AnalyzerSettings Apply(AnalyzerSettings original, IReadOnlyDictionary<string, string[]> form)
    {
      if (original == null) throw new ArgumentNullException(nameof(original));
      if (form == null) throw new ArgumentNullException(nameof(form));
      if (Read(form, "AnalyzerSettingsFormVersion") != Version)
        throw new InvalidOperationException("This editor page is out of date. Reload it before saving.");
      if (Read(form, "AnalyzerSettingsReady") != "true")
        throw new InvalidOperationException("Wait for every settings section to load before saving.");
      if (!String.Equals(Read(form, "AnalyzerSettingsRevision"), Revision(original), StringComparison.Ordinal))
        throw new InvalidOperationException("Analyzer settings changed after this page was loaded. Reload the page before saving your changes.");

      AnalyzerSettings updated = Clone(original);
      SetFields(updated.MarketAnalyzer, form, "MarketAnalyzer_", new[]
      {
        "StoreDataMaxHours", "IntervalMinutes", "ExcludeMainCurrency",
        "UseProfitTrailerEnabledPairs", "UseProfitTrailerAllowedUnderlyingTypes"
      });
      if (form.ContainsKey("MarketAnalyzer_IncludedUnderlyingTypes") &&
        Read(form, "MarketAnalyzer_IncludedUnderlyingTypes") != String.Join(", ", original.MarketAnalyzer.IncludedUnderlyingTypes ?? new List<string>()))
        updated.MarketAnalyzer.IncludedUnderlyingTypes = Read(form, "MarketAnalyzer_IncludedUnderlyingTypes")
          .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim().ToUpperInvariant())
          .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

      List<MarketTrend> trends = new List<MarketTrend>();
      foreach (string key in ParentKeys(form, "MarketAnalyzer_MarketTrend_", "Name"))
      {
        string token = ParentToken(key, "MarketAnalyzer_MarketTrend_", "Name");
        MarketTrend trend = Existing(original.MarketAnalyzer.MarketTrends, token, item => item.Name, () => new MarketTrend());
        SetFields(trend, form, "MarketAnalyzer_MarketTrend_" + token + "|", new[]
        {
          "Name", "Platform", "TrendMinutes", "MaxMarkets", "TrendCurrency", "Metrics",
          "IgnoredMarkets", "AllowedMarkets", "TrendThreshold", "DisplayGraph", "DisplayOnMarketAnalyzerList",
          "ExcludeMainCurrency", "ATRPeriod", "VolumeExpansionLookback", "ATRPBreadthThreshold",
          "VolumeExpansionBreadthThreshold", "StrongVolumeExpansionThreshold", "TopNVolumeCount", "CorrelationLookback"
        });
        trends.Add(trend);
      }
      updated.MarketAnalyzer.MarketTrends = trends;

      List<GlobalSetting> globals = new List<GlobalSetting>();
      foreach (string key in ParentKeys(form, "MarketAnalyzer_GlobalSetting_", "SettingName"))
      {
        string token = ParentToken(key, "MarketAnalyzer_GlobalSetting_", "SettingName");
        GlobalSetting setting = Existing(original.GlobalSettings, token, item => item.SettingName, () => new GlobalSetting());
        string prefix = "MarketAnalyzer_GlobalSetting_" + token + "|";
        SetFields(setting, form, prefix, new[] { "SettingName", "TriggerConnection" });
        if (form.ContainsKey(prefix + "TriggersRendered"))
          setting.Triggers = ReadTriggers(setting.Triggers, form, prefix, false);
        setting.PairsProperties = ReadProperties(setting.PairsProperties, form, prefix, "Pairs");
        setting.DCAProperties = ReadProperties(setting.DCAProperties, form, prefix, "DCA");
        setting.IndicatorsProperties = ReadProperties(setting.IndicatorsProperties, form, prefix, "Indicators");
        globals.Add(setting);
      }
      updated.GlobalSettings = globals;

      List<SingleMarketSetting> singles = new List<SingleMarketSetting>();
      foreach (string key in ParentKeys(form, "MarketAnalyzer_SingleMarketSetting_", "SettingName"))
      {
        string token = ParentToken(key, "MarketAnalyzer_SingleMarketSetting_", "SettingName");
        SingleMarketSetting setting = Existing(original.SingleMarketSettings, token, item => item.SettingName, () => new SingleMarketSetting());
        string prefix = "MarketAnalyzer_SingleMarketSetting_" + token + "|";
        SetFields(setting, form, prefix, new[]
        {
          "SettingName", "TriggerConnection", "OffTriggerConnection", "IgnoredMarkets", "AllowedMarkets",
          "IgnoredGlobalSettings", "AllowedGlobalSettings", "StopProcessWhenTriggered", "RefreshOffTriggers"
        });
        if (form.ContainsKey(prefix + "TriggersRendered"))
          setting.Triggers = ReadTriggers(setting.Triggers, form, prefix, true);
        if (form.ContainsKey(prefix + "OffTriggersRendered"))
          setting.OffTriggers = ReadOffTriggers(setting.OffTriggers, form, prefix);
        setting.PairsProperties = ReadProperties(setting.PairsProperties, form, prefix, "Pairs");
        setting.DCAProperties = ReadProperties(setting.DCAProperties, form, prefix, "DCA");
        setting.IndicatorsProperties = ReadProperties(setting.IndicatorsProperties, form, prefix, "Indicators");
        singles.Add(setting);
      }
      updated.SingleMarketSettings = singles;
      TriggerConfigurationValidator.ThrowIfInvalid(updated);
      return updated;
    }

    private static List<Trigger> ReadTriggers(List<Trigger> original, IReadOnlyDictionary<string, string[]> form,
      string parent, bool single)
    {
      List<Trigger> result = new List<Trigger>();
      foreach (string token in RowOrder(form, parent + "TriggerOrder"))
      {
        Trigger trigger = OriginalRow(original, token, () => new Trigger());
        string prefix = parent + "Trigger_" + token + "|";
        string kind = Read(form, prefix + "Kind");
        if (kind == "metric")
        {
          SetFields(trigger, form, prefix, new[] { "MarketTrendName", "Metric", "Tag", "MarketTrendRelation", "MinChange", "MaxChange" });
        }
        else if (single && kind == "volume")
        {
          SetFields(trigger, form, prefix, new[] { "Tag", "Min24hVolume", "Max24hVolume" });
        }
        else if (single && kind == "age")
        {
          SetFields(trigger, form, prefix, new[] { "Tag", "AgeDaysLowerThan" });
        }
        else throw new InvalidOperationException("Unknown trigger row kind '" + kind + "'. Reload the editor.");
        result.Add(trigger);
      }
      return result;
    }

    private static List<OffTrigger> ReadOffTriggers(List<OffTrigger> original,
      IReadOnlyDictionary<string, string[]> form, string parent)
    {
      List<OffTrigger> result = new List<OffTrigger>();
      foreach (string token in RowOrder(form, parent + "OffTriggerOrder"))
      {
        OffTrigger trigger = OriginalRow(original, token, () => new OffTrigger());
        string prefix = parent + "OffTrigger_" + token + "|";
        string kind = Read(form, prefix + "Kind");
        if (kind == "metric")
          SetFields(trigger, form, prefix, new[] { "MarketTrendName", "Metric", "Tag", "MarketTrendRelation", "MinChange", "MaxChange" });
        else if (kind == "volume")
          SetFields(trigger, form, prefix, new[] { "Tag", "Min24hVolume", "Max24hVolume" });
        else if (kind == "hours")
          SetFields(trigger, form, prefix, new[] { "Tag", "HoursSinceTriggered" });
        else throw new InvalidOperationException("Unknown off-trigger row kind '" + kind + "'. Reload the editor.");
        result.Add(trigger);
      }
      return original == null && result.Count == 0 ? null : result;
    }

    private static IEnumerable<string> RowOrder(IReadOnlyDictionary<string, string[]> form, string key)
    {
      if (!form.TryGetValue(key, out string[] values)) return Array.Empty<string>();
      if (values == null || values.Any(String.IsNullOrWhiteSpace) || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
        throw new InvalidOperationException("Trigger row identities are missing or duplicated. Reload the editor.");
      return values;
    }

    private static T OriginalRow<T>(IReadOnlyList<T> original, string token, Func<T> create)
    {
      if (token.StartsWith("new_", StringComparison.Ordinal) && token.Length > 4) return create();
      if (token.Length < 2 || token[0] != 'i' || !Int32.TryParse(token.Substring(1), NumberStyles.None, Invariant, out int index)
        || index < 0 || token != "i" + index.ToString(Invariant) || original == null || index >= original.Count || original[index] is null)
        throw new InvalidOperationException("Trigger row '" + token + "' no longer exists. Reload the editor.");
      return Clone(original[index]);
    }

    private static T Existing<T>(IEnumerable<T> originals, string token, Func<T, string> name, Func<T> create)
    {
      T[] matches = (originals ?? Enumerable.Empty<T>()).Where(item =>
        SystemHelper.StripBadCode(name(item), Constants.WhiteListNames) == token).ToArray();
      if (matches.Length == 0 && token.StartsWith("new_", StringComparison.Ordinal) && token.Length > 4) return create();
      if (matches.Length != 1)
        throw new InvalidOperationException("Setting identity '" + token + "' is missing or ambiguous. Reload the editor and use unique names.");
      return Clone(matches[0]);
    }

    private static IEnumerable<string> ParentKeys(IReadOnlyDictionary<string, string[]> form, string prefix, string field) =>
      form.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal) && key.EndsWith("|" + field, StringComparison.Ordinal)
        && key.IndexOf('|') == key.Length - field.Length - 1);

    private static string ParentToken(string key, string prefix, string field) =>
      key.Substring(prefix.Length, key.Length - prefix.Length - field.Length - 1);

    private static void SetFields(object target, IReadOnlyDictionary<string, string[]> form, string prefix, IEnumerable<string> fields)
    {
      foreach (string field in fields)
      {
        string key = prefix + field;
        if (!form.TryGetValue(key, out string[] values)) continue; // Unrendered fields retain their original value.
        PropertyInfo property = target.GetType().GetProperty(field);
        if (property == null || !property.CanWrite) throw new InvalidOperationException("Unsupported editor field '" + field + "'.");
        if (property.PropertyType == typeof(bool))
        {
          if (values == null || values.Length == 0 || values.Any(value => value != "false" && value != "true" && value != "on"))
            throw new InvalidOperationException("Invalid checkbox value for '" + field + "'.");
          property.SetValue(target, values.Any(value => value == "true" || value == "on"));
          continue;
        }
        string value = Read(form, key);
        if (property.PropertyType == typeof(string))
        {
          if ((property.GetValue(target) as string ?? "") != value) property.SetValue(target, value);
        }
        else if (property.PropertyType == typeof(int))
        {
          if (!Int32.TryParse(value, NumberStyles.Integer, Invariant, out int number))
            throw new InvalidOperationException("'" + field + "' must be a whole number.");
          property.SetValue(target, number);
        }
        else if (property.PropertyType == typeof(double))
        {
          if (value.Length == 0)
          {
            property.SetValue(target, field == "MinChange" ? Constants.MinTrendChange
              : field == "MaxChange" ? Constants.MaxTrendChange
              : field == "Max24hVolume" ? Constants.Max24hVolume : 0.0);
          }
          else
          {
            if (!Double.TryParse(value, NumberStyles.Float, Invariant, out double number) || !Double.IsFinite(number))
              throw new InvalidOperationException("'" + field + "' must be a finite number (use a decimal point).");
            property.SetValue(target, number);
          }
        }
        else throw new InvalidOperationException("Unsupported editor field type for '" + field + "'.");
      }
    }

    private static Dictionary<string, object> ReadProperties(Dictionary<string, object> original,
      IReadOnlyDictionary<string, string[]> form, string parent, string kind)
    {
      if (!form.ContainsKey(parent + kind + "PropertiesRendered")) return original;
      Dictionary<string, object> result = new Dictionary<string, object>();
      string prefix = parent + kind + "Property_";
      foreach (string key in form.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal) && key.IndexOf('|', parent.Length) < 0))
      {
        string[] names = form[key];
        if (!form.TryGetValue(key + "|Value", out string[] values) || !form.TryGetValue(key + "|ValueMode", out string[] modes)
          || names == null || values == null || modes == null || names.Length != values.Length || names.Length != modes.Length)
          throw new InvalidOperationException("Incomplete property row. Reload the editor.");
        for (int index = 0; index < names.Length; index++)
        {
          string name = names[index] + modes[index];
          if (String.IsNullOrWhiteSpace(name) || result.ContainsKey(name))
            throw new InvalidOperationException("Property names must be nonempty and unique: '" + name + "'.");
          string value = values[index] ?? "";
          if (original != null && original.TryGetValue(name, out object existing)
            && String.Equals(AnalyzerSettingsEditor.PropertyValue(existing), value, StringComparison.Ordinal))
          {
            result.Add(name, existing); // Preserve original string/bool/number type on no-edit saves.
          }
          else if (Boolean.TryParse(value, out bool flag)) result.Add(name, flag);
          else if (Int64.TryParse(value, NumberStyles.Integer, Invariant, out long integer)) result.Add(name, integer);
          else if (Double.TryParse(value, NumberStyles.Float, Invariant, out double number) && Double.IsFinite(number)) result.Add(name, number);
          else result.Add(name, value);
        }
      }
      return original == null && result.Count == 0 ? null : result;
    }

    private static string Read(IReadOnlyDictionary<string, string[]> form, string key)
    {
      if (!form.TryGetValue(key, out string[] values) || values == null || values.Length != 1)
        throw new InvalidOperationException("Missing or repeated editor field '" + key + "'. Reload the editor.");
      return values[0] ?? "";
    }

    private static T Clone<T>(T value) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value));
  }
}
