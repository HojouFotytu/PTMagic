# Normalized Binance PriceTrend compatibility regression

Run: `dotnet run --project Tests/NormalizedPriceTrendMigration/NormalizedPriceTrendMigration.csproj`

11 in-memory numerical/behavior cases for: implicit blank Metrics, explicit
PriceTrend, non-price-only metrics, omitted GlobalSetting/SMS Metric dependencies,
closed endpoints, 90% coverage, global vs asset populations, USD benchmark,
both-scope resolution, and Binance Spot/Binance US/Futures routing. No HTTP.

Run existing five harnesses plus this one, and follow with deployed TestMode
checks for all three Binance exchanges before claiming production acceptance.
