using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Core.Main;
using Core.Helper;
using Monitor._Internal;
using Microsoft.Extensions.Logging.Abstractions;

internal static class Program
{
  private sealed record Call(string Name, int Page, int Size, Dictionary<string, string> Query);
  private sealed class Handler : HttpMessageHandler
  {
    public readonly List<Call> Calls = new();
    public Func<Call, HttpResponseMessage> Respond;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
      token.ThrowIfCancellationRequested();
      Check(request.Method == HttpMethod.Get, "read-only GET");
      Check(request.RequestUri.AbsolutePath.StartsWith("/instance/api/v2/data/"), "configured target prefix");
      var query = request.RequestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Split('=', 2)).ToDictionary(x => WebUtility.UrlDecode(x[0]), x => WebUtility.UrlDecode(x[1]));
      Check(query["token"] == "secret-token+&?", "token encoding");
      Check(!query.ContainsKey("hashcode"), "no conditional hash response requested");
      var call = new Call(request.RequestUri.Segments.Last(),
        query.TryGetValue("page", out var page) ? int.Parse(page) : 0,
        query.TryGetValue("perPage", out var size) ? int.Parse(size) : 0, query);
      if (call.Name == "buys" || call.Name == "sales")
      {
        Check(call.Page >= 1, "PT history pages MUST start at one");
        Check(query["sort"] == (call.Name == "buys" ? "BOUGHTDATE" : "SOLDDATE"), "history sort");
        Check(query["sortDirection"] == "DESCENDING", "sort direction");
      }
      Calls.Add(call);
      return Task.FromResult(Respond(call));
    }
  }
  private sealed record Export(Dictionary<string, string> Files, List<Call> Calls)
  {
    public JsonElement Manifest => Parse(Files["manifest.json"]);
    public JsonElement Dataset(string name) => Manifest.GetProperty("datasets").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
    public JsonElement Rows(string name) => Parse(Files[$"Trades/{name}.json"]).GetProperty("data");
    public string Status(string name) => Dataset(name).GetProperty("status").GetString();
    public string Error(string name) => Dataset(name).GetProperty("error_code").GetString();
  }
  private static int tests;
  private static JsonElement Parse(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
  // Match Kestrel's non-seekable, async-only response body.
  private sealed class AsyncOnlyDestination : Stream
  {
    public readonly MemoryStream Bytes = new();
    public Func<CancellationToken, Task> BeforeWrite;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new InvalidOperationException("Synchronous flush");
    public override Task FlushAsync(CancellationToken ct) => Bytes.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous write");
    public override void Write(ReadOnlySpan<byte> buffer) => throw new InvalidOperationException("Synchronous write");
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
      WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
      if (BeforeWrite != null) await BeforeWrite(ct);
      await Bytes.WriteAsync(buffer, ct);
    }
  }

  private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
  private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
    new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
  private static string Rows(int start, int count) => JsonSerializer.Serialize(Enumerable.Range(start, count).Select(i => new
  {
    id = i + 1, market = "PAIR" + i, firstBoughtDate = 1780000000L,
    lastBoughtDate = 1794680000L - i, soldDate = 1794680000L - i,
    extraInfo = new[] { new { name = "DYN-BUYNOTE1", extraInfo = "CV: " + i, currentValue = i + 0.125 } }
  }));
  // Independently derived from the official 2.6.1 service bytecode, not from
  // the exporter's loop: offset = perPage * (page - 1). See CONTRACT.md.
  private static HttpResponseMessage PTPage(Call call, int total)
  {
    int offset = call.Size * (call.Page - 1);
    Check(offset >= 0, "negative PT SQL offset");
    int count = Math.Max(0, Math.Min(call.Size, total - offset));
    return Json("{\"hashcode\":" + (1234 + offset) + ",\"data\":" + Rows(offset, count) + "}");
  }
  private static async Task<Export> Run(Func<Call, HttpResponseMessage> respond,
    PTDataExportSelection selection = null, int responseLimit = 32 * 1024 * 1024,
    long transferLimit = 128L * 1024 * 1024, int pageLimit = 10000)
  {
    using var handler = new Handler { Respond = respond };
    using var client = new HttpClient(handler);
    using var stream = await PTDataExportService.GenerateAsync(new PTMagicConfiguration(),
      selection ?? new PTDataExportSelection { IncludeBuys = true }, client, CancellationToken.None,
      responseLimit, transferLimit, pageLimit);
    using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
    var files = new Dictionary<string, string>();
    foreach (var entry in zip.Entries)
    {
      using var reader = new StreamReader(entry.Open());
      files.Add(entry.FullName, await reader.ReadToEndAsync());
    }
    foreach (string content in files.Values) _ = Parse(content);
    return new Export(files, handler.Calls);
  }
  private static async Task Test(string name, Func<Task> test)
  {
    await test(); tests++; Console.WriteLine("PASS " + name);
  }
  private static void Complete(Export result, string name, int count)
  {
    Check(result.Status(name) == "available", name + " available");
    Check(result.Rows(name).GetArrayLength() == count, "exact trade count");
    Check(result.Dataset(name).GetProperty("range_coverage_complete").GetBoolean(), "bounded scan complete");
    Check(!result.Manifest.GetProperty("lifetime_history_complete").GetBoolean(), "no lifetime completeness claim");
    Check(!result.Manifest.GetProperty("snapshot_consistency_guaranteed").GetBoolean(), "no atomic snapshot claim");
  }

  public static async Task Main()
  {
    await Test("official one-based paging: 0, 1, 9, 10, 11, 20, 21, 400 trades on BOTH endpoints", async () =>
    {
      foreach (string name in new[] { "buys", "sales" })
      foreach (int total in new[] { 0, 1, 9, 10, 11, 20, 21, 400 })
      {
        var result = await Run(c => PTPage(c, total), new PTDataExportSelection { IncludeBuys = name == "buys", IncludeSales = name == "sales" });
        Complete(result, name, total);
        Check(result.Calls.Select(c => c.Page).SequenceEqual(Enumerable.Range(1, total / 10 + 1)), "one-based contiguous requests");
        Check(result.Rows(name).EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).SequenceEqual(Enumerable.Range(1, total)), "no missing/duplicate transaction");
      }
    });
    await Test("new collection bypasses the observed page-zero/page-one alias", async () =>
    {
      // Live archive supplied the alias evidence; the official service supplied
      // the arithmetic. Never use this alias to infer zero-based API support.
      int LegacyOffset(int page, int size) => Math.Max(0, size * (page - 1));
      Check(LegacyOffset(0, 10) == LegacyOffset(1, 10), "old request sequence repeats the first offset");
      var result = await Run(c => PTPage(c, 23));
      Complete(result, "buys", 23);
      Check(result.Calls[0].Page == 1 && result.Calls[1].Page == 2, "first two distinct PT offsets");
    });
    await Test("raw bytes and original nested JSON/CV/numeric spelling retained", async () =>
    {
      string record = "{\"id\":1,\"extraInfo\":[{\"name\":\"DYN-SELLNOTE1\",\"extraInfo\":\"CV: 12.50\",\"currentValue\":1.2500e+1}],\"unknown\":{\"keep\":null}}";
      string body = " { \"hashcode\":73,\"data\":[" + record + "] } ";
      var result = await Run(c => Json(body));
      Complete(result, "buys", 1);
      Check(result.Rows("buys")[0].GetRawText() == record, "original record text copied without mapping");
      Check(result.Files["Trades/buys-pages/page-00001-size-10.json"] == body, "unmodified response bytes");
      Check(Parse(result.Files["Trades/buys.json"]).GetProperty("sourcePageHashcodes")[0].GetInt32() == 73, "source hash retained");
    });
    await Test("bare arrays and supported data/content wrappers", async () =>
    {
      foreach (string body in new[] { Rows(0, 2), "{\"data\":{\"content\":" + Rows(0, 2) + "}}" })
        Complete(await Run(c => Json(body)), "buys", 2);
    });
    await Test("unexpected shape preserves raw page and cannot masquerade as empty history", async () =>
    {
      string body = "{\"unexpected\":[{\"id\":1}]}";
      var result = await Run(c => Json(body));
      Check(result.Status("buys") == "failed" && result.Error("buys") == "history_response_shape", "explicit shape error");
      Check(result.Files.Values.Contains(body) && !result.Files.ContainsKey("Trades/buys.json"), "raw evidence without invented empty trades");
    });
    await Test("repeated page retains prior trades and safe request evidence", async () =>
    {
      var result = await Run(c => Json(Rows(0, 10)));
      Check(result.Status("buys") == "partial" && result.Error("buys") == "history_page_repeated", "repeat classification");
      Check(result.Rows("buys").GetArrayLength() == 10 && result.Calls.Count == 2, "bounded repeat handling");
      Check(result.Dataset("buys").GetProperty("failed_page").GetInt32() == 2, "failed API page is two");
      Check(result.Files.ContainsKey("Trades/buys-pages/page-00002-size-10.json"), "repeat raw response preserved");
    });
    await Test("changing ticker fields cannot disguise an identical set of history IDs", async () =>
    {
      var result = await Run(c => Json(Rows(0, 10).Replace("CV: ", "CV: request" + c.Page + "-")));
      Check(result.Error("buys") == "history_page_repeated" && result.Rows("buys").GetArrayLength() == 10, "identity rather than mutable content");
      Check(result.Dataset("buys").GetProperty("record_versions_changed").GetInt32() == 10, "changed versions diagnosed and retained raw");
    });
    await Test("overlap retains new trades but does not claim complete coverage", async () =>
    {
      var result = await Run(c => Json(c.Page == 1 ? Rows(0, 10) : Rows(9, 3)));
      Check(result.Status("buys") == "partial" && result.Rows("buys").GetArrayLength() == 12, "overlap deduplicated");
      Check(result.Dataset("buys").GetProperty("duplicate_records").GetInt32() == 1, "overlap warning");
    });
    await Test("same timestamp does not merge different transaction IDs", async () =>
    {
      string body = "[{\"id\":1,\"soldDate\":1794680000},{\"id\":2,\"soldDate\":1794680000}]";
      Complete(await Run(c => Json(body), new PTDataExportSelection { IncludeSales = true }), "sales", 2);
    });
    await Test("missing IDs preserve distinct records using content identity", async () =>
    {
      Complete(await Run(c => Json("[{\"market\":\"A\"},{\"market\":\"B\"}]")), "buys", 2);
    });
    await Test("malformed later JSON and invalid rows cannot erase prior pages", async () =>
    {
      foreach (var failure in new[] { ("not JSON", "invalid_json"), ("[null]", "history_record_shape") })
      {
        var result = await Run(c => c.Page == 1 ? PTPage(c, 21) : Json(failure.Item1));
        Check(result.Error("buys") == failure.Item2 && result.Rows("buys").GetArrayLength() == 10, "valid partial JSON survives");
      }
    });
    await Test("204 is not terminal history; HTTP errors do not leak response bodies", async () =>
    {
      foreach (var status in new[] { HttpStatusCode.NoContent, HttpStatusCode.Forbidden, HttpStatusCode.Redirect })
      {
        var result = await Run(c => Json("secret-token+&? https://pt.invalid/private", status));
        Check(result.Status("buys") == "failed", "non-data response is not a successful empty history");
        Check(result.Files.Values.All(x => !x.Contains("secret-token") && !x.Contains("pt.invalid")), "secrets absent");
      }
    });
    await Test("response overflow reduces size at the SAME OFFSET without restarting", async () =>
    {
      var result = await Run(c => (c.Size == 10 && c.Page == 2) || (c.Size == 5 && c.Page == 3)
        ? Json(new string('x', 5000)) : PTPage(c, 23), responseLimit: 4000);
      Complete(result, "buys", 23);
      Check(result.Calls.Take(4).Select(c => (c.Page, c.Size)).SequenceEqual(new[] { (1, 10), (2, 10), (3, 5), (11, 1) }), "offset ten preserved on 10 -> 5 -> 1");
      Check(result.Rows("buys").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).SequenceEqual(Enumerable.Range(1, 23)), "no skips or replay");
      Check(result.Dataset("buys").GetProperty("page_size_reductions").GetInt32() == 2, "two bounded reductions");
    });
    await Test("a single oversized trade stops with explicit partial failure", async () =>
    {
      var result = await Run(c => c.Page == 1 && c.Size == 10 ? PTPage(c, 30) : Json(new string('x', 5000)), responseLimit: 4000);
      Check(result.Error("buys") == "response_size_limit" && result.Calls.Last().Size == 1, "cannot shrink below one");
      Check(result.Rows("buys").GetArrayLength() == 10, "preceding trades survive");
    });
    await Test("server ignoring perPage is retained but never called complete", async () =>
    {
      var result = await Run(c => Json(Rows(0, 12)));
      Check(result.Error("buys") == "history_page_size_mismatch" && result.Rows("buys").GetArrayLength() == 12, "no invented pagination offset");
    });
    await Test("custom day week month periods and invalid ranges", () =>
    {
      DateTime end = new DateTime(2026, 3, 31, 12, 0, 0, DateTimeKind.Utc);
      Check(ExportHistoryPeriod.TryFromInput(4, "days", end, out string code, out DateTime? start)
        && code == "4d" && start == end.AddDays(-4), "custom days");
      Check(ExportHistoryPeriod.TryFromInput(2, "weeks", end, out code, out start)
        && code == "2w" && start == end.AddDays(-14), "custom weeks");
      Check(ExportHistoryPeriod.TryFromInput(1, "months", end, out code, out start)
        && code == "1m" && start == new DateTime(2026, 2, 28, 12, 0, 0, DateTimeKind.Utc), "calendar month");
      Check(ExportHistoryPeriod.TryFromInput(0, "all", end, out code, out start)
        && code == "all" && start == null, "all history");
      Check(!ExportHistoryPeriod.TryFromInput(0, "days", end, out _, out _), "zero invalid");
      Check(!ExportHistoryPeriod.TryFromInput(-1, "weeks", end, out _, out _), "negative invalid");
      Check(!ExportHistoryPeriod.TryFromInput(1, "years", end, out _, out _), "unknown unit invalid");
      Check(!ExportHistoryPeriod.TryFromCode("0m", end, out _), "zero months invalid");
      Check(!ExportHistoryPeriod.TryFromCode("2147483647m", end, out _), "overflow months invalid");
      Check(ExportHistoryPeriod.TryFromCode("7d", end, out start) && start == end.AddDays(-7),
        "legacy ranges still accepted");
      return Task.CompletedTask;
    });
    await Test("log ZIP file filtering uses dated files and modification fallback", () =>
    {
      DateTime end = new DateTime(2026, 3, 31, 12, 0, 0, DateTimeKind.Utc);
      DateTime start = end.AddDays(-7);
      Check(LogArchiveDateFilter.Includes("2026-03-31.txt", start, end), "current dated log");
      Check(LogArchiveDateFilter.Includes("2026-03-24.txt", start, end), "boundary dated log");
      Check(!LogArchiveDateFilter.Includes("2026-03-20.txt", start, end), "old dated log excluded");
      Check(LogArchiveDateFilter.Includes("2026-03-20.txt", null, end), "all available includes old logs");
      string folder = Path.Combine(Path.GetTempPath(), "ptm-log-filter-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(folder);
      try
      {
        string unknown = Path.Combine(folder, "legacy-log.txt");
        File.WriteAllText(unknown, "test");
        File.SetLastWriteTimeUtc(unknown, start.AddHours(-1));
        Check(!LogArchiveDateFilter.Includes(unknown, start, end), "old unknown-name log excluded");
        File.SetLastWriteTimeUtc(unknown, end.AddHours(-1));
        Check(LogArchiveDateFilter.Includes(unknown, start, end), "recent unknown-name log included");
      }
      finally { Directory.Delete(folder, true); }
      return Task.CompletedTask;
    });
    await Test("data ZIP period filters history but preserves current state and caches", async () =>
    {
      var end = new DateTime(2026, 11, 27, 6, 0, 0, DateTimeKind.Utc);
      var start = end.AddDays(-1);
      string folder = Path.Combine(Path.GetTempPath(), "ptm-data-archive-" + Guid.NewGuid().ToString("N"));
      try
      {
        void Put(string relative, string text)
        {
          string path = Path.Combine(folder, relative);
          Directory.CreateDirectory(Path.GetDirectoryName(path));
          File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        Put("DecisionHistory/2026-11-25-000001.jsonl", "{\"RecordedAtUtc\":\"2026-11-25T12:00:00Z\",\"id\":0}\n");
        const string beforeCutoff = "{\"RecordedAtUtc\":\"2026-11-26T05:59:59Z\",\"id\":0}\n";
        const string included = "{\"RecordedAtUtc\":\"2026-11-26T06:00:00Z\",\"id\":1}\n";
        Put("DecisionHistory/2026-11-26-000001.jsonl", beforeCutoff + included + "{\"incomplete\":");
        Put("Exchange/MarketData_2026-11-26_05.59.json", "{}");
        Put("Exchange/MarketData_2026-11-26_06.00.json", "{}");
        Put("Exchange/Metadata.json", "{}");
        Put("NormalizedOhlcvCache/BINANCEFUTURES/test.gz", "gzip bytes");
        Put("MarketTrendAnalytics.json", "{}");
        var members = StreamingDownloadArchive.CaptureData(folder, start, end, default);
        Check(members.Count == 5 && members.All(x => !x.Entry.Contains("11-25") && !x.Entry.Contains("05.59")), "bounded history selection");
        Check(StreamingDownloadArchive.CaptureData(folder, null, end, default).Count == 7, "all history");
        File.AppendAllText(Path.Combine(folder, "DecisionHistory/2026-11-26-000001.jsonl"), "true}\n{\"id\":2}\n");
        Put("MarketTrendAnalytics.json", "{\"newSnapshot\":true}");
        var output = new AsyncOnlyDestination();
        Check(await StreamingDownloadArchive.WriteAsync(output, members, default) == 5, "async nonseekable ZIP");
        output.Bytes.Position = 0;
        using var zip = new ZipArchive(output.Bytes, ZipArchiveMode.Read);
        string Read(string name)
        {
          using var reader = new StreamReader(zip.GetEntry("_data/" + name).Open());
          return reader.ReadToEnd();
        }
        Check(Read("DecisionHistory/2026-11-26-000001.jsonl") == included, "exact cutoff and no appended/interrupted records");
        Check(Read("MarketTrendAnalytics.json") == "{\"newSnapshot\":true}", "replacement snapshot not truncated");
        Check(Read("NormalizedOhlcvCache/BINANCEFUTURES/test.gz") == "gzip bytes", "compressed cache intact");
        Check(zip.Entries.Count == 5, "correct archive membership");
      }
      finally { Directory.Delete(folder, true); }
    });
    await Test("compressed raid names select exact UTC time without decompressing and exclude unfinished writes", async () =>
    {
      var end = new DateTimeOffset(2026, 11, 27, 6, 0, 0, TimeSpan.Zero);
      var from = end.AddDays(-1);
      string folder = Path.Combine(Path.GetTempPath(), "ptm-compact-history-" + Guid.NewGuid().ToString("N"));
      string history = Path.Combine(folder, "DecisionHistory");
      Directory.CreateDirectory(history);
      try
      {
        string Add(DateTimeOffset stamp)
        {
          string name = DecisionHistoryFileNames.CompactName(stamp, Guid.NewGuid());
          using var file = File.Create(Path.Combine(history, name));
          using var gzip = new GZipStream(file, CompressionLevel.Fastest);
          using var writer = new StreamWriter(gzip);
          writer.WriteLine("{\"SchemaVersion\":2,\"RecordedAtUtc\":\"" + stamp.ToString("O") + "\"}");
          return name;
        }
        Add(from.AddTicks(-1));
        string lower = Add(from);
        string upper = Add(end);
        Add(end.AddTicks(1));
        File.WriteAllText(Path.Combine(history, upper + ".tmp"), "unfinished");
        var members = StreamingDownloadArchive.CaptureData(folder, from.UtcDateTime, end.UtcDateTime, default);
        Check(members.Count == 2 && members.All(m => m.Entry.EndsWith(lower) || m.Entry.EndsWith(upper)), "exact inclusive timestamp boundaries");
        Check(StreamingDownloadArchive.CaptureData(folder, null, end.UtcDateTime, default).Count == 4, "all retains published files only");
        var output = new AsyncOnlyDestination();
        Check(await StreamingDownloadArchive.WriteAsync(output, members, default) == 2, "stream compact files");
        output.Bytes.Position = 0;
        using var zip = new ZipArchive(output.Bytes, ZipArchiveMode.Read);
        foreach (var member in members)
        {
          using var bytes = new MemoryStream();
          using (var input = zip.GetEntry(member.Entry).Open()) await input.CopyToAsync(bytes);
          Check(bytes.ToArray().SequenceEqual(File.ReadAllBytes(member.Path)), "gzip member stored intact");
        }
      }
      finally { Directory.Delete(folder, true); }
    });
    await Test("legacy finite export scans large records without including out-of-range or incomplete records", async () =>
    {
      var end = new DateTime(2026, 11, 27, 6, 0, 0, DateTimeKind.Utc);
      var from = end.AddDays(-1);
      string folder = Path.Combine(Path.GetTempPath(), "ptm-legacy-history-" + Guid.NewGuid().ToString("N"));
      string history = Path.Combine(folder, "DecisionHistory");
      Directory.CreateDirectory(history);
      try
      {
        string name = "2026-11-26-000001.jsonl";
        string before = "{\"RecordedAtUtc\":\"2026-11-26T05:59:59Z\",\"Data\":\"" + new string('x', 200000) + "\"}\n";
        string included = "{\"RecordedAtUtc\":\"2026-11-26T06:00:00Z\",\"Data\":\"" + new string('y', 200000) + "\"}\n";
        string after = "{\"RecordedAtUtc\":\"2026-11-27T06:00:01Z\"}\n";
        string path = Path.Combine(history, name);
        File.WriteAllText(path, before + included + after + "{\"RecordedAtUtc\":\"2026-11-26T07:00:00Z\"");
        var members = StreamingDownloadArchive.CaptureData(folder, from, end, default);
        var output = new AsyncOnlyDestination();
        await StreamingDownloadArchive.WriteAsync(output, members, default);
        output.Bytes.Position = 0;
        using var zip = new ZipArchive(output.Bytes, ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.Entries.Single().Open());
        Check(reader.ReadToEnd() == included, "only complete in-range record preserved");
      }
      finally { Directory.Delete(folder, true); }
    });
    await Test("log ZIP streams bounded source prefixes with existing date filtering", async () =>
    {
      var end = new DateTime(2026, 11, 27, 6, 0, 0, DateTimeKind.Utc);
      string folder = Path.Combine(Path.GetTempPath(), "ptm-log-archive-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(folder);
      try
      {
        string current = Path.Combine(folder, end.ToLocalTime().ToString("yyyy-MM-dd") + ".txt");
        string older = Path.Combine(folder, end.AddDays(-3).ToLocalTime().ToString("yyyy-MM-dd") + ".txt");
        File.WriteAllText(current, "first\n");
        File.WriteAllText(older, "older\n");
        var members = StreamingDownloadArchive.CaptureLogs(new[] { (folder, "PTMagic/") }, end.AddDays(-1), end, default);
        Check(members.Count == 1, "old log excluded");
        File.AppendAllText(current, "later\n");
        var output = new AsyncOnlyDestination();
        Check(await StreamingDownloadArchive.WriteAsync(output, members, default) == 1, "async ZIP");
        output.Bytes.Position = 0;
        using var zip = new ZipArchive(output.Bytes, ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.Entries.Single().Open());
        Check(reader.ReadToEnd() == "first\n", "bounded active log");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        bool cancelled = false;
        try { await StreamingDownloadArchive.WriteAsync(new AsyncOnlyDestination(), members, cts.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "cancellation propagates");
      }
      finally { Directory.Delete(folder, true); }
    });
    await Test("ZIP streaming survives backpressure and stops on cancellation or downstream failure", async () =>
    {
      string folder = Path.Combine(Path.GetTempPath(), "ptm-zip-backpressure-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(folder);
      try
      {
        // A stored member much larger than the bounded queue forces backpressure.
        byte[] payload = new byte[2 * 1024 * 1024];
        new Random(27).NextBytes(payload);
        string path = Path.Combine(folder, "cache.gz");
        File.WriteAllBytes(path, payload);
        var members = new[] { new StreamingDownloadArchive.Member(path, "_data/cache.gz", null) };
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new AsyncOnlyDestination
        {
          BeforeWrite = async ct =>
          {
            entered.TrySetResult(true);
            await release.Task.WaitAsync(ct);
          }
        };
        var transfer = StreamingDownloadArchive.WriteAsync(slow, members, default);
        try
        {
          await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
          Check(!transfer.IsCompleted, "first bytes are available before the entire ZIP is produced");
        }
        finally { release.TrySetResult(true); }
        Check(await transfer.WaitAsync(TimeSpan.FromSeconds(10)) == 1, "slow async output completes");
        slow.Bytes.Position = 0;
        using (var zip = new ZipArchive(slow.Bytes, ZipArchiveMode.Read))
        using (var input = zip.Entries.Single().Open())
        using (var extracted = new MemoryStream())
        {
          await input.CopyToAsync(extracted);
          Check(extracted.ToArray().SequenceEqual(payload), "large streamed entry and ZIP directory preserved");
        }

        using var cts = new CancellationTokenSource();
        var blocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledOutput = new AsyncOnlyDestination
        {
          BeforeWrite = async ct =>
          {
            blocked.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, ct);
          }
        };
        var cancelledTransfer = StreamingDownloadArchive.WriteAsync(cancelledOutput, members, cts.Token);
        try { await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { cts.Cancel(); }
        bool cancelled = false;
        try { await cancelledTransfer.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "cancelled consumer unblocks and joins the producer");

        var failedOutput = new AsyncOnlyDestination
        {
          BeforeWrite = _ => Task.FromException(new IOException("downstream failed"))
        };
        bool failed = false;
        try { await StreamingDownloadArchive.WriteAsync(failedOutput, members, default).WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (IOException ex) when (ex.Message == "downstream failed") { failed = true; }
        Check(failed, "downstream failure propagates without hanging the producer");
      }
      finally { Directory.Delete(folder, true); }
    });
    await Test("multipart ZIPs respect finalized size and reconstruct exactly the complete selected dataset", () =>
    {
      string root = Path.Combine(Path.GetTempPath(), "ptm-multipart-" + Guid.NewGuid().ToString("N"));
      string data = Path.Combine(root, "data");
      string spool = Path.Combine(root, "spool");
      Directory.CreateDirectory(data);
      try
      {
        var original = new Dictionary<string, byte[]>();
        var members = new List<StreamingDownloadArchive.Member>();
        var random = new Random(42);
        for (int i = 0; i < 7; i++)
        {
          string entry = "_data/DecisionHistory/raid-" + i + ".jsonl.gz";
          byte[] bytes = new byte[400000 + i * 100];
          random.NextBytes(bytes);
          string path = Path.Combine(data, "raid-" + i + ".gz");
          File.WriteAllBytes(path, bytes);
          original.Add(entry, bytes);
          members.Add(new StreamingDownloadArchive.Member(path, entry, null));
        }
        Check(MultipartZipBatch.TryLimit(1, out long cap) && cap == 1048576 &&
          MultipartZipBatch.TryLimit(0, out long unlimited) && unlimited == 0 &&
          !MultipartZipBatch.TryLimit(-1, out _), "positive integer MiB and unlimited zero");
        string id = Guid.NewGuid().ToString("N");
        var batch = new MultipartZipBatch(spool, "PTMagic-data-" + id, id, members, cap);
        var first = batch.PrepareNext(default);
        Check(first.Parts.Length == 1 && !first.Complete && first.ProcessedSources > 0,
          "one part prepared immediately, not the full export");
        var firstPath = Path.Combine(spool, first.Parts[0].FileName);
        byte[] firstBytes = File.ReadAllBytes(firstPath);
        // The prepared part must remain retryable even when source content changes.
        File.WriteAllText(members[0].Path, "changed after first part was prepared");
        var current = first;
        while (!current.Complete) current = batch.PrepareNext(default);
        Check(current.Parts.Length > 1 && current.ProcessedSources == members.Count &&
          current.Parts.Sum(p => p.Files) == members.Count, "all sources processed exactly once");
        var merged = new Dictionary<string, byte[]>();
        foreach (var part in current.Parts.Reverse())
        {
          var opened = batch.OpenPart(part.Number);
          using var file = opened.Stream;
          Check(file.Length == part.Bytes && part.Bytes <= cap && !part.ExceedsLimit,
            "actual finalized ZIP including directory fits cap");
          using var zip = new ZipArchive(file, ZipArchiveMode.Read);
          foreach (var entry in zip.Entries)
          {
            using var input = entry.Open();
            using var bytes = new MemoryStream();
            input.CopyTo(bytes);
            merged.Add(entry.FullName, bytes.ToArray());
          }
        }
        Check(merged.Count == original.Count && original.All(pair => merged[pair.Key].SequenceEqual(pair.Value)),
          "extract in reverse order, no duplicates or missing bytes");
        var retry = batch.OpenPart(1);
        using (var input = retry.Stream)
        using (var bytes = new MemoryStream())
        {
          input.CopyTo(bytes);
          Check(firstBytes.SequenceEqual(bytes.ToArray()), "retry serves identical prepared ZIP");
        }
        Check(!Directory.GetFiles(spool, "*.tmp").Any(), "no uncommitted archive remains");
        batch.Delete();
        Check(!Directory.Exists(spool), "explicit release cleans private spool");
      }
      finally { Directory.Delete(root, true); }
      return Task.CompletedTask;
    });
    await Test("multipart permits only an explicitly identified indivisible oversized ZIP", () =>
    {
      string root = Path.Combine(Path.GetTempPath(), "ptm-multipart-big-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(root);
      try
      {
        var members = new List<StreamingDownloadArchive.Member>();
        string large = Path.Combine(root, "large.gz");
        string small = Path.Combine(root, "small.gz");
        string compressible = Path.Combine(root, "compressible.txt");
        File.WriteAllBytes(large, new byte[1300000]); // .gz member is stored, not recompressed
        File.WriteAllBytes(small, new byte[10]);
        File.WriteAllBytes(compressible, new byte[1500000]);
        members.Add(new(large, "_data/large.gz", null));
        members.Add(new(small, "_data/small.gz", null));
        members.Add(new(compressible, "Monitor/compressible.txt", null));
        var batch = new MultipartZipBatch(Path.Combine(root, "spool"), "PTMagic-logs-test",
          Guid.NewGuid().ToString("N"), members, 1048576);
        var state = batch.Snapshot();
        while (!state.Complete) state = batch.PrepareNext(default);
        Check(state.Parts.Count(p => p.ExceedsLimit) == 1, "only truly oversized finalized part marked");
        var oversize = state.Parts.Single(p => p.ExceedsLimit);
        Check(oversize.Files == 1 && oversize.OversizedEntry == "_data/large.gz" && oversize.Bytes > 1048576,
          "oversized unsplittable file explicitly reported");
        Check(state.Parts.Where(p => !p.ExceedsLimit).All(p => p.Bytes <= 1048576),
          "compressible large source can fit compressed");
        Check(state.Parts.Sum(p => p.Files) == 3, "nothing silently omitted");
      }
      finally { Directory.Delete(root, true); }
      return Task.CompletedTask;
    });
    await Test("multipart handles finalized ZIP overhead and changing source lengths without exceeding cap", () =>
    {
      string root = Path.Combine(Path.GetTempPath(), "ptm-multipart-overhead-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(root);
      try
      {
        string a = Path.Combine(root, "a.gz");
        string b = Path.Combine(root, "b.gz");
        File.WriteAllBytes(a, new byte[500000]);
        File.WriteAllBytes(b, new byte[500000]);
        // Artificially low estimate from a captured append boundary on the first
        // file is still safe: actual finalized ZIP is measured, not guessed.
        var members = new[] { new StreamingDownloadArchive.Member(a, "_data/a.gz", null),
          new StreamingDownloadArchive.Member(b, "_data/b.gz", null) };
        var batch = new MultipartZipBatch(Path.Combine(root, "spool"), "PTMagic-data-test",
          Guid.NewGuid().ToString("N"), members, 1000000);
        var state = batch.Snapshot();
        while (!state.Complete) state = batch.PrepareNext(default);
        Check(state.Parts.Length == 2 && state.Parts.All(p => p.Bytes <= 1000000 && !p.ExceedsLimit),
          "ZIP headers count toward the cap");
      }
      finally { Directory.Delete(root, true); }
      return Task.CompletedTask;
    });
    await Test("multipart cancellation and source loss leave earlier parts intact and do not advance pending sources", () =>
    {
      string root = Path.Combine(Path.GetTempPath(), "ptm-multipart-retry-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(root);
      try
      {
        string a = Path.Combine(root, "a.gz");
        string b = Path.Combine(root, "b.gz");
        byte[] content = new byte[800000];
        File.WriteAllBytes(a, content); File.WriteAllBytes(b, content);
        var batch = new MultipartZipBatch(Path.Combine(root, "spool"), "PTMagic-data-test",
          Guid.NewGuid().ToString("N"),
          new[] { new StreamingDownloadArchive.Member(a, "_data/a.gz", null),
            new StreamingDownloadArchive.Member(b, "_data/b.gz", null) }, 1048576);
        var first = batch.PrepareNext(default);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        bool cancelled = false;
        try { batch.PrepareNext(cts.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && batch.Snapshot().ProcessedSources == first.ProcessedSources, "cancelled before next part");
        File.Delete(b);
        bool missing = false;
        try { batch.PrepareNext(default); }
        catch (IOException) { missing = true; }
        Check(missing && batch.Snapshot().Parts.Length == 1 &&
          batch.Snapshot().ProcessedSources == first.ProcessedSources, "missing source not silently skipped");
        File.WriteAllBytes(b, content);
        var final = batch.PrepareNext(default);
        Check(final.Complete && final.Parts.Length == 2, "retry after source restored");
      }
      finally { Directory.Delete(root, true); }
      return Task.CompletedTask;
    });
    await Test("automatic multipart exports allow a new request without releasing previously prepared downloads", async () =>
    {
      string root = Path.Combine(Path.GetTempPath(), "ptm-auto-multipart-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(root);
      using var service = new MultipartDownloadService(root, NullLogger<MultipartDownloadService>.Instance);
      try
      {
        string a = Path.Combine(root, "a.gz");
        string b = Path.Combine(root, "b.gz");
        File.WriteAllBytes(a, new byte[800000]);
        File.WriteAllBytes(b, new byte[800000]);
        var members = new[] { new StreamingDownloadArchive.Member(a, "_data/a.gz", null),
          new StreamingDownloadArchive.Member(b, "_data/b.gz", null) };
        var first = await service.StartAsync("session-a", "PTMagic-data", members, 1048576, default);
        first = await service.PrepareAsync("session-a", first.ExportId, default);
        Check(first.Parts.Length == 1 && !first.Complete, "first export still preparing");
        bool conflict = false;
        try { await service.StartAsync("session-b", "PTMagic-data", members, 1048576, default); }
        catch (InvalidOperationException) { conflict = true; }
        Check(conflict, "another session cannot supersede an active preparation");
        var second = await service.StartAsync("session-a", "PTMagic-data", members, 1048576, default);
        Check(second.ExportId != first.ExportId, "same-session new click supersedes old preparation without manual release");
        bool superseded = false;
        try { await service.PrepareAsync("session-a", first.ExportId, default); }
        catch (KeyNotFoundException) { superseded = true; }
        Check(superseded, "superseded batch cannot prepare further files");
        var original = await service.OpenAsync("session-a", first.ExportId, 1, default);
        using (original.Stream)
          Check(original.Stream.Length == first.Parts[0].Bytes, "earlier browser retry remains available");
        bool denied = false;
        try
        {
          var forbidden = await service.OpenAsync("session-b", first.ExportId, 1, default);
          forbidden.Stream.Dispose();
        }
        catch (KeyNotFoundException) { denied = true; }
        Check(denied, "retained parts remain session bound");
        while (!second.Complete) second = await service.PrepareAsync("session-a", second.ExportId, default);
        var third = await service.StartAsync("session-b", "PTMagic-logs", members, 1048576, default);
        Check(third.ExportId != second.ExportId, "completed export no longer blocks another session");
        var previous = await service.OpenAsync("session-a", second.ExportId, 2, default);
        using (previous.Stream)
          Check(previous.Stream.Length == second.Parts[1].Bytes, "completed export is retryable after another session starts");
        await service.ReleaseAsync("session-a", first.ExportId, default);
        bool released = false;
        try
        {
          var unavailable = await service.OpenAsync("session-a", first.ExportId, 1, default);
          unavailable.Stream.Dispose();
        }
        catch (KeyNotFoundException) { released = true; }
        Check(released, "explicit API release remains supported");
      }
      finally
      {
        await service.StopAsync(default);
        Check(!Directory.EnumerateDirectories(Path.Combine(root, "_download_exports")).Any(), "shutdown cleans retained spools");
        Directory.Delete(root, true);
      }
    });
    await Test("multipart transfer receipts distinguish unrequested, in-progress, completed and failed parts", async () =>
    {
      string root = Path.Combine(Path.GetTempPath(), "ptm-multipart-receipt-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(root);
      using var service = new MultipartDownloadService(root, NullLogger<MultipartDownloadService>.Instance);
      try
      {
        string a = Path.Combine(root, "a.gz");
        string b = Path.Combine(root, "b.gz");
        File.WriteAllBytes(a, new byte[800000]);
        File.WriteAllBytes(b, new byte[800000]);
        var members = new[] { new StreamingDownloadArchive.Member(a, "_data/a.gz", null),
          new StreamingDownloadArchive.Member(b, "_data/b.gz", null) };
        var state = await service.StartAsync("owner", "PTMagic-data", members, 1048576, default);
        state = await service.PrepareAsync("owner", state.ExportId, default);
        var pending = await service.TransferStatusAsync("owner", state.ExportId, 1, default);
        Check(pending.Status == "waiting" && pending.BytesSent == 0 &&
          pending.TotalBytes == state.Parts[0].Bytes, "no false receipt before actual attachment request");
        var opened = await service.OpenTrackedAsync("owner", state.ExportId, 1, default);
        using (opened.Stream)
        {
          var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
          var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
          var destination = new AsyncOnlyDestination();
          var transfer = opened.Receipt.SendAsync(opened.Stream, destination, async () =>
          {
            entered.TrySetResult(true);
            await finish.Task;
          }, default);
          try
          {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var inProgress = await service.TransferStatusAsync("owner", state.ExportId, 1, default);
            Check(inProgress.Status == "sending" && inProgress.BytesSent == inProgress.TotalBytes &&
              !transfer.IsCompleted, "no success until response completion");
            bool denied = false;
            try { await service.TransferStatusAsync("other", state.ExportId, 1, default); }
            catch (KeyNotFoundException) { denied = true; }
            Check(denied, "receipt is owner bound");
            bool duplicate = false;
            try
            {
              var unexpected = await service.OpenTrackedAsync("owner", state.ExportId, 1, default);
              unexpected.Stream.Dispose();
            }
            catch (InvalidOperationException) { duplicate = true; }
            Check(duplicate, "duplicate GET cannot replace live receipt");
          }
          finally { finish.TrySetResult(true); }
          await transfer.WaitAsync(TimeSpan.FromSeconds(10));
          var sent = await service.TransferStatusAsync("owner", state.ExportId, 1, default);
          Check(sent.Status == "sent" && sent.BytesSent == sent.TotalBytes &&
            sent.TotalBytes == destination.Bytes.Length, "full completed server transfer");
          destination.Bytes.Position = 0;
          using var archive = new ZipArchive(destination.Bytes, ZipArchiveMode.Read);
          Check(archive.Entries.Single().FullName == "_data/a.gz", "native ZIP bytes preserved");
        }
        state = await service.PrepareAsync("owner", state.ExportId, default);
        Check(state.Complete && state.Parts.Length == 2, "next independent part can be prepared");
        var second = await service.OpenTrackedAsync("owner", state.ExportId, 2, default);
        using (second.Stream)
        {
          bool failed = false;
          try
          {
            await second.Receipt.SendAsync(second.Stream,
              new AsyncOnlyDestination { BeforeWrite = _ => Task.FromException(new IOException("transfer failure")) },
              () => Task.CompletedTask, default);
          }
          catch (IOException) { failed = true; }
          Check(failed && (await service.TransferStatusAsync("owner", state.ExportId, 2, default)).Status == "failed",
            "failed stream cannot appear complete");
        }
        var retry = await service.OpenTrackedAsync("owner", state.ExportId, 2, default);
        using (retry.Stream)
        using (var cts = new CancellationTokenSource())
        {
          cts.Cancel();
          bool cancelled = false;
          try { await retry.Receipt.SendAsync(retry.Stream, new AsyncOnlyDestination(), () => Task.CompletedTask, cts.Token); }
          catch (OperationCanceledException) { cancelled = true; }
          Check(cancelled && (await service.TransferStatusAsync("owner", state.ExportId, 2, default)).Status == "failed",
            "cancelled transfer cannot appear complete");
        }
      }
      finally
      {
        await service.StopAsync(default);
        Directory.Delete(root, true);
      }
    });
    await Test("UTC windows accept seconds, milliseconds, strings and ISO timestamps", async () =>
    {
      DateTimeOffset recent = DateTimeOffset.UtcNow.AddHours(-1);
      string body = JsonSerializer.Serialize(new object[]
      {
        new { id = 1, soldDate = recent.ToUnixTimeSeconds() }, new { id = 2, soldDate = recent.ToUnixTimeMilliseconds() },
        new { id = 3, soldDate = recent.ToUnixTimeSeconds().ToString() }, new { id = 4, soldDate = recent.ToString("O") },
        new { id = 5, soldDate = recent.AddMonths(-7).ToUnixTimeSeconds() }, new { id = 6, soldDate = recent.AddDays(2).ToUnixTimeSeconds() }
      });
      foreach (string range in new[] { "7d", "1m", "3m", "6m" })
        Complete(await Run(c => Json(body), new PTDataExportSelection { IncludeSales = true, HistoryRange = range }), "sales", 4);
    });
    await Test("bounded buy history stops at cutoff instead of scanning older pages", async () =>
    {
      long recent = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
      long old = DateTimeOffset.UtcNow.AddMonths(-8).ToUnixTimeSeconds();
      string firstPage = JsonSerializer.Serialize(Enumerable.Range(1, 10).Select(i => new
      {
        id = i, firstBoughtDate = old, lastBoughtDate = i <= 6 ? recent - i : old,
        extraInfo = new[] { new { name = "DYN-BUYNOTE1", extraInfo = "CV: " + i } }
      }));
      var result = await Run(c => c.Page == 1 ? Json(firstPage) : throw new Exception("Must not request older pages"),
        new PTDataExportSelection { IncludeBuys = true, HistoryRange = "7d" });
      Complete(result, "buys", 6);
      Check(result.Calls.Count == 1, "stops at cutoff page");
      Check(result.Dataset("buys").GetProperty("termination").GetString() == "date_cutoff_reached", "cutoff termination");
      Check(result.Rows("buys")[0].GetProperty("extraInfo")[0].GetProperty("extraInfo").GetString() == "CV: 1",
        "original buy note retained");
    });
    await Test("bounded sales stop at cutoff; All available still traverses older pages", async () =>
    {
      long recent = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
      long old = DateTimeOffset.UtcNow.AddMonths(-8).ToUnixTimeSeconds();
      string firstPage = JsonSerializer.Serialize(Enumerable.Range(1, 10).Select(i => new
      {
        id = i, soldDate = i <= 7 ? recent - i : old
      }));
      var selection = new PTDataExportSelection { IncludeSales = true, HistoryRange = "7d" };
      var bounded = await Run(c => c.Page == 1 ? Json(firstPage) : throw new Exception("Must not request older pages"), selection);
      Complete(bounded, "sales", 7);
      Check(bounded.Calls.Count == 1, "sales stop at cutoff");
      var all = await Run(c => PTPage(c, 21), new PTDataExportSelection { IncludeSales = true, HistoryRange = "all" });
      Complete(all, "sales", 21);
      Check(all.Calls.Select(c => c.Page).SequenceEqual(new[] { 1, 2, 3 }), "all scans through terminal page");
    });
    await Test("unknown transaction dates remain available and explicitly unfilterable", async () =>
    {
      var result = await Run(c => Json("[{\"id\":1,\"extraInfo\":{\"CV\":42}}]"), new PTDataExportSelection { IncludeBuys = true, HistoryRange = "7d" });
      Check(result.Status("buys") == "partial" && result.Rows("buys").GetArrayLength() == 1, "unknown date preserved");
      Check(result.Dataset("buys").GetProperty("unfilterable_records_preserved").GetInt32() == 1, "cannot claim window filtering succeeded");
    });
    await Test("timeout and connection errors preserve prior records without private messages", async () =>
    {
      foreach (Exception error in new Exception[] { new TaskCanceledException("secret-token"), new HttpRequestException("secret-token") })
      {
        var result = await Run(c => c.Page == 1 ? PTPage(c, 30) : throw error);
        Check(result.Status("buys") == "partial" && result.Rows("buys").GetArrayLength() == 10, "partial retained");
        Check(result.Files.Values.All(x => !x.Contains("secret-token")), "exception details not exported");
      }
    });
    await Test("503 retries exactly the same page and succeeds on third request", async () =>
    {
      int calls = 0;
      var result = await Run(c => ++calls < 3 ? Json("", HttpStatusCode.ServiceUnavailable) : PTPage(c, 1));
      Complete(result, "buys", 1);
      Check(result.Calls.Count == 3 && result.Calls.All(c => c.Page == 1), "bounded same-page retries");
    });
    await Test("page and total-byte safety limits preserve valid partial ZIPs", async () =>
    {
      var capped = await Run(c => PTPage(c, 30), pageLimit: 1);
      Check(capped.Error("buys") == "history_page_limit" && capped.Rows("buys").GetArrayLength() == 10, "page cap");
      // Derive the limit from the actual serialized fixture. A fixed 4000-byte
      // budget fits two pages here and therefore cannot assert one retained page.
      using var firstPage = PTPage(new Call("buys", 1, 10, new Dictionary<string, string>()), 30);
      long firstPageBytes = (await firstPage.Content.ReadAsByteArrayAsync()).LongLength;
      var bytes = await Run(c => PTPage(c, 30), transferLimit: firstPageBytes + 1);
      Check(bytes.Error("buys") == "export_transfer_limit" && bytes.Rows("buys").GetArrayLength() == 10, "transfer cap");
      Check(bytes.Status("buys") == "partial" && bytes.Calls.Select(c => c.Page).SequenceEqual(new[] { 1, 2 }),
        "first page retained; cumulative byte limit stops the second request");
    });
    await Test("snapshots and optional funding remain byte-identical and unpaged", async () =>
    {
      string body = "{\"hashcode\":1,\"data\":[{\"market\":\"PAIR\",\"value\":1.250}]}";
      var result = await Run(c => Json(body), new PTDataExportSelection { IncludeCurrentPositions = true, IncludeFunding = true });
      Check(result.Calls.Count == 5 && result.Calls.All(c => c.Size == 0), "only original snapshot endpoints");
      Check(result.Manifest.GetProperty("dataset_count_available").GetInt32() == 5, "five available sources");
      Check(result.Files.Where(x => x.Key != "manifest.json").All(x => x.Value == body), "raw snapshots unchanged");
    });
    await Test("one failed history cannot suppress the other history or positions", async () =>
    {
      var result = await Run(c => c.Name == "buys" ? Json("private", HttpStatusCode.Forbidden)
        : c.Name == "sales" ? PTPage(c, 11) : Json("[]"),
        new PTDataExportSelection { IncludeBuys = true, IncludeSales = true, IncludeCurrentPositions = true });
      Check(result.Status("buys") == "failed" && result.Status("sales") == "available" && result.Status("dca") == "available", "per-source isolation");
      Check(result.Manifest.GetProperty("overall_status").GetString() == "partial", "overall status honest");
    });
    await Test("all selected histories produce identifiable schema-1.3 output", async () =>
    {
      var result = await Run(c => PTPage(c, 21), new PTDataExportSelection { IncludeBuys = true, IncludeSales = true });
      Complete(result, "buys", 21); Complete(result, "sales", 21);
      Check(result.Manifest.GetProperty("schema_version").GetString() == "1.3" && result.Manifest.GetProperty("exporter_version").GetString() == "pt-history-v3", "deployment fingerprint");
      Check(result.Manifest.GetProperty("overall_status").GetString() == "complete_for_selected_sources", "successful selected sources");
      Check(result.Status("funding") == "skipped", "optional funding remains opt-in");
    });
    await Test("cancellation aborts safely and releases the export gate", async () =>
    {
      using var cancel = new CancellationTokenSource();
      using var handler = new Handler { Respond = c => { cancel.Cancel(); return PTPage(c, 30); } };
      using var client = new HttpClient(handler);
      bool aborted = false;
      try { using var stream = await PTDataExportService.GenerateAsync(new PTMagicConfiguration(), new PTDataExportSelection { IncludeBuys = true }, client, cancel.Token); }
      catch (OperationCanceledException) { aborted = true; }
      Check(aborted, "caller cancellation must not become a normal partial download");
      Complete(await Run(c => PTPage(c, 1)), "buys", 1);
    });
    Console.WriteLine("PASS all " + tests + " PT history rebuild test groups");
  }
}
