using System;
using System.Collections.Generic;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Immutable value and validity metadata for one derived analytics feature.
  /// A zero value is distinct from missing or invalid data.
  /// </summary>
  public sealed class FeatureValueSnapshot
  {
    public double? Value { get; init; }
    public DateTimeOffset CalculatedAtUtc { get; init; }
    public DateTimeOffset? SourceDataTimestampUtc { get; init; }
    public int SampleCount { get; init; }
    public bool IsValid { get; init; }
    public bool IsStale { get; init; }
    public string InvalidReason { get; init; } = "";
  }

  /// <summary>
  /// Standard immutable Layer-B output boundary for one analyzed asset.
  /// </summary>
  public sealed class AssetFeatureSnapshot
  {
    public string Symbol { get; init; } = "";
    public DateTimeOffset CalculatedAtUtc { get; init; }
    public DateTimeOffset? SourceDataTimestampUtc { get; init; }
    public bool IsValid { get; init; }
    public bool IsStale { get; init; }
    public int SourceSampleCount { get; init; }
    public IReadOnlyDictionary<string, FeatureValueSnapshot> Features { get; init; } = new Dictionary<string, FeatureValueSnapshot>();
  }

  /// <summary>
  /// Standard immutable Layer-C output boundary. Phase 1B populates price-derived
  /// direction, breadth and dispersion before later phases add additional aggregates.
  /// </summary>
  public sealed class MarketAnalysisSnapshot
  {
    public DateTimeOffset TimestampUtc { get; init; }
    public AnalyzerUniverseSnapshot Universe { get; init; }
    public IReadOnlyDictionary<string, FeatureValueSnapshot> MarketFeatures { get; init; } = new Dictionary<string, FeatureValueSnapshot>();
    public IReadOnlyDictionary<string, AssetFeatureSnapshot> Assets { get; init; } = new Dictionary<string, AssetFeatureSnapshot>(StringComparer.OrdinalIgnoreCase);
  }
}
