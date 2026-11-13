using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
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
    public int MaximumHistoryRecords { get; set; }
    public bool IncludeBuys { get; set; }
    public bool IncludeSales { get; set; }
    public bool IncludeCurrentPositions { get; set; }
    public bool IncludeFunding { get; set; }

    public bool AnySelected => IncludeBuys || IncludeSales || IncludeCurrentPositions || IncludeFunding;
    public bool ValidLimit => MaximumHistoryRecords == 0 || MaximumHistoryRecords == 250
      || MaximumHistoryRecords == 1000 || MaximumHistoryRecords == 5000;
  }

  public static class PTDataExportService
  {
    private const int MaxResponseBytes = 32 * 1024 * 1024;
    private const long MaxTotalSourceBytes = 128L * 1024 * 1024;
    private static readonly SemaphoreSlim ExportGate = new SemaphoreSlim(1, 1);
    private static readonly HttpClient Client = CreateClient();

    private sealed class Source
    {
      public string Name { get; }
      public string Group { get; }
      public string Endpoint { get; }
      public string ZipPath { get; }
      public bool IsHistory { get; }

      public Source(string name, string group, string endpoint, string zipPath, bool isHistory)
      {
        Name = name;
        Group = group;
        Endpoint = endpoint;
        ZipPath = zipPath;
        IsHistory = isHistory;
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
      new Source("funding", "Funding Updates", "api/v2/data/funding", "Funding/funding-updates.json", true)
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

    private static Uri BuildUri(PTMagicConfiguration configuration, string endpoint)
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
        Query = "token=" + Uri.EscapeDataString(token),
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

    public static async Task<Stream> GenerateAsync(PTMagicConfiguration configuration,
      PTDataExportSelection selection, CancellationToken requestAborted)
    {
      if (configuration == null) throw new ArgumentNullException(nameof(configuration));
      if (selection == null || !selection.AnySelected || !selection.ValidLimit)
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
                  byte[] received = await ReadResponseAsync(BuildUri(configuration, source.Endpoint), budget.Token)
                    .ConfigureAwait(false);
                  if (collectedBytes + received.Length > MaxTotalSourceBytes)
                    throw new InvalidDataException("PT Data total size limit was exceeded.");

                  using (JsonDocument document = JsonDocument.Parse(received))
                  {
                    if (document.RootElement.ValueKind != JsonValueKind.Array
                      && document.RootElement.ValueKind != JsonValueKind.Object)
                      throw new JsonException("Unexpected PT data shape.");

                    int? returnedCount = document.RootElement.ValueKind == JsonValueKind.Array
                      ? document.RootElement.GetArrayLength() : (int?)null;
                    int? includedCount = returnedCount;
                    bool reduced = false;
                    byte[] output = received;

                    if (source.IsHistory && selection.MaximumHistoryRecords > 0)
                    {
                      if (!returnedCount.HasValue)
                        throw new InvalidDataException("Cannot apply a record limit to a non-array history response.");
                      if (returnedCount.Value > selection.MaximumHistoryRecords)
                      {
                        using (var trimmed = new MemoryStream())
                        {
                          using (var writer = new Utf8JsonWriter(trimmed))
                          {
                            writer.WriteStartArray();
                            int written = 0;
                            foreach (JsonElement row in document.RootElement.EnumerateArray())
                            {
                              if (written >= selection.MaximumHistoryRecords) break;
                              row.WriteTo(writer);
                              written++;
                            }
                            writer.WriteEndArray();
                          }
                          output = trimmed.ToArray();
                        }
                        includedCount = selection.MaximumHistoryRecords;
                        reduced = true;
                      }
                    }

                    var entry = archive.CreateEntry(source.ZipPath, CompressionLevel.Optimal);
                    using (var entryStream = entry.Open())
                    {
                      await entryStream.WriteAsync(output, 0, output.Length, requestAborted)
                        .ConfigureAwait(false);
                    }

                    collectedBytes += received.Length;
                    item["status"] = "available";
                    item["zip_path"] = source.ZipPath;
                    item["retrieved_at_utc"] = DateTime.UtcNow.ToString("O");
                    item["received_bytes"] = received.Length;
                    item["included_bytes"] = output.Length;
                    item["source_record_count"] = returnedCount;
                    item["included_record_count"] = includedCount;
                    item["raw_source_payload"] = !reduced;
                    item["limit_applied"] = reduced;
                    if (reduced) item["limit_method"] = "first_N_records_in_PT_response_order";
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
              int failed = 0;
              foreach (var item in results)
              {
                if ((string)item["status"] == "available") available++;
                else if ((string)item["status"] == "failed") failed++;
              }

              var manifest = new
              {
                schema_version = "1.0",
                export_id = Guid.NewGuid().ToString("N"),
                export_started_utc = started.ToString("O"),
                export_finished_utc = DateTime.UtcNow.ToString("O"),
                bot_scope = "configured_PT_target_of_this_Monitor",
                bot_instance_name = configuration.GeneralSettings.Application.InstanceName,
                record_limit_per_history_dataset = selection.MaximumHistoryRecords == 0
                  ? "all_returned" : selection.MaximumHistoryRecords.ToString(),
                record_limit_order = "PT_response_order_not_verified_chronological",
                lifetime_history_complete = false,
                pagination_verified = false,
                history_note = "Only PT-returned data is available. There is no guarantee of full lifetime history or pagination.",
                positions_note = "Current Positions are point-in-time snapshots, not historical positions.",
                funding_note = "PT funding updates may be missing while PT was offline; do not treat them as complete settled funding cashflows.",
                dataset_count_available = available,
                dataset_count_failed = failed,
                overall_status = failed > 0 ? (available > 0 ? "partial" : "failed") : "complete_for_selected_sources",
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
