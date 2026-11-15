using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Core.Main;
using Monitor._Internal;

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
    await Test("buy filtering uses transaction date, not the position's first buy", async () =>
    {
      long recent = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
      long old = DateTimeOffset.UtcNow.AddMonths(-8).ToUnixTimeSeconds();
      string oldPage = JsonSerializer.Serialize(Enumerable.Range(1, 10).Select(i => new { id = i, firstBoughtDate = old, lastBoughtDate = old }));
      string laterDca = JsonSerializer.Serialize(new[] { new { id = 11, firstBoughtDate = old, lastBoughtDate = recent } });
      var result = await Run(c => Json(c.Page == 1 ? oldPage : laterDca), new PTDataExportSelection { IncludeBuys = true, HistoryRange = "7d" });
      Complete(result, "buys", 1);
      Check(result.Rows("buys")[0].GetProperty("id").GetInt32() == 11, "no unsafe early date cutoff");
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
      var bytes = await Run(c => PTPage(c, 30), transferLimit: 4000);
      Check(bytes.Error("buys") == "export_transfer_limit" && bytes.Rows("buys").GetArrayLength() == 10, "transfer cap");
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
