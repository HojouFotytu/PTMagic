using System;

namespace Core.MarketAnalyzer.MarketData
{
  public sealed class MarketDataRefreshStatus
  {
    public string Symbol { get; init; } = "";
    public DateTimeOffset RefreshedAtUtc { get; init; }
    public int RequestedCandleCount { get; init; }
    public int ReceivedCandleCount { get; init; }
    public bool Succeeded { get; init; }
    public string Error { get; init; } = "";
  }
}
