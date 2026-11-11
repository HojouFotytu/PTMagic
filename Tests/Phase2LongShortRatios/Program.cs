using System;
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

string json = """
[
 {"symbol":"BTCUSDT","longShortRatio":"1.5","longAccount":"0.60","shortAccount":"0.40","timestamp":1700000000000},
 {"symbol":"BTCUSDT","longShortRatio":"0.5","longAccount":"0.40","shortAccount":"0.60","timestamp":1700000300000}
]
""";
var mapped = FuturesLongShortRatio.MapHistory(JArray.Parse(json),
  FuturesLongShortRatioSource.GlobalAccount, "BTCUSDT",
  DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_300_000));
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
string conflict = json.Replace("1.5","1.6").Replace("0.60","0.60");
string duplicateConflict = """
[
 {"symbol":"BTCUSDT","longShortRatio":"1.5","longAccount":"0.60","shortAccount":"0.40","timestamp":1700000000000},
 {"symbol":"BTCUSDT","longShortRatio":"1.6","longAccount":"0.60","shortAccount":"0.40","timestamp":1700000000000}
]
""";
Throws<InvalidDataException>(() => FuturesLongShortRatio.MapHistory(JArray.Parse(duplicateConflict),
  FuturesLongShortRatioSource.GlobalAccount, "BTCUSDT",
  DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_300_000)), "conflicting duplicate timestamp rejected");

var samples = new List<FuturesLongShortRatioObservation>();
long start = 1_700_000_000_000;
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
