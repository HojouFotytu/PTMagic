using System;
using System.IO;
using Newtonsoft.Json;

namespace Core.ProfitTrailer
{
  /// <summary>
  /// Tracks PTM's temporary ownership of the native global SOM switch separately
  /// from configuration delivery. Contains no properties, URLs or API tokens.
  /// PendingRelease is saved before enabling so an interrupted request is retried.
  /// </summary>
  public sealed class StartupSellOnlyModeState
  {
    public int SchemaVersion { get; set; } = 1;
    public string DestinationHash { get; set; } = "";
    public bool PendingRelease { get; set; }
    public bool EnabledAcknowledged { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public static StartupSellOnlyModeState Load(string path)
    {
      if (!File.Exists(path)) return null;
      StartupSellOnlyModeState state = JsonConvert.DeserializeObject<StartupSellOnlyModeState>(File.ReadAllText(path));
      return state?.SchemaVersion == 1 ? state : null;
    }

    public void Save(string path)
    {
      UpdatedAtUtc = DateTimeOffset.UtcNow;
      Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
      string temporary = path + ".tmp";
      File.WriteAllText(temporary, JsonConvert.SerializeObject(this, Formatting.Indented));
      File.Move(temporary, path, true);
    }
  }
}
