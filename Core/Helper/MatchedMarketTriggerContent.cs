using System;
using System.Collections.Generic;

namespace Core.Helper
{
  /// <summary>
  /// Keeps market-level comment aggregation independent of each SMS's
  /// persisted MatchTriggersContent snapshot.
  /// </summary>
  public static class MatchedMarketTriggerContent
  {
    public static void Append(
      IDictionary<string, List<string>> matchedMarketTriggers,
      string market,
      IEnumerable<string> settingTriggerContent)
    {
      if (matchedMarketTriggers == null)
      {
        throw new ArgumentNullException(nameof(matchedMarketTriggers));
      }
      if (String.IsNullOrWhiteSpace(market))
      {
        throw new ArgumentException("Market must be nonempty.", nameof(market));
      }
      if (settingTriggerContent == null)
      {
        throw new ArgumentNullException(nameof(settingTriggerContent));
      }

      if (matchedMarketTriggers.TryGetValue(market, out List<string> combined))
      {
        combined.AddRange(settingTriggerContent);
      }
      else
      {
        // Crucial: never retain the same List<string> used by an SMS snapshot.
        matchedMarketTriggers.Add(market, new List<string>(settingTriggerContent));
      }
    }

    public static List<string> CopyForSnapshot(IEnumerable<string> settingTriggerContent)
    {
      if (settingTriggerContent == null)
      {
        throw new ArgumentNullException(nameof(settingTriggerContent));
      }

      return new List<string>(settingTriggerContent);
    }
  }
}
