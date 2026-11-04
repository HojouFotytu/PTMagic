using System;
using System.Collections.Generic;
using Core.MarketAnalyzer.MarketData;

internal static class Program
{
  private static readonly TriggerTruthValue T = TriggerTruthValue.True;
  private static readonly TriggerTruthValue F = TriggerTruthValue.False;
  private static readonly TriggerTruthValue U = TriggerTruthValue.Unavailable;
  private static int _passed;

  private static int Main()
  {
    try
    {
      Check("true OR unavailable", T, "OR", T, U);
      Check("false OR unavailable", U, "OR", F, U);
      Check("true AND unavailable", U, "AND", T, U);
      Check("false AND unavailable", F, "AND", F, U);
      Check("unknown OR unknown", U, "OR", U, U);
      Check("unknown AND unknown", U, "AND", U, U);
      Check("true OR false", T, "OR", T, F);
      Check("false AND true", F, "AND", F, T);
      Check("true AND true", T, "AND", T, T);
      Check("false OR false", F, "OR", F, F);
      Check("single true", T, "AND", T);
      Check("single unknown", U, "AND", U);
      Check("empty cannot match", F, "OR");
      Check("not unavailable", U, "!A", new Dictionary<string, TriggerTruthValue> { ["A"] = U });
      Check("not false", T, "!A", new Dictionary<string, TriggerTruthValue> { ["A"] = F });
      Check("nested OR with unavailable", T, "(A && B) || C",
        new Dictionary<string, TriggerTruthValue> { ["A"] = T, ["B"] = U, ["C"] = T });
      Check("nested AND indeterminate", U, "(A || B) && C",
        new Dictionary<string, TriggerTruthValue> { ["A"] = F, ["B"] = U, ["C"] = T });
      Check("nested AND definitively false", F, "(A || B) && C",
        new Dictionary<string, TriggerTruthValue> { ["A"] = F, ["B"] = U, ["C"] = F });
      Check("negated unknown cannot activate", U, "A && !B",
        new Dictionary<string, TriggerTruthValue> { ["A"] = T, ["B"] = U });
      Check("true branch independent of negated unknown", T, "A || !B",
        new Dictionary<string, TriggerTruthValue> { ["A"] = T, ["B"] = U });
      Check("not OR unknown", U, "!(A || B)",
        new Dictionary<string, TriggerTruthValue> { ["A"] = F, ["B"] = U });
      Check("false branch dominates unknown", F, "A && (!B || C)",
        new Dictionary<string, TriggerTruthValue> { ["A"] = F, ["B"] = U, ["C"] = U });
      Check("exact tag names do not overlap", T, "A || AA",
        new Dictionary<string, TriggerTruthValue> { ["A"] = F, ["AA"] = T });
      Check("off trigger OR true despite unknown", T, "OR", U, T);
      Check("off trigger OR unknown cannot deactivate", U, "OR", U, F);
      Check("off trigger AND false despite unknown", F, "AND", U, F);
      Check("equality unknown remains unknown", U, "A == B", new Dictionary<string, TriggerTruthValue> { ["A"] = U, ["B"] = T });
      Check("valid equality", T, "A == B", new Dictionary<string, TriggerTruthValue> { ["A"] = T, ["B"] = T });
      Check("legacy false-only custom path", F, "(A && B) || C",
        new Dictionary<string, TriggerTruthValue> { ["A"] = T, ["B"] = F, ["C"] = F });

      Console.WriteLine("Phase 1 tri-state trigger tests passed: " + _passed + ".");
      return 0;
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine("Phase 1 tri-state trigger tests FAILED: " + ex);
      return 1;
    }
  }

  private static void Check(string name, TriggerTruthValue expected, string connection, params TriggerTruthValue[] values)
  {
    TriggerTruthValue actual = ThreeValuedTriggerLogic.Evaluate(connection, values);
    Expect(name, expected, actual);
  }

  private static void Check(string name, TriggerTruthValue expected, string expression, Dictionary<string, TriggerTruthValue> tags)
  {
    TriggerTruthValue actual = ThreeValuedTriggerLogic.Evaluate(
      expression, new List<TriggerTruthValue>(tags.Values), tags);
    Expect(name, expected, actual);
  }

  private static void Expect(string name, TriggerTruthValue expected, TriggerTruthValue actual)
  {
    if (actual != expected)
    {
      throw new InvalidOperationException(name + ": expected " + expected + ", got " + actual + ".");
    }
    _passed++;
  }
}
