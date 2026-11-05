using System;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Rejects configured horizons that cannot be represented exactly by the
  /// normalized five-minute candle source. Never silently round horizons.
  /// </summary>
  public static class MarketTrendTimeframe
  {
    public static void Validate(MarketTrend trend, TimeSpan interval)
    {
      if (trend == null) throw new ArgumentNullException(nameof(trend));
      if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));

      double minutes = interval.TotalMinutes;
      if (trend.TrendMinutes <= 0 || minutes <= 0 ||
        trend.TrendMinutes % minutes != 0)
      {
        throw new InvalidOperationException("MarketTrend '" + (trend.Name ?? "(unnamed)") +
          "' has TrendMinutes=" + trend.TrendMinutes +
          ". Normalized MarketTrends require a positive TrendMinutes value divisible by " +
          minutes.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture) +
          " (five-minute OHLCV source). Correct settings.analyzer.json before restarting PTMagic.");
      }
    }
  }
}
