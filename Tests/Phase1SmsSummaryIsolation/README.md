# SMS summary trigger list isolation regression tests

Run:

```powershell
dotnet run --project Tests/Phase1SmsSummaryIsolation/Phase1SmsSummaryIsolation.csproj
```

Five deterministic checks exercise the actual production aggregation and
snapshot-copy helper used by Core/Main/PTMagic.cs. They verify independent
SMS trigger descriptions, preservation of PT comment line aggregation,
per-market separation, defensive copying, and serialized JSON snapshots.

No Binance calls are made. Follow with a PTMagic TestMode raid using at
least two simultaneously triggered SMS settings on BTCUSDT or ETHUSDT.
The first SMS's `TriggerSnapshot.MatchedTriggersContent` must contain
only its own messages; the market-level PT comment must still include
all triggered SMS messages.
