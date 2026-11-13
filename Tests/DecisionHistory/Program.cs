using System.Reflection;
using System.Text;
using Core.Helper;
using Core.Main;
using Core.Main.DataObjects.PTMagicData;
using Core.ProfitTrailer;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;

internal static class Program
{
  private static int _passed;
  private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

  private static int Main()
  {
    try
    {
      Run("commit build number encodes Git timestamp as Korea date and time", directory =>
      {
        long commitTimeUtc = new DateTimeOffset(2026, 11, 13, 9, 50, 0, TimeSpan.Zero)
          .ToUnixTimeSeconds();
        Require(BuildIdentity.FormatCommitBuildNumber(commitTimeUtc) == "111320261850");
        Require(BuildIdentity.FormatCommitBuildNumber(commitTimeUtc + 60) == "111320261851");
        Require(BuildIdentity.CommitBuildNumber == "unknown" ||
          System.Text.RegularExpressions.Regex.IsMatch(BuildIdentity.CommitBuildNumber, @"^[0-9]{12}$"));
      });
      Run("append preserves independently parseable complete records", directory =>
      {
        var writer = new DecisionHistoryWriter(directory);
        string path = writer.Append(Record("one"), 30, 64);
        Require(writer.Append(Record("two"), 30, 64) == path);
        var records = Read(path);
        Require(records.Length == 2 && records[0].RaidId == "one" && records[1].RaidId == "two");
        Require(File.ReadAllBytes(path)[^1] == (byte)'\n');
      });
      Run("UTC date selects a new segment", directory =>
      {
        var writer = new DecisionHistoryWriter(directory);
        var first = Record("first"); first.RecordedAtUtc = new DateTimeOffset(Now.UtcDateTime.Date, TimeSpan.Zero).AddHours(1);
        var next = Record("next"); next.RecordedAtUtc = first.RecordedAtUtc.AddDays(1);
        string a = writer.Append(first, 30, 64);
        string b = writer.Append(next, 30, 64);
        Require(a != b && Read(a)[0].RaidId == "first" && Read(b)[0].RaidId == "next");
      });
      Run("expired whole segments are removed and unrelated files survive", directory =>
      {
        var writer = new DecisionHistoryWriter(directory);
        var old = Record("expired"); old.RecordedAtUtc = Now.AddDays(-31);
        string oldPath = writer.Append(old, 30, 64);
        File.SetLastWriteTimeUtc(oldPath, Now.UtcDateTime.AddDays(-31));
        string unrelated = Path.Combine(directory, "operator-notes.txt"); File.WriteAllText(unrelated, "keep");
        string current = writer.Append(Record("current"), 30, 64);
        Require(!File.Exists(oldPath) && File.Exists(current) && File.ReadAllText(unrelated) == "keep");
      });
      Run("an interrupted tail is preserved and the next record rotates", directory =>
      {
        var writer = new DecisionHistoryWriter(directory);
        string old = writer.Append(Record("complete"), 30, 64);
        File.AppendAllText(old, "{\"RaidId\":\"interrupted");
        string before = File.ReadAllText(old);
        string next = writer.Append(Record("after"), 30, 64);
        Require(next != old && File.ReadAllText(old) == before && Read(next).Single().RaidId == "after");
      });
      Run("16 MiB segment rotation retains complete records", directory =>
      {
        var writer = new DecisionHistoryWriter(directory);
        var a = Record("large-a"); a.MetricInputs = LargeInputs(9 * 1024 * 1024);
        var b = Record("large-b"); b.MetricInputs = LargeInputs(9 * 1024 * 1024);
        string first = writer.Append(a, 30, 64);
        string second = writer.Append(b, 30, 64);
        Require(first != second && Read(first).Single().RaidId == "large-a" && Read(second).Single().RaidId == "large-b");
        Require(new FileInfo(first).Length <= 16L * 1024 * 1024 && new FileInfo(second).Length <= 16L * 1024 * 1024);
      });
      Run("quota prunes complete older segments and keeps newest record", directory =>
      {
        var writer = new DecisionHistoryWriter(directory);
        var a = Record("old"); a.MetricInputs = LargeInputs(9 * 1024 * 1024);
        var b = Record("new"); b.MetricInputs = LargeInputs(9 * 1024 * 1024);
        string old = writer.Append(a, 30, 16);
        string newest = writer.Append(b, 30, 16);
        Require(!File.Exists(old) && Read(newest).Single().RaidId == "new");
        Require(Directory.GetFiles(directory, "*.jsonl").Sum(path => new FileInfo(path).Length) <= 16L * 1024 * 1024);
      });
      Run("one oversized record is rejected before damaging existing history", directory =>
      {
        var writer = new DecisionHistoryWriter(directory);
        string path = writer.Append(Record("preserved"), 30, 64);
        byte[] before = File.ReadAllBytes(path);
        var large = Record("too-large"); large.MetricInputs = LargeInputs(16 * 1024 * 1024);
        Throws<InvalidOperationException>(() => writer.Append(large, 30, 64));
        Require(File.ReadAllBytes(path).SequenceEqual(before) && Directory.GetFiles(directory, "*.jsonl").Length == 1);
      });
      Run("invalid retention and quota fail without creating files", directory =>
      {
        var writer = new DecisionHistoryWriter(directory);
        Throws<ArgumentOutOfRangeException>(() => writer.Append(Record("bad"), 0, 64));
        Throws<ArgumentOutOfRangeException>(() => writer.Append(Record("bad"), 3651, 64));
        Throws<ArgumentOutOfRangeException>(() => writer.Append(Record("bad"), 30, 15));
        Throws<ArgumentOutOfRangeException>(() => writer.Append(Record("bad"), 30, 102401));
        Require(Directory.GetFiles(directory).Length == 0);
      });
      Run("acknowledgement checkpoint stores identities without raw properties", directory =>
      {
        var delivery = new ConfigurationDeliveryCoordinator();
        const string sentinel = "test-secret-value-not-for-history";
        var payload = new ConfigurationPayload("ACTIVE", new[] { "api_key = " + sentinel },
          Array.Empty<string>(), Array.Empty<string>(), ConfigurationPayload.Hash("https://fake.invalid/"));
        delivery.Deliver(payload, _ => ConfigurationDeliveryResult.Applied(), false);
        string path = Path.Combine(directory, "ack.json"); delivery.SaveConfirmed(path);
        string json = File.ReadAllText(path);
        Require(!json.Contains(sentinel) && !json.Contains("https://fake.invalid/"));
        var restored = ConfigurationDeliveryCoordinator.Load(path)!;
        Require(restored.AppliedPayloadHash == payload.PayloadHash && restored.AppliedSettingName == "ACTIVE");
      });
      Run("TestMode cannot replace a live acknowledgement checkpoint", directory =>
      {
        var delivery = new ConfigurationDeliveryCoordinator();
        var live = Payload("LIVE"); var preview = Payload("PREVIEW");
        delivery.Deliver(live, _ => ConfigurationDeliveryResult.Applied(), false);
        string path = Path.Combine(directory, "ack.json"); delivery.SaveConfirmed(path);
        string before = File.ReadAllText(path);
        delivery.Deliver(preview, _ => throw new Exception("TestMode attempted transport"), true);
        delivery.SaveConfirmed(path);
        Require(File.ReadAllText(path) == before && delivery.Confirmed.AppliedSettingName == "LIVE");
      });
      Run("actual raid capture preserves separate RelativeTrigger inputs", directory =>
      {
        WithRaid(directory, magic =>
        {
          var trend = new MarketTrend { Name = "SameTrend" };
          Invoke(magic, "CaptureDecisionMetric", trend, "PriceTrend", "BTCUSDT", "RelativeTrigger", 1.5, "", "SMS OffTrigger", "First", "#1");
          Invoke(magic, "CaptureDecisionMetric", trend, "PriceTrend", "BTCUSDT", "RelativeTrigger", -2.5, "", "SMS OffTrigger", "Second", "#1");
          Invoke(magic, "RecordDecisionHistory", "");
          var record = Read(Directory.GetFiles(Path.Combine(directory, "_data", "DecisionHistory"), "*.jsonl").Single()).Single();
          Require(record.MetricInputs.Length == 2);
          Require(record.MetricInputs.Single(input => input.SettingName == "First").Value == 1.5);
          Require(record.MetricInputs.Single(input => input.SettingName == "Second").Value == -2.5);
        });
      });
      Run("actual failed raid history is single-write and excludes credentials", directory =>
      {
        WithRaid(directory, magic =>
        {
          magic.PTMagicConfiguration.GeneralSettings.Application.ProfitTrailerLicense = "SENTINEL-LICENSE-SECRET";
          magic.PTMagicConfiguration.GeneralSettings.Application.ProfitTrailerServerAPIToken = "SENTINEL-TOKEN-SECRET";
          Set(magic, "_desiredGlobalSettingName", "CANDIDATE");
          Invoke(magic, "CaptureDecisionObservation", "Volume24h", "BTCUSDT", null, "Recent market volume is unavailable.", "SMS activation", "VolumeGate", "#1");
          Invoke(magic, "RecordDecisionHistory", "InvalidOperationException");
          Invoke(magic, "RecordDecisionHistory", "");
          string path = Directory.GetFiles(Path.Combine(directory, "_data", "DecisionHistory"), "*.jsonl").Single();
          string json = File.ReadAllText(path); var record = Read(path).Single();
          Require(!json.Contains("SENTINEL-LICENSE-SECRET") && !json.Contains("SENTINEL-TOKEN-SECRET"));
          Require(record.ErrorCategory == "InvalidOperationException" && record.DesiredGlobalSetting == "CANDIDATE");
          Require(record.ConfirmedGlobalSetting == "" && !record.DecisionFresh && record.AnalysisAsOfUtc == null);
          Require(!record.MetricInputs.Single().Available && record.MetricInputs.Single().Value == null);
          Require(!String.IsNullOrWhiteSpace(record.SourceRevision) && record.SourceRevision == BuildIdentity.SourceRevision);
        });
      });
      Run("disabled history creates no decision file", directory =>
      {
        WithRaid(directory, magic =>
        {
          magic.PTMagicConfiguration.GeneralSettings.Application.DecisionHistoryEnabled = false;
          Invoke(magic, "RecordDecisionHistory", "");
          Require(!Directory.Exists(Path.Combine(directory, "_data", "DecisionHistory")));
        });
      });
      Console.WriteLine($"Decision history checks passed: {_passed}.");
      return 0;
    }
    catch (Exception exception)
    {
      Console.Error.WriteLine("Decision history checks FAILED: " + exception);
      return 1;
    }
  }

  private static DecisionHistoryRecord Record(string id) => new()
  {
    RaidId = id, StartedAtUtc = Now.AddSeconds(-10), RecordedAtUtc = Now,
    ApplicationVersion = "2.10.0", SourceRevision = BuildIdentity.SourceRevision,
    Exchange = "BinanceFutures", DeliveryStatus = "Deferred"
  };
  private static DecisionMetricInput[] LargeInputs(int length) => new[]
    { new DecisionMetricInput { Metric = "Fixture", Reason = new string('x', length) } };
  private static ConfigurationPayload Payload(string name) => new(name, new[] { "DEFAULT_sell_only_mode_enabled = true" },
    Array.Empty<string>(), Array.Empty<string>(), "test-destination");
  private static DecisionHistoryRecord[] Read(string path) => File.ReadAllLines(path)
    .Where(line => !String.IsNullOrWhiteSpace(line))
    .Select(line => JsonConvert.DeserializeObject<DecisionHistoryRecord>(line)!).ToArray();
  private static void Run(string name, Action<string> test)
  {
    string directory = Path.Combine(Path.GetTempPath(), "ptm210-history-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try { test(directory); _passed++; Console.WriteLine("PASS " + name); }
    finally { Directory.Delete(directory, true); }
  }
  private static void WithRaid(string directory, Action<Core.Main.PTMagic> action)
  {
    string previous = Directory.GetCurrentDirectory();
    try
    {
      var general = new GeneralSettingsWrapper { GeneralSettings = new GeneralSettings
      {
        Application = new Application { Exchange = "BinanceFutures", ProfitTrailerMonitorURL = "http://localhost/", TestMode = true },
        Monitor = new Core.Main.DataObjects.PTMagicData.Monitor(), Telegram = new Core.Main.DataObjects.PTMagicData.Telegram(), Backup = new Backup()
      } };
      var analyzer = new AnalyzerSettingsWrapper { AnalyzerSettings = new AnalyzerSettings
      {
        MarketAnalyzer = new Core.Main.DataObjects.PTMagicData.MarketAnalyzer { StoreDataMaxHours = 744, MarketTrends = new() },
        GlobalSettings = new() { new GlobalSetting { SettingName = "DEFAULT" } }, SingleMarketSettings = new()
      } };
      File.WriteAllText(Path.Combine(directory, "settings.general.json"), JsonConvert.SerializeObject(general));
      File.WriteAllText(Path.Combine(directory, "settings.analyzer.json"), JsonConvert.SerializeObject(analyzer));
      Directory.SetCurrentDirectory(directory);
      var magic = new Core.Main.PTMagic(new LogHelper(NullLogger<LogHelper>.Instance))
      {
        PTMagicConfiguration = new PTMagicConfiguration(directory), LastRuntime = Now.UtcDateTime,
        CurrentVersion = new Version(2, 10, 0), LastRuntimeSummary = new Summary()
      };
      Set(magic, "_raidId", "fixture-raid");
      action(magic);
    }
    finally { Directory.SetCurrentDirectory(previous); }
  }
  private static void Invoke(object instance, string name, params object?[] args)
  {
    try { instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args); }
    catch (TargetInvocationException exception) when (exception.InnerException != null) { throw exception.InnerException; }
  }
  private static void Set(object instance, string name, object? value) => instance.GetType()
    .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
  private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
  private static void Throws<T>(Action action) where T : Exception
  {
    try { action(); } catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
  }
}
