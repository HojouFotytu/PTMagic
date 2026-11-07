using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using Core.Main;
using Core.Main.DataObjects.PTMagicData;
using Newtonsoft.Json;

namespace Monitor.Configuration
{
  /// <summary>Lossless editor values shared by the AJAX pages and form harness.</summary>
  public static class AnalyzerSettingsEditor
  {
    public static string Json(object value) => JsonConvert.SerializeObject(value,
      new JsonSerializerSettings { StringEscapeHandling = StringEscapeHandling.EscapeHtml });

    public static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static string PropertyValue(object value) => value switch
    {
      null => "",
      bool flag => flag ? "true" : "false",
      double number => Number(number),
      float number => number.ToString("R", CultureInfo.InvariantCulture),
      IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
      _ => value.ToString()
    };

    public static string Options(IEnumerable<string> choices, string selected)
    {
      List<string> values = choices.ToList();
      if (!String.IsNullOrEmpty(selected) && !values.Contains(selected, StringComparer.OrdinalIgnoreCase))
        values.Insert(0, selected); // Keep an unavailable reference visible until it is repaired.
      return String.Join("", values.Select(value =>
      {
        bool match = String.Equals(value, selected, StringComparison.OrdinalIgnoreCase);
        string saved = match ? selected : value; // Keep original reference spelling on no-edit saves.
        return "<option value=\"" + WebUtility.HtmlEncode(saved) + "\"" +
          (match ? " selected=\"selected\"" : "") + ">" + WebUtility.HtmlEncode(value) + "</option>";
      }));
    }

    public static string ValueModes(string key)
    {
      string mode = key.EndsWith("_OFFSETPERCENT", StringComparison.OrdinalIgnoreCase) ? key.Substring(key.Length - 14)
        : key.EndsWith("_OFFSET", StringComparison.OrdinalIgnoreCase) ? key.Substring(key.Length - 7) : "";
      return String.Join("", new[] { "", "_OFFSET", "_OFFSETPERCENT" }.Select(value =>
        "<option value=\"" + (value.Equals(mode, StringComparison.OrdinalIgnoreCase) ? mode : value) + "\"" +
        (value.Equals(mode, StringComparison.OrdinalIgnoreCase) ? " selected=\"selected\"" : "") + ">" +
        (value == "" ? "Flat value" : value == "_OFFSET" ? "Offset by flat value" : "Offset by percent") + "</option>"));
    }

    public static object[] TriggerRows(IEnumerable<Trigger> triggers, IEnumerable<MarketTrend> trends,
      string settingType, string settingName, string mainMarket)
    {
      bool single = settingType == "SingleMarketSetting";
      return (triggers ?? Enumerable.Empty<Trigger>()).Select((trigger, index) => (object)new
      {
        settingType, settingName, mainMarket,
        rowId = "i" + index,
        triggerPrefix = "",
        kind = single && (trigger.Min24hVolume > 0 || trigger.Max24hVolume < Constants.Max24hVolume) ? "volume"
          : single && trigger.AgeDaysLowerThan > 0 ? "age" : "metric",
        metric = trigger.Metric,
        tag = trigger.Tag,
        minChange = trigger.MinChange == Constants.MinTrendChange ? "" : Number(trigger.MinChange),
        maxChange = trigger.MaxChange == Constants.MaxTrendChange ? "" : Number(trigger.MaxChange),
        min24hVolume = trigger.Min24hVolume == 0 ? "" : Number(trigger.Min24hVolume),
        max24hVolume = trigger.Max24hVolume == Constants.Max24hVolume ? "" : Number(trigger.Max24hVolume),
        coinAgeDays = trigger.AgeDaysLowerThan.ToString(CultureInfo.InvariantCulture),
        marketTrends = Options(trends.Select(trend => trend.Name), trigger.MarketTrendName),
        marketTrendRelations = Options(new[] { "Relative", "Absolute" }, trigger.MarketTrendRelation)
      }).ToArray();
    }

    public static object[] OffTriggerRows(IEnumerable<OffTrigger> triggers, IEnumerable<MarketTrend> trends,
      string settingName, string mainMarket) => (triggers ?? Enumerable.Empty<OffTrigger>())
      .Select((trigger, index) => (object)new
      {
        settingType = "SingleMarketSetting", settingName, mainMarket,
        rowId = "i" + index,
        triggerPrefix = "Off",
        kind = trigger.HoursSinceTriggered > 0 ? "hours"
          : trigger.Min24hVolume > 0 || trigger.Max24hVolume < Constants.Max24hVolume ? "volume" : "metric",
        metric = trigger.Metric,
        tag = trigger.Tag,
        minChange = trigger.MinChange == Constants.MinTrendChange ? "" : Number(trigger.MinChange),
        maxChange = trigger.MaxChange == Constants.MaxTrendChange ? "" : Number(trigger.MaxChange),
        min24hVolume = trigger.Min24hVolume == 0 ? "" : Number(trigger.Min24hVolume),
        max24hVolume = trigger.Max24hVolume == Constants.Max24hVolume ? "" : Number(trigger.Max24hVolume),
        hoursSinceTriggered = trigger.HoursSinceTriggered.ToString(CultureInfo.InvariantCulture),
        marketTrends = Options(trends.Select(trend => trend.Name), trigger.MarketTrendName),
        marketTrendRelations = Options(new[] { "Relative", "RelativeTrigger", "Absolute" }, trigger.MarketTrendRelation)
      }).ToArray();

    public static object[] PropertyRows(Dictionary<string, object> properties, string settingType,
      string settingName, string propertyType) => (properties ?? new Dictionary<string, object>())
      .Select((property, index) =>
      {
        int suffixLength = property.Key.EndsWith("_OFFSETPERCENT", StringComparison.OrdinalIgnoreCase) ? 14
          : property.Key.EndsWith("_OFFSET", StringComparison.OrdinalIgnoreCase) ? 7 : 0;
        return (object)new
        {
          settingType, settingName, propertyType,
          rowId = "i" + index,
          propertyKeySimple = property.Key.Substring(0, property.Key.Length - suffixLength),
          value = PropertyValue(property.Value),
          valueModes = ValueModes(property.Key)
        };
      }).ToArray();
  }
}
