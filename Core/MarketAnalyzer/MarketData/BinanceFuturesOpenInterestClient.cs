using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.Helper;
using Core.MarketAnalyzer;
using Newtonsoft.Json.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Optional Phase 2 USD-M open-interest source. This client is NOT yet
  /// attached to regular raids; no new requests occur unless explicitly called.
  /// It shares the established HTTP/rate-limit/retry policy in BaseAnalyzer.
  /// </summary>
  public static class BinanceFuturesOpenInterestClient
  {
    public const string ApiBase = "https://fapi.binance.com";
    public const int MaxHistoricalRowsPerRequest = 500;

    public static string CurrentUrl(string symbol)
    {
      return ApiBase + "/fapi/v1/openInterest?symbol=" +
        Uri.EscapeDataString(FuturesOpenInterest.NormalizeSymbol(symbol));
    }

    public static string HistoryUrl(string symbol, TimeSpan period,
      DateTimeOffset asOfUtc, int limit)
    {
      if (limit < 1 || limit > MaxHistoricalRowsPerRequest)
        throw new ArgumentOutOfRangeException(nameof(limit), "Binance OI history accepts 1..500 records per request.");
      string periodName = FuturesOpenInterest.PeriodName(period);
      return ApiBase + "/futures/data/openInterestHist?symbol=" +
        Uri.EscapeDataString(FuturesOpenInterest.NormalizeSymbol(symbol)) +
        "&period=" + periodName +
        "&endTime=" + asOfUtc.ToUniversalTime().ToUnixTimeMilliseconds().ToString(
          System.Globalization.CultureInfo.InvariantCulture) +
        "&limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<FuturesOpenInterestObservation> GetCurrentAsync(
      string symbol, LogHelper log, CancellationToken cancellationToken = default)
    {
      if (log == null) throw new ArgumentNullException(nameof(log));
      JObject payload = await BaseAnalyzer.GetSimpleJsonObjectFromURLAsync(
        CurrentUrl(symbol), log, cancellationToken: cancellationToken).ConfigureAwait(false);
      if (payload == null) throw new InvalidOperationException("Current Binance OI response was unavailable.");
      return FuturesOpenInterest.MapCurrent(payload, symbol, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Fetch ONE bounded historical page. The caller owns selection, retention,
    /// scheduling and additional pagination. No live PTMagic code calls this yet.
    /// </summary>
    public static async Task<IReadOnlyList<FuturesOpenInterestObservation>> GetHistoryPageAsync(
      string symbol, TimeSpan period, DateTimeOffset pinnedAsOfUtc,
      int limit, LogHelper log, CancellationToken cancellationToken = default)
    {
      if (log == null) throw new ArgumentNullException(nameof(log));
      JArray payload = await BaseAnalyzer.GetSimpleJsonArrayFromURLAsync(
        HistoryUrl(symbol, period, pinnedAsOfUtc, limit), log, cancellationToken).ConfigureAwait(false);
      if (payload == null)
        throw new InvalidOperationException("Binance OI history response was unavailable.");
      return FuturesOpenInterest.MapHistory(payload, symbol, period, pinnedAsOfUtc);
    }
  }
}
