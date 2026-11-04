using System;
using System.Collections.Generic;
using System.Linq;
using Core.Helper;
using Core.Main.DataObjects.PTMagicData;
using Newtonsoft.Json;

internal static class Program
{
  private static int _passed;

  private static int Main()
  {
    try
    {
      TestFirstSnapshotNotContaminatedByLaterSms();
      TestAggregationRetainsEverySettingInOrder();
      TestDistinctMarketsAreIndependent();
      TestAggregationAndSnapshotDoNotShareMutableLists();
      TestExportedSnapshotsRemainIsolated();
      Console.WriteLine("SMS trigger summary isolation tests passed: " + _passed + ".");
      return 0;
    }
    catch (Exception error)
    {
      Console.Error.WriteLine("SMS trigger summary isolation tests FAILED: " + error);
      return 1;
    }
  }

  private static void TestFirstSnapshotNotContaminatedByLaterSms()
  {
    Dictionary<string, List<string>> aggregate = new Dictionary<string, List<string>>();
    List<string> firstTriggers = new List<string> { "DIAG_SMS_BTC_A: ATRP" };
    MatchedMarketTriggerContent.Append(aggregate, "BTCUSDT", firstTriggers);

    TriggerSnapshot firstSnapshot = new TriggerSnapshot
    {
      MatchedTriggersContent = MatchedMarketTriggerContent.CopyForSnapshot(firstTriggers)
    };

    List<string> secondTriggers = new List<string> { "DIAG_SMS_BTC_B: VolumeExpansion", "DIAG_SMS_BTC_B: Breadth" };
    MatchedMarketTriggerContent.Append(aggregate, "BTCUSDT", secondTriggers);

    Check(firstSnapshot.MatchedTriggersContent.SequenceEqual(new[] { "DIAG_SMS_BTC_A: ATRP" }),
      "First BTC SMS snapshot must contain only its own matched trigger descriptions");
    Check(aggregate["BTCUSDT"].Count == 3,
      "Market-level PT comment aggregation must still include all settings");
    _passed++;
  }

  private static void TestAggregationRetainsEverySettingInOrder()
  {
    Dictionary<string, List<string>> aggregate = new Dictionary<string, List<string>>();
    MatchedMarketTriggerContent.Append(aggregate, "ETHUSDT", new[] { "SMS1: ATRP", "SMS1: MedianATRP" });
    MatchedMarketTriggerContent.Append(aggregate, "ETHUSDT", new[] { "SMS2: VolumeExpansion" });
    MatchedMarketTriggerContent.Append(aggregate, "ETHUSDT", new[] { "SMS3: PriceTrend" });
    Check(aggregate["ETHUSDT"].SequenceEqual(
      new[] { "SMS1: ATRP", "SMS1: MedianATRP", "SMS2: VolumeExpansion", "SMS3: PriceTrend" }),
      "Market-level PT comment lines must retain original ordering and content");
    _passed++;
  }

  private static void TestDistinctMarketsAreIndependent()
  {
    Dictionary<string, List<string>> aggregate = new Dictionary<string, List<string>>();
    MatchedMarketTriggerContent.Append(aggregate, "BTCUSDT", new[] { "BTC: ATRP" });
    MatchedMarketTriggerContent.Append(aggregate, "ETHUSDT", new[] { "ETH: ATRP" });
    MatchedMarketTriggerContent.Append(aggregate, "BTCUSDT", new[] { "BTC: VolumeExpansion" });
    Check(aggregate["BTCUSDT"].Count == 2 && aggregate["ETHUSDT"].SequenceEqual(new[] { "ETH: ATRP" }),
      "Different market aggregation lists must be isolated");
    _passed++;
  }

  private static void TestAggregationAndSnapshotDoNotShareMutableLists()
  {
    Dictionary<string, List<string>> aggregate = new Dictionary<string, List<string>>();
    List<string> source = new List<string> { "SMS: original" };
    MatchedMarketTriggerContent.Append(aggregate, "SOLUSDT", source);
    List<string> snapshot = MatchedMarketTriggerContent.CopyForSnapshot(source);
    Check(!ReferenceEquals(aggregate["SOLUSDT"], source) && !ReferenceEquals(snapshot, source)
      && !ReferenceEquals(snapshot, aggregate["SOLUSDT"]),
      "Source, market aggregate and persisted snapshot must be three distinct lists");

    source.Add("SMS: source mutation");
    aggregate["SOLUSDT"].Add("SMS: aggregate mutation");
    snapshot.Add("SMS: snapshot mutation");
    Check(aggregate["SOLUSDT"].SequenceEqual(new[] { "SMS: original", "SMS: aggregate mutation" }),
      "Mutating source/snapshot must not alter market aggregation");
    Check(snapshot.SequenceEqual(new[] { "SMS: original", "SMS: snapshot mutation" }),
      "Mutating source/aggregate must not alter SMS snapshot");
    _passed++;
  }

  private static void TestExportedSnapshotsRemainIsolated()
  {
    Dictionary<string, List<string>> aggregate = new Dictionary<string, List<string>>();
    List<string> first = new List<string> { "ETH_SMS1: ATRP", "ETH_SMS1: VolumeExpansion" };
    List<string> second = new List<string> { "ETH_SMS2: MedianATRP" };
    MatchedMarketTriggerContent.Append(aggregate, "ETHUSDT", first);
    TriggerSnapshot firstSnapshot = new TriggerSnapshot
    {
      MatchedTriggersContent = MatchedMarketTriggerContent.CopyForSnapshot(first),
      RelevantTriggers = new Dictionary<int, double> { [0] = 1.3, [1] = 2.2 }
    };
    MatchedMarketTriggerContent.Append(aggregate, "ETHUSDT", second);
    TriggerSnapshot secondSnapshot = new TriggerSnapshot
    {
      MatchedTriggersContent = MatchedMarketTriggerContent.CopyForSnapshot(second),
      RelevantTriggers = new Dictionary<int, double> { [0] = 1.9 }
    };
    string json = JsonConvert.SerializeObject(new[] { firstSnapshot, secondSnapshot });
    TriggerSnapshot[] roundTrip = JsonConvert.DeserializeObject<TriggerSnapshot[]>(json);
    Check(roundTrip != null && roundTrip.Length == 2,
      "Both SMS snapshots must persist into JSON");
    Check(roundTrip[0].MatchedTriggersContent.SequenceEqual(first)
      && roundTrip[1].MatchedTriggersContent.SequenceEqual(second)
      && roundTrip[0].RelevantTriggers.Count == 2
      && roundTrip[1].RelevantTriggers.Count == 1,
      "Saved SMS snapshots must contain only their own trigger strings and values");
    _passed++;
  }

  private static void Check(bool condition, string description)
  {
    if (!condition)
    {
      throw new InvalidOperationException(description);
    }
  }
}
