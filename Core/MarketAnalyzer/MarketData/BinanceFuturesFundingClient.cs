using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Core.Helper;
using Core.MarketAnalyzer;
using Newtonsoft.Json.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>Read-only USD-M historical funding. Uses the shared PTMagic
  /// Binance rate limiter/retry transport. The history endpoint returns ASCENDING
  /// records when startTime is supplied, so paging is forward by fundingTime.</summary>
  public static class BinanceFuturesFundingClient
  {
    public const int MaximumPageSize = 1000;
    public const string ApiBase = "https://fapi.binance.com";

    public static string HistoryUrl(string symbol, DateTimeOffset startUtc,
      DateTimeOffset endUtc, int limit)
    {
      if (limit < 1 || limit > MaximumPageSize)
        throw new ArgumentOutOfRangeException(nameof(limit), "Funding history page size must be 1–1000.");
      long start = startUtc.ToUniversalTime().ToUnixTimeMilliseconds();
      long end = endUtc.ToUniversalTime().ToUnixTimeMilliseconds();
      if (start <= 0 || end < start)
        throw new ArgumentOutOfRangeException(nameof(startUtc), "Funding query boundaries are invalid.");
      return ApiBase + "/fapi/v1/fundingRate?symbol=" +
        Uri.EscapeDataString(FuturesOpenInterest.NormalizeSymbol(symbol)) +
        "&startTime=" + start.ToString(CultureInfo.InvariantCulture) +
        "&endTime=" + end.ToString(CultureInfo.InvariantCulture) +
        "&limit=" + limit.ToString(CultureInfo.InvariantCulture);
    }

    public static async Task<IReadOnlyList<FuturesFundingObservation>> GetHistoryPageAsync(
      string symbol, DateTimeOffset startUtc, DateTimeOffset endUtc,
      int limit, LogHelper log, CancellationToken ct = default)
    {
      if (log == null) throw new ArgumentNullException(nameof(log));
      JArray payload = await BaseAnalyzer.GetSimpleJsonArrayFromURLAsync(
        HistoryUrl(symbol, startUtc, endUtc, limit), log, ct).ConfigureAwait(false);
      if (payload == null) throw new InvalidOperationException("Funding history request returned no response.");
      return FuturesFunding.MapHistory(payload, symbol, endUtc);
    }
  }
}
