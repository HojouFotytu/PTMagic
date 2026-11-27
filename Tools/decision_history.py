#!/usr/bin/env python3
"""Decode PTMagic DecisionHistory v1/v2 to expanded JSONL, preserving input order.

Usage: python decision_history.py FILE [FILE ...]
Accepts native .jsonl and .jsonl.gz. Writes expanded schema-1-shaped records
to stdout; originals are never changed. Malformed input raises an error.
"""
import argparse
import gzip
import json
import sys
from pathlib import Path

OBS_FIELDS = (
    "MarketTrend", "Metric", "Market", "Relation", "Value", "Available",
    "Reason", "SourceDataTimestampUtc", "PreviousSourceDataTimestampUtc",
    "CoveragePercent", "CurrentMatchedValue", "PreviousMatchedValue",
)

def lookup(table, index):
    if type(index) is not int or not 0 <= index < len(table):
        raise ValueError("Invalid DecisionHistory table index")
    return table[index]

def expand(record):
    version = record.get("SchemaVersion", 1)
    if version == 1:
        return record
    if version != 2:
        raise ValueError(f"Unsupported DecisionHistory schema {version}")
    coverage = record["CoverageDiagnostics"]
    observations = record["Observations"]
    triggers = record["TriggerDefinitions"]
    inputs = []
    for pair in record["TriggerInputs"]:
        if not isinstance(pair, list) or len(pair) != 2:
            raise ValueError("Invalid trigger reference pair")
        trigger = lookup(triggers, pair[0])
        observation = lookup(observations, pair[1])
        row = {field: trigger.get(field) for field in ("Scope", "SettingName", "TriggerId")}
        row.update({field: observation.get(field) for field in OBS_FIELDS})
        for field in ("VolumeCoverage", "PreviousVolumeCoverage"):
            index = observation.get(field + "Id")
            row[field] = None if index is None else lookup(coverage, index)
        inputs.append(row)
    expanded = {k: v for k, v in record.items()
                if k not in ("CoverageDiagnostics", "Observations", "TriggerDefinitions", "TriggerInputs")}
    expanded["SchemaVersion"] = 1
    expanded["MetricInputs"] = inputs
    return expanded

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("files", nargs="+", type=Path)
    args = parser.parse_args()
    for path in args.files:
        opener = gzip.open if path.name.lower().endswith(".gz") else open
        with opener(path, "rt", encoding="utf-8") as source:
            for line in source:
                if line.strip():
                    if not line.endswith("\n"):
                        raise ValueError(f"Incomplete JSONL record: {path}")
                    json.dump(expand(json.loads(line)), sys.stdout, ensure_ascii=False, separators=(",", ":"))
                    sys.stdout.write("\n")

if __name__ == "__main__":
    main()
