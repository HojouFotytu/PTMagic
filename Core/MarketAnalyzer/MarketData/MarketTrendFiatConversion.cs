using System;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Time-aligned quote-to-USD conversion for normalized PriceTrend returns.
  /// Matches the legacy convention that USDT (and USD) represent USD units.
  /// Other quote currencies require complete, same-exchange quote/USDT
  /// reference candles; missing conversion must never silently use Market.
  /// </summary>
  public static class MarketTrendFiatConversion
  {
    public static bool IsFiat(string trendCurrency) =>
      String.Equals(trendCurrency, "Fiat", StringComparison.OrdinalIgnoreCase);

    public static string ConversionSymbol(string quoteCurrency)
    {
      string quote = quoteCurrency?.Trim().ToUpperInvariant() ?? "";
      if (quote == "USDT" || quote == "USD") return null;
      return quote.Length == 0 ? "" : quote + "USDT";
    }

    public static FeatureValueSnapshot CalculateReturn(
      MarketDataSeriesSnapshot marketSeries,
      MarketDataSeriesSnapshot conversionSeries,
      string quoteCurrency,
      int trendMinutes,
      DateTimeOffset asOfUtc)
    {
      FeatureValueSnapshot market = MarketTrendAnalyticsEngine.CalculateReturn(marketSeries, trendMinutes, asOfUtc);
      if (market == null || !market.IsValid || market.IsStale || !market.Value.HasValue)
      {
        return market;
      }

      string referenceSymbol = ConversionSymbol(quoteCurrency);
      if (referenceSymbol == null) return market;
      if (referenceSymbol.Length == 0 || conversionSeries == null)
      {
        return Invalid(asOfUtc, "Fiat PriceTrend needs a quote-to-USDT conversion series for '" +
          (quoteCurrency ?? "(empty)") + "'; no conversion data was provided.");
      }

      FeatureValueSnapshot conversion = MarketTrendAnalyticsEngine.CalculateReturn(
        conversionSeries, trendMinutes, asOfUtc);
      if (conversion == null || !conversion.IsValid || conversion.IsStale || !conversion.Value.HasValue)
      {
        return Invalid(asOfUtc, "Fiat PriceTrend requires complete, aligned conversion candles for '" +
          referenceSymbol + "': " + (conversion?.InvalidReason ?? "No conversion values available."));
      }

      // (asset quote end / start) × (quote USD end / start) − 1.
      double adjusted = ((1.0 + market.Value.Value / 100.0) *
        (1.0 + conversion.Value.Value / 100.0) - 1.0) * 100.0;
      if (!double.IsFinite(adjusted))
      {
        return Invalid(asOfUtc, "Fiat PriceTrend conversion for '" + referenceSymbol +
          "' produced an invalid result.");
      }

      return new FeatureValueSnapshot
      {
        Value = adjusted,
        CalculatedAtUtc = asOfUtc,
        SourceDataTimestampUtc = market.SourceDataTimestampUtc,
        SampleCount = 2,
        IsValid = true,
        IsStale = false,
        InvalidReason = ""
      };
    }

    private static FeatureValueSnapshot Invalid(DateTimeOffset asOfUtc, string reason)
    {
      return new FeatureValueSnapshot
      {
        Value = null,
        CalculatedAtUtc = asOfUtc,
        SampleCount = 0,
        IsValid = false,
        IsStale = false,
        InvalidReason = reason
      };
    }
  }
}
