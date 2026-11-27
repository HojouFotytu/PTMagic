using System;
using System.Globalization;
using System.IO;

namespace Core.Helper
{
  // New names encode the exact immutable record time; old names identify
  // UTC daily segments. Only recognized history files are subject to pruning.
  public static class DecisionHistoryFileNames
  {
    private const string StampFormat = "yyyy-MM-dd-HHmmss.fffffff'Z'";
    private const string CompactSuffix = ".jsonl.gz";

    public static string CompactName(DateTimeOffset recordedAtUtc, Guid id) =>
      recordedAtUtc.UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture) +
      "-" + id.ToString("N") + CompactSuffix;

    public static bool TryCompactTimestamp(string path, out DateTime utc)
    {
      utc = default;
      string name = Path.GetFileName(path);
      return name.Length == 68 && name[26] == '-' &&
        name.EndsWith(CompactSuffix, StringComparison.OrdinalIgnoreCase) &&
        Guid.TryParseExact(name.Substring(27, 32), "N", out _) &&
        DateTime.TryParseExact(name.Substring(0, 26), StampFormat, CultureInfo.InvariantCulture,
          DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);
    }

    public static bool TryLegacyDate(string path, out DateTime utc)
    {
      utc = default;
      string name = Path.GetFileName(path);
      return name.Length == 23 && name[10] == '-' &&
        name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) &&
        Int32.TryParse(name.Substring(11, 6), NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
        DateTime.TryParseExact(name.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture,
          DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);
    }

    public static bool IsHistoryFile(string path) =>
      TryCompactTimestamp(path, out _) || TryLegacyDate(path, out _);
  }
}
