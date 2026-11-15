# PTMagic 2.10 — 35-metric multi-trend diagnostic analyzer

**Updated:** 2026-11-15. **Fixture:** `settings.analyzer.json` v2.2.0. **Application:** PTMagic `c6b4f9e` or a compatible later 35-metric source. **Test-only:** must run in an isolated Binance Futures instance with `TestMode=true` in a separate local `settings.general.json`.

## Exact coverage

- **10 MarketTrends:** M15, M30, H1, H2, H4, H6, H12, H24, H48 and H72, with **both global- and asset-capable metrics in every trend**, graph and market-list display enabled.
- **All 35 registered names selected** across the timeframes. **Every one of the 26 global-capable metrics** is referenced by a diagnostic GlobalSetting trigger; **every one of the 10 asset-capable metric names** (nine asset-only plus both-scope PriceTrend) appears in an SMS activation or OffTrigger.
- **8 GlobalSettings:** 7 non-default groups for taker pressure, positive funding, negative funding, price/breadth, volatility/volume, concentration and BTC correlation; plus `Default` as the final fallback. The first matching group has priority. The non-default groups have **empty** property dictionaries and inherit the same Default preset.
- **11 SMS:** short-horizon flow, relative strength, volatility, correlation, swing, higher-horizon, funding/contrarian and cross-timeframe diagnostics. Several have OffTriggers; all use empty property dictionaries and apply under all GlobalSettings.
- **96h of normalized source retention**, adjusted ATR/volume/correlation lookbacks on longer horizons, and H24/H48/H72 funding. `MaxMarkets=0` at every trend (no global cap). COIN, EQUITY and INDEX are eligible without PT allowed-pair filtering or a fixed allowlist.

| Trend | Minutes | Selected metrics | ATR period | Volume lookback | Correlation lookback |
|---|---:|---:|---:|---:|---:|
| M15 | 15 | 13 | 14 | 20 | 24 |
| M30 | 30 | 18 | 14 | 20 | 24 |
| H1 | 60 | 28 | 14 | 20 | 24 |
| H2 | 120 | 21 | 14 | 20 | 20 |
| H4 | 240 | 28 | 14 | 20 | 16 |
| H6 | 360 | 28 | 10 | 10 | 10 |
| H12 | 720 | 20 | 6 | 5 | 6 |
| H24 | 1440 | 20 | 14 | 20 | 30 |
| H48 | 2880 | 18 | 14 | 20 | 30 |
| H72 | 4320 | 12 | 14 | 20 | 30 |

The Market Analyzer's grouped **Market-Wide Metrics** and its **expandable per-asset rows** can therefore show the current catalogue on several horizons. H1/H4 provide the full non-funding metric range; H24/H48 include funding and taker groups. Values are shown **only if actually calculated and published** by the analysis engine; Monitor visibility does not activate new calculations. PriceTrend is already displayed in the primary trend cells and is not duplicated in expandable rows. Missing source or insufficient coverage may legitimately show `--`; a selected metric does not guarantee an available value.

## Preset validity and diagnostic safety

The repository contains only `PTMagic/_presets/Default`; the default setting is named **`Default`** to match Linux case-sensitive directories. It references `PAIRS.properties`, `DCA.properties` and `INDICATORS.properties`; verify the **installed local** file names match exact case. The other seven GlobalSettings contain **no `File` entries**, so PTMagic inherits the default trio rather than looking for missing diagnostic preset folders. **No preset directory or property file is created or renamed.**

Trigger thresholds are diagnostic examples, not investment/trading rules. Ordered global triggers may stop after the first match; other groups can be configured but not selected in a particular raid. SMS may similarly remain inactive because of real market conditions or missing data. OffTriggers exercise existing SMS lifecycle behavior. All GS/SMS property overrides are empty except the inherited Default file references. Do not load into a live trading installation.

## Verification boundary

The new fixture has been structurally checked for canonical names, metric scope, global/SMS trigger references, unique tags, ordered thresholds, 5-minute alignment and source-retention capacity. **No .NET build, focused harness run, configured TestMode raid, cache v1→v2 check or Monitor browser acceptance is claimed for this fixture.** Check `MarketTrendAnalytics.json`, `LastRuntimeSummary.json`, `DecisionHistory`, raw log diagnostics, eligible/valid population counts, and actual Monitor table and expanded per-asset values.

Suggested focused commands from the application clone: `dotnet build .\PTMagic.sln`, `dotnet run --project .\Tests\Phase2Funding`, `dotnet run --project .\Tests\Phase1COhlcv`, `dotnet run --project .\Tests\Phase2AdaptiveAcquisition`, `dotnet run --project .\Tests\ConfigurationLifecycle`. Existing 26-metric fixture evidence is historical and does not validate this 35-metric configuration.

No code, credentials, general settings, user LONG/SHORT strategies, presets or other files are changed here.
