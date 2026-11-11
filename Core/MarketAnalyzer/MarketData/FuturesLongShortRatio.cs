using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  public enum FuturesLongShortRatioSource
  {
    GlobalAccount,
    TopTraderAccount,
    TopTraderPosition
  }

  /// <summary>
  /// One native Binance USD-M 5m long/short observation. The provider ratio and
  /// its two component shares are retained independently; Binance's examples
  /// are not always internally consistent, so the ratio is never reconstructed.
  /// For the position endpoint, the API still names the component fields
  /// longAccount/shortAccount even though they represent position proportions.
  /// </summary>
  public sealed class FuturesLongShortRatioObservation
  {
    public string Exchange { get; init; } = "BinanceFutures";
    public string Symbol { get; init; } = "";
    public FuturesLongShortRatioSource Source { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
    public TimeSpan Period { get; init; }
    public decimal LongShortRatio { get; init; }
    public decimal LongSideShare { get; init; }
    public decimal ShortSideShare { get; init; }
  }

  public static class FuturesLongShortRatio
  {
    public static readonly TimeSpan SourcePeriod = TimeSpan.FromMinutes(5);
    public const int MaximumHistoryMinutes = 30 * 24 * 60;
    public const int MaximumRowsPerRequest = 500;

    public static string MetricName(FuturesLongShortRatioSource source)
    {
      return source switch
      {
        FuturesLongShortRatioSource.GlobalAccount => MarketTrendMetrics.GlobalLongShortAccountRatio,
        FuturesLongShortRatioSource.TopTraderAccount => MarketTrendMetrics.TopTraderLongShortAccountRatio,
        FuturesLongShortRatioSource.TopTraderPosition => MarketTrendMetrics.TopTraderLongShortPositionRatio,
        _ => throw new ArgumentOutOfRangeException(nameof(source))
      };
    }

    public static bool RequiresApiKey(FuturesLongShortRatioSource source) =>
      source == FuturesLongShortRatioSource.TopTraderAccount ||
      source == FuturesLongShortRatioSource.TopTraderPosition;

    public static string EndpointPath(FuturesLongShortRatioSource source)
    {
      return source switch
      {
        FuturesLongShortRatioSource.GlobalAccount => "/futures/data/globalLongShortAccountRatio",
        FuturesLongShortRatioSource.TopTraderAccount => "/futures/data/topLongShortAccountRatio",
        FuturesLongShortRatioSource.TopTraderPosition => "/futures/data/topLongShortPositionRatio",
        _ => throw new ArgumentOutOfRangeException(nameof(source))
      };
    }

    public static string NormalizeSymbol(string symbol) => FuturesOpenInterest.NormalizeSymbol(symbol);

    public static IReadOnlyList<FuturesLongShortRatioObservation> MapHistory(
      JArray payload, FuturesLongShortRatioSource source, string requestedSymbol, DateTimeOffset asOfUtc)
    {
      if (payload == null) throw new ArgumentNullException(nameof(payload));
      if (!Enum.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
      string symbol = NormalizeSymbol(requestedSymbol);
      var rows = new SortedDictionary<long, FuturesLongShortRatioObservation>();

      foreach (JToken token in payload)
      {
        if (token is not JObject item)
          throw new InvalidDataException("A Binance long/short ratio row must be a JSON object.");

        string actual = item.Value<string>("symbol") ?? "";
        if (!String.Equals(NormalizeSymbol(actual), symbol, StringComparison.Ordinal))
          throw new InvalidDataException("The Binance long/short ratio symbol does not match the request.");

        DateTimeOffset timestamp = ReadTimestamp(item["timestamp"]);
        if (timestamp > asOfUtc.ToUniversalTime()) continue;

        var candidate = new FuturesLongShortRatioObservation
        {
          Symbol = symbol,
          Source = source,
          TimestampUtc = timestamp,
          Period = SourcePeriod,
          LongShortRatio = ReadDecimal(item["longShortRatio"], "longShortRatio", 0m, Decimal.MaxValue),
          LongSideShare = ReadDecimal(item["longAccount"], "longAccount", 0m, 1m),
          ShortSideShare = ReadDecimal(item["shortAccount"], "shortAccount", 0m, 1m)
        };

        Validate(candidate, source, symbol);
        long key = timestamp.ToUnixTimeMilliseconds();
        if (rows.TryGetValue(key, out FuturesLongShortRatioObservation previous))
        {
          if (!Same(previous, candidate))
            throw new InvalidDataException("Conflicting Binance long/short ratio rows share a timestamp.");
        }
        else
        {
          rows.Add(key, candidate);
        }
      }

      return rows.Values.ToArray();
    }

    public static void Validate(FuturesLongShortRatioObservation row,
      FuturesLongShortRatioSource expectedSource, string expectedSymbol)
    {
      if (row == null ||
        !String.Equals(row.Exchange, "BinanceFutures", StringComparison.OrdinalIgnoreCase) ||
        !String.Equals(row.Symbol, NormalizeSymbol(expectedSymbol), StringComparison.OrdinalIgnoreCase) ||
        row.Source != expectedSource || row.Period != SourcePeriod ||
        row.TimestampUtc.ToUnixTimeMilliseconds() <= 0 ||
        row.LongShortRatio < 0m ||
        row.LongSideShare < 0m || row.LongSideShare > 1m ||
        row.ShortSideShare < 0m || row.ShortSideShare > 1m)
        throw new InvalidDataException("Binance long/short ratio observation has invalid identity, period or values.");
    }

    private static bool Same(FuturesLongShortRatioObservation left, FuturesLongShortRatioObservation right) =>
      left.LongShortRatio == right.LongShortRatio &&
      left.LongSideShare == right.LongSideShare &&
      left.ShortSideShare == right.ShortSideShare;

    private static decimal ReadDecimal(JToken token, string field, decimal minimum, decimal maximum)
    {
      string raw = token?.ToString();
      if (String.IsNullOrWhiteSpace(raw) ||
        !decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value) ||
        value < minimum || value > maximum)
        throw new InvalidDataException("Binance long/short ratio field is missing or invalid: " + field + ".");
      return value;
    }

    private static DateTimeOffset ReadTimestamp(JToken token)
    {
      if (token == null ||
        !long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long milliseconds) ||
        milliseconds <= 0)
        throw new InvalidDataException("Binance long/short ratio timestamp is missing or invalid.");
      try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
      catch (ArgumentOutOfRangeException)
      {
        throw new InvalidDataException("Binance long/short ratio timestamp is outside the supported UTC range.");
      }
    }
  }
}
