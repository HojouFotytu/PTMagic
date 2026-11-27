using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
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
    // Set for Delta relation to audit both independent observation endpoints.
    public DateTimeOffset? PreviousSourceDataTimestampUtc { get; set; }
    public double? CoveragePercent { get; set; }
    public Core.MarketAnalyzer.MarketData.VolumeCoverageDiagnostics VolumeCoverage { get; set; }
    public Core.MarketAnalyzer.MarketData.VolumeCoverageDiagnostics PreviousVolumeCoverage { get; set; }
    public double? CurrentMatchedValue { get; set; }
    public double? PreviousMatchedValue { get; set; }
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
    public bool ProvisionalDecision { get; set; }
    public bool ConfirmedIncompleteData { get; set; }
    public bool StartupSellOnlyModeActive { get; set; }
    public bool StartupSellOnlyModePending { get; set; }
    public string StartupSellOnlyModeStatus { get; set; }
    public bool TestMode { get; set; }
    public string DesiredGlobalSetting { get; set; }
    public string ConfirmedGlobalSetting { get; set; }
    public string[] DesiredSingleMarketSettings { get; set; } = Array.Empty<string>();
    public string DeliveryStatus { get; set; }
    public string ErrorCategory { get; set; }
    public int? DesiredHeldMarketDefense { get; set; }
    public int? ConfirmedHeldMarketDefense { get; set; }
    public DecisionTrendUniverse[] Universes { get; set; } = Array.Empty<DecisionTrendUniverse>();
    public DecisionMetricInput[] MetricInputs { get; set; } = Array.Empty<DecisionMetricInput>();

    internal DecisionHistoryRecord CompactHeader()
    {
      var header = (DecisionHistoryRecord)MemberwiseClone();
      header.SchemaVersion = DecisionHistoryCodec.CurrentSchemaVersion;
      header.MetricInputs = null;
      return header;
    }
  }

  public sealed class DecisionHistoryWriter
  {
    private readonly object _sync = new object();
    private readonly string _directory;
    private DateTimeOffset? _lastPruneUtc;
    private int _lastRetentionDays;
    public DecisionHistoryWriter(string directory) => _directory = Path.GetFullPath(directory);

    public string Append(DecisionHistoryRecord record, int retentionDays)
    {
      if (record == null) throw new ArgumentNullException(nameof(record));
      if (retentionDays < 1 || retentionDays > 3650) throw new ArgumentOutOfRangeException(nameof(retentionDays));

      lock (_sync)
      {
        Directory.CreateDirectory(_directory);
        DateTimeOffset now = record.RecordedAtUtc.ToUniversalTime();
        if (!_lastPruneUtc.HasValue || now < _lastPruneUtc.Value ||
          now - _lastPruneUtc.Value >= TimeSpan.FromHours(1) || _lastRetentionDays != retentionDays)
        {
          Prune(now.UtcDateTime.AddDays(-retentionDays));
          _lastPruneUtc = now;
          _lastRetentionDays = retentionDays;
        }

        // One immutable compressed record per raid. No record-size or quota
        // limit; atomic same-directory publication prevents interrupted files
        // from masquerading as complete history. Legacy segments stay intact.
        string path = Path.Combine(_directory, DecisionHistoryFileNames.CompactName(now, Guid.NewGuid()));
        string temporary = path + ".tmp";
        try
        {
          using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
          {
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true))
            using (var text = new StreamWriter(gzip, new UTF8Encoding(false), 65536, leaveOpen: true))
              DecisionHistoryCodec.WriteLine(text, record);
            file.Flush(flushToDisk: true);
          }
          File.Move(temporary, path);
          return path;
        }
        finally
        {
          if (File.Exists(temporary)) File.Delete(temporary);
        }
      }
    }

    private void Prune(DateTime cutoffUtc)
    {
      foreach (string path in Directory.EnumerateFiles(_directory, "*", SearchOption.TopDirectoryOnly))
      {
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
        if (DecisionHistoryFileNames.TryCompactTimestamp(path, out DateTime recorded))
        {
          if (recorded < cutoffUtc) info.Delete();
        }
        else if (DecisionHistoryFileNames.TryLegacyDate(path, out _) &&
          info.LastWriteTimeUtc < cutoffUtc)
          info.Delete();
      }
    }
  }
}
