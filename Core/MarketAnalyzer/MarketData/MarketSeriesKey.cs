using System;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Identifies one normalized market time series by symbol and candle interval.
  /// </summary>
  public readonly struct MarketSeriesKey : IEquatable<MarketSeriesKey>
  {
    public MarketSeriesKey(string symbol, TimeSpan interval)
    {
      if (String.IsNullOrWhiteSpace(symbol))
      {
        throw new ArgumentException("A market-series symbol is required.", nameof(symbol));
      }
      if (interval <= TimeSpan.Zero)
      {
        throw new ArgumentOutOfRangeException(nameof(interval), "A market-series interval must be greater than zero.");
      }

      Symbol = symbol.Trim().ToUpperInvariant();
      Interval = interval;
    }

    public string Symbol { get; }
    public TimeSpan Interval { get; }

    public bool Equals(MarketSeriesKey other)
    {
      return StringComparer.OrdinalIgnoreCase.Equals(Symbol, other.Symbol) && Interval.Equals(other.Interval);
    }

    public override bool Equals(object obj)
    {
      return obj is MarketSeriesKey other && Equals(other);
    }

    public override int GetHashCode()
    {
      return HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Symbol ?? String.Empty), Interval);
    }

    public override string ToString()
    {
      return Symbol + "@" + Interval;
    }

    public static bool operator ==(MarketSeriesKey left, MarketSeriesKey right)
    {
      return left.Equals(right);
    }

    public static bool operator !=(MarketSeriesKey left, MarketSeriesKey right)
    {
      return !left.Equals(right);
    }
  }
}
