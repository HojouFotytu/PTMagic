using System;
using System.Linq;
using System.IO;
using Core.Helper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Monitor.Configuration;
using Newtonsoft.Json;

namespace Monitor.Pages
{
  [RequestFormLimits(ValueCountLimit = 32768)]
  public class SettingsAnalyzerModel : _Internal.BasePageModelSecure
  {
    public string ValidationMessage = "";
    public string SettingsRevision => AnalyzerSettingsForm.Revision(PTMagicConfiguration.AnalyzerSettings);

    public void OnGet()
    {
      base.Init();
      if (GetStringParameter("n", "").Equals("BackupRestored"))
      {
        NotifyHeadline = "Backup restored!";
        NotifyMessage = "Your backup of settings.analyzer.json was successfully restored.";
        NotifyType = "success";
      }
    }

    public void OnPost()
    {
      base.Init();
      try
      {
        // The form transformer works on a detached copy. A stale page, missing
        // reference or invalid expression cannot partially mutate the live file.
        var requestConfiguration = new Core.Main.PTMagicConfiguration(PTMagicBasePath);
        var updated = AnalyzerSettingsStore.Save(PTMagicBasePath,
          HttpContext.Request.Form.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal));

        requestConfiguration.AnalyzerSettings.MarketAnalyzer = updated.MarketAnalyzer;
        requestConfiguration.AnalyzerSettings.GlobalSettings = updated.GlobalSettings;
        requestConfiguration.AnalyzerSettings.SingleMarketSettings = updated.SingleMarketSettings;
        PTMagicConfiguration = requestConfiguration;

        var warnings = TriggerConfigurationValidator.GetMetricWarnings(updated);
        NotifyHeadline = "Settings saved!";
        NotifyMessage = warnings.Count == 0
          ? "Settings saved successfully to settings.analyzer.json."
          : "Settings saved. Some requested metrics are unavailable: " + String.Join(" ", warnings);
        NotifyType = warnings.Count == 0 ? "success" : "warning";
      }
      catch (Exception ex) when (ex is InvalidOperationException || ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
      {
        ValidationMessage = ex.Message;
        NotifyHeadline = "Settings were not saved";
        NotifyMessage = ex.Message;
        NotifyType = "error";
      }
    }
  }
}
