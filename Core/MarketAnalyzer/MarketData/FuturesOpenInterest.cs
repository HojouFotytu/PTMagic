using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// A native Binance USD-M futures OI observation. The period-statistic
  /// fields are deliberately kept distinct: sumOpenInterest is the exchange's
  /// native quantity; sumOpenInterestValue is its reported value. Neither is
  /// inferred from the other or converted using a price.
  /// </summary>
  public sealed class FuturesOpenInterestObservation
  {
    public string Exchange { get; init; } = "BinanceFutures";
    public string Symbol { get; init; } = "";
    public DateTimeOffset TimestampUtc { get; init; }
    public TimeSpan? Period { get; init; }
    public decimal NativeOpenInterest { get; init; }
    public decimal? ReportedOpenInterestValue { get; init; }
    public bool IsHistoricalStatistic { get; init; }
  }

  public static class FuturesOpenInterest
  {
    public static readonly TimeSpan SourcePeriod = TimeSpan.FromMinutes(5);

    // Binance USD-M historical periods, as documented for openInterestHist.
    public static string PeriodName(TimeSpan period)
    {
      if (period == TimeSpan.FromMinutes(5)) return "5m";
      if (period == TimeSpan.FromMinutes(15)) return "15m";
      if (period == TimeSpan.FromMinutes(30)) return "30m";
      if (period == TimeSpan.FromHours(1)) return "1h";
      if (period == TimeSpan.FromHours(2)) return "2h";
      if (period == TimeSpan.FromHours(4)) return "4h";
      if (period == TimeSpan.FromHours(6)) return "6h";
      if (period == TimeSpan.FromHours(12)) return "12h";
      if (period == TimeSpan.FromDays(1)) return "1d";
      throw new ArgumentOutOfRangeException(nameof(period), "Unsupported Binance open-interest history period.");
    }

    public static string NormalizeSymbol(string symbol)
    {
      if (String.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("A futures symbol is required.", nameof(symbol));
      string value = symbol.Trim().ToUpperInvariant();
      if (value.Length < 2 || value.Length > 40)
        throw new ArgumentException("A futures symbol contains unsupported characters.", nameof(symbol));
      foreach (Rune rune in value.EnumerateRunes())
      {
        if (!Rune.IsLetterOrDigit(rune) && rune.Value != '_')
          throw new ArgumentException("A futures symbol contains unsupported characters.", nameof(symbol));
      }
      return value;
    }

    /// <summary>
    /// Map GET /fapi/v1/openInterest. The current sample has no provider
    /// notional/value field and is not treated as a completed 5m history bar.
    /// </summary>
    public static FuturesOpenInterestObservation MapCurrent(JObject payload, string requestedSymbol,
      DateTimeOffset observedNoLaterThanUtc)
    {
      if (payload == null) throw new ArgumentNullException(nameof(payload));
      string symbol = NormalizeSymbol(requestedSymbol);
      CheckSymbol(payload, symbol);
      DateTimeOffset timestamp = ReadTimestamp(payload, "time");
      if (timestamp > observedNoLaterThanUtc.ToUniversalTime())
        throw new InvalidDataException("Current OI timestamp is after the observation boundary.");
      return new FuturesOpenInterestObservation
      {
        Symbol = symbol, TimestampUtc = timestamp,
        NativeOpenInterest = ReadQuantity(payload, "openInterest"),
        IsHistoricalStatistic = false
      };
    }

    /// <summary>
    /// Map GET /futures/data/openInterestHist. Missing buckets are left missing.
    /// A newer-than-pin sample is excluded; repeated identical buckets are
    /// deduplicated, while conflicting duplicates fail instead of guessing.
    /// </summary>
    public static IReadOnlyList<FuturesOpenInterestObservation> MapHistory(JArray payload,
      string requestedSymbol, TimeSpan period, DateTimeOffset asOfUtc)
    {
      if (payload == null) throw new ArgumentNullException(nameof(payload));
      PeriodName(period);
      string symbol = NormalizeSymbol(requestedSymbol);
      SortedDictionary<long, FuturesOpenInterestObservation> samples =
        new SortedDictionary<long, FuturesOpenInterestObservation>();

      foreach (JToken token in payload)
      {
        if (token is not JObject item)
          throw new InvalidDataException("A historical open-interest row must be an object.");
        CheckSymbol(item, symbol);
        DateTimeOffset timestamp = ReadTimestamp(item, "timestamp");
        if (timestamp > asOfUtc.ToUniversalTime()) continue;
        FuturesOpenInterestObservation candidate = new FuturesOpenInterestObservation
        {
          Symbol = symbol, TimestampUtc = timestamp, Period = period,
          NativeOpenInterest = ReadQuantity(item, "sumOpenInterest"),
          ReportedOpenInterestValue = ReadQuantity(item, "sumOpenInterestValue"),
          IsHistoricalStatistic = true
        };
        long key = timestamp.ToUnixTimeMilliseconds();
        if (samples.TryGetValue(key, out FuturesOpenInterestObservation existing))
        {
          if (existing.NativeOpenInterest != candidate.NativeOpenInterest ||
              existing.ReportedOpenInterestValue != candidate.ReportedOpenInterestValue)
            throw new InvalidDataException("Conflicting OI rows have an identical timestamp.");
        }
        else samples.Add(key, candidate);
      }
      return samples.Values.ToArray();
    }

    /// <summary>
    /// Percentage change between two EXACT completed 5m historical endpoints.
    /// The chosen observations must share symbol, exchange and 5m interval;
    /// missing provider buckets are never interpolated. Value changes and
    /// quantity changes are separate, explicitly selected calculations.
    /// </summary>
    public static FeatureValueSnapshot CalculateChangePercent(
      IEnumerable<FuturesOpenInterestObservation> history, string requestedSymbol,
      int trendMinutes, DateTimeOffset asOfUtc, bool useReportedValue = false)
    {
      FeatureValueSnapshot Invalid(string message) => new FeatureValueSnapshot
      {
        CalculatedAtUtc = asOfUtc.ToUniversalTime(), InvalidReason = message
      };
      if (trendMinutes < 5 || trendMinutes % 5 != 0)
        return Invalid("Open-interest change requires a positive whole 5m TrendMinutes horizon.");
      if (history == null) return Invalid("No OI observations were supplied.");
      string symbol = NormalizeSymbol(requestedSymbol);

      long intervalMs = (long)SourcePeriod.TotalMilliseconds;
      long pinMs = asOfUtc.ToUniversalTime().ToUnixTimeMilliseconds();
      long endMs = pinMs - pinMs % intervalMs;
      long startMs = endMs - (long)trendMinutes * 60_000L;
      FuturesOpenInterestObservation[] series = history.ToArray();
      if (series.Any(row => row == null || !row.IsHistoricalStatistic ||
        row.Period != SourcePeriod || !String.Equals(row.Exchange, "BinanceFutures", StringComparison.OrdinalIgnoreCase) ||
        !String.Equals(row.Symbol, symbol, StringComparison.OrdinalIgnoreCase)))
        return Invalid("OI observations are not a homogeneous Binance USD-M 5m series.");

      FuturesOpenInterestObservation start = series.FirstOrDefault(row =>
        row.TimestampUtc.ToUnixTimeMilliseconds() == startMs);
      FuturesOpenInterestObservation end = series.FirstOrDefault(row =>
        row.TimestampUtc.ToUnixTimeMilliseconds() == endMs);
      if (start == null || end == null)
        return Invalid("Missing an exact historical OI endpoint; no nearest-time or zero substitution is allowed.");
      if (series.Count(row => row.TimestampUtc.ToUnixTimeMilliseconds() == startMs) != 1 ||
          series.Count(row => row.TimestampUtc.ToUnixTimeMilliseconds() == endMs) != 1)
        return Invalid("Duplicate OI endpoints cannot be used for a change.");

      decimal? beginning = useReportedValue ? start.ReportedOpenInterestValue : start.NativeOpenInterest;
      decimal? ending = useReportedValue ? end.ReportedOpenInterestValue : end.NativeOpenInterest;
      if (!beginning.HasValue || !ending.HasValue || beginning.Value <= 0m || ending.Value < 0m)
        return Invalid("OI change requires a positive reference and a valid nonnegative ending amount.");
      try
      {
        decimal percent = (ending.Value / beginning.Value - 1m) * 100m;
        double result = (double)percent;
        if (Double.IsInfinity(result) || Double.IsNaN(result))
          return Invalid("The OI change is outside a finite numerical range.");
        return new FeatureValueSnapshot
        {
          CalculatedAtUtc = asOfUtc.ToUniversalTime(),
          SourceDataTimestampUtc = end.TimestampUtc.ToUniversalTime(),
          SampleCount = 2, Value = result, IsValid = true
        };
      }
      catch (OverflowException)
      {
        return Invalid("The OI change cannot be represented without numerical overflow.");
      }
    }

    private static void CheckSymbol(JObject item, string expected)
    {
      string actual = item.Value<string>("symbol");
      if (String.IsNullOrWhiteSpace(actual) ||
        !String.Equals(NormalizeSymbol(actual), expected, StringComparison.Ordinal))
        throw new InvalidDataException("The Binance OI response symbol does not match the requested symbol.");
    }

    private static DateTimeOffset ReadTimestamp(JObject item, string field)
    {
      string raw = item[field]?.ToString();
      if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long unixMs) || unixMs <= 0)
        throw new InvalidDataException("Binance OI timestamp is missing or invalid: " + field + ".");
      try { return DateTimeOffset.FromUnixTimeMilliseconds(unixMs); }
      catch (ArgumentOutOfRangeException)
      {
        throw new InvalidDataException("Binance OI timestamp is outside a supported UTC range.");
      }
    }

    private static decimal ReadQuantity(JObject item, string field)
    {
      string raw = item[field]?.ToString();
      if (String.IsNullOrWhiteSpace(raw) ||
        !decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value) || value < 0m)
        throw new InvalidDataException("Binance OI numeric field is missing or invalid: " + field + ".");
      return value;
    }
  }
}
