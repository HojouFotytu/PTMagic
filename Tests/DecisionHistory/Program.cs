using System.Reflection;
using System.IO.Compression;
using Newtonsoft.Json.Linq;
using Core.MarketAnalyzer.MarketData;
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
        // Do not accept the "unknown" fallback: a released build must carry
        // the current commit timestamp in Core's assembly metadata.
        var stamp = typeof(BuildIdentity).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
          .SingleOrDefault(attribute => attribute.Key == "PTMCommitUnixSeconds")?.Value;
        Require(long.TryParse(stamp, System.Globalization.NumberStyles.None,
          System.Globalization.CultureInfo.InvariantCulture, out long embeddedSeconds));
        Require(BuildIdentity.CommitBuildNumber == BuildIdentity.FormatCommitBuildNumber(embeddedSeconds));
        Require(System.Text.RegularExpressions.Regex.IsMatch(BuildIdentity.CommitBuildNumber, @"^[0-9]{12}$"));
      });
      Run("atomic compressed raids are independently readable with exact UTC filenames", directory =>
      {
        var writer = new DecisionHistoryWriter(directory);
        var first = Record("one");
        string one = writer.Append(first, 30);
        string two = writer.Append(Record("two"), 30);
        Require(one != two && Read(one).Single().RaidId == "one" && Read(two).Single().RaidId == "two");
        Require(DecisionHistoryFileNames.TryCompactTimestamp(one, out var recorded) && recorded == first.RecordedAtUtc.UtcDateTime);
        Require(ReadRaw(one).EndsWith("\n") && !Directory.EnumerateFiles(directory, "*.tmp").Any());
        Require(Read(one).Single().SchemaVersion == 2);
      });
      Run("UTC date remains encoded in distinct compressed raid files", directory =>
      {
        var writer = new DecisionHistoryWriter(directory);
        var first = Record("first"); first.RecordedAtUtc = new DateTimeOffset(Now.UtcDateTime.Date, TimeSpan.Zero).AddHours(1);
        var next = Record("next"); next.RecordedAtUtc = first.RecordedAtUtc.AddDays(1);
        string a = writer.Append(first, 30);
        string b = writer.Append(next, 30);
        Require(a != b && Read(a)[0].RaidId == "first" && Read(b)[0].RaidId == "next");
        Require(Path.GetFileName(a).StartsWith(first.RecordedAtUtc.ToString("yyyy-MM-dd")));
        Require(Path.GetFileName(b).StartsWith(next.RecordedAtUtc.ToString("yyyy-MM-dd")));
      });
      Run("expired compact and legacy history removed; unrelated files survive", directory =>
      {
        var writer = new DecisionHistoryWriter(directory);
        var old = Record("expired"); old.RecordedAtUtc = Now.AddDays(-31);
        string oldPath = writer.Append(old, 30);
        string legacy = Path.Combine(directory, old.RecordedAtUtc.ToString("yyyy-MM-dd") + "-000001.jsonl");
        File.WriteAllText(legacy, JsonConvert.SerializeObject(old) + "\n");
        File.SetLastWriteTimeUtc(legacy, Now.UtcDateTime.AddDays(-31));
        string unrelated = Path.Combine(directory, "operator-notes.txt"); File.WriteAllText(unrelated, "keep");
        string current = writer.Append(Record("current"), 30);
        Require(!File.Exists(oldPath) && !File.Exists(legacy) && File.Exists(current) && File.ReadAllText(unrelated) == "keep");
      });
      Run("interrupted legacy evidence is not altered by new compressed capture", directory =>
      {
        var writer = new DecisionHistoryWriter(directory);
        string old = Path.Combine(directory, Now.ToString("yyyy-MM-dd") + "-000001.jsonl");
        File.WriteAllText(old, JsonConvert.SerializeObject(Record("complete")) + "\n{\"RaidId\":\"interrupted");
        string before = File.ReadAllText(old);
        string next = writer.Append(Record("after"), 30);
        Require(next != old && File.ReadAllText(old) == before && Read(next).Single().RaidId == "after");
        Require(DecisionHistoryCodec.ReadLine(before.Split('\n')[0]).RaidId == "complete");
      });
      Run("large raids are independently compressed with no segment-size cap", directory =>
      {
        var writer = new DecisionHistoryWriter(directory);
        var a = Record("large-a"); a.MetricInputs = LargeInputs(9 * 1024 * 1024);
        var b = Record("large-b"); b.MetricInputs = LargeInputs(9 * 1024 * 1024);
        string first = writer.Append(a, 30);
        string second = writer.Append(b, 30);
        Require(first != second && Read(first).Single().MetricInputs.Single().Reason == a.MetricInputs.Single().Reason &&
          Read(second).Single().MetricInputs.Single().Reason == b.MetricInputs.Single().Reason);
        Require(new FileInfo(first).Length < 9 * 1024 * 1024 && new FileInfo(second).Length < 9 * 1024 * 1024);
      });
      Run("older raids survive storage growth with no cumulative quota", directory =>
      {
        var writer = new DecisionHistoryWriter(directory);
        var a = Record("old"); a.MetricInputs = LargeInputs(9 * 1024 * 1024);
        var b = Record("new"); b.MetricInputs = LargeInputs(9 * 1024 * 1024);
        string old = writer.Append(a, 30);
        string newest = writer.Append(b, 30);
        Require(File.Exists(old) && File.Exists(newest) && old != newest);
        Require(Read(old).Single().RaidId == "old" && Read(newest).Single().RaidId == "new");
        Require(ReadRaw(old).Length + ReadRaw(newest).Length > 16L * 1024 * 1024);
      });
      Run("oversized raid preserves complete input and surrounding history", directory =>
      {
        var writer = new DecisionHistoryWriter(directory);
        string path = writer.Append(Record("preserved"), 30);
        byte[] before = File.ReadAllBytes(path);
        var large = Record("large-raid"); large.MetricInputs = LargeInputs(16 * 1024 * 1024);
        string largePath = writer.Append(large, 30);
        string next = writer.Append(Record("after-large"), 30);
        Require(path != largePath && largePath != next && path != next);
        Require(File.ReadAllBytes(path).SequenceEqual(before));
        Require(ReadRaw(largePath).Length > 16L * 1024 * 1024);
        var restored = Read(largePath).Single();
        Require(restored.RaidId == large.RaidId && restored.MetricInputs.Single().Reason == large.MetricInputs.Single().Reason);
        Require(Read(next).Single().RaidId == "after-large");
        Require(Directory.GetFiles(directory, "*.jsonl.gz").Length == 3);
      });
      Run("wide raid deduplicates observations but preserves every trigger reference", directory =>
      {
        var writer = new DecisionHistoryWriter(directory);
        var raid = Record("wide-analyzer-raid");
        raid.MetricInputs = Enumerable.Range(0, 729).SelectMany(market =>
          Enumerable.Range(0, 100).Select(trigger => new DecisionMetricInput
          {
            Scope = "SMS activation", SettingName = "FixtureSMS", TriggerId = "C" + trigger,
            MarketTrend = "Swing72", Metric = "RelativeStrengthMarket",
            Market = "FIXTURE" + market + "USDT", Relation = "Delta", Value = 1.25,
            Available = true, Reason = "", SourceDataTimestampUtc = Now,
            PreviousSourceDataTimestampUtc = Now.AddHours(-72), CoveragePercent = 100
          })).ToArray();
        string path = writer.Append(raid, 30);
        string raw = ReadRaw(path);
        var compact = JObject.Parse(raw);
        Require(compact["MetricInputs"] == null && compact["Observations"]!.Count() == 729 &&
          compact["TriggerDefinitions"]!.Count() == 100 && compact["TriggerInputs"]!.Count() == 72900);
        Require(raw.Length < JsonConvert.SerializeObject(raid).Length);
        var restored = Read(path).Single();
        Require(restored.RaidId == raid.RaidId && restored.MetricInputs.Length == 72900);
        Require(restored.MetricInputs.First().Market == "FIXTURE0USDT" &&
          restored.MetricInputs.Last().Market == "FIXTURE728USDT" &&
          restored.MetricInputs.Last().TriggerId == "C99" &&
          restored.MetricInputs.Last().Value == 1.25 &&
          restored.MetricInputs.Last().PreviousSourceDataTimestampUtc == Now.AddHours(-72));
        Require(JsonConvert.SerializeObject(restored.MetricInputs) == JsonConvert.SerializeObject(raid.MetricInputs));
      });
      Run("different values, unavailable evidence, timestamps and coverage remain losslessly distinct", directory =>
      {
        VolumeCoverageDiagnostics Cover() => new()
        {
          EligibleAssets = new[] { "BTCUSDT", "ETHUSDT" }, ValidAssets = new[] { "BTCUSDT" },
          ExcludedAssets = new[] { "ETHUSDT" }, EstimatedVolumeWeightedCoveragePercent = 95,
          ActualQuoteVolumeDenominator = 120, EstimateAgeMinutes = 60
        };
        DecisionMetricInput Input(string trigger, double? value, string? reason, VolumeCoverageDiagnostics? coverage) => new()
        {
          Scope = "SMS activation", SettingName = "SMS1", TriggerId = trigger, MarketTrend = "T1",
          Metric = "VolumeConcentration", Market = null, Relation = "Delta", Value = value,
          Available = value.HasValue, Reason = reason, SourceDataTimestampUtc = Now.ToOffset(TimeSpan.FromHours(9)),
          PreviousSourceDataTimestampUtc = Now.AddHours(-1), CoveragePercent = 95,
          VolumeCoverage = coverage, PreviousVolumeCoverage = coverage, CurrentMatchedValue = 20, PreviousMatchedValue = 10
        };
        var raid = Record("distinct");
        raid.MetricInputs = new[] { Input("C1", 10, "", Cover()), Input("C2", 10, "", Cover()),
          Input("C3", 11, "", Cover()), Input("C4", null, "unavailable", null), Input("C5", null, null, null) };
        var path = new DecisionHistoryWriter(directory).Append(raid, 30);
        var raw = JObject.Parse(ReadRaw(path));
        Require(raw["Observations"]!.Count() == 4 && raw["CoverageDiagnostics"]!.Count() == 1 &&
          raw["TriggerInputs"]!.Count() == 5);
        var restored = Read(path).Single();
        Require(JsonConvert.SerializeObject(restored.MetricInputs) == JsonConvert.SerializeObject(raid.MetricInputs));
        Require(restored.MetricInputs[0].SourceDataTimestampUtc!.Value.Offset == TimeSpan.FromHours(9));
        var bad = JObject.Parse(ReadRaw(path)); bad["TriggerInputs"]![0]![1] = 99999;
        Throws<InvalidDataException>(() => DecisionHistoryCodec.ReadLine(bad.ToString(Formatting.None)));
      });
      Run("schema 1 retained records remain readable", directory =>
      {
        var raid = Record("legacy");
        raid.MetricInputs = new[] { new DecisionMetricInput { Metric = "Breadth", Value = 42, Available = true } };
        string path = Path.Combine(directory, Now.ToString("yyyy-MM-dd") + "-000001.jsonl");
        File.WriteAllText(path, JsonConvert.SerializeObject(raid) + "\n");
        var restored = Read(path).Single();
        Require(restored.SchemaVersion == 1 && restored.RaidId == "legacy" && restored.MetricInputs.Single().Value == 42);
      });
      Run("invalid day retention fails before writing files", directory =>
      {
        var writer = new DecisionHistoryWriter(directory);
        Throws<ArgumentOutOfRangeException>(() => writer.Append(Record("bad"), 0));
        Throws<ArgumentOutOfRangeException>(() => writer.Append(Record("bad"), 3651));
        Require(Directory.GetFiles(directory).Length == 0);
      });
      Run("MarketData-only cleanup does not remove exchange metadata", directory =>
      {
        string folder = Path.Combine(directory, "Exchange"); Directory.CreateDirectory(folder);
        string old = Path.Combine(folder, "MarketData_old.json");
        string fresh = Path.Combine(folder, "MarketData_recent.json");
        string metadata = Path.Combine(folder, "MarketInfo.json");
        File.WriteAllText(old, "{}"); File.WriteAllText(fresh, "{}"); File.WriteAllText(metadata, "{}");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-32));
        File.SetLastWriteTimeUtc(metadata, DateTime.UtcNow.AddDays(-32));
        FileHelper.CleanupMarketDataFiles(folder, 30 * 24);
        Require(!File.Exists(old) && File.Exists(fresh) && File.Exists(metadata));
      });
      Run("legacy history-days migration keeps explicit setting and drops old quota", directory =>
      {
        var general = new GeneralSettingsWrapper { GeneralSettings = new GeneralSettings
        {
          Application = new Application { Exchange = "BinanceFutures", ProfitTrailerMonitorURL = "http://localhost/" },
          Monitor = new Core.Main.DataObjects.PTMagicData.Monitor(), Backup = new Backup(),
          Telegram = new Core.Main.DataObjects.PTMagicData.Telegram()
        }};
        string json = JsonConvert.SerializeObject(general).Replace("\"HistoryRetentionDays\":30",
          "\"DecisionHistoryRetentionDays\":42");
        Require(json.Contains("\"DecisionHistoryRetentionDays\":42"));
        File.WriteAllText(Path.Combine(directory, "settings.general.json"), json);
        File.WriteAllText(Path.Combine(directory, "settings.analyzer.json"), JsonConvert.SerializeObject(new AnalyzerSettingsWrapper
        {
          AnalyzerSettings = new AnalyzerSettings { MarketAnalyzer = new Core.Main.DataObjects.PTMagicData.MarketAnalyzer
            { StoreDataMaxHours = 48, MarketTrends = new() }, GlobalSettings = new(), SingleMarketSettings = new() }
        }));
        var settings = new PTMagicConfiguration(directory);
        Require(settings.GeneralSettings.Application.HistoryRetentionDays == 42);
        settings.WriteGeneralSettings();
        // The test supplies a directory without a trailing slash. Both the
        // backup and the updated file must remain inside that directory.
        Require(File.Exists(Path.Combine(directory, "settings.general.json.backup")));
        Require(!File.Exists(directory + "settings.general.json"));
        string migrated = File.ReadAllText(Path.Combine(directory, "settings.general.json"));
        Require(migrated.Contains("\"HistoryRetentionDays\": 42") &&
          !migrated.Contains("DecisionHistoryRetentionDays") && !migrated.Contains("DecisionHistoryMaxMegabytes"));
        settings.WriteAnalyzerSettings();
        Require(File.Exists(Path.Combine(directory, "settings.analyzer.json.backup")));
        Require(!File.Exists(directory + "settings.analyzer.json"));
      });
      Run("combined historical disk-growth estimator uses both datasets", directory =>
      {
        string market = Path.Combine(directory, "_data", "Exchange");
        string history = Path.Combine(directory, "_data", "DecisionHistory");
        Directory.CreateDirectory(market); Directory.CreateDirectory(history);
        string marketFile = Path.Combine(market, "MarketData_recent.json");
        string historyFile = Path.Combine(history, "2026-11-24-000001.jsonl");
        string oldFile = Path.Combine(market, "MarketData_old.json");
        File.WriteAllText(marketFile, new string('m', 2048));
        File.WriteAllText(historyFile, new string('d', 6144));
        string compactFile = Path.Combine(history, DecisionHistoryFileNames.CompactName(DateTimeOffset.UtcNow.AddHours(-2), Guid.NewGuid()));
        File.WriteAllText(compactFile, new string('c', 1024));
        File.WriteAllText(oldFile, new string('o', 512));
        DateTime clock = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(marketFile, clock.AddHours(-24));
        File.SetLastWriteTimeUtc(historyFile, clock.AddHours(-12));
        File.SetLastWriteTimeUtc(compactFile, clock.AddHours(-2));
        File.SetLastWriteTimeUtc(oldFile, clock.AddDays(-5));
        var estimate = HistoryStorageEstimator.Measure(directory, clock);
        Require(estimate.ExistingBytes == 9728 && estimate.SampleFileCount == 3);
        Require(estimate.ObservedHours >= 23.9 && estimate.ObservedHours <= 48);
        Require(estimate.ObservedDailyBytes > 0 && estimate.AvailableBytes >= 0);
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
          var record = Read(Directory.GetFiles(Path.Combine(directory, "_data", "DecisionHistory"), "*.jsonl.gz").Single()).Single();
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
          string path = Directory.GetFiles(Path.Combine(directory, "_data", "DecisionHistory"), "*.jsonl.gz").Single();
          string json = ReadRaw(path); var record = Read(path).Single();
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
  private static DecisionHistoryRecord[] Read(string path) => DecisionHistoryCodec.ReadFile(path).ToArray();
  private static string ReadRaw(string path)
  {
    using var file = File.OpenRead(path);
    using var gzip = new GZipStream(file, CompressionMode.Decompress);
    using var reader = new StreamReader(gzip, Encoding.UTF8);
    return reader.ReadToEnd();
  }
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
