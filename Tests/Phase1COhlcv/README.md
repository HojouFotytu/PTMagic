# Phase 1C OHLCV metrics deterministic tests

Run: `dotnet run --project Tests/Phase1COhlcv/Phase1COhlcv.csproj`

Fixtures cover 10-minute trend aggregation from 5-minute source data,
Wilder ATR(14) seed and recurrence, close-only endpoint, quote-volume
expansion, invalid history/gaps/zero baseline/staleness/misalignment,
90% cross-section coverage, 100% concentration completeness, median and
breadth values, BTC/ETH shares, registry scopes and shared analytics
snapshot integration. The 24h ATRP insufficient-72h test exercises the
actual daily trend-bar builder.

No Binance calls are made. The tests must be run by a .NET 10 SDK
environment, then followed by Binance Futures TestMode validation.
