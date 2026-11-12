using System;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Canonical PTMagic OHLCV observation for one exchange candle.
  /// </summary>
  public sealed class MarketCandle
  {
    public string Symbol { get; init; } = "";
    public TimeSpan Interval { get; init; }

    public DateTimeOffset OpenTimeUtc { get; init; }
    public DateTimeOffset CloseTimeUtc { get; init; }

    public double Open { get; init; }
    public double High { get; init; }
    public double Low { get; init; }
    public double Close { get; init; }

    public double BaseVolume { get; init; }
    public double QuoteVolume { get; init; }
    // Null means this kline's taker-buy quote volume was not source-verified
    // (including candles restored from the legacy v1 OHLCV cache).
    public double? TakerBuyQuoteVolume { get; init; }

    public long TradeCount { get; init; }
    public bool IsClosed { get; init; }
  }
}
