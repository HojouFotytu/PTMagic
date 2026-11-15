using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Core.Main;

namespace Monitor._Internal
{
  public sealed class PTDataExportSelection
  {
    public string HistoryRange { get; set; } = "all";
    public bool ValidHistoryRange => HistoryRange == "all" || HistoryRange == "7d"
      || HistoryRange == "1m" || HistoryRange == "3m" || HistoryRange == "6m";
    public DateTime? HistoryStartUtc(DateTime end) => HistoryRange switch
    {
      "7d" => end.AddDays(-7), "1m" => end.AddMonths(-1),
      "3m" => end.AddMonths(-3), "6m" => end.AddMonths(-6), _ => null
    };
    public bool IncludeBuys { get; set; }
    public bool IncludeSales { get; set; }
    public bool IncludeCurrentPositions { get; set; }
    public bool IncludeFunding { get; set; }
    public bool AnySelected => IncludeBuys || IncludeSales || IncludeCurrentPositions || IncludeFunding;
  }

  // Read-only exporter. Each HTTP response is bounded; complete histories live on disk, not in RAM.
  public static class PTDataExportService
  {
    private const int InitialPageSize = 10;
    private const int ResponseLimit = 32 * 1024 * 1024;
    private const int PageLimit = 10000;
    private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
    private static readonly HttpClient Client = new HttpClient(new HttpClientHandler
    {
      AllowAutoRedirect = false,
      AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    }) { Timeout = TimeSpan.FromSeconds(25) };

    private sealed record Source(string Name, string Group, string Path, bool History = false);
    private static readonly Source[] Sources =
    {
      new Source("buys", "Buy History", "Trades/buys.json", true),
      new Source("sales", "Sell History", "Trades/sales.json", true),
      new Source("watchmode", "Current Positions", "CurrentPositions/watchmode.json"),
      new Source("pending", "Current Positions", "CurrentPositions/pending.json"),
      new Source("pairs", "Current Positions", "CurrentPositions/pairs.json"),
      new Source("dca", "Current Positions", "CurrentPositions/dca.json"),
      new Source("funding", "Funding Updates", "Funding/funding-updates.json")
    };

    private sealed class ExportError : Exception
    {
      public string Code { get; }
      public ExportError(string code) : base(code) { Code = code; }
    }

    private static bool Selected(Source source, PTDataExportSelection selection) => source.Name switch
    {
      "buys" => selection.IncludeBuys, "sales" => selection.IncludeSales,
      "funding" => selection.IncludeFunding, _ => selection.IncludeCurrentPositions
    };

    private static FileStream TemporaryFile() => new FileStream(
      System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ptm-export-" + Guid.NewGuid().ToString("N")),
      FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536,
      FileOptions.Asynchronous | FileOptions.DeleteOnClose);

    private static Uri Endpoint(PTMagicConfiguration configuration, string name, string query = "")
    {
      var application = configuration.GeneralSettings.Application;
      if (String.IsNullOrWhiteSpace(application.ProfitTrailerServerAPIToken)
        || !Uri.TryCreate(application.ProfitTrailerMonitorURL, UriKind.Absolute, out Uri address)
        || (address.Scheme != "http" && address.Scheme != "https")
        || address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0)
        throw new InvalidOperationException("PT connection configuration is unavailable.");
      return new UriBuilder(address)
      {
        Path = address.AbsolutePath.TrimEnd('/') + "/api/v2/data/" + name,
        Query = "token=" + Uri.EscapeDataString(application.ProfitTrailerServerAPIToken) + query
      }.Uri;
    }

    private static async Task<byte[]> ReadAsync(HttpClient client, Uri uri, int limit, CancellationToken token)
    {
      for (int attempt = 0; ; attempt++)
      {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (attempt < 2 && (response.StatusCode == HttpStatusCode.TooManyRequests
          || response.StatusCode == HttpStatusCode.ServiceUnavailable))
        {
          await Task.Delay(750 * (attempt + 1), token).ConfigureAwait(false);
          continue;
        }
        if (!response.IsSuccessStatusCode)
          throw new HttpRequestException("PT request failed.", null, response.StatusCode);
        if (response.Content.Headers.ContentLength > limit) throw new ExportError("response_size_limit");
        using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
          if (output.Length + count > limit) throw new ExportError("response_size_limit");
          output.Write(buffer, 0, count);
        }
        return output.ToArray();
      }
    }

    // Accept a JSON array and the data/content wrappers used by paged JSON APIs.
    // Do not guess record fields or silently turn an unknown response into an empty history.
    private static JsonElement Records(JsonElement root)
    {
      for (int depth = 0; depth < 4; depth++)
      {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind != JsonValueKind.Object) break;
        if (root.TryGetProperty("data", out JsonElement data)) root = data;
        else if (root.TryGetProperty("content", out JsonElement content)) root = content;
        else break;
      }
      throw new ExportError("history_response_shape");
    }

    private static long? Timestamp(JsonElement record, string field)
    {
      if (record.ValueKind != JsonValueKind.Object || !record.TryGetProperty(field, out JsonElement value)) return null;
      string text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
      if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number))
      {
        if (number >= 1000000000000m) number /= 1000m;
        return number > 0 && number <= 253402300799m ? (long)number : null;
      }
      if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset date))
        return date.ToUnixTimeSeconds();
      return null;
    }

    private static string Fingerprint(JsonElement element) =>
      Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(element.GetRawText())));

    private static string ErrorCode(Exception error) => error switch
    {
      ExportError known => known.Code,
      HttpRequestException http => http.StatusCode.HasValue ? "http_" + (int)http.StatusCode.Value : "connection_error",
      JsonException => "invalid_json",
      OperationCanceledException => "request_or_export_timeout",
      IOException => "io_error",
      _ => "collection_error"
    };

    private static async Task AddAsync(ZipArchive archive, string path, Stream input, CancellationToken token)
    {
      using var output = archive.CreateEntry(path, CompressionLevel.Optimal).Open();
      input.Position = 0;
      await input.CopyToAsync(output, token).ConfigureAwait(false);
    }

    private static async Task HistoryAsync(PTMagicConfiguration configuration, Source source,
      PTDataExportSelection selection, DateTime started, HttpClient client, ZipArchive archive,
      Dictionary<string, object> item, int limit, CancellationToken budget, CancellationToken request)
    {
      using var aggregate = TemporaryFile();
      using var writer = new Utf8JsonWriter(aggregate);
      writer.WriteStartObject();
      writer.WritePropertyName("data");
      writer.WriteStartArray();
      var seenRecords = new HashSet<string>(StringComparer.Ordinal);
      var seenPages = new HashSet<string>(StringComparer.Ordinal);
      var scanRecords = new HashSet<string>(StringComparer.Ordinal);
      var hashes = new List<JsonElement>();
      string dateField = source.Name == "buys" ? "lastBoughtDate" : "soldDate";
      string sort = source.Name == "buys" ? "BOUGHTDATE" : "SOLDDATE";
      DateTime? cutoff = selection.HistoryStartUtc(started);
      long startSeconds = cutoff.HasValue ? new DateTimeOffset(cutoff.Value).ToUnixTimeSeconds() : 0;
      long endSeconds = new DateTimeOffset(started).ToUnixTimeSeconds();
      int page = 0, size = InitialPageSize, scan = 0, pages = 0, acceptedPages = 0;
      int examined = 0, included = 0, unknownDates = 0, duplicates = 0;
      long received = 0;
      bool ended = false;
      string error = null;
      string prefix = "Trades/" + source.Name + "-pages/";
      item["raw_pages_prefix"] = prefix;

      while (pages < PageLimit)
      {
        try
        {
          budget.ThrowIfCancellationRequested();
          string query = $"&page={page}&perPage={size}&sort={sort}&sortDirection=DESCENDING";
          byte[] bytes;
          try { bytes = await ReadAsync(client, Endpoint(configuration, source.Name, query), limit, budget).ConfigureAwait(false); }
          catch (ExportError failure) when (failure.Code == "response_size_limit" && size > 1)
          {
            // Page numbers are offsets relative to page size: restart at zero to avoid gaps.
            // Exact-record deduplication retains already-downloaded rows without duplicating them.
            size = Math.Max(1, size / 2);
            page = 0;
            scan++;
            seenPages.Clear();
            scanRecords.Clear();
            continue;
          }
          received += bytes.Length;
          using var document = JsonDocument.Parse(bytes);
          // Retain every successful JSON response, even when its envelope is unexpected.
          using (var raw = new MemoryStream(bytes, writable: false))
            await AddAsync(archive, prefix + $"scan-{scan:D2}-page-{page:D5}.json", raw, request).ConfigureAwait(false);
          pages++;
          JsonElement data = Records(document.RootElement);
          int count = data.GetArrayLength();
          if (count > 0 && !seenPages.Add(Fingerprint(data))) throw new ExportError("history_page_repeated");
          // Validate the whole page before appending it to the combined JSON.
          foreach (JsonElement row in data.EnumerateArray())
            if (row.ValueKind != JsonValueKind.Object) throw new ExportError("history_record_shape");
          acceptedPages++;
          if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("hashcode", out JsonElement hash)) hashes.Add(hash.Clone());
          foreach (JsonElement row in data.EnumerateArray())
          {
            examined++;
            string fingerprint = Fingerprint(row);
            if (!scanRecords.Add(fingerprint)) duplicates++;
            if (!seenRecords.Add(fingerprint)) continue;
            // A reused id is not an error: distinct records are preserved in full.
            if (cutoff.HasValue)
            {
              long? timestamp = Timestamp(row, dateField);
              if (!timestamp.HasValue) unknownDates++;
              else if (timestamp.Value < startSeconds || timestamp.Value > endSeconds) continue;
            }
            row.WriteTo(writer);
            included++;
          }
          await writer.FlushAsync(request).ConfigureAwait(false);
          // A short page is the final page. Do not reject responses exceeding the requested count.
          if (count < size) { ended = true; break; }
          page++;
        }
        catch (Exception failure) when (!request.IsCancellationRequested
          && (failure is ExportError || failure is HttpRequestException || failure is JsonException
            || failure is OperationCanceledException || failure is IOException))
        {
          error = ErrorCode(failure);
          break;
        }
      }
      request.ThrowIfCancellationRequested();
      if (!ended && error == null) error = "history_page_limit";
      writer.WriteEndArray();
      writer.WritePropertyName("sourcePageHashcodes");
      writer.WriteStartArray();
      foreach (JsonElement hash in hashes) hash.WriteTo(writer);
      writer.WriteEndArray();
      writer.WriteEndObject();
      await writer.FlushAsync(request).ConfigureAwait(false);
      bool complete = ended && unknownDates == 0 && duplicates == 0;
      item["status"] = complete ? "available" : acceptedPages > 0 ? "partial" : "failed";
      if (acceptedPages > 0)
      {
        await AddAsync(archive, source.Path, aggregate, request).ConfigureAwait(false);
        item["zip_path"] = source.Path;
      }
      if (error != null) { item["error_code"] = error; item["failed_page"] = page; }
      item["received_bytes"] = received;
      item["included_bytes"] = aggregate.Length;
      item["source_record_count"] = examined;
      item["included_record_count"] = included;
      item["pages_retrieved"] = pages;
      item["pagination_page_size"] = size;
      item["page_size_reductions"] = scan;
      item["sort"] = sort;
      item["sort_direction"] = "DESCENDING";
      item["date_field"] = dateField;
      item["history_range"] = selection.HistoryRange;
      item["history_start_utc"] = cutoff?.ToString("O");
      item["history_end_utc"] = started.ToString("O");
      item["reached_history_end"] = ended;
      item["range_coverage_complete"] = complete;
      item["duplicate_records"] = duplicates;
      item["unfilterable_records_preserved"] = unknownDates;
      item["raw_source_payload"] = false;
      item["limit_applied"] = cutoff.HasValue;
      item["record_preservation"] = "original_record_fields_and_values";
    }

    public static Task<Stream> GenerateAsync(PTMagicConfiguration configuration,
      PTDataExportSelection selection, CancellationToken requestAborted) =>
      GenerateAsync(configuration, selection, Client, requestAborted);

    // The injectable client runs the actual HTTP -> JSON -> ZIP path in focused regression tests.
    internal static async Task<Stream> GenerateAsync(PTMagicConfiguration configuration,
      PTDataExportSelection selection, HttpClient client, CancellationToken requestAborted, int responseLimit = ResponseLimit)
    {
      if (configuration == null) throw new ArgumentNullException(nameof(configuration));
      if (selection == null || !selection.ValidHistoryRange || !selection.AnySelected)
        throw new ArgumentException("Select data to export and a valid history range.");
      _ = Endpoint(configuration, "buys");
      if (!await Gate.WaitAsync(0, requestAborted).ConfigureAwait(false))
        throw new InvalidOperationException("Another PT Data export is currently running.");
      try
      {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        budget.CancelAfter(TimeSpan.FromSeconds(120));
        DateTime started = DateTime.UtcNow;
        var results = new List<Dictionary<string, object>>();
        var stream = TemporaryFile();
        try
        {
          using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
          {
            foreach (Source source in Sources)
            {
              var item = new Dictionary<string, object>
              {
                ["name"] = source.Name, ["group"] = source.Group, ["source"] = "ProfitTrailer",
                ["status"] = "skipped", ["zip_path"] = null
              };
              results.Add(item);
              if (!Selected(source, selection)) continue;
              try
              {
                if (source.History)
                  await HistoryAsync(configuration, source, selection, started, client, archive,
                    item, responseLimit, budget.Token, requestAborted).ConfigureAwait(false);
                else
                {
                  byte[] bytes = await ReadAsync(client, Endpoint(configuration, source.Name), responseLimit, budget.Token).ConfigureAwait(false);
                  using var document = JsonDocument.Parse(bytes);
                  JsonElement root = document.RootElement;
                  if (root.ValueKind != JsonValueKind.Array && root.ValueKind != JsonValueKind.Object)
                    throw new ExportError("snapshot_response_shape");
                  using (var raw = new MemoryStream(bytes, writable: false))
                    await AddAsync(archive, source.Path, raw, requestAborted).ConfigureAwait(false);
                  item["status"] = "available";
                  item["zip_path"] = source.Path;
                  item["received_bytes"] = bytes.Length;
                  item["included_bytes"] = bytes.Length;
                  item["source_record_count"] = root.ValueKind == JsonValueKind.Array ? (int?)root.GetArrayLength() : null;
                  item["included_record_count"] = item["source_record_count"];
                  item["raw_source_payload"] = true;
                  item["limit_applied"] = false;
                }
                item["retrieved_at_utc"] = DateTime.UtcNow.ToString("O");
              }
              catch (Exception failure) when (!requestAborted.IsCancellationRequested
                && (failure is ExportError || failure is HttpRequestException || failure is JsonException
                  || failure is OperationCanceledException || failure is IOException))
              {
                item["status"] = "failed";
                item["error_code"] = ErrorCode(failure);
              }
            }
            int available = 0, partial = 0, failed = 0;
            foreach (var item in results)
              switch ((string)item["status"])
              {
                case "available": available++; break;
                case "partial": partial++; break;
                case "failed": failed++; break;
              }
            var manifest = new
            {
              schema_version = "1.2", exporter_version = "paged-json-v2",
              export_id = Guid.NewGuid().ToString("N"),
              export_started_utc = started.ToString("O"), export_finished_utc = DateTime.UtcNow.ToString("O"),
              bot_scope = "configured_PT_target_of_this_Monitor",
              bot_instance_name = configuration.GeneralSettings.Application.InstanceName,
              history_range = selection.HistoryRange, history_start_utc = selection.HistoryStartUtc(started)?.ToString("O"),
              history_end_utc = started.ToString("O"), history_pagination_page_size = InitialPageSize,
              lifetime_history_complete = false,
              history_note = "Small newest-first API pages. Combined history is date-filtered locally; raw page files are unfiltered API responses. All fields are preserved. Undated records are retained and flagged. Live pagination can shift; PT retention may limit history.",
              positions_note = "Current Positions are point-in-time snapshots, not historical positions.",
              funding_note = "PT funding updates may be missing while PT was offline; they are not complete settled cashflows.",
              dataset_count_available = available, dataset_count_partial = partial, dataset_count_failed = failed,
              overall_status = failed + partial == 0 ? "complete_for_selected_sources" : available + partial > 0 ? "partial" : "failed",
              datasets = results
            };
            using var manifestStream = archive.CreateEntry("manifest.json", CompressionLevel.Optimal).Open();
            await JsonSerializer.SerializeAsync(manifestStream, manifest,
              new JsonSerializerOptions { WriteIndented = true }, requestAborted).ConfigureAwait(false);
          }
          stream.Position = 0;
          return stream;
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
      }
      finally { Gate.Release(); }
    }
  }
}
