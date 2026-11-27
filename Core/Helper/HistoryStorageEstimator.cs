using System;
using System.IO;

namespace Core.Helper
{
  /// <summary>
  /// Disk-only estimate for the two configured historical datasets. Never
  /// opens/reads raw decision records or scans the normalized OHLCV cache.
  /// This is an estimate, not a disk reservation or retention guarantee.
  /// </summary>
  public sealed class HistoryStorageEstimate
  {
    public double ObservedDailyBytes { get; init; }
    public long ExistingBytes { get; init; }
    public long AvailableBytes { get; init; } = -1;
    public double ObservedHours { get; init; }
    public int SampleFileCount { get; init; }
    public bool HasGrowthEstimate => SampleFileCount > 0 && ObservedHours >= 1;
  }

  public static class HistoryStorageEstimator
  {
    private const double SampleHours = 48;

    public static HistoryStorageEstimate Measure(string basePath, DateTime? utcNow = null)
    {
      if (String.IsNullOrWhiteSpace(basePath))
        throw new ArgumentException("An instance directory is required.", nameof(basePath));
      string root = Path.GetFullPath(basePath);
      DateTime now = (utcNow ?? DateTime.UtcNow).ToUniversalTime();
      DateTime start = now.AddHours(-SampleHours);
      DateTime earliest = now;
      long total = 0, recent = 0;
      int recentFiles = 0;

      // Enumerate metadata only. No JSONL parsing, no loaded history, no
      // market-data acquisition, and no work per character typed in Monitor.
      void Count(string folder, string pattern)
      {
        if (!Directory.Exists(folder)) return;
        foreach (string path in Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly))
        {
          try
          {
            var file = new FileInfo(path);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            if (pattern == "*.jsonl.gz" && !DecisionHistoryFileNames.TryCompactTimestamp(path, out _)) continue;
            long length = file.Length;
            DateTime modified = file.LastWriteTimeUtc;
            if (length < 0 || modified > now.AddMinutes(5)) continue;
            total = total > long.MaxValue - length ? long.MaxValue : total + length;
            if (modified < start) continue;
            recent = recent > long.MaxValue - length ? long.MaxValue : recent + length;
            recentFiles++;
            DateTime born = file.CreationTimeUtc;
            DateTime first = born <= now && born.Year > 2000 ? born : modified;
            if (modified < first) first = modified;
            if (first < earliest) earliest = first;
          }
          catch (IOException) { /* A concurrent cleanup or rotation removed this file. */ }
          catch (UnauthorizedAccessException) { /* Inaccessible file is not sampled. */ }
        }
      }

      Count(Path.Combine(root, "_data", "Exchange"), "MarketData_*.json");
      Count(Path.Combine(root, "_data", "DecisionHistory"), "????-??-??-??????.jsonl");
      Count(Path.Combine(root, "_data", "DecisionHistory"), "*.jsonl.gz");
      double hours = recentFiles == 0 ? 0 :
        Math.Clamp((now - (earliest < start ? start : earliest)).TotalHours, 0, SampleHours);
      long available = -1;
      try
      {
        string volumeRoot = Path.GetPathRoot(root);
        if (!String.IsNullOrWhiteSpace(volumeRoot))
          available = new DriveInfo(volumeRoot).AvailableFreeSpace;
      }
      catch (IOException) { }
      catch (UnauthorizedAccessException) { }
      catch (ArgumentException) { }
      return new HistoryStorageEstimate
      {
        ObservedDailyBytes = recentFiles > 0 && hours >= 1 ? recent * 24.0 / hours : 0,
        ExistingBytes = total, AvailableBytes = available,
        ObservedHours = hours, SampleFileCount = recentFiles
      };
    }
  }
}
