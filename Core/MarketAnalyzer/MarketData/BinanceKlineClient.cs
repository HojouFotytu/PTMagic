using System;
using System.Collections.Generic;
using System.IO;
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

    internal static async Task<List<MarketCandle>> GetOneMinuteCandlesAsync(string exchangeName, string apiBase, string symbol, int candlesNeeded, LogHelper log, CancellationToken cancellationToken = default)
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

      long observedAtMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
      long endTimeMilliseconds = observedAtMilliseconds;
      int candlesFetched = 0;

      while (candlesFetched < candlesNeeded)
      {
        cancellationToken.ThrowIfCancellationRequested();

        int requestLimit = Math.Min(MaxKlinesPerRequest, candlesNeeded - candlesFetched);
        string url = apiBase + "/klines?interval=1m&symbol=" + symbol + "&endTime=" + endTimeMilliseconds.ToString() + "&limit=" + requestLimit.ToString();

        log.DoLogDebug(exchangeName + " - Getting " + requestLimit.ToString() + " normalized 1m candles for '" + symbol + "'...");
        JArray jsonArray = await BaseAnalyzer.GetSimpleJsonArrayFromURLAsync(url, log, cancellationToken).ConfigureAwait(false);
        if (jsonArray == null || jsonArray.Count == 0)
        {
          break;
        }

        List<MarketCandle> page = MapKlines(symbol, jsonArray, observedAtMilliseconds);
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

    internal static List<MarketCandle> MapKlines(string symbol, JArray jsonArray, long observedAtMilliseconds)
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

        result.Add(new MarketCandle
        {
          Symbol = symbol.Trim().ToUpperInvariant(),
          Interval = OneMinuteInterval,
          OpenTimeUtc = DateTimeOffset.FromUnixTimeMilliseconds(openTimeMilliseconds),
          CloseTimeUtc = DateTimeOffset.FromUnixTimeMilliseconds(closeTimeMilliseconds),
          Open = kline[1].Value<double>(),
          High = kline[2].Value<double>(),
          Low = kline[3].Value<double>(),
          Close = kline[4].Value<double>(),
          BaseVolume = kline[5].Value<double>(),
          QuoteVolume = kline[7].Value<double>(),
          TradeCount = kline[8].Value<long>(),
          IsClosed = closeTimeMilliseconds <= observedAtMilliseconds
        });
      }

      return result;
    }
  }
}
