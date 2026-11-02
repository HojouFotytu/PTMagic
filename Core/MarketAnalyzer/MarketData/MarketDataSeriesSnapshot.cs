using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Immutable read boundary for one normalized market time series.
  /// </summary>
  public sealed class MarketDataSeriesSnapshot
  {
    internal MarketDataSeriesSnapshot(MarketSeriesKey key, IEnumerable<MarketCandle> candles, DateTimeOffset asOfUtc, TimeSpan staleAfter)
    {
      Key = key;
      AsOfUtc = asOfUtc.ToUniversalTime();

      MarketCandle[] orderedCandles = (candles ?? Enumerable.Empty<MarketCandle>())
        .OrderBy(candle => candle.OpenTimeUtc)
        .ToArray();

      Candles = new ReadOnlyCollection<MarketCandle>(orderedCandles);
      SampleCount = orderedCandles.Length;
      IsAvailable = SampleCount > 0;

      if (!IsAvailable)
      {
        FirstOpenTimeUtc = null;
        LatestOpenTimeUtc = null;
        LatestCloseTimeUtc = null;
        IsStale = true;
        MissingCandleCount = 0;
        HasDetectedGaps = false;
        return;
      }

      FirstOpenTimeUtc = orderedCandles[0].OpenTimeUtc;
      LatestOpenTimeUtc = orderedCandles[SampleCount - 1].OpenTimeUtc;
      LatestCloseTimeUtc = orderedCandles[SampleCount - 1].CloseTimeUtc;
      IsStale = AsOfUtc - LatestCloseTimeUtc.Value > staleAfter;

      int missingCandleCount = 0;
      for (int index = 1; index < orderedCandles.Length; index++)
      {
        TimeSpan gap = orderedCandles[index].OpenTimeUtc - orderedCandles[index - 1].OpenTimeUtc;
        if (gap <= key.Interval)
        {
          continue;
        }

        double expectedIntervals = gap.TotalMilliseconds / key.Interval.TotalMilliseconds;
        int missing = Math.Max(0, (int)Math.Round(expectedIntervals, MidpointRounding.AwayFromZero) - 1);
        missingCandleCount += missing;
      }

      MissingCandleCount = missingCandleCount;
      HasDetectedGaps = missingCandleCount > 0;
    }

    public MarketSeriesKey Key { get; }
    public string Symbol => Key.Symbol;
    public TimeSpan Interval => Key.Interval;
    public IReadOnlyList<MarketCandle> Candles { get; }
    public DateTimeOffset AsOfUtc { get; }
    public int SampleCount { get; }
    public bool IsAvailable { get; }
    public bool IsStale { get; }
    public bool HasDetectedGaps { get; }
    public int MissingCandleCount { get; }
    public DateTimeOffset? FirstOpenTimeUtc { get; }
    public DateTimeOffset? LatestOpenTimeUtc { get; }
    public DateTimeOffset? LatestCloseTimeUtc { get; }
  }
}
