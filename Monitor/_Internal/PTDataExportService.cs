using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Core.Main;

namespace Monitor._Internal
{
  // This service reads only the explicitly permitted ProfitTrailer datasets.
  // It never writes PT settings, submits trades, or contacts an external AI service.
  public sealed class PTDataExportSelection
  {
    // Date ranges apply only to trade histories, not positions or funding.
    public string HistoryRange { get; set; } = "all";
    public bool ValidHistoryRange => HistoryRange == "all" || HistoryRange == "7d"
      || HistoryRange == "1m" || HistoryRange == "3m" || HistoryRange == "6m";

    public DateTime? HistoryStartUtc(DateTime exportStartedUtc)
    {
      switch (HistoryRange)
      {
        case "7d": return exportStartedUtc.AddDays(-7);
        case "1m": return exportStartedUtc.AddMonths(-1);
        case "3m": return exportStartedUtc.AddMonths(-3);
        case "6m": return exportStartedUtc.AddMonths(-6);
        default: return null;
      }
    }
    public bool IncludeBuys { get; set; }
    public bool IncludeSales { get; set; }
    public bool IncludeCurrentPositions { get; set; }
    public bool IncludeFunding { get; set; }

    public bool AnySelected => IncludeBuys || IncludeSales || IncludeCurrentPositions || IncludeFunding;
  }

  public static class PTDataExportService
  {
    private const int MaxResponseBytes = 32 * 1024 * 1024;
    // PT's documented perPage default is 25; larger sizes are not yet confirmed.
    private const int HistoryPageSize = 25;
    private const int MaxHistoryPages = 10000;
    private const long MaxTotalSourceBytes = 128L * 1024 * 1024;
    private static readonly SemaphoreSlim ExportGate = new SemaphoreSlim(1, 1);
    private static readonly HttpClient Client = CreateClient();

    private sealed class Source
    {
      public string Name { get; }
      public string Group { get; }
      public string Endpoint { get; }
      public string ZipPath { get; }
      public bool IsTradeHistory { get; }

      public Source(string name, string group, string endpoint, string zipPath, bool isTradeHistory)
      {
        Name = name;
        Group = group;
        Endpoint = endpoint;
        ZipPath = zipPath;
        IsTradeHistory = isTradeHistory;
      }
    }

    private static readonly Source[] Sources =
    {
      new Source("buys", "Buy History", "api/v2/data/buys", "Trades/buys.json", true),
      new Source("sales", "Sell History", "api/v2/data/sales", "Trades/sales.json", true),
      new Source("watchmode", "Current Positions", "api/v2/data/watchmode", "CurrentPositions/watchmode.json", false),
      new Source("pending", "Current Positions", "api/v2/data/pending", "CurrentPositions/pending.json", false),
      new Source("pairs", "Current Positions", "api/v2/data/pairs", "CurrentPositions/pairs.json", false),
      new Source("dca", "Current Positions", "api/v2/data/dca", "CurrentPositions/dca.json", false),
      new Source("funding", "Funding Updates", "api/v2/data/funding", "Funding/funding-updates.json", false)
    };

    private static HttpClient CreateClient()
    {
      var handler = new HttpClientHandler
      {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
      };
      return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
    }

    private static bool Selected(Source source, PTDataExportSelection selection)
    {
      switch (source.Name)
      {
        case "buys": return selection.IncludeBuys;
        case "sales": return selection.IncludeSales;
        case "funding": return selection.IncludeFunding;
        default: return selection.IncludeCurrentPositions;
      }
    }

    private static Uri BuildUri(PTMagicConfiguration configuration, string endpoint, string pagination = null)
    {
      var application = configuration.GeneralSettings.Application;
      string baseAddress = application.ProfitTrailerMonitorURL;
      string token = application.ProfitTrailerServerAPIToken;
      if (String.IsNullOrWhiteSpace(token) || !Uri.TryCreate(baseAddress, UriKind.Absolute, out Uri baseUri)
        || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
        || !String.IsNullOrEmpty(baseUri.UserInfo) || !String.IsNullOrEmpty(baseUri.Query)
        || !String.IsNullOrEmpty(baseUri.Fragment))
        throw new InvalidOperationException("PT connection configuration is unavailable.");

      // Preserve the configured PT instance path prefix, as the existing Monitor client does.
      var builder = new UriBuilder(baseUri)
      {
        Path = baseUri.AbsolutePath.TrimEnd('/') + "/" + endpoint,
        Query = "token=" + Uri.EscapeDataString(token)
          + (String.IsNullOrEmpty(pagination) ? "" : "&" + pagination),
        Fragment = ""
      };
      return builder.Uri;
    }

    private static async Task<byte[]> ReadResponseAsync(Uri url, CancellationToken cancellationToken)
    {
      for (int attempt = 0; attempt < 2; attempt++)
      {
        using (var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
          .ConfigureAwait(false))
        {
          if (attempt == 0 && (response.StatusCode == HttpStatusCode.TooManyRequests
            || response.StatusCode == HttpStatusCode.ServiceUnavailable))
          {
            await Task.Delay(750, cancellationToken).ConfigureAwait(false);
            continue;
          }

          if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("PT returned an HTTP failure.", null, response.StatusCode);
          if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new InvalidDataException("PT response exceeded the per-dataset size limit.");

          using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
          using (var buffer = new MemoryStream())
          {
            var bytes = new byte[65536];
            int read;
            while ((read = await input.ReadAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
              if (buffer.Length + read > MaxResponseBytes)
                throw new InvalidDataException("PT response exceeded the per-dataset size limit.");
              buffer.Write(bytes, 0, read);
            }
            return buffer.ToArray();
          }
        }
      }
      throw new HttpRequestException("PT data request could not complete.");
    }

    private static string FailureCode(Exception error)
    {
      if (error is HttpRequestException request)
        return request.StatusCode.HasValue ? "http_" + ((int)request.StatusCode.Value).ToString() : "connection_error";
      if (error is OperationCanceledException) return "timeout_or_cancelled";
      if (error is JsonException) return "invalid_json";
      if (error is InvalidDataException) return "invalid_or_oversized_data";
      if (error is InvalidOperationException) return "configuration_unavailable";
      return "collection_error";
    }


    // All trade records stay intact, including their historical BUYNOTE/SELLNOTE extraInfo CV values.
    private sealed class HistoryResult
    {
      public byte[] Output;
      public long ReceivedBytes;
      public int ExaminedRecords;
      public int IncludedRecords;
      public int PagesRetrieved;
      public int DuplicateRecords;
      public bool ReachedCutoff;
      public bool ReachedEnd;
      public bool DescendingOrderVerified = true;
      public string DateField;
      public string SortField;
    }

    private static long TimestampSeconds(JsonElement record, string field)
    {
      if (!record.TryGetProperty(field, out JsonElement value)
        || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long seconds)
        || seconds <= 0 || seconds > 253402300799L)
        throw new InvalidDataException("PT history record has an invalid " + field + " timestamp.");
      return seconds; // PT history timestamps are Unix seconds.
    }

    private static string HistoryIdentity(JsonElement record)
    {
      if (record.TryGetProperty("id", out JsonElement value)
        && (value.ValueKind == JsonValueKind.String || value.ValueKind == JsonValueKind.Number))
      {
        string id = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        if (!String.IsNullOrWhiteSpace(id) && id != "0") return "id:" + id;
      }
      return "raw:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record.GetRawText())));
    }

    private static async Task<HistoryResult> ReadHistoryAsync(
      PTMagicConfiguration configuration, Source source, DateTime? cutoffUtc, DateTime endUtc,
      long remainingSourceBytes, CancellationToken cancellationToken)
    {
      var result = new HistoryResult
      {
        DateField = source.Name == "buys" ? "lastBoughtDate" : "soldDate",
        SortField = source.Name == "buys" ? "BOUGHTDATE" : "SOLDDATE"
      };
      long? previousTimestamp = null;
      long cutoffSeconds = cutoffUtc.HasValue ? new DateTimeOffset(cutoffUtc.Value).ToUnixTimeSeconds() : 0;
      long endSeconds = new DateTimeOffset(endUtc).ToUnixTimeSeconds();
      var records = new List<JsonElement>();
      var seen = new Dictionary<string, string>(StringComparer.Ordinal);
      var pageHashcodes = new List<JsonElement>();

      for (int page = 0; page < MaxHistoryPages; page++)
      {
        cancellationToken.ThrowIfCancellationRequested();
        string pagination = "page=" + page.ToString(CultureInfo.InvariantCulture)
          + "&perPage=" + HistoryPageSize.ToString(CultureInfo.InvariantCulture)
          + "&sort=" + result.SortField + "&sortDirection=DESCENDING";
        byte[] response = await ReadResponseAsync(
          BuildUri(configuration, source.Endpoint, pagination), cancellationToken).ConfigureAwait(false);
        result.ReceivedBytes += response.Length;
        if (result.ReceivedBytes > remainingSourceBytes)
          throw new InvalidDataException("PT Data total source size limit was exceeded.");

        using (JsonDocument document = JsonDocument.Parse(response))
        {
          JsonElement root = document.RootElement;
          if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("PT history response must contain a data array.");

          result.PagesRetrieved++;
          if (root.TryGetProperty("hashcode", out JsonElement hashcode))
            pageHashcodes.Add(hashcode.Clone());

          int pageCount = data.GetArrayLength();
          if (pageCount > HistoryPageSize)
            throw new InvalidDataException("PT ignored the requested history page size.");
          if (pageCount == 0)
          {
            result.ReachedEnd = true;
            break;
          }

          bool encounteredOlder = false;
          int uniqueOnPage = 0;
          foreach (JsonElement record in data.EnumerateArray())
          {
            if (record.ValueKind != JsonValueKind.Object)
              throw new InvalidDataException("PT history contains a non-object record.");

            result.ExaminedRecords++;
            string key = HistoryIdentity(record);
            string fingerprint = Convert.ToHexString(
              SHA256.HashData(Encoding.UTF8.GetBytes(record.GetRawText())));
            if (seen.TryGetValue(key, out string previousFingerprint))
            {
              if (!String.Equals(previousFingerprint, fingerprint, StringComparison.Ordinal))
                throw new InvalidDataException("PT history changed during pagination.");
              result.DuplicateRecords++;
              continue;
            }
            seen.Add(key, fingerprint);
            uniqueOnPage++;

            if (cutoffUtc.HasValue)
            {
              long timestamp = TimestampSeconds(record, result.DateField);
              if (previousTimestamp.HasValue && timestamp > previousTimestamp.Value)
                result.DescendingOrderVerified = false;
              previousTimestamp = timestamp;

              // BOUGHTDATE may sort by firstBoughtDate; only stop early when
              // it agrees with the per-order lastBoughtDate used for filtering.
              if (source.Name == "buys" && (!record.TryGetProperty("firstBoughtDate", out JsonElement first)
                || first.ValueKind != JsonValueKind.Number || !first.TryGetInt64(out long firstSeconds)
                || firstSeconds != timestamp))
                result.DescendingOrderVerified = false;

              if (timestamp < cutoffSeconds) encounteredOlder = true;
              else if (timestamp <= endSeconds) records.Add(record.Clone());
            }
            else records.Add(record.Clone());
          }

          if (uniqueOnPage == 0)
            throw new InvalidDataException("PT returned a repeated history page without progress.");

          // An out-of-order response falls back to scanning every page instead
          // of truncating at the first old transaction.
          if (cutoffUtc.HasValue && encounteredOlder && result.DescendingOrderVerified)
          {
            result.ReachedCutoff = true;
            break;
          }
        }
      }

      if (!result.ReachedEnd && !result.ReachedCutoff)
        throw new InvalidDataException("PT history pagination exceeded the safety limit.");

      using (var output = new MemoryStream())
      {
        using (var writer = new Utf8JsonWriter(output))
        {
          writer.WriteStartObject();
          writer.WritePropertyName("data");
          writer.WriteStartArray();
          foreach (JsonElement record in records) record.WriteTo(writer);
          writer.WriteEndArray();
          // Individual PT page hashes are not an aggregate hashcode.
          writer.WritePropertyName("sourcePageHashcodes");
          writer.WriteStartArray();
          foreach (JsonElement hashcode in pageHashcodes) hashcode.WriteTo(writer);
          writer.WriteEndArray();
          writer.WriteEndObject();
        }
        result.Output = output.ToArray();
      }
      result.IncludedRecords = records.Count;
      return result;
    }

    public static async Task<Stream> GenerateAsync(PTMagicConfiguration configuration,
      PTDataExportSelection selection, CancellationToken requestAborted)
    {
      if (configuration == null) throw new ArgumentNullException(nameof(configuration));
      if (selection == null || !selection.AnySelected || !selection.ValidHistoryRange)
        throw new ArgumentException("Select data to export and a valid history amount.");

      if (!await ExportGate.WaitAsync(0, requestAborted).ConfigureAwait(false))
        throw new InvalidOperationException("Another PT Data export is currently running.");

      try
      {
        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(requestAborted))
        {
          budget.CancelAfter(TimeSpan.FromSeconds(120));
          DateTime started = DateTime.UtcNow;
          var results = new List<Dictionary<string, object>>();
          long collectedBytes = 0;
          var fileName = Path.Combine(Path.GetTempPath(), "ptm-data-" + Guid.NewGuid().ToString("N") + ".zip");
          var stream = new FileStream(fileName, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
          try
          {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
              foreach (Source source in Sources)
              {
                bool requested = Selected(source, selection);
                var item = new Dictionary<string, object>
                {
                  ["name"] = source.Name,
                  ["group"] = source.Group,
                  ["source"] = "ProfitTrailer",
                  ["status"] = requested ? "pending" : "skipped",
                  ["zip_path"] = null
                };
                results.Add(item);
                if (!requested) continue;
                if (budget.IsCancellationRequested)
                {
                  item["status"] = "failed";
                  item["error_code"] = "export_time_limit";
                  continue;
                }

                try
                {
                  HistoryResult history = null;
                  byte[] output;
                  long receivedBytes;
                  int? returnedCount;
                  int? includedCount;
                  if (source.IsTradeHistory)
                  {
                    history = await ReadHistoryAsync(configuration, source,
                      selection.HistoryStartUtc(started), started,
                      MaxTotalSourceBytes - collectedBytes, budget.Token).ConfigureAwait(false);
                    output = history.Output;
                    receivedBytes = history.ReceivedBytes;
                    returnedCount = history.ExaminedRecords;
                    includedCount = history.IncludedRecords;
                  }
                  else
                  {
                    byte[] received = await ReadResponseAsync(
                      BuildUri(configuration, source.Endpoint), budget.Token).ConfigureAwait(false);
                    using (JsonDocument document = JsonDocument.Parse(received))
                    {
                      JsonElement root = document.RootElement;
                      if (root.ValueKind != JsonValueKind.Array && root.ValueKind != JsonValueKind.Object)
                        throw new JsonException("Unexpected PT data shape.");
                      returnedCount = root.ValueKind == JsonValueKind.Array ? root.GetArrayLength() : (int?)null;
                    }
                    output = received;
                    receivedBytes = received.Length;
                    includedCount = returnedCount;
                  }

                  if (collectedBytes + receivedBytes > MaxTotalSourceBytes)
                    throw new InvalidDataException("PT Data total source size limit was exceeded.");

                  var entry = archive.CreateEntry(source.ZipPath, CompressionLevel.Optimal);
                  using (var entryStream = entry.Open())
                  {
                    await entryStream.WriteAsync(output, 0, output.Length, budget.Token)
                      .ConfigureAwait(false);
                  }

                  collectedBytes += receivedBytes;
                  item["status"] = history != null && history.DuplicateRecords > 0 ? "partial" : "available";
                  item["zip_path"] = source.ZipPath;
                  item["retrieved_at_utc"] = DateTime.UtcNow.ToString("O");
                  item["received_bytes"] = receivedBytes;
                  item["included_bytes"] = output.Length;
                  item["source_record_count"] = returnedCount;
                  item["included_record_count"] = includedCount;
                  item["raw_source_payload"] = history == null;
                  item["limit_applied"] = history != null && selection.HistoryRange != "all";
                  if (history != null)
                  {
                    item["history_range"] = selection.HistoryRange;
                    item["history_start_utc"] = selection.HistoryStartUtc(started)?.ToString("O");
                    item["history_end_utc"] = started.ToString("O");
                    item["pagination_page_size"] = HistoryPageSize;
                    item["pages_retrieved"] = history.PagesRetrieved;
                    item["sort"] = history.SortField;
                    item["sort_direction"] = "DESCENDING";
                    item["date_field"] = history.DateField;
                    item["descending_order_verified"] = history.DescendingOrderVerified;
                    item["reached_date_cutoff"] = history.ReachedCutoff;
                    item["reached_history_end"] = history.ReachedEnd;
                    item["duplicate_records"] = history.DuplicateRecords;
                    item["range_coverage_complete"] = history.DuplicateRecords == 0;
                    item["record_preservation"] = "original_record_fields_and_values";
                  }
                }
                catch (Exception error) when (!requestAborted.IsCancellationRequested
                  && (error is HttpRequestException || error is InvalidDataException
                    || error is JsonException || error is OperationCanceledException
                    || error is InvalidOperationException || error is IOException))
                {
                  // Never include exception text, connection URLs or tokens in the ZIP.
                  item["status"] = "failed";
                  item["error_code"] = FailureCode(error);
                }
              }

              int available = 0;
              int partial = 0;
              int failed = 0;
              foreach (var item in results)
              {
                if ((string)item["status"] == "available") available++;
                else if ((string)item["status"] == "partial") partial++;
                else if ((string)item["status"] == "failed") failed++;
              }

              var manifest = new
              {
                schema_version = "1.1",
                export_id = Guid.NewGuid().ToString("N"),
                export_started_utc = started.ToString("O"),
                export_finished_utc = DateTime.UtcNow.ToString("O"),
                bot_scope = "configured_PT_target_of_this_Monitor",
                bot_instance_name = configuration.GeneralSettings.Application.InstanceName,
                history_range = selection.HistoryRange,
                history_start_utc = selection.HistoryStartUtc(started)?.ToString("O"),
                history_end_utc = started.ToString("O"),
                history_pagination_page_size = HistoryPageSize,
                lifetime_history_complete = false,
                history_note = "Buys/sales are sorted newest-first and paged through the requested UTC period. PT retention may limit available history. The aggregate data array preserves every PT trade field; sourcePageHashcodes records original page hashes. Pagination can shift while PT is trading.",
                positions_note = "Current Positions are point-in-time snapshots, not historical positions.",
                funding_note = "PT funding updates may be missing while PT was offline; do not treat them as complete settled funding cashflows.",
                dataset_count_available = available,
                dataset_count_partial = partial,
                dataset_count_failed = failed,
                overall_status = failed > 0 || partial > 0
                  ? (available + partial > 0 ? "partial" : "failed") : "complete_for_selected_sources",
                datasets = results
              };

              var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
              using (var manifestStream = manifestEntry.Open())
              {
                byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest,
                  new JsonSerializerOptions { WriteIndented = true }));
                await manifestStream.WriteAsync(json, 0, json.Length, requestAborted).ConfigureAwait(false);
              }
            }

            stream.Position = 0;
            return stream; // The FileStreamResult disposes it; DeleteOnClose removes the private temporary ZIP.
          }
          catch
          {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
          }
        }
      }
      finally
      {
        ExportGate.Release();
      }
    }
  }
}
