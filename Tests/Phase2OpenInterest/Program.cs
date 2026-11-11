using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Core.MarketAnalyzer.MarketData;
using Newtonsoft.Json.Linq;

internal static class Program
{
  private static readonly DateTimeOffset Pin =
    new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
  private static int _passed;

  private static int Main()
  {
    try
    {
      TestCurrentSnapshot();
      TestCurrentTimestampBoundary();
      TestHistoryNativeAndValueFields();
      TestNoAutomaticNotionalConversion();
      TestMissingHistoricalFields();
      TestWrongSymbol();
      TestInvalidQuantity();
      TestMissingTradeBucket();
      TestNoFutureLeakage();
      TestDuplicateBuckets();
      TestExactChange();
      TestIndependentValueChange();
      TestNoReferenceFallback();
      TestZeroReference();
      TestUnsupportedHorizon();
      TestMixedScopeOrPeriod();
      TestPinnedRequestAndBounds();
      TestSupportedPeriodsAndInputGuard();
      TestUnicodeFuturesSymbols();
      Console.WriteLine("PASS: Phase 2 open-interest raw foundation, " + _passed + " offline case groups.");
      Console.WriteLine("LIMIT: No live Binance requests, no ordinary-raid integration, no PT/Monitor acceptance.");
      return 0;
    }
    catch (Exception exception)
    {
      Console.Error.WriteLine("FAIL: Phase 2 open-interest raw foundation: " + exception);
      return 1;
    }
  }

  private static JObject Current(string symbol, string value, long? ms = null) => new JObject
  {
    ["symbol"] = symbol, ["openInterest"] = value,
    ["time"] = ms ?? Pin.AddSeconds(-5).ToUnixTimeMilliseconds()
  };

  private static JObject Historical(string symbol, decimal amount, decimal value, DateTimeOffset timestamp) =>
    new JObject
    {
      ["symbol"] = symbol,
      ["sumOpenInterest"] = amount.ToString(CultureInfo.InvariantCulture),
      ["sumOpenInterestValue"] = value.ToString(CultureInfo.InvariantCulture),
      ["timestamp"] = timestamp.ToUnixTimeMilliseconds()
    };

  private static JArray Array(params JObject[] rows) => new JArray(rows);

  private static FuturesOpenInterestObservation[] Parse(params JObject[] rows) =>
    FuturesOpenInterest.MapHistory(Array(rows), "BTCUSDT", TimeSpan.FromMinutes(5), Pin).ToArray();

  private static void Check(bool condition, string message)
  {
    if (!condition) throw new Exception(message);
  }

  private static void Throws<T>(Action action, string message) where T : Exception
  {
    try { action(); }
    catch (T) { return; }
    throw new Exception(message);
  }

  private static void Near(double actual, double expected, string message)
  {
    Check(Math.Abs(actual - expected) <= 1e-9, message + ": " + actual + " instead of " + expected);
  }

  private static void TestCurrentSnapshot()
  {
    var current = FuturesOpenInterest.MapCurrent(Current("btcusdt", "10659.509"), "BTCUSDT", Pin);
    Check(current.Exchange == "BinanceFutures" && current.Symbol == "BTCUSDT"
      && current.NativeOpenInterest == 10659.509m && !current.ReportedOpenInterestValue.HasValue
      && !current.IsHistoricalStatistic && current.Period == null, "Current OI must remain its own native data type.");
    _passed++;
  }

  private static void TestCurrentTimestampBoundary()
  {
    Throws<InvalidDataException>(() => FuturesOpenInterest.MapCurrent(
      Current("BTCUSDT", "1", Pin.AddSeconds(1).ToUnixTimeMilliseconds()), "BTCUSDT", Pin),
      "Future current OI must be refused.");
    _passed++;
  }

  private static void TestHistoryNativeAndValueFields()
  {
    var a = Parse(Historical("BTCUSDT", 20403.12345678m, 176196512.12345678m, Pin));
    Check(a.Length == 1 && a[0].NativeOpenInterest == 20403.12345678m
      && a[0].ReportedOpenInterestValue == 176196512.12345678m
      && a[0].IsHistoricalStatistic && a[0].Period == TimeSpan.FromMinutes(5),
      "Preserve two independent provider fields with their original precision.");
    _passed++;
  }

  private static void TestNoAutomaticNotionalConversion()
  {
    var a = Parse(Historical("BTCUSDT", 100m, 10000m, Pin));
    Check(a[0].NativeOpenInterest == 100m && a[0].ReportedOpenInterestValue == 10000m,
      "Do not infer value/quantity conversions from BTC price or other inputs.");
    _passed++;
  }

  private static void TestMissingHistoricalFields()
  {
    JObject row = Historical("BTCUSDT", 1m, 10m, Pin);
    row.Remove("sumOpenInterestValue");
    Throws<InvalidDataException>(() => Parse(row), "Missing value field must not be fabricated.");
    _passed++;
  }

  private static void TestWrongSymbol()
  {
    Throws<InvalidDataException>(() => Parse(Historical("ETHUSDT", 1m, 2m, Pin)),
      "Do not accept mixed symbol responses.");
    _passed++;
  }

  private static void TestInvalidQuantity()
  {
    JObject invalid = Historical("BTCUSDT", 1m, 2m, Pin);
    invalid["sumOpenInterest"] = "-1";
    Throws<InvalidDataException>(() => Parse(invalid), "Negative open interest must be invalid.");
    invalid["sumOpenInterest"] = "NaN";
    Throws<InvalidDataException>(() => Parse(invalid), "Nonfinite open interest must be invalid.");
    invalid["sumOpenInterest"] = "1,25";
    Throws<InvalidDataException>(() => Parse(invalid), "Locale-dependent amounts must not be accepted.");
    _passed++;
  }

  private static void TestMissingTradeBucket()
  {
    var history = Parse(Historical("BTCUSDT", 110m, 1100m, Pin));
    var result = FuturesOpenInterest.CalculateChangePercent(history, "BTCUSDT", 5, Pin);
    Check(!result.IsValid && result.InvalidReason.Contains("exact historical OI endpoint"),
      "Omitted/no-trades periods must remain missing, not be filled.");
    _passed++;
  }

  private static void TestNoFutureLeakage()
  {
    var a = Parse(Historical("BTCUSDT", 100m, 1000m, Pin),
      Historical("BTCUSDT", 105m, 1050m, Pin.AddMinutes(5)));
    Check(a.Length == 1 && a[0].TimestampUtc == Pin, "Discard post-pin OI data.");
    _passed++;
  }

  private static void TestDuplicateBuckets()
  {
    JObject a = Historical("BTCUSDT", 100m, 1000m, Pin);
    Check(Parse(a, (JObject)a.DeepClone()).Length == 1, "Deduplicate identical OI observations.");
    Throws<InvalidDataException>(() => Parse(a, Historical("BTCUSDT", 101m, 1000m, Pin)),
      "Conflicting duplicate OI observations must fail.");
    _passed++;
  }

  private static void TestExactChange()
  {
    var a = Parse(Historical("BTCUSDT", 100m, 400m, Pin.AddMinutes(-60)),
      Historical("BTCUSDT", 120m, 800m, Pin));
    var feature = FuturesOpenInterest.CalculateChangePercent(a, "BTCUSDT", 60, Pin);
    Check(feature.IsValid && feature.SampleCount == 2 && feature.SourceDataTimestampUtc == Pin,
      "Exact OI endpoints must yield a valid timestamped change.");
    Near(feature.Value!.Value, 20, "Native OI percent change");
    _passed++;
  }

  private static void TestIndependentValueChange()
  {
    var a = Parse(Historical("BTCUSDT", 100m, 400m, Pin.AddMinutes(-60)),
      Historical("BTCUSDT", 120m, 800m, Pin));
    var feature = FuturesOpenInterest.CalculateChangePercent(a, "BTCUSDT", 60, Pin, useReportedValue: true);
    Near(feature.Value!.Value, 100, "Reported OI value change stays independent of native quantity change");
    _passed++;
  }

  private static void TestNoReferenceFallback()
  {
    var a = Parse(Historical("BTCUSDT", 100m, 1000m, Pin.AddMinutes(-65)),
      Historical("BTCUSDT", 120m, 1200m, Pin));
    var feature = FuturesOpenInterest.CalculateChangePercent(a, "BTCUSDT", 60, Pin);
    Check(!feature.IsValid, "Do not use a nearby reference observation.");
    _passed++;
  }

  private static void TestZeroReference()
  {
    var a = Parse(Historical("BTCUSDT", 0m, 0m, Pin.AddMinutes(-60)),
      Historical("BTCUSDT", 1m, 1m, Pin));
    Check(!FuturesOpenInterest.CalculateChangePercent(a, "BTCUSDT", 60, Pin).IsValid,
      "Zero reference is not a valid change denominator.");
    _passed++;
  }

  private static void TestUnsupportedHorizon()
  {
    var a = Parse(Historical("BTCUSDT", 1m, 10m, Pin));
    Check(!FuturesOpenInterest.CalculateChangePercent(a, "BTCUSDT", 7, Pin).IsValid,
      "An unsupported 5m alignment must remain unavailable.");
    _passed++;
  }

  private static void TestMixedScopeOrPeriod()
  {
    var current = FuturesOpenInterest.MapCurrent(Current("BTCUSDT", "100"), "BTCUSDT", Pin);
    Check(!FuturesOpenInterest.CalculateChangePercent(new[] { current }, "BTCUSDT", 5, Pin).IsValid,
      "Never splice current-tick OI into completed historical 5m buckets.");
    var hourly = FuturesOpenInterest.MapHistory(
      Array(Historical("BTCUSDT", 100m, 1000m, Pin)),
      "BTCUSDT", TimeSpan.FromHours(1), Pin);
    Check(!FuturesOpenInterest.CalculateChangePercent(hourly, "BTCUSDT", 5, Pin).IsValid,
      "Explicit sampling interval mismatches must remain unavailable.");
    _passed++;
  }

  private static void TestPinnedRequestAndBounds()
  {
    string uri = BinanceFuturesOpenInterestClient.HistoryUrl(
      "BTCUSDT", TimeSpan.FromMinutes(5), Pin.AddMinutes(2), 100);
    Check(uri.Contains("/futures/data/openInterestHist") &&
      uri.Contains("period=5m") && uri.Contains("limit=100") &&
      uri.Contains("endTime=" + Pin.AddMinutes(2).ToUnixTimeMilliseconds()) &&
      !uri.Contains("startTime="), "History URL must retain pinned boundary and bounded row limit.");
    Check(BinanceFuturesOpenInterestClient.CurrentUrl("btcusdt").EndsWith("symbol=BTCUSDT"),
      "Current OI endpoint is not historical OI.");
    Throws<ArgumentOutOfRangeException>(() =>
      BinanceFuturesOpenInterestClient.HistoryUrl("BTCUSDT", TimeSpan.FromMinutes(5), Pin, 501),
      "Never exceed provider page size.");
    _passed++;
  }

  private static void TestUnicodeFuturesSymbols()
  {
    string[] symbols = { "哈基米USDT", "币安人生USDT", "我踏马来了USDT", "牛来USDT", "龙虾USDT" };
    foreach (string symbol in symbols)
    {
      Check(FuturesOpenInterest.NormalizeSymbol(symbol) == symbol,
        "Legitimate Unicode futures symbol is preserved: " + symbol);
      string url = BinanceFuturesOpenInterestClient.CurrentUrl(symbol);
      Check(url.Contains(Uri.EscapeDataString(symbol), StringComparison.Ordinal),
        "Unicode futures symbol is safely URL-encoded: " + symbol);
    }
    Throws<ArgumentException>(() => FuturesOpenInterest.NormalizeSymbol("BTCUSDT&evil=true"),
      "Query separators remain invalid in futures symbols.");
    Throws<ArgumentException>(() => FuturesOpenInterest.NormalizeSymbol("BTC USDT"),
      "Embedded whitespace remains invalid in futures symbols.");
    _passed++;
  }

  private static void TestSupportedPeriodsAndInputGuard()
  {
    foreach (TimeSpan interval in new[] { TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15),
      TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(4),
      TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromDays(1) })
      Check(FuturesOpenInterest.PeriodName(interval).Length > 0, "Supported OI periods require exact names.");
    Throws<ArgumentOutOfRangeException>(() => FuturesOpenInterest.PeriodName(TimeSpan.FromHours(8)),
      "Do not send an unsupported Binance OI period.");
    Throws<ArgumentException>(() => BinanceFuturesOpenInterestClient.CurrentUrl("BTCUSDT&evil=true"),
      "Do not allow query injection via symbol.");
    _passed++;
  }
}
