using System;
using System.Linq;
using Core.Main;
using Core.Helper;
using Core.Main.DataObjects.PTMagicData;
using Monitor.Configuration;

namespace Monitor.Pages
{
  public class SettingsSingleMarketSettingsModel : _Internal.BasePageModelSecure
  {
    public SingleMarketSetting SingleMarketSetting = null;
    public string SettingName = "";

    public void OnGet()
    {
      base.Init();
      SettingName = GetStringParameter("gs", "");
      if (!String.IsNullOrEmpty(SettingName))
        SingleMarketSetting = PTMagicConfiguration.AnalyzerSettings.SingleMarketSettings.SingleOrDefault(setting =>
          SystemHelper.StripBadCode(setting.SettingName, Constants.WhiteListNames) == SettingName);
      else
      {
        SingleMarketSetting = new SingleMarketSetting { SettingName = "New Setting" };
        SettingName = "new_" + SystemHelper.StripBadCode(GetStringParameter("newid", Guid.NewGuid().ToString("N")), Constants.WhiteListNames);
      }
      if (SingleMarketSetting == null) throw new InvalidOperationException("This setting no longer exists. Reload the editor.");
    }

    public string GetMarketTrendSelection(Trigger trigger) => AnalyzerSettingsEditor.Options(
      PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.MarketTrends.Select(trend => trend.Name), trigger?.MarketTrendName);
    public string GetValueModes(string key) => AnalyzerSettingsEditor.ValueModes(key);

    public string GetOffTriggerMarketTrendSelection(OffTrigger trigger) => AnalyzerSettingsEditor.Options(
      PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.MarketTrends.Select(trend => trend.Name), trigger?.MarketTrendName);
    public string GetMarketTrendRelationSelection(Trigger trigger) => AnalyzerSettingsEditor.Options(
      new[] { "Relative", "Absolute" }, trigger?.MarketTrendRelation ?? "Relative");
    public string GetOffTriggerMarketTrendRelationSelection(OffTrigger trigger) => AnalyzerSettingsEditor.Options(
      new[] { "Relative", "RelativeTrigger", "Absolute" }, trigger?.MarketTrendRelation ?? "Relative");

  }
}
