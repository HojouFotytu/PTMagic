using System;
using System.Collections.Generic;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Phase 1C: one immutable result per symbol and user-configured MarketTrend
  /// duration. Source observations are closed normalized candles only.
  /// </summary>
  public sealed class TrendPeriodFeatures
  {
    public FeatureValueSnapshot ATRP { get; init; }
    public FeatureValueSnapshot VolumeExpansion { get; init; }
    public FeatureValueSnapshot LastQuoteVolume { get; init; }
  }

  public static class TrendPeriodMetricsCalculator
  {
    public const int DefaultATRPeriod = 14;
    public const int DefaultVolumeLookback = 20;

    private readonly struct TrendBar
    {
      public readonly double Open;
      public readonly double High;
      public readonly double Low;
      public readonly double Close;
      public readonly double QuoteVolume;
      public readonly DateTimeOffset CloseTimeUtc;

      public TrendBar(double open, double high, double low, double close,
        double quoteVolume, DateTimeOffset closeTimeUtc)
      {
        Open = open;
        High = high;
        Low = low;
        Close = close;
        QuoteVolume = quoteVolume;
        CloseTimeUtc = closeTimeUtc;
      }
    }

    public static TrendPeriodFeatures Calculate(
      MarketDataSeriesSnapshot series,
      int trendMinutes,
      DateTimeOffset asOfUtc,
      int atrPeriod = DefaultATRPeriod,
      int volumeLookback = DefaultVolumeLookback,
      bool needATR = true,
      bool needVolumeExpansion = true,
      bool needLastVolume = true)
    {
      if (!needATR && !needVolumeExpansion && !needLastVolume)
      {
        return new TrendPeriodFeatures();
      }

      string configurationError = "";
      if (series == null || series.Interval <= TimeSpan.Zero)
      {
        configurationError = "No normalized source series is available.";
      }
      else if (trendMinutes <= 0 || TimeSpan.FromMinutes(trendMinutes).Ticks % series.Interval.Ticks != 0)
      {
        configurationError = "TrendMinutes must be a positive exact multiple of the normalized candle interval.";
      }
      else if (needATR && (atrPeriod < 1 || atrPeriod > 1000))
      {
        configurationError = "ATRPeriod must be between 1 and 1000.";
      }
      else if (needVolumeExpansion && (volumeLookback < 1 || volumeLookback > 1000))
      {
        configurationError = "VolumeExpansionLookback must be between 1 and 1000.";
      }
      else if (!series.HasClosedData)
      {
        configurationError = "No closed normalized source candles are available.";
      }
      else if (series.IsStale)
      {
        configurationError = "Normalized source candles are stale.";
      }

      if (configurationError.Length > 0)
      {
        return InvalidAll(needATR, needVolumeExpansion, needLastVolume,
          asOfUtc, 0, configurationError, series?.IsStale == true);
      }

      TimeSpan period = TimeSpan.FromMinutes(trendMinutes);
      long periodMs = checked((long)period.TotalMilliseconds);
      long nowMs = asOfUtc.ToUniversalTime().ToUnixTimeMilliseconds();
      DateTimeOffset expectedCloseUtc = DateTimeOffset.FromUnixTimeMilliseconds(
        (nowMs / periodMs) * periodMs - 1);

      List<TrendBar> barsNewestFirst = new List<TrendBar>();
      int sourceCount = series.ClosedCandles.Count;
      if (sourceCount == 0)
      {
        return InvalidAll(needATR, needVolumeExpansion, needLastVolume,
          asOfUtc, 0, "No closed normalized candles are available.", false);
      }

      // Start at the last completed MarketTrend-sized UTC boundary.
      // Stop at the first incomplete period. Earlier history is not
      // permitted to bridge a missing source candle in the active window.
      int barsToCheck = needATR ? sourceCount : 0;
      if (needVolumeExpansion)
      {
        barsToCheck = Math.Max(barsToCheck, volumeLookback + 1);
      }
      if (needLastVolume)
      {
        barsToCheck = Math.Max(barsToCheck, 1);
      }
      for (int index = 0; index < barsToCheck; index++)
      {
        DateTimeOffset barClose = expectedCloseUtc - TimeSpan.FromTicks(period.Ticks * index);
        if (!TryAggregateTrendBar(series.ClosedCandles, series.Interval, period, barClose, out TrendBar bar))
        {
          break;
        }
        barsNewestFirst.Add(bar);
      }

      FeatureValueSnapshot lastVolume = null;
      if (needLastVolume)
      {
        lastVolume = barsNewestFirst.Count > 0
          ? Valid(barsNewestFirst[0].QuoteVolume, asOfUtc, expectedCloseUtc, 1)
          : Invalid(asOfUtc, 0, "The latest complete MarketTrend-duration quote-volume period is missing or gapped.", false);
      }

      FeatureValueSnapshot atrp = null;
      if (needATR)
      {
        int minimumBars = atrPeriod + 1;
        if (barsNewestFirst.Count < minimumBars)
        {
          atrp = Invalid(asOfUtc, barsNewestFirst.Count,
            "Insufficient complete " + trendMinutes + "m candles for ATRP(" + atrPeriod +
            "): " + barsNewestFirst.Count + "/" + minimumBars +
            ". StoreDataMaxHours may be insufficient, or source candles are missing.", false);
        }
        else
        {
          // Reverse to oldest-first for a stable Wilder seed at the oldest
          // available contiguous window. Use SMA of first N defined true
          // ranges (N+1 bars), then Wilder smoothing to the latest bar.
          barsNewestFirst.Reverse();
          double atr = 0;
          for (int i = 1; i <= atrPeriod; i++)
          {
            atr += TrueRange(barsNewestFirst[i], barsNewestFirst[i - 1]);
          }
          atr /= atrPeriod;
          for (int i = atrPeriod + 1; i < barsNewestFirst.Count; i++)
          {
            double tr = TrueRange(barsNewestFirst[i], barsNewestFirst[i - 1]);
            atr = ((atrPeriod - 1.0) * atr + tr) / atrPeriod;
          }

          TrendBar last = barsNewestFirst[barsNewestFirst.Count - 1];
          double percentage = (atr / last.Close) * 100.0;
          atrp = double.IsFinite(percentage) && last.Close > 0
            ? Valid(percentage, asOfUtc, expectedCloseUtc, barsNewestFirst.Count)
            : Invalid(asOfUtc, barsNewestFirst.Count, "ATRP result or final close is invalid.", false);
          barsNewestFirst.Reverse();
        }
      }

      FeatureValueSnapshot expansion = null;
      if (needVolumeExpansion)
      {
        int required = volumeLookback + 1;
        if (barsNewestFirst.Count < required)
        {
          expansion = Invalid(asOfUtc, barsNewestFirst.Count,
            "Insufficient complete " + trendMinutes + "m quote-volume periods for VolumeExpansion(" +
            volumeLookback + "): " + barsNewestFirst.Count + "/" + required +
            ". StoreDataMaxHours may be insufficient, or source candles are missing.", false);
        }
        else
        {
          double baselineSum = 0;
          for (int i = 1; i <= volumeLookback; i++)
          {
            baselineSum += barsNewestFirst[i].QuoteVolume;
          }
          double baseline = baselineSum / volumeLookback;
          double ratio = baseline > 0 ? barsNewestFirst[0].QuoteVolume / baseline : double.NaN;
          expansion = double.IsFinite(ratio) && ratio >= 0
            ? Valid(ratio, asOfUtc, expectedCloseUtc, required)
            : Invalid(asOfUtc, required, "The prior quote-volume baseline is zero or invalid.", false);
        }
      }

      return new TrendPeriodFeatures
      {
        ATRP = atrp,
        VolumeExpansion = expansion,
        LastQuoteVolume = lastVolume
      };
    }

    private static double TrueRange(TrendBar current, TrendBar previous)
    {
      return Math.Max(current.High - current.Low,
        Math.Max(Math.Abs(current.High - previous.Close), Math.Abs(current.Low - previous.Close)));
    }

    private static bool TryAggregateTrendBar(
      IReadOnlyList<MarketCandle> candles,
      TimeSpan interval,
      TimeSpan trendPeriod,
      DateTimeOffset targetCloseUtc,
      out TrendBar result)
    {
      result = default;
      long baseMs = checked((long)interval.TotalMilliseconds);
      long periodMs = checked((long)trendPeriod.TotalMilliseconds);
      long firstOpen = targetCloseUtc.ToUnixTimeMilliseconds() + 1 - periodMs;
      if (firstOpen % baseMs != 0 || periodMs % baseMs != 0)
      {
        return false;
      }

      int samples = checked((int)(periodMs / baseMs));
      double open = 0, close = 0, high = double.NegativeInfinity,
        low = double.PositiveInfinity, quoteVolume = 0;
      for (int i = 0; i < samples; i++)
      {
        long expectedOpen = firstOpen + i * baseMs;
        MarketCandle candle = FindByOpen(candles, expectedOpen);
        if (candle == null || !candle.IsClosed
          || candle.OpenTimeUtc.ToUnixTimeMilliseconds() != expectedOpen
          || candle.CloseTimeUtc.ToUnixTimeMilliseconds() != expectedOpen + baseMs - 1
          || !IsValidCandle(candle))
        {
          return false;
        }
        if (i == 0)
        {
          open = candle.Open;
        }
        if (i == samples - 1)
        {
          close = candle.Close;
        }
        high = Math.Max(high, candle.High);
        low = Math.Min(low, candle.Low);
        quoteVolume += candle.QuoteVolume;
      }

      if (!double.IsFinite(quoteVolume) || quoteVolume < 0)
      {
        return false;
      }

      result = new TrendBar(open, high, low, close, quoteVolume, targetCloseUtc);
      return true;
    }

    private static MarketCandle FindByOpen(IReadOnlyList<MarketCandle> candles, long targetOpen)
    {
      int lo = 0, hi = candles.Count - 1;
      while (lo <= hi)
      {
        int mid = lo + (hi - lo) / 2;
        long candidate = candles[mid].OpenTimeUtc.ToUnixTimeMilliseconds();
        if (candidate == targetOpen)
        {
          return candles[mid];
        }
        if (candidate < targetOpen) lo = mid + 1;
        else hi = mid - 1;
      }
      return null;
    }

    private static bool IsValidCandle(MarketCandle candle)
    {
      return candle.Open > 0 && candle.Close > 0 && candle.Low > 0
        && candle.High >= Math.Max(candle.Open, candle.Close)
        && candle.Low <= Math.Min(candle.Open, candle.Close)
        && candle.High >= candle.Low
        && double.IsFinite(candle.Open) && double.IsFinite(candle.High)
        && double.IsFinite(candle.Low) && double.IsFinite(candle.Close)
        && double.IsFinite(candle.QuoteVolume) && candle.QuoteVolume >= 0;
    }

    private static TrendPeriodFeatures InvalidAll(bool atr, bool expansion, bool volume,
      DateTimeOffset when, int count, string reason, bool stale)
    {
      return new TrendPeriodFeatures
      {
        ATRP = atr ? Invalid(when, count, reason, stale) : null,
        VolumeExpansion = expansion ? Invalid(when, count, reason, stale) : null,
        LastQuoteVolume = volume ? Invalid(when, count, reason, stale) : null
      };
    }

    private static FeatureValueSnapshot Valid(double value, DateTimeOffset when,
      DateTimeOffset sourceTime, int count)
    {
      return new FeatureValueSnapshot
      {
        Value = value, IsValid = true, IsStale = false,
        CalculatedAtUtc = when, SourceDataTimestampUtc = sourceTime,
        SampleCount = count
      };
    }

    private static FeatureValueSnapshot Invalid(DateTimeOffset when, int count,
      string reason, bool stale)
    {
      return new FeatureValueSnapshot
      {
        IsValid = false, IsStale = stale, InvalidReason = reason,
        CalculatedAtUtc = when, SampleCount = count
      };
    }
  }
}
