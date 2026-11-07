using System;
using System.Linq;
using Core.Main;
using Core.Helper;
using Core.Main.DataObjects.PTMagicData;
using Monitor.Configuration;

namespace Monitor.Pages
{
  public class SettingsGlobalSettingsModel : _Internal.BasePageModelSecure
  {
    public GlobalSetting GlobalSetting = null;
    public string SettingName = "";
    public bool IsDefaultSetting
    {
      get
      {
        var settings = PTMagicConfiguration.AnalyzerSettings.GlobalSettings;
        var fallback = settings.FirstOrDefault(setting => String.Equals(setting.SettingName, "default", StringComparison.OrdinalIgnoreCase))
          ?? settings.FirstOrDefault(setting => setting.SettingName.IndexOf("default", StringComparison.OrdinalIgnoreCase) >= 0);
        return fallback != null && String.Equals(fallback.SettingName, GlobalSetting.SettingName, StringComparison.OrdinalIgnoreCase);
      }
    }

    public void OnGet()
    {
      base.Init();
      SettingName = GetStringParameter("gs", "");
      if (!String.IsNullOrEmpty(SettingName))
        GlobalSetting = PTMagicConfiguration.AnalyzerSettings.GlobalSettings.SingleOrDefault(setting =>
          SystemHelper.StripBadCode(setting.SettingName, Constants.WhiteListNames) == SettingName);
      else
      {
        GlobalSetting = new GlobalSetting { SettingName = "New Setting" };
        SettingName = "new_" + SystemHelper.StripBadCode(GetStringParameter("newid", Guid.NewGuid().ToString("N")), Constants.WhiteListNames);
      }
      if (GlobalSetting == null) throw new InvalidOperationException("This setting no longer exists. Reload the editor.");
    }

    public string GetMarketTrendSelection(Trigger trigger) => AnalyzerSettingsEditor.Options(
      PTMagicConfiguration.AnalyzerSettings.MarketAnalyzer.MarketTrends.Select(trend => trend.Name), trigger?.MarketTrendName);
    public string GetValueModes(string key) => AnalyzerSettingsEditor.ValueModes(key);

  }
}
