using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>One historical USD-M funding settlement, in the provider's
  /// native decimal rate. Regular and Special settlement types are distinct.
  /// A percentage-point metric multiplies NativeRate by 100.</summary>
  public sealed class FuturesFundingObservation
  {
    public string Symbol { get; init; } = "";
    public DateTimeOffset FundingTimeUtc { get; init; }
    public decimal NativeRate { get; init; }
    public string RateType { get; init; } = "Unknown";
    public decimal? MarkPrice { get; init; }
    public bool IsRegular => String.Equals(RateType, "Regular", StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>Historical settled funding; not the separate premiumIndex
  /// lastFundingRate or a forward estimate. Never assumes an 8h schedule.</summary>
  public static class FuturesFunding
  {
    /// <summary>Validate exchange-provided Futures symbols, including Unicode
    /// letters and digits, without accepting URL/query separators.</summary>
    public static string NormalizeSymbol(string symbol)
    {
      if (String.IsNullOrWhiteSpace(symbol))
        throw new ArgumentException("A futures symbol is required.", nameof(symbol));
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

    /// <summary>Validate every bulk result without collapsing duplicate provider
    /// records, since their raw count determines pagination completeness.</summary>
    public static IReadOnlyList<FuturesFundingObservation> MapBulkHistory(
      JArray payload, DateTimeOffset asOfUtc)
    {
      if (payload == null) throw new ArgumentNullException(nameof(payload));
      var rows = new List<FuturesFundingObservation>(payload.Count);
      foreach (JToken token in payload)
      {
        if (token is not JObject item)
          throw new InvalidDataException("Binance bulk funding history row must be an object.");
        string symbol = item.Value<string>("symbol");
        if (String.IsNullOrWhiteSpace(symbol))
          throw new InvalidDataException("Bulk funding history row has no symbol.");
        IReadOnlyList<FuturesFundingObservation> mapped =
          MapHistory(new JArray(item), symbol, DateTimeOffset.MaxValue);
        if (mapped.Count != 1 || mapped[0].FundingTimeUtc > asOfUtc.ToUniversalTime())
          throw new InvalidDataException("Bulk funding row falls beyond the pinned boundary.");
        rows.Add(mapped[0]);
      }
      return rows.AsReadOnly();
    }

    public static IReadOnlyList<FuturesFundingObservation> MapHistory(
      JArray payload, string requestedSymbol, DateTimeOffset asOfUtc)
    {
      if (payload == null) throw new ArgumentNullException(nameof(payload));
      string symbol = FuturesFunding.NormalizeSymbol(requestedSymbol);
      var rows = new SortedDictionary<(long Time, string Type), FuturesFundingObservation>();
      foreach (JToken token in payload)
      {
        if (token is not JObject item)
          throw new InvalidDataException("Binance funding history row must be a JSON object.");
        string actual = item.Value<string>("symbol") ?? "";
        if (!String.Equals(FuturesFunding.NormalizeSymbol(actual), symbol, StringComparison.Ordinal))
          throw new InvalidDataException("Funding history symbol does not match the request.");
        DateTimeOffset when = ReadTime(item["fundingTime"]);
        if (when > asOfUtc.ToUniversalTime()) continue;
        string rateType = (item.Value<string>("rateType") ?? "Unknown").Trim();
        if (String.IsNullOrWhiteSpace(rateType)) rateType = "Unknown";
        if (rateType.Length > 64 || rateType.Any(c => Char.IsControl(c)))
          throw new InvalidDataException("Funding rateType is malformed.");
        var row = new FuturesFundingObservation
        {
          Symbol = symbol, FundingTimeUtc = when, RateType = rateType,
          NativeRate = ReadDecimal(item["fundingRate"], "fundingRate", allowNegative: true),
          MarkPrice = item["markPrice"] == null || item["markPrice"].Type == JTokenType.Null
            ? null : ReadDecimal(item["markPrice"], "markPrice", allowNegative: false)
        };
        var key = (when.ToUnixTimeMilliseconds(), rateType.ToUpperInvariant());
        if (rows.TryGetValue(key, out var previous))
        {
          if (previous.NativeRate != row.NativeRate || previous.MarkPrice != row.MarkPrice)
            throw new InvalidDataException("Conflicting funding observations share a settlement time and rate type.");
        }
        else rows.Add(key, row);
      }
      return rows.Values.ToArray();
    }

    public static void Validate(FuturesFundingObservation row, string requestedSymbol)
    {
      if (row == null || !String.Equals(row.Symbol, FuturesFunding.NormalizeSymbol(requestedSymbol),
        StringComparison.OrdinalIgnoreCase) || String.IsNullOrWhiteSpace(row.RateType) ||
        row.RateType.Length > 64 || row.RateType.Any(Char.IsControl) ||
        row.FundingTimeUtc.ToUnixTimeMilliseconds() <= 0 ||
        row.MarkPrice.HasValue && row.MarkPrice.Value < 0)
        throw new InvalidDataException("Funding observation has invalid identity, type or timestamp.");
    }

    public static decimal ReadDecimal(JToken token, string name, bool allowNegative)
    {
      if (token == null || token.Type == JTokenType.Null ||
        !decimal.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value) ||
        (!allowNegative && value < 0))
        throw new InvalidDataException("Invalid Binance funding field: " + name + ".");
      return value;
    }

    private static DateTimeOffset ReadTime(JToken token)
    {
      if (token == null ||
        !long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms) || ms <= 0)
        throw new InvalidDataException("Invalid fundingTime.");
      try { return DateTimeOffset.FromUnixTimeMilliseconds(ms); }
      catch (ArgumentOutOfRangeException)
      { throw new InvalidDataException("Funding time falls outside the supported UTC range."); }
    }
  }
}
