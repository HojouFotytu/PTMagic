using System;
using System.Globalization;

namespace Monitor._Internal
{
  public sealed record MultipartExportSelection(string Kind, string HistoryRange,
    DateTime? FromUtc, DateTime ThroughUtc, long MaxBytes)
  {
    public string FileTag => HistoryRange + "-" +
      (MaxBytes / 1048576L).ToString(CultureInfo.InvariantCulture) + "MiB";

    public static bool TryCreate(string kind, int? historyCount, string historyUnit,
      int? maxZipMiB, DateTime endUtc, out MultipartExportSelection selection)
    {
      selection = null;
      if ((kind != "data" && kind != "logs") || !historyCount.HasValue ||
        !maxZipMiB.HasValue || maxZipMiB.Value <= 0 ||
        !MultipartZipBatch.TryLimit(maxZipMiB.Value, out long maxBytes) ||
        !ExportHistoryPeriod.TryFromInput(historyCount.Value, historyUnit, endUtc,
          out string range, out DateTime? fromUtc))
        return false;
      selection = new MultipartExportSelection(kind, range, fromUtc, endUtc, maxBytes);
      return true;
    }

    public static bool TryFromCode(string kind, string range, int maxZipMiB, DateTime endUtc,
      out MultipartExportSelection selection)
    {
      selection = null;
      if ((kind != "data" && kind != "logs") || maxZipMiB <= 0 ||
        !MultipartZipBatch.TryLimit(maxZipMiB, out long maxBytes) ||
        !ExportHistoryPeriod.TryFromCode(range, endUtc, out DateTime? fromUtc))
        return false;
      selection = new MultipartExportSelection(kind, range, fromUtc, endUtc, maxBytes);
      return true;
    }
  }
}
