using System;
using System.Globalization;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Bounds delay between a raid's pinned observation time and a decision.
  /// A four-hour/daily metric may deliberately use an older complete bar;
  /// that metric endpoint is not the clock checked by this policy.
  /// </summary>
  public static class AnalysisDecisionFreshness
  {
    public static bool IsFresh(DateTimeOffset? analysisAsOfUtc, DateTimeOffset decisionTimeUtc,
      int maxAgeSeconds, out string reason)
    {
      reason = "";
      if (maxAgeSeconds <= 0)
      {
        reason = "Application.MaxAnalysisAgeSeconds must be greater than zero.";
        return false;
      }
      if (!analysisAsOfUtc.HasValue)
      {
        reason = "No pinned observation time is available for the current raid.";
        return false;
      }

      double seconds = (decisionTimeUtc.ToUniversalTime() - analysisAsOfUtc.Value.ToUniversalTime()).TotalSeconds;
      if (seconds < 0)
      {
        reason = "The raid's pinned observation time is in the future; check the system clock.";
        return false;
      }
      if (seconds > maxAgeSeconds)
      {
        reason = "Analysis is " + seconds.ToString("0.###", CultureInfo.InvariantCulture) +
          " seconds old at decision time; Application.MaxAnalysisAgeSeconds=" + maxAgeSeconds +
          ". Retain the applied configuration (or startup SOM) until a fresh raid completes.";
        return false;
      }
      return true;
    }
  }
}
