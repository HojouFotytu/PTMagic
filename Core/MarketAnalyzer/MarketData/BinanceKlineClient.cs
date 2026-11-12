using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Helper;
using Newtonsoft.Json.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Maps Binance kline responses into PTMagic-owned normalized candle objects.
  /// Network scheduling and cache policy belong to MarketDataService, not this mapper.
  /// </summary>
  internal static class BinanceKlineClient
  {
    private const int MaxKlinesPerRequest = 500;
    private static readonly TimeSpan OneMinuteInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FiveMinuteInterval = TimeSpan.FromMinutes(5);

    internal static Task<List<MarketCandle>> GetOneMinuteCandlesAsync(string exchangeName, string apiBase, string symbol, int candlesNeeded, LogHelper log, CancellationToken cancellationToken = default)
    {
      return GetCandlesAsync(exchangeName, apiBase, symbol, "1m", OneMinuteInterval, candlesNeeded, log, cancellationToken);
    }

    internal static Task<List<MarketCandle>> GetFiveMinuteCandlesAsync(string exchangeName, string apiBase, string symbol, int candlesNeeded, LogHelper log, CancellationToken cancellationToken = default)
    {
      return GetFiveMinuteCandlesAsync(exchangeName, apiBase, symbol, candlesNeeded, log, cancellationToken, null);
    }

    internal static Task<List<MarketCandle>> GetFiveMinuteCandlesAsync(string exchangeName, string apiBase, string symbol, int candlesNeeded, LogHelper log, CancellationToken cancellationToken, DateTimeOffset? asOfUtc)
    {
      return GetCandlesAsync(exchangeName, apiBase, symbol, "5m", FiveMinuteInterval, candlesNeeded, log, cancellationToken, asOfUtc);
    }

    private static async Task<List<MarketCandle>> GetCandlesAsync(string exchangeName, string apiBase, string symbol, string intervalName, TimeSpan interval, int candlesNeeded, LogHelper log, CancellationToken cancellationToken, DateTimeOffset? asOfUtc = null)
    {
      List<MarketCandle> result = new List<MarketCandle>();
      if (candlesNeeded <= 0)
      {
        return result;
      }
      if (String.IsNullOrWhiteSpace(symbol))
      {
        throw new ArgumentException("A Binance symbol is required.", nameof(symbol));
      }

      // Every symbol in a normalized raid uses the same upper boundary. A
      // late request must not move the retained window forward and evict a
      // reference candle still required by the pinned analysis.
      long observedAtMilliseconds = (asOfUtc ?? DateTimeOffset.UtcNow).ToUniversalTime().ToUnixTimeMilliseconds();
      long endTimeMilliseconds = observedAtMilliseconds;
      int candlesFetched = 0;

      while (candlesFetched < candlesNeeded)
      {
        cancellationToken.ThrowIfCancellationRequested();

        int requestLimit = Math.Min(MaxKlinesPerRequest, candlesNeeded - candlesFetched);
        string url = apiBase + "/klines?interval=" + intervalName + "&symbol=" + symbol + "&endTime=" + endTimeMilliseconds.ToString() + "&limit=" + requestLimit.ToString();

        log.DoLogDebug(exchangeName + " - Getting " + requestLimit.ToString() + " normalized " + intervalName + " candles for '" + symbol + "'...");
        JArray jsonArray = await BaseAnalyzer.GetSimpleJsonArrayFromURLAsync(url, log, cancellationToken).ConfigureAwait(false);
        if (jsonArray == null || jsonArray.Count == 0)
        {
          break;
        }

        List<MarketCandle> page = MapKlines(symbol, interval, jsonArray, observedAtMilliseconds);
        if (page.Count == 0)
        {
          break;
        }

        result.AddRange(page);
        candlesFetched += page.Count;

        long oldestOpenTimeMilliseconds = page.Min(candle => candle.OpenTimeUtc.ToUnixTimeMilliseconds());
        long nextEndTimeMilliseconds = oldestOpenTimeMilliseconds - 1;
        if (nextEndTimeMilliseconds >= endTimeMilliseconds)
        {
          break;
        }

        endTimeMilliseconds = nextEndTimeMilliseconds;
        if (jsonArray.Count < requestLimit)
        {
          break;
        }
      }

      return result
        .GroupBy(candle => candle.OpenTimeUtc.ToUnixTimeMilliseconds())
        .Select(group => group.Last())
        .OrderBy(candle => candle.OpenTimeUtc)
        .TakeLast(candlesNeeded)
        .ToList();
    }

    internal static List<MarketCandle> MapKlines(string symbol, TimeSpan interval, JArray jsonArray, long observedAtMilliseconds)
    {
      List<MarketCandle> result = new List<MarketCandle>(jsonArray?.Count ?? 0);
      if (jsonArray == null)
      {
        return result;
      }

      foreach (JToken token in jsonArray)
      {
        if (!(token is JArray kline) || kline.Count < 9)
        {
          throw new InvalidDataException("Binance returned an invalid kline payload for '" + symbol + "'.");
        }

        long openTimeMilliseconds = kline[0].Value<long>();
        long closeTimeMilliseconds = kline[6].Value<long>();

        double quoteVolume = kline[7].Value<double>();
        double? takerBuyQuoteVolume = null;
        // Binance kline position 10 is taker-buy *quote* volume; position 9
        // is base volume and must never be combined with quote volume.
        // Preserve valid OHLCV if the optional taker field is malformed.
        if (kline.Count > 10 && kline[10] != null &&
          Double.TryParse(kline[10].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedTaker) &&
          Double.IsFinite(parsedTaker) && parsedTaker >= 0 && parsedTaker <= quoteVolume)
        {
          takerBuyQuoteVolume = parsedTaker;
        }

        result.Add(new MarketCandle
        {
          Symbol = symbol.Trim().ToUpperInvariant(),
          Interval = interval,
          OpenTimeUtc = DateTimeOffset.FromUnixTimeMilliseconds(openTimeMilliseconds),
          CloseTimeUtc = DateTimeOffset.FromUnixTimeMilliseconds(closeTimeMilliseconds),
          Open = kline[1].Value<double>(),
          High = kline[2].Value<double>(),
          Low = kline[3].Value<double>(),
          Close = kline[4].Value<double>(),
          BaseVolume = kline[5].Value<double>(),
          QuoteVolume = quoteVolume,
          TakerBuyQuoteVolume = takerBuyQuoteVolume,
          TradeCount = kline[8].Value<long>(),
          IsClosed = closeTimeMilliseconds <= observedAtMilliseconds
        });
      }

      return result;
    }
  }
}
