using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Core.ProfitTrailer
{
  /// <summary>A complete desired PT configuration. Comments do not make a new configuration.</summary>
  public sealed class ConfigurationPayload
  {
    public string SettingName { get; }
    public IReadOnlyList<string> Pairs { get; }
    public IReadOnlyList<string> Dca { get; }
    public IReadOnlyList<string> Indicators { get; }
    public string DestinationHash { get; }
    public string PayloadHash { get; }

    public ConfigurationPayload(string settingName, IEnumerable<string> pairs, IEnumerable<string> dca,
      IEnumerable<string> indicators, string destinationHash)
    {
      SettingName = settingName ?? "";
      Pairs = Array.AsReadOnly((pairs ?? throw new ArgumentNullException(nameof(pairs))).ToArray());
      Dca = Array.AsReadOnly((dca ?? throw new ArgumentNullException(nameof(dca))).ToArray());
      Indicators = Array.AsReadOnly((indicators ?? throw new ArgumentNullException(nameof(indicators))).ToArray());
      DestinationHash = destinationHash ?? "";
      PayloadHash = Hash(JsonConvert.SerializeObject(new
      {
        SettingName, DestinationHash,
        Pairs = CanonicalLines(Pairs), Dca = CanonicalLines(Dca), Indicators = CanonicalLines(Indicators)
      }));
    }

    private static string[] CanonicalLines(IEnumerable<string> lines) => lines
      .Where(line => !String.IsNullOrWhiteSpace(line))
      .Select(line => line.Trim())
      .Where(line => !line.StartsWith("#", StringComparison.Ordinal) && !line.StartsWith("!", StringComparison.Ordinal))
      .ToArray();

    public static string Hash(string text) => Convert.ToHexString(
      SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""))).ToLowerInvariant();
  }

  public sealed class ConfigurationDeliveryResult
  {
    public bool Succeeded { get; init; }
    public bool Attempted { get; init; }
    public bool Simulated { get; init; }
    public string Status { get; init; } = "Deferred";
    // Only a bounded status/category is stored. Never include credentials or an HTTP body.
    public string Error { get; init; } = "";
    public static ConfigurationDeliveryResult Applied() => new ConfigurationDeliveryResult
      { Succeeded = true, Attempted = true, Status = "Applied" };
    public static ConfigurationDeliveryResult Failed(string error) => new ConfigurationDeliveryResult
      { Succeeded = false, Attempted = true, Status = "Failed", Error = error ?? "Transfer failed." };
    public static ConfigurationDeliveryResult Deferred(string reason) => new ConfigurationDeliveryResult
      { Succeeded = false, Status = "Deferred", Error = reason ?? "" };
  }

  /// <summary>Persisted acknowledgement metadata only. No PT properties, keys, tokens or URLs.</summary>
  public sealed class ConfigurationDeliveryState
  {
    public int SchemaVersion { get; set; } = 1;
    public string AppliedSettingName { get; set; } = "";
    public string AppliedPayloadHash { get; set; } = "";
    public string DestinationHash { get; set; } = "";
    public DateTimeOffset? AppliedAtUtc { get; set; }
  }

  /// <summary>
  /// Production delivery state machine. An attempted/failed transfer never advances the
  /// acknowledged state. The next identical desired payload therefore remains dirty.
  /// TestMode uses an independent preview state and cannot overwrite a live checkpoint.
  /// </summary>
  public sealed class ConfigurationDeliveryCoordinator
  {
    private ConfigurationDeliveryState _preview;
    public ConfigurationDeliveryState Confirmed { get; private set; }
    public ConfigurationDeliveryResult LastResult { get; private set; } = ConfigurationDeliveryResult.Deferred("No delivery yet.");
    public bool Pending { get; private set; }

    public ConfigurationDeliveryCoordinator(ConfigurationDeliveryState confirmed = null)
    {
      Confirmed = confirmed ?? new ConfigurationDeliveryState();
    }

    public ConfigurationDeliveryResult Deliver(ConfigurationPayload payload,
      Func<ConfigurationPayload, ConfigurationDeliveryResult> send, bool testMode, bool force = false)
    {
      if (payload == null) throw new ArgumentNullException(nameof(payload));
      ConfigurationDeliveryState current = testMode ? _preview : Confirmed;
      bool unchanged = current != null && current.AppliedPayloadHash == payload.PayloadHash
        && current.DestinationHash == payload.DestinationHash;
      // A previous attempt may have reached only some PT targets. Even the old
      // confirmed payload must be replayed until every target acknowledges it.
      if (unchanged && !force && !Pending)
      {
        Pending = false;
        return LastResult = new ConfigurationDeliveryResult
          { Succeeded = true, Simulated = testMode, Status = testMode ? "TestMode unchanged" : "Unchanged" };
      }

      if (testMode)
      {
        _preview = Acknowledged(payload);
        Pending = false;
        return LastResult = new ConfigurationDeliveryResult
          { Succeeded = true, Simulated = true, Status = "TestMode preview" };
      }

      Pending = true;
      try
      {
        LastResult = (send ?? throw new ArgumentNullException(nameof(send)))(payload)
          ?? ConfigurationDeliveryResult.Failed("Transport returned no acknowledgement.");
      }
      catch (Exception exception)
      {
        LastResult = ConfigurationDeliveryResult.Failed("Transport exception: " + exception.GetType().Name);
      }
      if (LastResult.Succeeded && !LastResult.Simulated)
      {
        Confirmed = Acknowledged(payload);
        Pending = false;
      }
      return LastResult;
    }

    private static ConfigurationDeliveryState Acknowledged(ConfigurationPayload payload) => new ConfigurationDeliveryState
    {
      AppliedSettingName = payload.SettingName, AppliedPayloadHash = payload.PayloadHash,
      DestinationHash = payload.DestinationHash,
      AppliedAtUtc = DateTimeOffset.UtcNow
    };

    public static ConfigurationDeliveryState Load(string path)
    {
      if (!File.Exists(path)) return null;
      ConfigurationDeliveryState state = JsonConvert.DeserializeObject<ConfigurationDeliveryState>(File.ReadAllText(path));
      return state?.SchemaVersion == 1 ? state : null;
    }

    public void SaveConfirmed(string path)
    {
      if (String.IsNullOrWhiteSpace(Confirmed.AppliedPayloadHash)) return;
      Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
      string temporary = path + ".tmp";
      File.WriteAllText(temporary, JsonConvert.SerializeObject(Confirmed, Formatting.Indented));
      File.Move(temporary, path, true);
    }
  }

}
