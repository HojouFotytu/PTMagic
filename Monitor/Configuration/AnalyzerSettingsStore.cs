using System;
using System.Collections.Generic;
using System.IO;
using Core.Main.DataObjects.PTMagicData;
using Newtonsoft.Json;

namespace Monitor.Configuration
{
  /// <summary>Serializes editor saves against fresh disk state and atomically replaces the file.</summary>
  public static class AnalyzerSettingsStore
  {
    private static readonly object SaveLock = new object();

    public static AnalyzerSettings Save(string basePath, IReadOnlyDictionary<string, string[]> form)
    {
      lock (SaveLock)
      {
        string path = Path.Combine(basePath, "settings.analyzer.json");
        string before = File.ReadAllText(path);
        var current = JsonConvert.DeserializeObject<AnalyzerSettingsWrapper>(before)?.AnalyzerSettings;
        if (current == null) throw new InvalidOperationException("The analyzer settings file is empty or invalid.");
        AnalyzerSettings updated = AnalyzerSettingsForm.Apply(current, form);
        var settings = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore };
        string json = JsonConvert.SerializeObject(new AnalyzerSettingsWrapper { AnalyzerSettings = updated }, Formatting.Indented, settings);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
          File.WriteAllText(temporary, json);
          if (File.ReadAllText(path) != before)
            throw new InvalidOperationException("Analyzer settings changed while saving. Reload the editor before trying again.");
          // Same-directory replacement keeps readers on a complete old or new
          // JSON file and preserves the replaced file as the usual backup.
          File.Replace(temporary, path, path + ".backup", true);
        }
        finally
        {
          if (File.Exists(temporary)) File.Delete(temporary);
        }
        return updated;
      }
    }
  }
}
