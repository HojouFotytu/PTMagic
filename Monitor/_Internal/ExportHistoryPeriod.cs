using System;
using System.Globalization;
using System.IO;

namespace Monitor._Internal
{
  // Shared by the PT Data and PTMagic log ZIP downloads. Calendar months use
  // AddMonths rather than a fixed number of days.
  public static class ExportHistoryPeriod
  {
    public static bool TryFromInput(int count, string unit, DateTime endUtc,
      out string code, out DateTime? startUtc)
    {
      code = null;
      startUtc = null;
      if (unit == "all") { code = "all"; return true; }
      string suffix = unit switch
      {
        "days" => "d", "weeks" => "w", "months" => "m", _ => null
      };
      if (suffix == null || count < 1) return false;
      string value = count.ToString(CultureInfo.InvariantCulture) + suffix;
      if (!TryFromCode(value, endUtc, out startUtc)) return false;
      code = value;
      return true;
    }

    public static bool TryFromCode(string code, DateTime endUtc, out DateTime? startUtc)
    {
      startUtc = null;
      if (code == "all") return true;
      if (String.IsNullOrEmpty(code) || code.Length < 2) return false;
      char unit = code[code.Length - 1];
      if (unit != 'd' && unit != 'w' && unit != 'm') return false;
      if (!Int32.TryParse(code.Substring(0, code.Length - 1), NumberStyles.None,
        CultureInfo.InvariantCulture, out int count) || count < 1) return false;
      try
      {
        startUtc = unit switch
        {
          'd' => endUtc.AddDays(-count),
          'w' => endUtc.AddDays(-7.0 * count),
          _ => endUtc.AddMonths(-count)
        };
        return true;
      }
      catch (ArgumentOutOfRangeException) { return false; }
    }
  }

  // The NLog files are daily: yyyy-MM-dd.txt (and dated rotation variants).
  // Include the entire boundary-date file rather than truncate partial log
  // records; unknown filenames fall back to filesystem modification time.
  public static class LogArchiveDateFilter
  {
    public static bool Includes(string path, DateTime? startUtc, DateTime endUtc)
    {
      if (!startUtc.HasValue) return true;
      string fileName = Path.GetFileName(path);
      if (fileName.Length >= 11 && (fileName[10] == '.' || fileName[10] == '-' || fileName[10] == '_')
        && DateTime.TryParseExact(fileName.Substring(0, 10), "yyyy-MM-dd",
          CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime fileDate))
      {
        return fileDate >= startUtc.Value.ToLocalTime().Date
          && fileDate <= endUtc.ToLocalTime().Date;
      }
      try { return File.GetLastWriteTimeUtc(path) >= startUtc.Value; }
      catch (IOException) { return false; }
      catch (UnauthorizedAccessException) { return false; }
    }
  }
}
