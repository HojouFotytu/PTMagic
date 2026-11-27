using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Core.MarketAnalyzer.MarketData;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Core.Helper
{
  /// <summary>
  /// Schema 2: one immutable raid header plus zero-based observation,
  /// coverage and trigger-definition tables. TriggerInputs are pairs
  /// [triggerDefinitionIndex, observationIndex], in original input order.
  /// Different values/reasons/timestamps/coverage never alias just because
  /// their metric, market and relation match.
  /// </summary>
  public static class DecisionHistoryCodec
  {
    public const int CurrentSchemaVersion = 2;

    private sealed record Observation(string MarketTrend, string Metric, string Market, string Relation,
      double? Value, bool Available, string Reason, string SourceDataTimestampUtc,
      string PreviousSourceDataTimestampUtc, double? CoveragePercent, int? VolumeCoverageId,
      int? PreviousVolumeCoverageId, double? CurrentMatchedValue, double? PreviousMatchedValue);

    private sealed record Trigger(string Scope, string SettingName, string TriggerId);

    private static JsonSerializer CreateSerializer() => JsonSerializer.Create(new JsonSerializerSettings
    {
      NullValueHandling = NullValueHandling.Ignore,
      DateParseHandling = DateParseHandling.None,
      Culture = CultureInfo.InvariantCulture
    });

    private static string Stamp(DateTimeOffset? value) =>
      value?.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseStamp(string value) =>
      value == null ? null : DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture,
        DateTimeStyles.None);

    public static void WriteLine(TextWriter destination, DecisionHistoryRecord record)
    {
      if (destination == null) throw new ArgumentNullException(nameof(destination));
      if (record == null) throw new ArgumentNullException(nameof(record));
      var serializer = CreateSerializer();
      var coverageByIdentity = new Dictionary<VolumeCoverageDiagnostics, int>(ReferenceEqualityComparer.Instance);
      var coverageByValue = new Dictionary<string, int>(StringComparer.Ordinal);
      var coverage = new List<VolumeCoverageDiagnostics>();
      var observations = new List<Observation>();
      var observationIds = new Dictionary<Observation, int>();
      var triggers = new List<Trigger>();
      var triggerIds = new Dictionary<Trigger, int>();
      var references = new List<(int Trigger, int Observation)>();

      int? CoverageId(VolumeCoverageDiagnostics value)
      {
        if (value == null) return null;
        if (coverageByIdentity.TryGetValue(value, out int found)) return found;
        // Shared runtime snapshots are resolved by identity; separately
        // constructed but equal diagnostics also share a single table row.
        string canonical = JObject.FromObject(value, serializer).ToString(Formatting.None);
        if (!coverageByValue.TryGetValue(canonical, out found))
        {
          found = coverage.Count;
          coverageByValue.Add(canonical, found);
          coverage.Add(value);
        }
        coverageByIdentity.Add(value, found);
        return found;
      }

      foreach (var input in record.MetricInputs ?? Array.Empty<DecisionMetricInput>())
      {
        if (input == null) throw new InvalidDataException("Null DecisionHistory input.");
        var observation = new Observation(input.MarketTrend, input.Metric, input.Market, input.Relation,
          input.Value, input.Available, input.Reason, Stamp(input.SourceDataTimestampUtc),
          Stamp(input.PreviousSourceDataTimestampUtc), input.CoveragePercent,
          CoverageId(input.VolumeCoverage), CoverageId(input.PreviousVolumeCoverage),
          input.CurrentMatchedValue, input.PreviousMatchedValue);
        if (!observationIds.TryGetValue(observation, out int observationId))
        {
          observationId = observations.Count;
          observationIds.Add(observation, observationId);
          observations.Add(observation);
        }
        var trigger = new Trigger(input.Scope, input.SettingName, input.TriggerId);
        if (!triggerIds.TryGetValue(trigger, out int triggerId))
        {
          triggerId = triggers.Count;
          triggerIds.Add(trigger, triggerId);
          triggers.Add(trigger);
        }
        references.Add((triggerId, observationId));
      }

      // Avoid materializing the original repeated MetricInputs JSON or a
      // whole-raid UTF-8 byte array; serialize directly into the gzip stream.
      var header = JObject.FromObject(record.CompactHeader(), serializer);
      using (var writer = new JsonTextWriter(destination) { CloseOutput = false, Formatting = Formatting.None })
      {
        writer.WriteStartObject();
        foreach (var property in header.Properties()) property.WriteTo(writer);
        writer.WritePropertyName("CoverageDiagnostics");
        serializer.Serialize(writer, coverage);
        writer.WritePropertyName("Observations");
        serializer.Serialize(writer, observations);
        writer.WritePropertyName("TriggerDefinitions");
        serializer.Serialize(writer, triggers);
        writer.WritePropertyName("TriggerInputs");
        writer.WriteStartArray();
        foreach (var reference in references)
        {
          writer.WriteStartArray();
          writer.WriteValue(reference.Trigger);
          writer.WriteValue(reference.Observation);
          writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
      }
      destination.Write('\n');
    }

    public static DecisionHistoryRecord ReadLine(string line)
    {
      var serializer = CreateSerializer();
      using var input = new StringReader(line);
      using var reader = new JsonTextReader(input) { DateParseHandling = DateParseHandling.None };
      var root = JObject.Load(reader);
      int version = root.Value<int?>("SchemaVersion") ?? 1;
      var record = root.ToObject<DecisionHistoryRecord>(serializer);
      if (version == 1) return record;
      if (version != CurrentSchemaVersion)
        throw new InvalidDataException("Unsupported DecisionHistory schema " + version);
      var coverage = root["CoverageDiagnostics"]?.ToObject<VolumeCoverageDiagnostics[]>(serializer)
        ?? throw new InvalidDataException("Missing coverage table.");
      var observations = root["Observations"]?.ToObject<Observation[]>(serializer)
        ?? throw new InvalidDataException("Missing observation table.");
      var triggers = root["TriggerDefinitions"]?.ToObject<Trigger[]>(serializer)
        ?? throw new InvalidDataException("Missing trigger table.");
      var references = root["TriggerInputs"] as JArray
        ?? throw new InvalidDataException("Missing trigger references.");
      T Lookup<T>(T[] table, int id) =>
        id >= 0 && id < table.Length ? table[id] : throw new InvalidDataException("Invalid DecisionHistory reference.");
      VolumeCoverageDiagnostics Coverage(int? id) => id.HasValue ? Lookup(coverage, id.Value) : null;
      var expanded = new DecisionMetricInput[references.Count];
      for (int i = 0; i < references.Count; i++)
      {
        if (references[i] is not JArray pair || pair.Count != 2 ||
          pair[0].Type != JTokenType.Integer || pair[1].Type != JTokenType.Integer)
          throw new InvalidDataException("Invalid trigger reference pair.");
        var trigger = Lookup(triggers, pair[0].Value<int>());
        var observation = Lookup(observations, pair[1].Value<int>());
        expanded[i] = new DecisionMetricInput
        {
          Scope = trigger.Scope, SettingName = trigger.SettingName, TriggerId = trigger.TriggerId,
          MarketTrend = observation.MarketTrend, Metric = observation.Metric, Market = observation.Market,
          Relation = observation.Relation, Value = observation.Value, Available = observation.Available,
          Reason = observation.Reason, SourceDataTimestampUtc = ParseStamp(observation.SourceDataTimestampUtc),
          PreviousSourceDataTimestampUtc = ParseStamp(observation.PreviousSourceDataTimestampUtc),
          CoveragePercent = observation.CoveragePercent, VolumeCoverage = Coverage(observation.VolumeCoverageId),
          PreviousVolumeCoverage = Coverage(observation.PreviousVolumeCoverageId),
          CurrentMatchedValue = observation.CurrentMatchedValue, PreviousMatchedValue = observation.PreviousMatchedValue
        };
      }
      record.MetricInputs = expanded;
      return record;
    }

    // Both retained legacy JSONL and new single-raid gzip files are readable.
    // A malformed/interrupted record is not silently treated as valid.
    public static IEnumerable<DecisionHistoryRecord> ReadFile(string path)
    {
      using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
      using var gzip = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
        ? new GZipStream(file, CompressionMode.Decompress, leaveOpen: true) : null;
      using var reader = new StreamReader(gzip == null ? file : (Stream)gzip, Encoding.UTF8);
      string line;
      while ((line = reader.ReadLine()) != null)
        if (!String.IsNullOrWhiteSpace(line)) yield return ReadLine(line);
    }
  }
}
