using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Helper;
using Core.MarketAnalyzer.MarketData;
using Microsoft.Extensions.Logging.Abstractions;

internal static class Program
{
  private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
  private static readonly DateTimeOffset Boundary = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
  private static readonly LogHelper Log = new LogHelper(NullLogger<LogHelper>.Instance);
  private static int _passed;

  private static async Task<int> Main()
  {
    string root = Path.Combine(Path.GetTempPath(), "PTM-WarmStart-" + Guid.NewGuid().ToString("N"));
    try
    {
      Directory.CreateDirectory(root);
      TestClosedOnlyRoundTrip(root);
      TestExchangeAndSymbolIsolation(root);
      await TestIncrementalRestart(root);
      await TestIncompleteCacheBackfills(root);
      await TestIncompleteMemoryBackfills();
      TestCorruptionFailOpen(root);
      TestBoundedRestoration(root);
      TestFileSaveThrottle(root);
      await TestLegacyTakerUpgrade(root);
      await TestTakerBeyondCapacityAvoidsFullReload();
      Console.WriteLine("Normalized warm-start cache tests passed: " + _passed + ".");
      return 0;
    }
    catch (Exception error)
    {
      Console.Error.WriteLine("Normalized warm-start cache tests FAILED: " + error);
      return 1;
    }
    finally
    {
      try { Directory.Delete(root, recursive: true); }
      catch (IOException) { }
      catch (UnauthorizedAccessException) { }
    }
  }

  private static void TestClosedOnlyRoundTrip(string root)
  {
    MarketDataWarmStartCache cache = NewCache(Path.Combine(root, "roundtrip"));
    MarketDataStore source = new MarketDataStore(20);
    source.UpsertCandles(Candles("BTCUSDT", 8));
    source.UpsertCandle(Candle("BTCUSDT", Boundary, 200, false));
    Assert(cache.TrySave("BinanceFutures", "BTCUSDT", Interval, Boundary, source, Log),
      "First cache checkpoint must be written.");
    MarketDataStore restored = new MarketDataStore(20);
    int count = cache.TryRestore("BinanceFutures", "BTCUSDT", Interval, Boundary, restored, Log);
    MarketDataSeriesSnapshot snapshot = restored.GetSnapshot("BTCUSDT", Interval, Boundary);
    Assert(count == 8 && snapshot.SampleCount == 8 && snapshot.ClosedSampleCount == 8,
      "Only completed candles must survive the round trip.");
    Assert(snapshot.Candles[7].Close == 107 && snapshot.Candles[7].QuoteVolume == 27,
      "OHLCV values must survive serialization exactly.");
    _passed++;
  }

  private static void TestExchangeAndSymbolIsolation(string root)
  {
    MarketDataWarmStartCache cache = NewCache(Path.Combine(root, "identity"));
    MarketDataStore source = new MarketDataStore(20);
    source.UpsertCandles(Candles("BTCUSDT", 5));
    cache.TrySave("BinanceFutures", "BTCUSDT", Interval, Boundary, source, Log);
    MarketDataStore other = new MarketDataStore(20);
    Assert(cache.TryRestore("Binance", "BTCUSDT", Interval, Boundary, other, Log) == 0,
      "Spot may not use Futures cached observations.");
    Assert(cache.TryRestore("BinanceFutures", "ETHUSDT", Interval, Boundary, other, Log) == 0,
      "A different market may not reuse BTC candles.");
    Assert(other.SeriesCount == 0, "Foreign cache keys must not populate the store.");
    _passed++;
  }

  private static async Task TestIncrementalRestart(string root)
  {
    MarketDataWarmStartCache cache = NewCache(Path.Combine(root, "restart"));
    int initialRequested = 0;
    MarketDataService initial = new MarketDataService(new MarketDataStore(10), cache);
    await initial.RefreshAtAsync(new[] { "BTCUSDT" }, Interval, 10,
      (symbol, count, logger, ct, asOf) =>
      {
        initialRequested = count;
        return Task.FromResult(Candles(symbol, count, 0));
      }, Log, maxConcurrency: 1, refreshAsOfUtc: Boundary, cacheExchange: "BinanceFutures");
    Assert(initialRequested == 10, "Cold startup must fetch the required full window.");

    MarketDataService warm = new MarketDataService(new MarketDataStore(10), cache);
    int restartRequested = 0;
    IReadOnlyList<MarketDataRefreshStatus> results = await warm.RefreshAtAsync(
      new[] { "BTCUSDT" }, Interval, 10,
      (symbol, count, logger, ct, asOf) =>
      {
        restartRequested = count;
        return Task.FromResult(Candles(symbol, count, 0).TakeLast(count).ToList());
      }, Log, maxConcurrency: 1, refreshAsOfUtc: Boundary, cacheExchange: "BinanceFutures");
    Assert(restartRequested == 3 && results[0].Succeeded,
      "Warm restart must fetch only the overlap and current window, not all 10 candles.");
    Assert(warm.Store.GetSnapshot("BTCUSDT", Interval, Boundary).SampleCount == 10,
      "Incremental refill must retain the requested window.");
    _passed++;
  }

  private static async Task TestIncompleteCacheBackfills(string root)
  {
    MarketDataWarmStartCache cache = NewCache(Path.Combine(root, "gappy"));
    MarketDataStore source = new MarketDataStore(20);
    List<MarketCandle> series = Candles("ETHUSDT", 10);
    source.UpsertCandles(series.Where((_, index) => index != 5));
    Assert(cache.TrySave("BinanceFutures", "ETHUSDT", Interval, Boundary, source, Log),
      "A cache may checkpoint an incomplete exchange response without claiming it is usable.");
    MarketDataService warm = new MarketDataService(new MarketDataStore(10), cache);
    int requested = 0;
    await warm.RefreshAtAsync(new[] { "ETHUSDT" }, Interval, 10,
      (symbol, count, logger, ct, asOf) =>
      {
        requested = count;
        return Task.FromResult(Candles(symbol, count));
      }, Log, maxConcurrency: 1, refreshAsOfUtc: Boundary, cacheExchange: "BinanceFutures");
    Assert(requested == 10, "Gaps must force full anchored backfill.");
    _passed++;
  }

  private static async Task TestIncompleteMemoryBackfills()
  {
    MarketDataStore store = new MarketDataStore(10);
    // No disk restoration: a short, otherwise contiguous in-memory response
    // must retain the pre-existing conservative full-backfill behavior.
    store.UpsertCandles(Candles("LTCUSDT", 9));
    MarketDataService service = new MarketDataService(store);
    int requested = 0;
    await service.RefreshAtAsync(new[] { "LTCUSDT" }, Interval, 10,
      (symbol, count, logger, ct, asOf) =>
      {
        requested = count;
        return Task.FromResult(Candles(symbol, count));
      }, Log, maxConcurrency: 1, refreshAsOfUtc: Boundary);
    Assert(requested == 10,
      "Incomplete ordinary in-memory history must request full backfill.");
    _passed++;
  }

  private static void TestCorruptionFailOpen(string root)
  {
    string directory = Path.Combine(root, "corruption");
    MarketDataWarmStartCache cache = NewCache(directory);
    MarketDataStore original = new MarketDataStore(20);
    original.UpsertCandles(Candles("SOLUSDT", 5));
    cache.TrySave("BinanceFutures", "SOLUSDT", Interval, Boundary, original, Log);
    string file = Directory.GetFiles(directory, "*.gz", SearchOption.AllDirectories).Single();
    File.WriteAllBytes(file, new byte[] { 0x1f, 0x8b, 0x01 });
    MarketDataStore restored = new MarketDataStore(20);
    Assert(cache.TryRestore("BinanceFutures", "SOLUSDT", Interval, Boundary, restored, Log) == 0
      && restored.SeriesCount == 0,
      "Truncated gzip must fail without inserting partial history.");
    _passed++;
  }

  private static void TestBoundedRestoration(string root)
  {
    MarketDataWarmStartCache cache = NewCache(Path.Combine(root, "bounded"));
    MarketDataStore original = new MarketDataStore(20);
    original.UpsertCandles(Candles("ADAUSDT", 12));
    cache.TrySave("BinanceFutures", "ADAUSDT", Interval, Boundary, original, Log);
    MarketDataStore smaller = new MarketDataStore(5);
    cache.TryRestore("BinanceFutures", "ADAUSDT", Interval, Boundary, smaller, Log);
    MarketDataSeriesSnapshot snapshot = smaller.GetSnapshot("ADAUSDT", Interval, Boundary);
    Assert(snapshot.SampleCount == 5
      && snapshot.FirstOpenTimeUtc == Boundary.AddMinutes(-25),
      "Restoration must honor the currently configured store capacity.");
    _passed++;
  }

  private static void TestFileSaveThrottle(string root)
  {
    MarketDataWarmStartCache cache = new MarketDataWarmStartCache(
      Path.Combine(root, "throttled"), TimeSpan.FromHours(1));
    MarketDataStore source = new MarketDataStore(20);
    source.UpsertCandles(Candles("BNBUSDT", 8));
    Assert(cache.TrySave("BinanceUS", "BNBUSDT", Interval, Boundary, source, Log),
      "Initial checkpoint should be saved.");
    Assert(!cache.TrySave("BinanceUS", "BNBUSDT", Interval, Boundary, source, Log),
      "Recent snapshots must not be needlessly rewritten.");
    _passed++;
  }

  private static async Task TestLegacyTakerUpgrade(string root)
  {
    string directory = Path.Combine(root, "legacy-taker");
    MarketDataWarmStartCache cache = new MarketDataWarmStartCache(directory, TimeSpan.FromHours(1));
    var initial = Candles("BTCUSDT", 8);
    MarketDataStore source = new MarketDataStore(8);
    source.UpsertCandles(initial);
    Assert(cache.TrySave("BinanceFutures", "BTCUSDT", Interval, Boundary, source, Log),
      "Initial file is created under the stable OHLCV cache path.");

    // Reproduce exact legacy v1 content, with no per-candle taker fields.
    string path = Directory.GetFiles(directory, "*.gz", SearchOption.AllDirectories).Single();
    using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
    using (GZipStream gzip = new GZipStream(fs, CompressionLevel.Fastest))
    using (BinaryWriter writer = new BinaryWriter(gzip, Encoding.UTF8))
    {
      writer.Write(0x50544D43);
      writer.Write(1);
      writer.Write("BINANCEFUTURES");
      writer.Write("BTCUSDT");
      writer.Write(Interval.Ticks);
      writer.Write(initial.Count);
      foreach (MarketCandle candle in initial)
      {
        writer.Write(candle.OpenTimeUtc.ToUnixTimeMilliseconds());
        writer.Write(candle.CloseTimeUtc.ToUnixTimeMilliseconds());
        writer.Write(candle.Open);
        writer.Write(candle.High);
        writer.Write(candle.Low);
        writer.Write(candle.Close);
        writer.Write(candle.BaseVolume);
        writer.Write(candle.QuoteVolume);
        writer.Write(candle.TradeCount);
      }
      writer.Write(0x43414348);
    }

    MarketDataService warm = new MarketDataService(new MarketDataStore(8), cache);
    int requested = 0;
    await warm.RefreshAtAsync(new[] { "BTCUSDT" }, Interval, 8,
      (symbol, count, logger, ct, asOf) =>
      {
        requested = count;
        var rows = Candles(symbol, count).Select(candle =>
          Candle(candle.Symbol, candle.OpenTimeUtc, candle.Close, takerQuote: candle.QuoteVolume / 2)).ToList();
        return Task.FromResult(rows);
      }, Log, maxConcurrency: 1, refreshAsOfUtc: Boundary, cacheExchange: "BinanceFutures",
      takerTrendMinutes: new[] { 10 });

    Assert(requested == 5, "Legacy cache must repair only the newest required taker window, not 8 stored bars.");
    MarketDataSeriesSnapshot repaired = warm.Store.GetSnapshot("BTCUSDT", Interval, Boundary);
    Assert(repaired.SampleCount == 8 && repaired.ClosedCandles.Last().TakerBuyQuoteVolume.HasValue,
      "Historical OHLCV must survive selective taker repair.");

    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
    using (GZipStream gzip = new GZipStream(fs, CompressionMode.Decompress))
    using (BinaryReader reader = new BinaryReader(gzip, Encoding.UTF8))
      Assert(reader.ReadInt32() == 0x50544D43 && reader.ReadInt32() == 2,
        "Verified taker repair must upgrade the old cache immediately despite save throttle.");

    MarketDataService restart = new MarketDataService(new MarketDataStore(8), cache);
    int restartRequested = 0;
    await restart.RefreshAtAsync(new[] { "BTCUSDT" }, Interval, 8,
      (symbol, count, logger, ct, asOf) =>
      {
        restartRequested = count;
        return Task.FromResult(Candles(symbol, count));
      }, Log, maxConcurrency: 1, refreshAsOfUtc: Boundary, cacheExchange: "BinanceFutures",
      takerTrendMinutes: new[] { 10 });
    Assert(restartRequested == 3,
      "Restored v2 taker coverage must avoid repeated taker-window backfill.");
    _passed++;
  }

  private static async Task TestTakerBeyondCapacityAvoidsFullReload()
  {
    MarketDataStore store = new MarketDataStore(8);
    store.UpsertCandles(Candles("BTCUSDT", 8));
    MarketDataService service = new MarketDataService(store);
    int requested = 0;
    await service.RefreshAtAsync(new[] { "BTCUSDT" }, Interval, 8,
      (symbol, count, logger, ct, asOf) =>
      {
        requested = count;
        return Task.FromResult(Candles(symbol, count));
      }, Log, maxConcurrency: 1, refreshAsOfUtc: Boundary,
      takerTrendMinutes: new[] { 1440 });
    Assert(requested == 3,
      "An impossible taker horizon must stay unavailable, not repeatedly force full history requests.");
    _passed++;
  }

  private static MarketDataWarmStartCache NewCache(string directory) =>
    new MarketDataWarmStartCache(directory, TimeSpan.Zero);

  private static List<MarketCandle> Candles(string symbol, int count, int offset = 0) =>
    Enumerable.Range(0, count).Select(index =>
      Candle(symbol, Boundary.AddMinutes((index - count + offset) * 5), 100 + index)).ToList();

  private static MarketCandle Candle(string symbol, DateTimeOffset open, double price, bool closed = true, double? takerQuote = null) =>
    new MarketCandle
    {
      Symbol = symbol,
      Interval = Interval,
      OpenTimeUtc = open,
      CloseTimeUtc = open.Add(Interval).AddMilliseconds(-1),
      Open = price,
      High = price + 1,
      Low = price - 1,
      Close = price,
      BaseVolume = price - 80,
      QuoteVolume = price - 80,
      TakerBuyQuoteVolume = takerQuote,
      TradeCount = 10,
      IsClosed = closed
    };

  private static void Assert(bool condition, string description)
  {
    if (!condition) throw new InvalidOperationException(description);
  }
}
