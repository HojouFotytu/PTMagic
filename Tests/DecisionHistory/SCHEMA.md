# DecisionHistory schema 2 (current)

Storage: `_data/DecisionHistory/yyyy-MM-dd-HHmmss.fffffffZ-<32hex>.jsonl.gz`. UTC filename timestamp equals RecordedAtUtc, one immutable gzip-compressed JSONL record per raid. Atomic publication after compression/flush; .tmp is not evidence. No record-size/quota limit. Only age-based pruning (new file exact timestamp, legacy file existing modification-time policy), sampled hourly or on retention change. Old schema 1 `yyyy-MM-dd-NNNNNN.jsonl` remains readable and untouched until normal retention expiry; do not mix duplicate reconstructed data with originals.

SchemaVersion=2; all existing top-level raid/header fields retain meaning; MetricInputs is replaced by four zero-based tables:
- CoverageDiagnostics: full VolumeCoverageDiagnostics objects, exact value deduplication.
- Observations: MarketTrend, Metric, Market, Relation, Value, Available, Reason, SourceDataTimestampUtc, PreviousSourceDataTimestampUtc, CoveragePercent, VolumeCoverageId, PreviousVolumeCoverageId, CurrentMatchedValue, PreviousMatchedValue. Null optional fields omitted; empty strings and false retained. Source timestamps ISO-8601 roundtrip strings.
- TriggerDefinitions: Scope, SettingName, TriggerId.
- TriggerInputs: ordered arrays [triggerDefinitionIndex, observationIndex]; one per original captured input, including duplicates and different RelativeTrigger values.

Reconstruct by merging trigger definition with observation, replace each coverage ID with its table object, default absent optional input fields to null. Never collapse observations by metric name/market alone. Availability, value, reason, two timestamps, both coverage snapshots and matched values participate in identity. No additional observation, trigger result or price data invented. Existing capture granularity unchanged.

Read using Core.Helper.DecisionHistoryCodec.ReadFile (v1 and v2), or Tools/decision_history.py. The Python reader writes expanded schema-1-shaped JSONL to stdout for existing analysis pipelines; the on-disk originals remain unchanged. External consumers that directly deserialize MetricInputs must adopt this decoder. No trading or configuration semantics change.

Export: new gzip files selected by exact RecordedAtUtc encoded in filename and copied without recompression. Legacy daily segments coarse-selected by UTC date, then whole complete newline-terminated records selected by actual RecordedAtUtc within inclusive request bounds; fixed-buffer scanning, no full-record parsing in Monitor. Malformed legacy header is explicit export failure; All available preserves complete legacy lines without time filtering. Current snapshots and caches included whole; Logs still whole matching dates.

No automatic legacy migration during live raids: old history can still dominate short-term exports. Benchmark actual compressed sizes, serialization duration and decision history integrity on both instances before marking operational acceptance. No retroactive conversion/deletion or reduction in retention.
