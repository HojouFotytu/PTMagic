using System;
using System.IO;
using Newtonsoft.Json;

namespace Core.ProfitTrailer
{
  /// <summary>
  /// PTM's past global SOM enable is NOT ownership of the PT global flag.
  /// Only the user releases global SOM in ProfitTrailer.
  /// </summary>
  public sealed class StartupSellOnlyModeState
  {
    public int SchemaVersion { get; set; } = 2;
    public string DestinationHash { get; set; } = "";
    public bool EnableIntentPending { get; set; }
    public bool EnabledAcknowledged { get; set; }
    public bool ManualReviewRequired { get; set; }
    // Count attempted startup raids since the first possible live SOM enable.
    // The temporary user-facing warning is displayed only on raids 1 and 2.
    public int NoticeRaidCount { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    // Read the old release intent for safe migration; never serialize it.
    public bool PendingRelease { get; set; }
    public bool ShouldSerializePendingRelease() => false;

    public static StartupSellOnlyModeState Load(string path)
    {
      if (!File.Exists(path)) return null;
      StartupSellOnlyModeState state = JsonConvert.DeserializeObject<StartupSellOnlyModeState>(File.ReadAllText(path));
      if (state == null) return null;
      if (state.SchemaVersion == 2) return state;
      if (state.SchemaVersion != 1) return null;
      // Old release intent may have overridden user SOM: force manual review.
      return new StartupSellOnlyModeState
      {
        DestinationHash = state.DestinationHash,
        EnableIntentPending = false,
        EnabledAcknowledged = false,
        ManualReviewRequired = state.PendingRelease || state.EnabledAcknowledged,
        UpdatedAtUtc = state.UpdatedAtUtc
      };
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
