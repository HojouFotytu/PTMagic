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
      Check(request.RequestUri.AbsolutePath.StartsWith("/instance/api/v2/data/"), "configured path prefix");
      var query = request.RequestUri.Query.TrimStart('?').Split('&').Where(x => x.Length > 0)
        .Select(x => x.Split('=', 2)).ToDictionary(x => WebUtility.UrlDecode(x[0]), x => WebUtility.UrlDecode(x[1]));
      Check(query["token"] == "secret-token+&?", "token URL encoding");
      var call = new Call(request.RequestUri.Segments.Last(),
        query.TryGetValue("page", out var p) ? int.Parse(p) : 0,
        query.TryGetValue("perPage", out var s) ? int.Parse(s) : 0, query);
      Calls.Add(call);
      return Task.FromResult(Respond(call));
    }
  }
  private sealed record Export(Dictionary<string, string> Files, List<Call> Calls)
  {
    public JsonElement Manifest => JsonDocument.Parse(Files["manifest.json"]).RootElement.Clone();
    public JsonElement Dataset(string name) => Manifest.GetProperty("datasets").EnumerateArray()
      .Single(x => x.GetProperty("name").GetString() == name);
    public JsonElement Rows(string name) => JsonDocument.Parse(Files[$"Trades/{name}.json"]).RootElement.GetProperty("data").Clone();
    public string Status(string name) => Dataset(name).GetProperty("status").GetString();
    public string Error(string name) => Dataset(name).GetProperty("error_code").GetString();
  }

  private static void Check(bool ok, string message)
  {
    if (!ok) throw new Exception(message);
  }
  private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
    new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
  private static string Rows(int start, int count, bool sameId = false) => JsonSerializer.Serialize(
    Enumerable.Range(start, count).Select(i => new
    {
      id = sameId ? 7 : i + 1, market = "PAIR" + i, soldDate = 1794680000L,
      lastBoughtDate = 1794680000L, extraInfo = new[] { new { name = "DYN-BUYNOTE1", currentValue = i + 0.125 } }
    }));
  private static async Task<Export> Run(Func<Call, HttpResponseMessage> respond,
    PTDataExportSelection selection = null, int limit = 32 * 1024 * 1024)
  {
    using var handler = new Handler { Respond = respond };
    using var client = new HttpClient(handler);
    using var stream = await PTDataExportService.GenerateAsync(new PTMagicConfiguration(),
      selection ?? new PTDataExportSelection { IncludeBuys = true }, client, CancellationToken.None, limit);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
    var files = new Dictionary<string, string>();
    foreach (var entry in archive.Entries)
    {
      using var reader = new StreamReader(entry.Open());
      files.Add(entry.FullName, await reader.ReadToEndAsync());
    }
    return new Export(files, handler.Calls);
  }
  private static async Task Test(string name, Func<Task> test)
  {
    await test();
    Console.WriteLine("PASS " + name);
  }

  public static async Task Main()
  {
    await Test("raw array, original fields, unmodified raw page", async () =>
    {
      string body = Rows(0, 3);
      var result = await Run(c => Json(body));
      Check(result.Status("buys") == "available" && result.Rows("buys").GetArrayLength() == 3, "three records");
      Check(result.Rows("buys")[2].GetProperty("extraInfo")[0].GetProperty("currentValue").GetDecimal() == 2.125m, "historical notes preserved");
      Check(result.Files["Trades/buys-pages/scan-00-page-00000.json"] == body, "raw JSON bytes preserved");
      Check(result.Calls.Count == 1 && result.Calls[0].Size == 10, "small final page");
      Check(result.Calls[0].Query["sort"] == "BOUGHTDATE" && result.Calls[0].Query["sortDirection"] == "DESCENDING", "explicit API sorting");
    });
    await Test("wrapped sales pages and original page hashes", async () =>
    {
      var result = await Run(c => Json("{\"hashcode\":" + c.Page + ",\"data\":" + Rows(c.Page * 10, c.Page == 0 ? 10 : 3) + "}"),
        new PTDataExportSelection { IncludeSales = true });
      Check(result.Rows("sales").GetArrayLength() == 13 && result.Calls.Count == 2, "two complete pages");
      Check(result.Calls.All(c => c.Name == "sales" && c.Query["sort"] == "SOLDDATE"), "sales endpoint and sort");
      Check(result.Status("sales") == "available" && result.Status("buys") == "skipped", "selected sources only");
    });
    await Test("nested data and content envelopes", async () =>
    {
      foreach (string body in new[] { "{\"data\":{\"data\":" + Rows(0, 2) + "}}", "{\"content\":" + Rows(0, 2) + "}" })
        Check((await Run(c => Json(body))).Rows("buys").GetArrayLength() == 2, "supported envelope");
    });
    await Test("server returns more than requested without losing history", async () =>
    {
      var result = await Run(c => Json(c.Page == 0 ? Rows(0, 12) : "[]"));
      Check(result.Status("buys") == "available" && result.Rows("buys").GetArrayLength() == 12, "larger page retained");
    });
    await Test("same id with different trades is not a fatal duplicate", async () =>
    {
      var result = await Run(c => Json(Rows(c.Page * 10, c.Page < 2 ? 10 : 5, true)));
      Check(result.Status("buys") == "available" && result.Rows("buys").GetArrayLength() == 25, "all distinct records retained");
    });
    await Test("repeated page retains completed JSON with precise error", async () =>
    {
      var result = await Run(c => Json(Rows(0, 10)));
      Check(result.Status("buys") == "partial" && result.Error("buys") == "history_page_repeated", "repeat classification");
      Check(result.Rows("buys").GetArrayLength() == 10 && result.Calls.Count == 2, "retained history and bounded requests");
    });
    await Test("malformed later page cannot erase preceding trades", async () =>
    {
      var result = await Run(c => Json(c.Page == 0 ? Rows(0, 10) : "not json"));
      Check(result.Rows("buys").GetArrayLength() == 10 && result.Error("buys") == "invalid_json", "partial history retained");
    });
    await Test("unexpected envelope still exports raw API JSON", async () =>
    {
      string body = "{\"unexpected\":[{\"id\":1}]}";
      var result = await Run(c => Json(body));
      Check(result.Status("buys") == "failed" && result.Error("buys") == "history_response_shape", "not called oversized");
      Check(result.Files["Trades/buys-pages/scan-00-page-00000.json"] == body, "raw payload available for diagnosis");
    });
    await Test("oversize reduces 10 to 5 to 2 to 1 and restarts without gaps", async () =>
    {
      var result = await Run(c => c.Size > 1 && c.Page == 1
        ? Json(new string('x', 3000)) : Json(Rows(c.Page * c.Size, Math.Max(0, Math.Min(c.Size, 12 - c.Page * c.Size)))), limit: 2048);
      Check(result.Status("buys") == "available" && result.Rows("buys").GetArrayLength() == 12, "complete after resizing");
      Check(result.Dataset("buys").GetProperty("page_size_reductions").GetInt32() == 3, "all smaller sizes tried");
      Check(result.Rows("buys").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).Distinct().Count() == 12, "no duplicated or skipped trades");
      foreach (int size in new[] { 10, 5, 2, 1 }) Check(result.Calls.Any(c => c.Size == size && c.Page == 0), "restart each size at page zero");
    });
    await Test("single oversized record is specifically reported", async () =>
    {
      var result = await Run(c => Json(new string('x', 3000)), limit: 100);
      Check(result.Error("buys") == "response_size_limit" && result.Calls.Last().Size == 1, "one-record limit reached");
    });
    await Test("date filtering accepts seconds, milliseconds, strings and ISO dates", async () =>
    {
      DateTimeOffset now = DateTimeOffset.UtcNow.AddHours(-1);
      long recent = now.ToUnixTimeSeconds(), old = now.AddDays(-20).ToUnixTimeSeconds();
      string body = JsonSerializer.Serialize(new object[]
      {
        new { id = 1, soldDate = recent }, new { id = 2, soldDate = recent * 1000 },
        new { id = 3, soldDate = recent.ToString() }, new { id = 4, soldDate = now.ToString("O") },
        new { id = 5, soldDate = old }
      });
      var result = await Run(c => Json(body), new PTDataExportSelection { IncludeSales = true, HistoryRange = "7d" });
      Check(result.Status("sales") == "available" && result.Rows("sales").GetArrayLength() == 4, "four recent records");
    });
    await Test("unknown dates preserved and flagged rather than killing export", async () =>
    {
      var result = await Run(c => Json("[{\"id\":1,\"extraInfo\":{\"keep\":true}}]"),
        new PTDataExportSelection { IncludeBuys = true, HistoryRange = "1m" });
      Check(result.Status("buys") == "partial" && result.Rows("buys").GetArrayLength() == 1, "undated row retained");
      Check(result.Dataset("buys").GetProperty("unfilterable_records_preserved").GetInt32() == 1, "date warning");
    });
    await Test("HTTP failures do not expose credentials or URLs", async () =>
    {
      var result = await Run(c => Json("secret-token+&?", HttpStatusCode.Forbidden));
      Check(result.Error("buys") == "http_403", "HTTP error classification");
      Check(result.Files.Values.All(s => !s.Contains("secret-token") && !s.Contains("pt.invalid")), "no connection secrets exported");
    });
    await Test("current positions and funding are separate unchanged snapshots", async () =>
    {
      var result = await Run(c => Json("[{\"market\":\"PAIR\",\"value\":1.25}]"),
        new PTDataExportSelection { IncludeCurrentPositions = true, IncludeFunding = true });
      Check(result.Calls.Count == 5 && result.Calls.All(c => c.Size == 0), "snapshot endpoints only");
      Check(result.Manifest.GetProperty("dataset_count_available").GetInt32() == 5, "all snapshots retained");
    });
    await Test("overlapping pages deduplicate exact rows and flag uncertainty", async () =>
    {
      var result = await Run(c => Json(c.Page == 0 ? Rows(0, 10) : Rows(9, 3)));
      Check(result.Status("buys") == "partial" && result.Rows("buys").GetArrayLength() == 12, "overlap handled");
      Check(result.Dataset("buys").GetProperty("duplicate_records").GetInt32() == 1, "overlap reported");
    });
    await Test("request timeout preserves already downloaded records", async () =>
    {
      var result = await Run(c => c.Page == 0 ? Json(Rows(0, 10)) : throw new TaskCanceledException("private URL"));
      Check(result.Status("buys") == "partial" && result.Rows("buys").GetArrayLength() == 10, "timeout keeps preceding page");
      Check(result.Error("buys") == "request_or_export_timeout", "safe timeout code");
    });
    await Test("both histories can export successfully in one ZIP", async () =>
    {
      var result = await Run(c => Json("{\"data\":" + Rows(0, 4) + "}"),
        new PTDataExportSelection { IncludeBuys = true, IncludeSales = true });
      Check(result.Rows("buys").GetArrayLength() == 4 && result.Rows("sales").GetArrayLength() == 4, "both JSON histories");
      Check(result.Manifest.GetProperty("overall_status").GetString() == "complete_for_selected_sources", "complete manifest");
    });
    await Test("transient 503 succeeds on the third request", async () =>
    {
      int count = 0;
      var result = await Run(c => ++count < 3 ? Json("", HttpStatusCode.ServiceUnavailable) : Json(Rows(0, 1)));
      Check(result.Status("buys") == "available" && count == 3, "bounded HTTP retries");
    });
    await Test("user cancellation aborts the export", async () =>
    {
      using var handler = new Handler { Respond = c => Json(Rows(0, 1)) };
      using var client = new HttpClient(handler);
      using var cancel = new CancellationTokenSource();
      cancel.Cancel();
      bool cancelled = false;
      try { using var result = await PTDataExportService.GenerateAsync(new PTMagicConfiguration(),
        new PTDataExportSelection { IncludeBuys = true }, client, cancel.Token); }
      catch (OperationCanceledException) { cancelled = true; }
      Check(cancelled && handler.Calls.Count == 0, "no API calls after cancellation");
    });
    await Test("larger history streams through all pages", async () =>
    {
      var result = await Run(c => Json(c.Page < 40 ? Rows(c.Page * 10, 10) : "[]"));
      Check(result.Status("buys") == "available" && result.Rows("buys").GetArrayLength() == 400, "all 400 trades");
      Check(result.Calls.Count == 41, "empty final page ends paging");
    });
    Console.WriteLine("PASS all 20 PT Data export regression tests");
  }
}
