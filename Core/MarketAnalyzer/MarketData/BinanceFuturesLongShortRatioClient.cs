using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Core.Helper;
using Core.MarketAnalyzer;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Read-only public USD-M global-account long/short ratio history.
  /// </summary>
  public static class BinanceFuturesLongShortRatioClient
  {
    public const string ApiBase = "https://fapi.binance.com";

    public static string HistoryUrl(FuturesLongShortRatioSource source, string symbol,
      DateTimeOffset startInclusiveUtc, DateTimeOffset endInclusiveUtc, int limit)
    {
      if (limit < 1 || limit > FuturesLongShortRatio.MaximumRowsPerRequest)
        throw new ArgumentOutOfRangeException(nameof(limit), "Binance long/short ratio page size must be 1–500.");
      if (startInclusiveUtc > endInclusiveUtc)
        throw new ArgumentOutOfRangeException(nameof(startInclusiveUtc), "Long/short ratio query boundaries are invalid.");

      return ApiBase + FuturesLongShortRatio.EndpointPath(source) + "?symbol=" +
        Uri.EscapeDataString(FuturesLongShortRatio.NormalizeSymbol(symbol)) +
        "&period=5m" +
        "&startTime=" + startInclusiveUtc.ToUniversalTime().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) +
        "&endTime=" + endInclusiveUtc.ToUniversalTime().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) +
        "&limit=" + limit.ToString(CultureInfo.InvariantCulture);
    }

    public static async Task<IReadOnlyList<FuturesLongShortRatioObservation>> GetHistoryPageAsync(
      FuturesLongShortRatioSource source, string symbol, DateTimeOffset startInclusiveUtc,
      DateTimeOffset endInclusiveUtc, int limit, LogHelper log,
      CancellationToken cancellationToken = default)
    {
      if (log == null) throw new ArgumentNullException(nameof(log));

      string response = await BaseAnalyzer.GetJsonStringFromURLAsync(
        HistoryUrl(source, symbol, startInclusiveUtc, endInclusiveUtc, limit),
        log, cancellationToken: cancellationToken).ConfigureAwait(false);
      if (String.IsNullOrWhiteSpace(response))
        throw new InvalidOperationException("Binance long/short ratio history returned no response.");

      JArray payload;
      try { payload = JArray.Parse(response); }
      catch (JsonException ex)
      {
        throw new InvalidDataException("Binance long/short ratio history was not a JSON array.", ex);
      }

      return FuturesLongShortRatio.MapHistory(payload, source, symbol, endInclusiveUtc);
    }
  }
}
