using System;
using System.Collections.Generic;
using System.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  public sealed class SynchronizedReturnVector
  {
    public IReadOnlyList<double> Returns { get; init; } = Array.Empty<double>();
    public IReadOnlyList<DateTimeOffset> EndTimesUtc { get; init; } = Array.Empty<DateTimeOffset>();
    public int TrendMinutes { get; init; }
    public bool IsValid { get; init; }
    public bool IsStale { get; init; }
    public string InvalidReason { get; init; } = "";
  }

  /// <summary>
  /// Pearson correlation of synchronized simple returns, never price levels.
  /// N returns require N+1 exact UTC trend-period close endpoints and every
  /// closed source observation between them. Gaps cannot shorten or shift N.
  /// </summary>
  public static class SynchronizedReturnCorrelationCalculator
  {
    public const int DefaultLookback = 30;
    public const int MinimumLookback = 3;
    public const int MaximumLookback = 1000;

    public static SynchronizedReturnVector BuildVector(
      MarketDataSeriesSnapshot series, TimeSpan sourceInterval, int trendMinutes,
      int lookback, DateTimeOffset asOfUtc, MarketDataSeriesSnapshot conversion = null,
      bool requireConversion = false, bool identityQuote = false)
    {
      if (sourceInterval <= TimeSpan.Zero || trendMinutes <= 0 ||
        TimeSpan.FromMinutes(trendMinutes).Ticks % sourceInterval.Ticks != 0)
      {
        return InvalidVector(trendMinutes, "Correlation TrendMinutes must be a positive exact multiple of the source interval.");
      }
      if (lookback < MinimumLookback || lookback > MaximumLookback)
      {
        return InvalidVector(trendMinutes, "CorrelationLookback must be between 3 and 1000 synchronized returns.");
      }

      TimeSpan period = TimeSpan.FromMinutes(trendMinutes);
      long periodMs = checked((long)period.TotalMilliseconds);
      long nowMs = asOfUtc.ToUniversalTime().ToUnixTimeMilliseconds();
      DateTimeOffset latestClose = DateTimeOffset.FromUnixTimeMilliseconds((nowMs / periodMs) * periodMs - 1);
      DateTimeOffset firstClose = latestClose - TimeSpan.FromTicks(checked(period.Ticks * lookback));

      double[] closes;
      if (identityQuote)
      {
        // BTC/BTC is an identity price. In Fiat mode the aligned BTC/USDT
        // conversion supplies its actual USD return; in Market mode its
        // zero variance is explicitly unavailable for correlation.
        closes = Enumerable.Repeat(1.0, lookback + 1).ToArray();
      }
      else if (!TryReadCloses(series, sourceInterval, period, firstClose, latestClose,
        lookback, out closes, out string seriesError))
      {
        return InvalidVector(trendMinutes, seriesError, series?.IsStale == true);
      }

      if (requireConversion)
      {
        if (!TryReadCloses(conversion, sourceInterval, period, firstClose, latestClose,
          lookback, out double[] conversionCloses, out string conversionError))
        {
          return InvalidVector(trendMinutes, "Fiat correlation conversion unavailable: " + conversionError,
            conversion?.IsStale == true);
        }
        for (int index = 0; index < closes.Length; index++)
        {
          closes[index] *= conversionCloses[index];
          if (!double.IsFinite(closes[index]) || closes[index] <= 0)
          {
            return InvalidVector(trendMinutes, "Fiat correlation conversion produced an invalid close.");
          }
        }
      }

      double[] returns = new double[lookback];
      DateTimeOffset[] timestamps = new DateTimeOffset[lookback];
      for (int index = 0; index < lookback; index++)
      {
        returns[index] = closes[index + 1] / closes[index] - 1.0;
        timestamps[index] = firstClose + TimeSpan.FromTicks(checked(period.Ticks * (index + 1L)));
        if (!double.IsFinite(returns[index]))
        {
          return InvalidVector(trendMinutes, "A synchronized simple return is nonfinite.");
        }
      }

      return new SynchronizedReturnVector
      {
        Returns = Array.AsReadOnly(returns), EndTimesUtc = Array.AsReadOnly(timestamps),
        TrendMinutes = trendMinutes, IsValid = true
      };
    }

    public static FeatureValueSnapshot Calculate(SynchronizedReturnVector asset,
      SynchronizedReturnVector benchmark, DateTimeOffset asOfUtc)
    {
      if (asset == null || benchmark == null || !asset.IsValid || !benchmark.IsValid ||
        asset.IsStale || benchmark.IsStale)
      {
        string reason = asset == null || !asset.IsValid || asset.IsStale
          ? "Asset return vector unavailable: " + (asset?.InvalidReason ?? "missing vector")
          : "BTC return vector unavailable: " + (benchmark?.InvalidReason ?? "missing vector");
        return InvalidFeature(asOfUtc, 0, reason, asset?.IsStale == true || benchmark?.IsStale == true);
      }

      int count = asset.Returns.Count;
      if (count < MinimumLookback || count > MaximumLookback || count != benchmark.Returns.Count ||
        asset.EndTimesUtc.Count != count || benchmark.EndTimesUtc.Count != count ||
        asset.TrendMinutes != benchmark.TrendMinutes || asset.TrendMinutes <= 0)
      {
        return InvalidFeature(asOfUtc, count, "Correlation vectors have different horizons or sample counts.");
      }
      TimeSpan period = TimeSpan.FromMinutes(asset.TrendMinutes);
      long periodMs = checked((long)period.TotalMilliseconds);
      DateTimeOffset expectedLastClose = DateTimeOffset.FromUnixTimeMilliseconds(
        (asOfUtc.ToUniversalTime().ToUnixTimeMilliseconds() / periodMs) * periodMs - 1);
      if (asset.EndTimesUtc[count - 1] != expectedLastClose)
      {
        return InvalidFeature(asOfUtc, count, "Correlation vectors do not end at the latest complete UTC trend period.");
      }
      for (int index = 0; index < count; index++)
      {
        if (asset.EndTimesUtc[index] != benchmark.EndTimesUtc[index] ||
          (index > 0 && asset.EndTimesUtc[index] - asset.EndTimesUtc[index - 1] != period) ||
          !double.IsFinite(asset.Returns[index]) || !double.IsFinite(benchmark.Returns[index]))
        {
          return InvalidFeature(asOfUtc, count, "Correlation requires the same complete return timestamps; no index-only alignment or gap filling is permitted.");
        }
      }

      double assetMean = asset.Returns.Average();
      double benchmarkMean = benchmark.Returns.Average();
      double assetScale = asset.Returns.Max(value => Math.Abs(value - assetMean));
      double benchmarkScale = benchmark.Returns.Max(value => Math.Abs(value - benchmarkMean));
      double assetTolerance = Math.Max(1e-15, asset.Returns.Max(value => Math.Abs(value)) * 1e-12);
      double benchmarkTolerance = Math.Max(1e-15, benchmark.Returns.Max(value => Math.Abs(value)) * 1e-12);
      if (assetScale <= assetTolerance || benchmarkScale <= benchmarkTolerance)
      {
        return InvalidFeature(asOfUtc, count, "Correlation is undefined for zero or numerically constant return variance.");
      }

      // Scaling centered returns bounds the products without changing Pearson r.
      double cross = 0, assetSquares = 0, benchmarkSquares = 0;
      for (int index = 0; index < count; index++)
      {
        double x = (asset.Returns[index] - assetMean) / assetScale;
        double y = (benchmark.Returns[index] - benchmarkMean) / benchmarkScale;
        cross += x * y;
        assetSquares += x * x;
        benchmarkSquares += y * y;
      }
      double coefficient = cross / Math.Sqrt(assetSquares * benchmarkSquares);
      if (!double.IsFinite(coefficient))
      {
        return InvalidFeature(asOfUtc, count, "Correlation produced a nonfinite coefficient.");
      }

      return new FeatureValueSnapshot
      {
        Value = Math.Clamp(coefficient, -1.0, 1.0), CalculatedAtUtc = asOfUtc,
        SourceDataTimestampUtc = asset.EndTimesUtc[count - 1], SampleCount = count,
        IsValid = true, IsStale = false
      };
    }

    private static bool TryReadCloses(MarketDataSeriesSnapshot series, TimeSpan interval,
      TimeSpan period, DateTimeOffset firstClose, DateTimeOffset lastClose, int lookback,
      out double[] closes, out string error)
    {
      closes = Array.Empty<double>();
      error = "";
      if (series == null || !series.HasClosedData || series.Interval != interval || series.IsStale)
      {
        error = "Source series is missing, stale, or uses an incompatible source interval.";
        return false;
      }

      int sourceSteps = checked((int)((lastClose - firstClose).Ticks / interval.Ticks));
      int stepsPerReturn = checked((int)(period.Ticks / interval.Ticks));
      closes = new double[lookback + 1];
      for (int index = 0; index <= sourceSteps; index++)
      {
        DateTimeOffset expectedClose = firstClose + TimeSpan.FromTicks(interval.Ticks * index);
        MarketCandle candle = FindByClose(series.ClosedCandles, expectedClose);
        if (candle == null || !candle.IsClosed || candle.Interval != interval ||
          candle.OpenTimeUtc != expectedClose - interval + TimeSpan.FromMilliseconds(1) ||
          !double.IsFinite(candle.Close) || candle.Close <= 0)
        {
          error = "Missing or invalid exact closed source candle for '" + series.Symbol +
            "' at " + expectedClose.ToString("O") +
            ". Correlation needs all " + lookback + " returns and their reference endpoint.";
          closes = Array.Empty<double>();
          return false;
        }
        if (index % stepsPerReturn == 0)
        {
          closes[index / stepsPerReturn] = candle.Close;
        }
      }
      return true;
    }

    private static MarketCandle FindByClose(IReadOnlyList<MarketCandle> candles, DateTimeOffset close)
    {
      int low = 0, high = candles.Count - 1;
      while (low <= high)
      {
        int middle = low + (high - low) / 2;
        int comparison = candles[middle].CloseTimeUtc.CompareTo(close);
        if (comparison == 0) return candles[middle];
        if (comparison < 0) low = middle + 1; else high = middle - 1;
      }
      return null;
    }

    private static SynchronizedReturnVector InvalidVector(int trendMinutes, string reason, bool stale = false)
    {
      return new SynchronizedReturnVector { TrendMinutes = trendMinutes, InvalidReason = reason, IsStale = stale };
    }

    private static FeatureValueSnapshot InvalidFeature(DateTimeOffset asOfUtc, int count, string reason, bool stale = false)
    {
      return new FeatureValueSnapshot { CalculatedAtUtc = asOfUtc, SampleCount = count,
        IsValid = false, IsStale = stale, InvalidReason = reason };
    }
  }
}
