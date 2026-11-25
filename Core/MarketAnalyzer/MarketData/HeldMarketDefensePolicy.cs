using System;
using System.Collections.Generic;
using System.Globalization;

namespace Core.MarketAnalyzer.MarketData
{
  public static class HeldMarketDefensePolicy
  {
    // For unavailable rules the supported interval is [conclusively true, possibly true].
    // Keep prior acknowledged defense within it. No new protection level is inferred from missing data.
    public static int Apply(List<string> pairs, string property, int? previous,
      IEnumerable<(int Level, TriggerTruthValue Result)> observations)
    {
      int index = -1;
      int floor = 0;
      for (int i = 0; i < pairs.Count; i++)
      {
        int equals = pairs[i].IndexOf('=');
        if (equals < 1 || !pairs[i].Substring(0, equals).Trim().Equals(property, StringComparison.OrdinalIgnoreCase)) continue;
        if (index >= 0 || !double.TryParse(pairs[i].Substring(equals + 1).Trim(),
            NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || !(value == 0 || value == 1 || value == 2))
          throw new InvalidOperationException("HeldMarketDefense requires one numeric PAIRS assignment of 0, 1 or 2 for " + property);
        index = i;
        floor = (int)value;
      }
      if (index < 0) throw new InvalidOperationException("Missing HeldMarketDefense PAIRS property " + property);

      int lower = floor, upper = floor;
      foreach (var observation in observations)
      {
        if (observation.Result == TriggerTruthValue.True) lower = Math.Max(lower, observation.Level);
        if (observation.Result != TriggerTruthValue.False) upper = Math.Max(upper, observation.Level);
      }
      int effective = Math.Clamp(previous.GetValueOrDefault(), lower, upper);
      if (effective != floor) pairs[index] = property + " = " + effective.ToString(CultureInfo.InvariantCulture);
      return effective;
    }
  }
}
