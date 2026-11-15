using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
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

  // Rebuilt 2026-11-15. PT 2.6.1's history service uses perPage * (page - 1),
  // NOT page * perPage. See Tests/PTDataExport/CONTRACT.md for release evidence.
  // Pipeline: GET one page -> retain raw JSON -> append original records -> advance.
  public static class PTDataExportService
  {
    private const int DefaultResponseLimit = 32 * 1024 * 1024;
    private const long DefaultTransferLimit = 128L * 1024 * 1024;
    private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
    private static readonly HttpClient Client = new HttpClient(new HttpClientHandler
    {
      AllowAutoRedirect = false,
      AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    }) { Timeout = TimeSpan.FromSeconds(25) };

    private sealed record Source(string Name, string Group, string Path, string Sort = null, string DateField = null)
    {
      public bool History => Sort != null;
    }
    private static readonly Source[] Sources =
    {
      new Source("buys", "Buy History", "Trades/buys.json", "BOUGHTDATE", "lastBoughtDate"),
      new Source("sales", "Sell History", "Trades/sales.json", "SOLDDATE", "soldDate"),
      new Source("watchmode", "Current Positions", "CurrentPositions/watchmode.json"),
      new Source("pending", "Current Positions", "CurrentPositions/pending.json"),
      new Source("pairs", "Current Positions", "CurrentPositions/pairs.json"),
      new Source("dca", "Current Positions", "CurrentPositions/dca.json"),
      new Source("funding", "Funding Updates", "Funding/funding-updates.json")
    };
    private sealed class CollectionFailure : Exception
    {
      public CollectionFailure(string code) : base(code) { }
    }
    private sealed class TransferBudget
    {
      public long Received;
      public readonly long Limit;
      public TransferBudget(long limit) { Limit = limit; }
    }

    private static bool Selected(Source source, PTDataExportSelection selection) => source.Name switch
    {
      "buys" => selection.IncludeBuys, "sales" => selection.IncludeSales,
      "funding" => selection.IncludeFunding, _ => selection.IncludeCurrentPositions
    };
    private static FileStream TemporaryFile() => new FileStream(
      Path.Combine(Path.GetTempPath(), "ptm-export-" + Guid.NewGuid().ToString("N")),
      FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536,
      FileOptions.Asynchronous | FileOptions.DeleteOnClose);

    private static Uri Endpoint(PTMagicConfiguration configuration, string name, string query = "")
    {
      var app = configuration.GeneralSettings.Application;
      if (String.IsNullOrWhiteSpace(app.ProfitTrailerServerAPIToken)
        || !Uri.TryCreate(app.ProfitTrailerMonitorURL, UriKind.Absolute, out Uri address)
        || (address.Scheme != "http" && address.Scheme != "https")
        || address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0)
        throw new InvalidOperationException("PT connection configuration is unavailable.");
      return new UriBuilder(address)
      {
        Path = address.AbsolutePath.TrimEnd('/') + "/api/v2/data/" + name,
        Query = "token=" + Uri.EscapeDataString(app.ProfitTrailerServerAPIToken) + query
      }.Uri;
    }

    // Only fixed error codes escape this boundary. Never export exception text,
    // authenticated URLs, response headers, or non-success response bodies.
    private static async Task<byte[]> FetchAsync(HttpClient client, Uri address, int responseLimit,
      TransferBudget transfer, Dictionary<string, object> trace, CancellationToken cancellation)
    {
      for (int attempt = 1; ; attempt++)
      {
        cancellation.ThrowIfCancellationRequested();
        if (transfer.Received >= transfer.Limit) throw new CollectionFailure("export_transfer_limit");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        trace["attempts"] = attempt;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        trace["http_status"] = (int)response.StatusCode;
        if (attempt < 3 && (response.StatusCode == HttpStatusCode.TooManyRequests
          || response.StatusCode == HttpStatusCode.ServiceUnavailable))
        {
          await Task.Delay(750 * attempt, cancellation).ConfigureAwait(false);
          continue;
        }
        // PT's 204 is a hash/cache response, not an empty-history page.
        if (response.StatusCode == HttpStatusCode.NoContent) throw new CollectionFailure("http_204_no_payload");
        if (response.StatusCode != HttpStatusCode.OK) throw new CollectionFailure("http_" + (int)response.StatusCode);
        if (response.Content.Headers.ContentLength > responseLimit) throw new CollectionFailure("response_size_limit");
        using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int length;
        while ((length = await input.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) != 0)
        {
          transfer.Received += length;
          if (transfer.Received > transfer.Limit) throw new CollectionFailure("export_transfer_limit");
          if (output.Length + length > responseLimit) throw new CollectionFailure("response_size_limit");
          output.Write(buffer, 0, length);
        }
        trace["bytes"] = output.Length;
        return output.ToArray();
      }
    }

    private static bool Recoverable(Exception error) => error is CollectionFailure || error is HttpRequestException
      || error is JsonException || error is OperationCanceledException || error is IOException;
    private static string Code(Exception error) => error switch
    {
      CollectionFailure known => known.Message,
      HttpRequestException => "connection_error",
      JsonException => "invalid_json",
      OperationCanceledException => "request_or_export_timeout",
      IOException => "io_error",
      _ => "collection_error"
    };
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static JsonElement Rows(JsonElement value)
    {
      for (int depth = 0; depth < 4; depth++)
      {
        if (value.ValueKind == JsonValueKind.Array) return value;
        if (value.ValueKind != JsonValueKind.Object) break;
        if (value.TryGetProperty("data", out JsonElement data)) value = data;
        else if (value.TryGetProperty("content", out JsonElement content)) value = content;
        else break;
      }
      throw new CollectionFailure("history_response_shape");
    }
    private static string RecordKey(JsonElement row, string fingerprint)
    {
      // History id is PositionHistoryEntity.id, not the current-position display index.
      if (row.TryGetProperty("id", out JsonElement id)
        && (id.ValueKind == JsonValueKind.Number || (id.ValueKind == JsonValueKind.String && !String.IsNullOrEmpty(id.GetString()))))
        return "id:" + id.ToString();
      return "content:" + fingerprint;
    }
    private static decimal? Timestamp(JsonElement row, string field)
    {
      if (!row.TryGetProperty(field, out JsonElement value)) return null;
      string text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
      if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number))
      {
        if (number >= 1000000000000m) number /= 1000m;
        return number > 0 && number <= 253402300799m ? number : null;
      }
      if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset date))
        return date.ToUnixTimeMilliseconds() / 1000m;
      return null;
    }
    private static async Task AddAsync(ZipArchive archive, string path, Stream input, CancellationToken cancellation)
    {
      using var output = archive.CreateEntry(path, CompressionLevel.Optimal).Open();
      input.Position = 0;
      await input.CopyToAsync(output, cancellation).ConfigureAwait(false);
    }

    private static async Task CollectHistoryAsync(PTMagicConfiguration configuration, Source source,
      PTDataExportSelection selection, DateTime end, HttpClient client, ZipArchive archive,
      Dictionary<string, object> result, TransferBudget transfer, int responseLimit, int pageLimit,
      CancellationToken budget, CancellationToken request)
    {
      using var combined = TemporaryFile();
      using var writer = new Utf8JsonWriter(combined);
      writer.WriteStartObject();
      writer.WriteStartArray("data");
      var seen = new Dictionary<string, string>(StringComparer.Ordinal);
      var hashes = new List<JsonElement>();
      var traces = new List<Dictionary<string, object>>();
      string prefix = "Trades/" + source.Name + "-pages/";
      DateTime? start = selection.HistoryStartUtc(end);
      decimal minimum = start.HasValue ? new DateTimeOffset(start.Value).ToUnixTimeMilliseconds() / 1000m : 0m;
      decimal maximum = new DateTimeOffset(end).ToUnixTimeMilliseconds() / 1000m;
      int offset = 0, size = 10, page = 1, pages = 0, accepted = 0, examined = 0, included = 0;
      int duplicates = 0, changed = 0, undated = 0, reductions = 0;
      bool ended = false;
      string error = null;

      while (pages < pageLimit)
      {
        // Size reductions divide the previous size (10 -> 5 -> 1).
        // Keeping the row offset avoids both restarting and skipping records.
        page = offset / size + 1;
        var trace = new Dictionary<string, object>
        {
          ["page"] = page, ["per_page"] = size, ["row_offset"] = offset,
          ["sort"] = source.Sort, ["sort_direction"] = "DESCENDING"
        };
        traces.Add(trace);
        try
        {
          byte[] bytes;
          try
          {
            bytes = await FetchAsync(client, Endpoint(configuration, source.Name,
              $"&page={page}&perPage={size}&sort={source.Sort}&sortDirection=DESCENDING"),
              responseLimit, transfer, trace, budget).ConfigureAwait(false);
          }
          catch (CollectionFailure failure) when (failure.Message == "response_size_limit" && size > 1)
          {
            trace["outcome"] = "response_size_limit";
            size = size == 10 ? 5 : 1;
            reductions++;
            continue;
          }
          using var document = JsonDocument.Parse(bytes);
          string rawPath = prefix + $"page-{page:D5}-size-{size:D2}.json";
          using (var raw = new MemoryStream(bytes, writable: false))
            await AddAsync(archive, rawPath, raw, request).ConfigureAwait(false);
          trace["raw_path"] = rawPath;
          trace["sha256"] = Convert.ToHexString(SHA256.HashData(bytes));
          pages++;
          JsonElement rows = Rows(document.RootElement);
          int count = rows.GetArrayLength();
          trace["row_count"] = count;
          foreach (JsonElement row in rows.EnumerateArray())
            if (row.ValueKind != JsonValueKind.Object) throw new CollectionFailure("history_record_shape");
          accepted++;
          if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("hashcode", out JsonElement hash)) hashes.Add(hash.Clone());
          int fresh = 0;
          foreach (JsonElement row in rows.EnumerateArray())
          {
            examined++;
            string original = row.GetRawText();
            string fingerprint = Hash(original);
            string key = RecordKey(row, fingerprint);
            if (seen.TryGetValue(key, out string previous))
            {
              duplicates++;
              if (previous != fingerprint) changed++;
              continue; // Every returned version remains in its original raw page.
            }
            seen.Add(key, fingerprint);
            fresh++;
            if (start.HasValue)
            {
              decimal? date = Timestamp(row, source.DateField);
              if (!date.HasValue) undated++;
              else if (date.Value < minimum || date.Value > maximum) continue;
            }
            writer.WriteRawValue(original, skipInputValidation: true);
            included++;
          }
          await writer.FlushAsync(request).ConfigureAwait(false);
          trace["new_records"] = fresh;
          if (count > 0 && fresh == 0) throw new CollectionFailure("history_page_repeated");
          // A server ignoring perPage has an unverified offset contract: keep the
          // returned records, but never invent the next offset or claim completeness.
          if (count > size) throw new CollectionFailure("history_page_size_mismatch");
          trace["outcome"] = "accepted";
          if (count < size) { ended = true; break; }
          offset += size;
          // Deliberately scan to the terminal page. BOUGHTDATE's database search
          // field is not assumed to equal lastBoughtDate for every kind of buy.
          // Local range filtering must not omit later DCA transactions.
        }
        catch (Exception failure) when (!request.IsCancellationRequested && Recoverable(failure))
        {
          error = Code(failure);
          trace["outcome"] = error;
          break;
        }
      }
      request.ThrowIfCancellationRequested();
      if (!ended && error == null) error = "history_page_limit";
      writer.WriteEndArray();
      writer.WriteStartArray("sourcePageHashcodes");
      foreach (JsonElement hash in hashes) hash.WriteTo(writer);
      writer.WriteEndArray();
      writer.WriteEndObject();
      await writer.FlushAsync(request).ConfigureAwait(false);
      bool complete = ended && error == null && duplicates == 0 && undated == 0;
      result["status"] = complete ? "available" : accepted > 0 ? "partial" : "failed";
      if (accepted > 0)
      {
        await AddAsync(archive, source.Path, combined, request).ConfigureAwait(false);
        result["zip_path"] = source.Path;
      }
      if (error != null) { result["error_code"] = error; result["failed_page"] = page; }
      result["pagination_first_page"] = 1;
      result["pagination_page_size"] = size;
      result["page_size_reductions"] = reductions;
      result["pages_retrieved"] = pages;
      result["source_record_count"] = examined;
      result["unique_record_count"] = seen.Count;
      result["included_record_count"] = included;
      result["duplicate_records"] = duplicates;
      result["record_versions_changed"] = changed;
      result["unfilterable_records_preserved"] = undated;
      result["reached_history_end"] = ended;
      result["range_coverage_complete"] = complete;
      result["termination"] = ended ? "end_of_available_history" : error;
      result["history_range"] = selection.HistoryRange;
      result["history_start_utc"] = start?.ToString("O");
      result["history_end_utc"] = end.ToString("O");
      result["date_field"] = source.DateField;
      result["sort"] = source.Sort;
      result["sort_direction"] = "DESCENDING";
      result["raw_pages_prefix"] = prefix;
      result["requests"] = traces;
      result["raw_source_payload"] = false;
      result["record_preservation"] = "original_record_json_first_observation_all_raw_versions_retained";
      result["snapshot_consistency_guaranteed"] = false;
      result["warning_codes"] = new List<string>();
      var warnings = (List<string>)result["warning_codes"];
      if (duplicates > 0) warnings.Add("history_overlap");
      if (changed > 0) warnings.Add("history_record_changed");
      if (undated > 0) warnings.Add("history_date_unknown");
    }

    public static Task<Stream> GenerateAsync(PTMagicConfiguration configuration,
      PTDataExportSelection selection, CancellationToken requestAborted) =>
      GenerateAsync(configuration, selection, Client, requestAborted);

    // Test seam links this production file: no separate test-only exporter.
    internal static async Task<Stream> GenerateAsync(PTMagicConfiguration configuration,
      PTDataExportSelection selection, HttpClient client, CancellationToken requestAborted,
      int responseLimit = DefaultResponseLimit, long transferLimit = DefaultTransferLimit, int pageLimit = 10000)
    {
      if (configuration == null) throw new ArgumentNullException(nameof(configuration));
      if (client == null) throw new ArgumentNullException(nameof(client));
      if (selection == null || !selection.ValidHistoryRange || !selection.AnySelected)
        throw new ArgumentException("Select data to export and a valid history range.");
      if (responseLimit < 1 || transferLimit < 1 || pageLimit < 1) throw new ArgumentOutOfRangeException(nameof(responseLimit));
      _ = Endpoint(configuration, "buys");
      if (!await Gate.WaitAsync(0, requestAborted).ConfigureAwait(false))
        throw new InvalidOperationException("Another PT Data export is currently running.");
      try
      {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        budget.CancelAfter(TimeSpan.FromSeconds(120));
        DateTime started = DateTime.UtcNow;
        var transfer = new TransferBudget(transferLimit);
        var datasets = new List<Dictionary<string, object>>();
        var output = TemporaryFile();
        try
        {
          using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
          {
            foreach (Source source in Sources)
            {
              requestAborted.ThrowIfCancellationRequested();
              var result = new Dictionary<string, object>
              {
                ["name"] = source.Name, ["group"] = source.Group, ["source"] = "ProfitTrailer",
                ["status"] = "skipped", ["zip_path"] = null
              };
              datasets.Add(result);
              if (!Selected(source, selection)) continue;
              if (source.History)
              {
                await CollectHistoryAsync(configuration, source, selection, started, client, archive,
                  result, transfer, responseLimit, pageLimit, budget.Token, requestAborted).ConfigureAwait(false);
                continue;
              }
              var trace = new Dictionary<string, object>();
              try
              {
                byte[] bytes = await FetchAsync(client, Endpoint(configuration, source.Name), responseLimit,
                  transfer, trace, budget.Token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(bytes);
                if (document.RootElement.ValueKind != JsonValueKind.Object && document.RootElement.ValueKind != JsonValueKind.Array)
                  throw new CollectionFailure("snapshot_response_shape");
                using var raw = new MemoryStream(bytes, writable: false);
                await AddAsync(archive, source.Path, raw, requestAborted).ConfigureAwait(false);
                result["status"] = "available";
                result["zip_path"] = source.Path;
                result["raw_source_payload"] = true;
                result["received_bytes"] = bytes.Length;
                // Funding retains its existing single-response contract, not a claim
                // that all historical funding has been collected.
                result["collection_scope"] = "single_api_response";
              }
              catch (Exception failure) when (!requestAborted.IsCancellationRequested && Recoverable(failure))
              {
                result["status"] = "failed";
                result["error_code"] = Code(failure);
              }
              result["request"] = trace;
            }
            int available = 0, partial = 0, failed = 0;
            foreach (var item in datasets)
            {
              if ((string)item["status"] == "available") available++;
              else if ((string)item["status"] == "partial") partial++;
              else if ((string)item["status"] == "failed") failed++;
            }
            var manifest = new
            {
              schema_version = "1.3", exporter_version = "pt-history-v3",
              export_started_utc = started.ToString("O"), export_completed_utc = DateTime.UtcNow.ToString("O"),
              bot_instance_name = configuration.GeneralSettings.Application.InstanceName,
              source_system = "ProfitTrailer", collection_method = "configured_target_read_only_api",
              history_range = selection.HistoryRange,
              history_start_utc = selection.HistoryStartUtc(started)?.ToString("O"), history_end_utc = started.ToString("O"),
              overall_status = partial == 0 && failed == 0 ? "complete_for_selected_sources"
                : available + partial > 0 ? "partial" : "failed",
              dataset_count_available = available, dataset_count_partial = partial, dataset_count_failed = failed,
              lifetime_history_complete = false, snapshot_consistency_guaranteed = false,
              received_source_bytes = transfer.Received, source_byte_limit = transfer.Limit,
              history_contract = "PT_2.6.1_one_based_page_offset_perPage_times_page_minus_one",
              record_notes = "Original nested trade JSON is retained. Raw pages include out-of-range rows. Completion describes a bounded API scan, not an atomic or lifetime account history.",
              datasets
            };
            using var manifestStream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest,
              new JsonSerializerOptions { WriteIndented = true }));
            await AddAsync(archive, "manifest.json", manifestStream, requestAborted).ConfigureAwait(false);
          }
          output.Position = 0;
          return output;
        }
        catch { output.Dispose(); throw; }
      }
      finally { Gate.Release(); }
    }
  }
}
