using System;
using System.Collections.Generic;
using System.Linq;

namespace Core.MarketAnalyzer.MarketData
{
  // Decision aid only. Missing measurements are not converted to zero or false.
  // Caller supplies the same configured first-match priority used for normal Globals.
  public sealed class IncompleteGlobalCandidate
  {
    public string SettingName { get; init; } = "";
    public TriggerTruthValue Result { get; init; }
    public int KnownMatches { get; init; }
  }

  public static class IncompleteGlobalPolicy
  {
    public const string ReservedName = "FAILSAFE";

    public static bool IsFailSafe(string name) =>
      String.Equals(name, ReservedName, StringComparison.OrdinalIgnoreCase);

    // A definite match wins. Otherwise prefer the possible classification with
    // most *observed* matching clauses, retaining configured priority on ties.
    // When no observations support a candidate, the last known classification
    // is itself the only available evidence; absent that, use DEFAULT.
    // This is explicitly provisional, not a claim of valid current measurements.
    public static string SelectBestAvailable(IEnumerable<IncompleteGlobalCandidate> candidates,
      string priorName, string defaultName)
    {
      var ordered = (candidates ?? Enumerable.Empty<IncompleteGlobalCandidate>())
        .Where(candidate => candidate != null && !IsFailSafe(candidate.SettingName))
        .ToList();
      var confirmed = ordered.FirstOrDefault(candidate => candidate.Result == TriggerTruthValue.True);
      if (confirmed != null) return confirmed.SettingName;
      var possible = ordered.Where(candidate => candidate.Result == TriggerTruthValue.Unavailable
          && candidate.KnownMatches > 0)
        .OrderByDescending(candidate => candidate.KnownMatches)
        .FirstOrDefault();
      if (possible != null) return possible.SettingName;
      if (!String.IsNullOrWhiteSpace(priorName) && !IsFailSafe(priorName)
          && (ordered.Count == 0 || ordered.Any(c => String.Equals(c.SettingName, priorName,
              StringComparison.OrdinalIgnoreCase) && c.Result != TriggerTruthValue.False)))
        return priorName;
      return defaultName;
    }
  }
}
