using System;
using System.Globalization;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Readiness policy for normalized cross-sectional market metrics. The
  /// denominator is the exact Phase 0-selected population for a MarketTrend.
  /// Missing observations remain in that denominator; a failed refresh cannot
  /// make an incomplete market appear complete.
  /// </summary>
  public static class MarketMetricCoverage
  {
    public const double MinimumCoverageRatio = 0.90;

    public static int RequiredValidMarkets(int eligibleMarkets, bool requireComplete = false)
    {
      if (eligibleMarkets <= 0)
      {
        return 0;
      }

      return requireComplete
        ? eligibleMarkets
        : (int)Math.Ceiling(eligibleMarkets * MinimumCoverageRatio);
    }

    public static bool IsReady(int validMarkets, int eligibleMarkets, bool requireComplete = false)
    {
      return eligibleMarkets > 0
        && validMarkets >= 0
        && validMarkets <= eligibleMarkets
        && validMarkets >= RequiredValidMarkets(eligibleMarkets, requireComplete);
    }

    public static FeatureValueSnapshot Apply(
      FeatureValueSnapshot measurement,
      int validMarkets,
      int eligibleMarkets,
      DateTimeOffset calculatedAtUtc,
      bool requireComplete = false)
    {
      bool ready = IsReady(validMarkets, eligibleMarkets, requireComplete);
      bool usable = measurement != null && measurement.IsValid && !measurement.IsStale
        && measurement.Value.HasValue && double.IsFinite(measurement.Value.Value);
      bool valid = ready && usable;

      string reason = "";
      if (!ready)
      {
        string minimum = ((requireComplete ? 1.0 : MinimumCoverageRatio) * 100.0)
          .ToString("0.##", CultureInfo.InvariantCulture);
        reason = "Insufficient market coverage: " + validMarkets + "/" + eligibleMarkets +
          " valid (" + (eligibleMarkets > 0 ? validMarkets * 100.0 / eligibleMarkets : 0.0)
            .ToString("0.##", CultureInfo.InvariantCulture) + "%); at least " + minimum +
          "% (" + RequiredValidMarkets(eligibleMarkets, requireComplete) + " markets) required.";
      }
      else if (!usable)
      {
        reason = String.IsNullOrWhiteSpace(measurement?.InvalidReason)
          ? "Market-wide measurement is invalid or stale."
          : measurement.InvalidReason;
      }

      return new FeatureValueSnapshot
      {
        Value = valid ? measurement.Value : null,
        CalculatedAtUtc = calculatedAtUtc,
        SourceDataTimestampUtc = measurement?.SourceDataTimestampUtc,
        SampleCount = validMarkets,
        EligibleSampleCount = eligibleMarkets,
        CoveragePercent = eligibleMarkets > 0 ? validMarkets * 100.0 / eligibleMarkets : null,
        IsValid = valid,
        IsStale = measurement?.IsStale == true,
        InvalidReason = reason
      };
    }
  }
}
