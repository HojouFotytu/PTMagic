using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace Core.Helper
{
  public static class BuildIdentity
  {
    public static string SourceRevision => typeof(BuildIdentity).Assembly
      .GetCustomAttributes<AssemblyMetadataAttribute>()
      .FirstOrDefault(attribute => attribute.Key == "PTMSourceRevision")?.Value ?? "unknown";

    // Stable for all builds of the same Git commit, regardless of restart time.
    // Timestamp is the HEAD committer time, rendered as Korea Standard Time.
    public static string CommitBuildNumber
    {
      get
      {
        string seconds = typeof(BuildIdentity).Assembly
          .GetCustomAttributes<AssemblyMetadataAttribute>()
          .FirstOrDefault(attribute => attribute.Key == "PTMCommitUnixSeconds")?.Value;
        if (!long.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out long unixSeconds))
          return "unknown";
        try { return FormatCommitBuildNumber(unixSeconds); }
        catch (ArgumentOutOfRangeException) { return "unknown"; }
      }
    }

    public static string FormatCommitBuildNumber(long unixSeconds) =>
      DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
        .ToOffset(TimeSpan.FromHours(9))
        .ToString("MMddyyyyHHmm", CultureInfo.InvariantCulture);
  }

  public sealed class DecisionMetricInput
  {
    public string Scope { get; set; }
    public string SettingName { get; set; }
    public string TriggerId { get; set; }
    public string MarketTrend { get; set; }
    public string Metric { get; set; }
    public string Market { get; set; }
    public string Relation { get; set; }
    public double? Value { get; set; }
    public bool Available { get; set; }
    public string Reason { get; set; }
    public DateTimeOffset? SourceDataTimestampUtc { get; set; }
    public double? CoveragePercent { get; set; }
  }

  public sealed class DecisionTrendUniverse
  {
    public string Name { get; set; }
    public int TrendMinutes { get; set; }
    public DateTimeOffset AsOfUtc { get; set; }
    public DateTimeOffset ExpectedClosedCandleTimeUtc { get; set; }
    public string[] GlobalSymbols { get; set; } = Array.Empty<string>();
    public string[] AssetSymbols { get; set; } = Array.Empty<string>();
  }

  /// <summary>
  /// Deliberately excludes GeneralSettings, HTTP messages, PT property contents and
  /// free-form exceptions. Configuration/preset identity is represented by hashes.
  /// </summary>
  public sealed class DecisionHistoryRecord
  {
    public int SchemaVersion { get; set; } = 1;
    public string RaidId { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    public string ApplicationVersion { get; set; }
    public string SourceRevision { get; set; }
    public string Exchange { get; set; }
    public string ConfigurationHash { get; set; }
    public string DesiredPayloadHash { get; set; }
    public string ConfirmedPayloadHash { get; set; }
    public DateTimeOffset? AnalysisAsOfUtc { get; set; }
    public bool DecisionFresh { get; set; }
    public bool SelectionIndeterminate { get; set; }
    public bool StartupSellOnlyModeActive { get; set; }
    public bool StartupSellOnlyModePending { get; set; }
    public string StartupSellOnlyModeStatus { get; set; }
    public bool TestMode { get; set; }
    public string DesiredGlobalSetting { get; set; }
    public string ConfirmedGlobalSetting { get; set; }
    public string[] DesiredSingleMarketSettings { get; set; } = Array.Empty<string>();
    public string DeliveryStatus { get; set; }
    public string ErrorCategory { get; set; }
    public DecisionTrendUniverse[] Universes { get; set; } = Array.Empty<DecisionTrendUniverse>();
    public DecisionMetricInput[] MetricInputs { get; set; } = Array.Empty<DecisionMetricInput>();
  }

  public sealed class DecisionHistoryWriter
  {
    private const long SegmentBytes = 16L * 1024 * 1024;
    private readonly object _sync = new object();
    private readonly string _directory;
    public DecisionHistoryWriter(string directory) => _directory = Path.GetFullPath(directory);

    public string Append(DecisionHistoryRecord record, int retentionDays, int maxMegabytes)
    {
      if (record == null) throw new ArgumentNullException(nameof(record));
      if (retentionDays < 1 || retentionDays > 3650) throw new ArgumentOutOfRangeException(nameof(retentionDays));
      if (maxMegabytes < 16 || maxMegabytes > 102400) throw new ArgumentOutOfRangeException(nameof(maxMegabytes));
      byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(record, Formatting.None) + "\n");
      long quota = (long)maxMegabytes * 1024 * 1024;
      if (bytes.LongLength > SegmentBytes) throw new InvalidOperationException("Decision record exceeds the 16 MiB history segment limit.");

      lock (_sync)
      {
        Directory.CreateDirectory(_directory);
        DateTimeOffset now = record.RecordedAtUtc.ToUniversalTime();
        foreach (FileInfo old in Files().Where(file => file.LastWriteTimeUtc < now.UtcDateTime.AddDays(-retentionDays))) old.Delete();

        string day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        FileInfo last = Files().Where(file => file.Name.StartsWith(day + "-", StringComparison.Ordinal)).LastOrDefault();
        string path = last?.FullName;
        bool completeTail = last == null || EndsWithNewLine(last.FullName);
        if (last == null || last.Length + bytes.LongLength > SegmentBytes || !completeTail)
        {
          int sequence = last == null ? 1 : Int32.Parse(Path.GetFileNameWithoutExtension(last.Name).Substring(11), CultureInfo.InvariantCulture) + 1;
          path = Path.Combine(_directory, day + "-" + sequence.ToString("D6", CultureInfo.InvariantCulture) + ".jsonl");
        }

        // Only whole old segments are pruned. Never truncate a record or alter an
        // earlier valid segment; an interrupted tail starts a fresh segment.
        FileInfo[] files = Files();
        long total = files.Sum(file => file.Length);
        foreach (FileInfo file in files)
        {
          if (total + bytes.LongLength <= quota) break;
          if (file.FullName == path) continue;
          total -= file.Length;
          file.Delete();
        }
        if (total + bytes.LongLength > quota)
        {
          // A single active segment can consume a small configured quota. Rotate
          // before pruning it so the newest full record is always retained.
          FileInfo active = new FileInfo(path);
          int sequence = Int32.Parse(Path.GetFileNameWithoutExtension(active.Name).Substring(11), CultureInfo.InvariantCulture) + 1;
          string rotated = Path.Combine(_directory, day + "-" + sequence.ToString("D6", CultureInfo.InvariantCulture) + ".jsonl");
          if (active.Exists) active.Delete();
          path = rotated;
        }
        using (FileStream stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
          stream.Write(bytes, 0, bytes.Length);
          stream.Flush(true);
        }
        return path;
      }
    }

    private FileInfo[] Files() => new DirectoryInfo(_directory).GetFiles("????-??-??-??????.jsonl")
      .OrderBy(file => file.Name, StringComparer.Ordinal).ToArray();

    private static bool EndsWithNewLine(string path)
    {
      using FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
      if (file.Length == 0) return true;
      file.Seek(-1, SeekOrigin.End);
      return file.ReadByte() == (byte)'\n';
    }
  }
}
