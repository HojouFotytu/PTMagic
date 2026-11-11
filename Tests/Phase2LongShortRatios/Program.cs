using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Core.MarketAnalyzer.MarketData;
using Newtonsoft.Json.Linq;

int passed = 0;
void Check(bool condition, string name)
{
  if (!condition) throw new InvalidOperationException("FAIL: " + name);
  passed++;
  Console.WriteLine("PASS: " + name);
}
void Throws<T>(Action action, string name) where T : Exception
{
  try { action(); }
  catch (T) { passed++; Console.WriteLine("PASS: " + name); return; }
  throw new InvalidOperationException("FAIL: " + name);
}

var servicePin = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
var serviceLog = new Core.Helper.LogHelper(Microsoft.Extensions.Logging.Abstractions.NullLogger<Core.Helper.LogHelper>.Instance);
FuturesLongShortRatioSeriesRequest Request(string symbol) => new()
{
  Source = FuturesLongShortRatioSource.GlobalAccount,
  HorizonMinutes = 60,
  Symbols = new[] { symbol }
};
Task<IReadOnlyList<FuturesLongShortRatioObservation>> ServiceRows(
  FuturesLongShortRatioSource source, string symbol, DateTimeOffset from,
  DateTimeOffset to, int limit, System.Threading.CancellationToken ct)
{
  long end = to.ToUnixTimeMilliseconds();
  var rows = Enumerable.Range(1, 12).Select(index => new FuturesLongShortRatioObservation
  {
    Symbol = symbol, Source = source,
    TimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(end - (12 - index) * 300_000L),
    Period = TimeSpan.FromMinutes(5), LongShortRatio = 1.5m,
    LongSideShare = .6m, ShortSideShare = .4m
  }).Where(row => row.TimestampUtc >= from && row.TimestampUtc <= to).Take(limit).ToArray();
  return Task.FromResult<IReadOnlyList<FuturesLongShortRatioObservation>>(rows);
}

string serviceCacheRoot = Path.Combine(Path.GetTempPath(), "PTM_RatioServiceRestart_" + Guid.NewGuid().ToString("N"));
try
{
  int firstRequests = 0;
  var firstService = new FuturesLongShortRatioDataService(serviceCacheRoot);
  var firstReport = await firstService.RefreshAsync(new[] { Request("BTCUSDT") }, servicePin, 2,
    (source, symbol, from, to, limit, ct) =>
    {
      firstRequests++;
      return ServiceRows(source, symbol, from, to, limit, ct);
    }, serviceLog);
  Check(firstRequests == 1 && firstReport.CompleteSeries == 1,
    "first ratio service persists a complete series");

  int restartRequests = 0;
  var restartedService = new FuturesLongShortRatioDataService(serviceCacheRoot);
  var restartReport = await restartedService.RefreshAsync(new[] { Request("BTCUSDT") }, servicePin, 2,
    (source, symbol, from, to, limit, ct) =>
    {
      restartRequests++;
      return ServiceRows(source, symbol, from, to, limit, ct);
    }, serviceLog);
  Check(restartReport.CompleteSeries == 1 && restartRequests == 0,
    "new ratio service restores complete coverage without another provider request");
  Check(restartedService.Snapshot(FuturesLongShortRatioSource.GlobalAccount, "BTCUSDT", 60, servicePin).Count == 12,
    "restarted ratio service exposes the same twelve observations");

  string corruptPath = new FuturesLongShortRatioDiskCache(serviceCacheRoot)
    .CachePath(FuturesLongShortRatioSource.GlobalAccount, "ETHUSDT");
  Directory.CreateDirectory(Path.GetDirectoryName(corruptPath)!);
  File.WriteAllText(corruptPath, "corrupt");
  int repairRequests = 0;
  var repairService = new FuturesLongShortRatioDataService(serviceCacheRoot);
  var repairReport = await repairService.RefreshAsync(new[] { Request("ETHUSDT") }, servicePin, 1,
    (source, symbol, from, to, limit, ct) =>
    {
      repairRequests++;
      return ServiceRows(source, symbol, from, to, limit, ct);
    }, serviceLog);
  var repairedCache = new FuturesLongShortRatioDiskCache(serviceCacheRoot)
    .TryRestore(FuturesLongShortRatioSource.GlobalAccount, "ETHUSDT", servicePin, serviceLog);
  Check(repairRequests == 1 && repairReport.Requests == 1 && repairReport.CompleteSeries == 1 &&
    File.Exists(corruptPath) && repairedCache.Rows.Count == 12 &&
    repairedCache.CompleteIntervals.Count > 0,
    "corrupt ratio cache is replaced with valid observations and coverage after one bounded request");
}
finally
{
  if (Directory.Exists(serviceCacheRoot)) Directory.Delete(serviceCacheRoot, recursive: true);
}

string partialCacheRoot = Path.Combine(Path.GetTempPath(), "PTM_RatioPartialRestart_" + Guid.NewGuid().ToString("N"));
const int partialHorizon = FuturesLongShortRatio.MaximumHistoryMinutes;
var partialRequest = new FuturesLongShortRatioSeriesRequest
{
  Source = FuturesLongShortRatioSource.GlobalAccount,
  HorizonMinutes = partialHorizon,
  Symbols = new[] { "XRPUSDT" }
};
FuturesLongShortRatioObservation RatioAt(string symbol, DateTimeOffset timestamp) => new()
{
  Symbol = symbol, Source = FuturesLongShortRatioSource.GlobalAccount,
  TimestampUtc = timestamp, Period = TimeSpan.FromMinutes(5),
  LongShortRatio = 1.5m, LongSideShare = .6m, ShortSideShare = .4m
};
try
{
  var firstPage = Enumerable.Range(0, 500)
    .Select(index => RatioAt("XRPUSDT", servicePin.AddMinutes(-partialHorizon + 5 + index * 5)))
    .ToArray();
  DateTimeOffset firstLast = firstPage[^1].TimestampUtc;
  var first = new FuturesLongShortRatioDataService(partialCacheRoot);
  var firstReport = await first.RefreshAsync(new[] { partialRequest }, servicePin, 1,
    (source, symbol, from, to, limit, ct) =>
      Task.FromResult<IReadOnlyList<FuturesLongShortRatioObservation>>(firstPage), serviceLog);
  Check(firstReport.Requests == 1 && firstReport.CompleteSeries == 0,
    "first ratio service persists one bounded partial page");

  DateTimeOffset secondFrom = default;
  var secondPage = Enumerable.Range(1, 500)
    .Select(index => RatioAt("XRPUSDT", firstLast.AddMinutes(index * 5)))
    .ToArray();
  DateTimeOffset secondLast = secondPage[^1].TimestampUtc;
  var second = new FuturesLongShortRatioDataService(partialCacheRoot);
  var secondReport = await second.RefreshAsync(new[] { partialRequest }, servicePin, 1,
    (source, symbol, from, to, limit, ct) =>
    {
      secondFrom = from;
      return Task.FromResult<IReadOnlyList<FuturesLongShortRatioObservation>>(secondPage);
    }, serviceLog);
  Check(secondReport.Requests == 1 && secondReport.CompleteSeries == 0 &&
    secondFrom.ToUnixTimeMilliseconds() == firstLast.ToUnixTimeMilliseconds() + 1,
    "restarted ratio service resumes immediately after the first covered range");

  DateTimeOffset thirdFrom = default;
  var third = new FuturesLongShortRatioDataService(partialCacheRoot);
  var thirdReport = await third.RefreshAsync(new[] { partialRequest }, servicePin, 1,
    (source, symbol, from, to, limit, ct) =>
    {
      thirdFrom = from;
      return Task.FromResult<IReadOnlyList<FuturesLongShortRatioObservation>>(
        Array.Empty<FuturesLongShortRatioObservation>());
    }, serviceLog);
  Check(thirdReport.Requests == 1 &&
    thirdFrom.ToUnixTimeMilliseconds() == secondLast.ToUnixTimeMilliseconds() + 1,
    "second partial ratio page is persisted immediately and survives another restart");
}
finally
{
  if (Directory.Exists(partialCacheRoot)) Directory.Delete(partialCacheRoot, recursive: true);
}

string cacheRoot = Path.Combine(Path.GetTempPath(), "PTM_RatioCache_" + Guid.NewGuid().ToString("N"));
try
{
  var disk = new FuturesLongShortRatioDiskCache(cacheRoot);
  var cacheRows = new[]
  {
    new FuturesLongShortRatioObservation
    {
      Symbol = "BTCUSDT", Source = FuturesLongShortRatioSource.GlobalAccount,
      TimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_100_000),
      Period = TimeSpan.FromMinutes(5), LongShortRatio = 1.5m,
      LongSideShare = .6m, ShortSideShare = .4m
    }
  };
  long startCoverage = 1_700_000_000_000;
  long endCoverage = 1_700_000_400_000;
  Check(disk.TrySave(FuturesLongShortRatioSource.GlobalAccount, "BTCUSDT",
    cacheRows, new[] { (startCoverage, endCoverage) },
    DateTimeOffset.FromUnixTimeMilliseconds(endCoverage), null), "ratio cache saves raw rows and verified coverage");
  var restored = disk.TryRestore(FuturesLongShortRatioSource.GlobalAccount, "BTCUSDT",
    DateTimeOffset.FromUnixTimeMilliseconds(endCoverage), null);
  Check(restored.Rows.Count == 1 && restored.CompleteIntervals.Count == 1 &&
    restored.Rows[0].LongShortRatio == 1.5m, "ratio cache restores after service restart");
  Check(disk.TryRestore(FuturesLongShortRatioSource.TopTraderAccount, "BTCUSDT",
    DateTimeOffset.FromUnixTimeMilliseconds(endCoverage), null).Rows.Count == 0,
    "ratio cache isolates endpoint sources");
  string corruptPath = disk.CachePath(FuturesLongShortRatioSource.GlobalAccount, "ETHUSDT");
  Directory.CreateDirectory(Path.GetDirectoryName(corruptPath)!);
  File.WriteAllText(corruptPath, "corrupt");
  Check(disk.TryRestore(FuturesLongShortRatioSource.GlobalAccount, "ETHUSDT",
    DateTimeOffset.FromUnixTimeMilliseconds(endCoverage), null).Rows.Count == 0 &&
    !File.Exists(corruptPath), "corrupt ratio cache is discarded fail-open");
}
finally
{
  if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true);
}

var sources = new[]
{
  FuturesLongShortRatioSource.GlobalAccount,
  FuturesLongShortRatioSource.TopTraderAccount,
  FuturesLongShortRatioSource.TopTraderPosition
};
var names = sources.Select(FuturesLongShortRatio.MetricName).ToArray();
Check(names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3, "three metric identities are distinct");
Check(!FuturesLongShortRatio.RequiresApiKey(FuturesLongShortRatioSource.GlobalAccount), "global account endpoint is public");
Check(FuturesLongShortRatio.RequiresApiKey(FuturesLongShortRatioSource.TopTraderAccount), "top-trader account requires API key");
Check(FuturesLongShortRatio.RequiresApiKey(FuturesLongShortRatioSource.TopTraderPosition), "top-trader position requires API key");
Check(FuturesLongShortRatio.EndpointPath(FuturesLongShortRatioSource.GlobalAccount).EndsWith("globalLongShortAccountRatio"), "global account endpoint path");
Check(FuturesLongShortRatio.EndpointPath(FuturesLongShortRatioSource.TopTraderAccount).EndsWith("topLongShortAccountRatio"), "top-trader account endpoint path");
Check(FuturesLongShortRatio.EndpointPath(FuturesLongShortRatioSource.TopTraderPosition).EndsWith("topLongShortPositionRatio"), "top-trader position endpoint path");

string url = BinanceFuturesLongShortRatioClient.HistoryUrl(
  FuturesLongShortRatioSource.GlobalAccount, "BTCUSDT",
  DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000),
  DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_300_000), 500);
Check(url.Contains("period=5m") && url.Contains("limit=500") && url.Contains("startTime=") && url.Contains("endTime="),
  "history URL pins 5m period and bounded time window");
Throws<ArgumentOutOfRangeException>(() => BinanceFuturesLongShortRatioClient.HistoryUrl(
  FuturesLongShortRatioSource.GlobalAccount, "BTCUSDT", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 501),
  "page size over 500 rejected");

const string unicodeSymbol = "我踏马来了USDT";
string unicodeCacheRoot = Path.Combine(Path.GetTempPath(), "PTM_RatioUnicode_" + Guid.NewGuid().ToString("N"));
try
{
  var unicodeService = new FuturesLongShortRatioDataService(unicodeCacheRoot);
  int unicodeRequests = 0;
  var unicodeReport = await unicodeService.RefreshAsync(new[] { Request(unicodeSymbol) }, servicePin, 2,
    (source, symbol, from, to, limit, ct) =>
    {
      unicodeRequests++;
      return ServiceRows(source, symbol, from, to, limit, ct);
    }, serviceLog);
  Check(unicodeRequests == 1 && unicodeReport.CompleteSeries == 1 &&
    unicodeService.Snapshot(FuturesLongShortRatioSource.GlobalAccount, unicodeSymbol, 60, servicePin).Count == 12,
    "ratio service accepts Unicode Binance symbol and stores its observations");
  var unicodeRestored = new FuturesLongShortRatioDiskCache(unicodeCacheRoot)
    .TryRestore(FuturesLongShortRatioSource.GlobalAccount, unicodeSymbol, servicePin, serviceLog);
  Check(unicodeRestored.Rows.Count == 12 && unicodeRestored.Rows.All(row => row.Symbol == unicodeSymbol),
    "ratio disk cache restores Unicode symbol identity");
  string unicodeUrl = BinanceFuturesLongShortRatioClient.HistoryUrl(
    FuturesLongShortRatioSource.GlobalAccount, unicodeSymbol,
    servicePin.AddMinutes(-60), servicePin, 500);
  Check(unicodeUrl.Contains(Uri.EscapeDataString(unicodeSymbol), StringComparison.Ordinal),
    "ratio request URL encodes Unicode Binance symbols");
}
finally
{
  if (Directory.Exists(unicodeCacheRoot)) Directory.Delete(unicodeCacheRoot, recursive: true);
}

string json = """
[
 {"symbol":"BTCUSDT","longShortRatio":"1.5","longAccount":"0.60","shortAccount":"0.40","timestamp":1700000100000},
 {"symbol":"BTCUSDT","longShortRatio":"0.5","longAccount":"0.40","shortAccount":"0.60","timestamp":1700000400000}
]
""";
var mapped = FuturesLongShortRatio.MapHistory(JArray.Parse(json),
  FuturesLongShortRatioSource.GlobalAccount, "BTCUSDT",
  DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_400_000));
Check(mapped.Count == 2, "maps two ordered 5m observations");
Check(mapped[0].LongShortRatio == 1.5m && mapped[0].LongSideShare == 0.60m &&
  mapped[0].ShortSideShare == 0.40m, "preserves ratio and source components independently");
Check(mapped[0].Period == TimeSpan.FromMinutes(5), "records native 5m period");
Check(FuturesLongShortRatio.MapHistory(JArray.Parse(json),
  FuturesLongShortRatioSource.TopTraderPosition, "BTCUSDT",
  DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_300_000))[0].Source ==
  FuturesLongShortRatioSource.TopTraderPosition, "same provider fields map with distinct position source identity");

string mismatch = json.Replace("BTCUSDT","ETHUSDT");
Throws<InvalidDataException>(() => FuturesLongShortRatio.MapHistory(JArray.Parse(mismatch),
  FuturesLongShortRatioSource.GlobalAccount, "BTCUSDT",
  DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_300_000)), "symbol mismatch rejected");
string invalidShare = json.Replace("0.60","1.20");
Throws<InvalidDataException>(() => FuturesLongShortRatio.MapHistory(JArray.Parse(invalidShare),
  FuturesLongShortRatioSource.GlobalAccount, "BTCUSDT",
  DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_300_000)), "component share outside 0..1 rejected");
string invalidRatio = json.Replace("1.5","-1.5");
Throws<InvalidDataException>(() => FuturesLongShortRatio.MapHistory(JArray.Parse(invalidRatio),
  FuturesLongShortRatioSource.GlobalAccount, "BTCUSDT",
  DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_300_000)), "negative ratio rejected");
string duplicateConflict = """
[
 {"symbol":"BTCUSDT","longShortRatio":"1.5","longAccount":"0.60","shortAccount":"0.40","timestamp":1700000100000},
 {"symbol":"BTCUSDT","longShortRatio":"1.6","longAccount":"0.60","shortAccount":"0.40","timestamp":1700000100000}
]
""";
Throws<InvalidDataException>(() => FuturesLongShortRatio.MapHistory(JArray.Parse(duplicateConflict),
  FuturesLongShortRatioSource.GlobalAccount, "BTCUSDT",
  DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_300_000)), "conflicting duplicate timestamp rejected");

var samples = new List<FuturesLongShortRatioObservation>();
long start = 1_700_000_100_000;
for (int i = 1; i <= 12; i++)
{
  samples.Add(new FuturesLongShortRatioObservation
  {
    Symbol = "BTCUSDT", Source = FuturesLongShortRatioSource.GlobalAccount,
    TimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(start + i * 300_000),
    Period = TimeSpan.FromMinutes(5), LongShortRatio = 1m,
    LongSideShare = 0.5m, ShortSideShare = 0.5m
  });
}
DateTimeOffset pin = DateTimeOffset.FromUnixTimeMilliseconds(start + 12 * 300_000);
var neutral = FuturesLongShortRatioFeatureEngine.CalculateWindowRatio(
  samples, FuturesLongShortRatioSource.GlobalAccount, "BTCUSDT", 60, pin);
Check(neutral.IsValid && Math.Abs(neutral.Value!.Value - 1.0) < 1e-12, "neutral 1.0 ratio remains neutral over a 60m window");
Check(neutral.SampleCount == 12 && neutral.CoveragePercent == 100.0, "window records exact sample count and coverage");

var bullish = samples.Select(x => new FuturesLongShortRatioObservation
{
  Symbol = x.Symbol, Source = x.Source, TimestampUtc = x.TimestampUtc,
  Period = x.Period, LongShortRatio = 3m, LongSideShare = 0.75m, ShortSideShare = 0.25m
}).ToArray();
var bullishResult = FuturesLongShortRatioFeatureEngine.CalculateWindowRatio(
  bullish, FuturesLongShortRatioSource.GlobalAccount, "BTCUSDT", 60, pin);
Check(bullishResult.IsValid && Math.Abs(bullishResult.Value!.Value - 3.0) < 1e-12,
  "constant bullish ratio remains unchanged by share aggregation");

var bearish = samples.Select(x => new FuturesLongShortRatioObservation
{
  Symbol = x.Symbol, Source = x.Source, TimestampUtc = x.TimestampUtc,
  Period = x.Period, LongShortRatio = 1m / 3m, LongSideShare = 0.25m, ShortSideShare = 0.75m
}).ToArray();
var bearishResult = FuturesLongShortRatioFeatureEngine.CalculateWindowRatio(
  bearish, FuturesLongShortRatioSource.GlobalAccount, "BTCUSDT", 60, pin);
Check(bearishResult.IsValid && Math.Abs(bearishResult.Value!.Value - 1.0 / 3.0) < 1e-12,
  "constant bearish ratio remains inverse-symmetric");

var incomplete = FuturesLongShortRatioFeatureEngine.CalculateWindowRatio(
  samples.Take(11), FuturesLongShortRatioSource.GlobalAccount, "BTCUSDT", 60, pin);
Check(!incomplete.IsValid && incomplete.SampleCount == 11 && incomplete.CoveragePercent.HasValue,
  "missing 5m observation makes full window unavailable");
Throws<InvalidDataException>(() => FuturesLongShortRatio.Validate(
  new FuturesLongShortRatioObservation { Symbol = "ETHUSDT", Source = FuturesLongShortRatioSource.GlobalAccount,
    TimestampUtc = pin, Period = TimeSpan.FromMinutes(5), LongShortRatio = 1m,
    LongSideShare = .5m, ShortSideShare = .5m },
  FuturesLongShortRatioSource.GlobalAccount, "BTCUSDT"), "foreign symbol observation rejected");

Check(FuturesLongShortRatioFeatureEngine.IsMetric(MarketTrendMetrics.GlobalLongShortAccountRatio) &&
  FuturesLongShortRatioFeatureEngine.IsMetric(MarketTrendMetrics.TopTraderLongShortAccountRatio) &&
  FuturesLongShortRatioFeatureEngine.IsMetric(MarketTrendMetrics.TopTraderLongShortPositionRatio),
  "all three ratio metrics recognized");
Check(!FuturesLongShortRatioFeatureEngine.IsMetric(MarketTrendMetrics.FundingRate),
  "unrelated funding metric not classified as ratio");

Console.WriteLine("PASS: " + passed + " Phase2LongShortRatios checks.");
