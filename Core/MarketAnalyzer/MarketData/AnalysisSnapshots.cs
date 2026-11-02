using System;
using System.Collections.Generic;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Standard immutable Layer-B output boundary. Phase 1B will add concrete feature fields.
  /// </summary>
  public sealed class AssetFeatureSnapshot
  {
    public string Symbol { get; init; } = "";
    public DateTimeOffset CalculatedAtUtc { get; init; }
    public bool IsValid { get; init; }
    public bool IsStale { get; init; }
    public int SourceSampleCount { get; init; }
    public IReadOnlyDictionary<string, double?> Features { get; init; } = new Dictionary<string, double?>();
  }

  /// <summary>
  /// Standard immutable Layer-C output boundary. Phase 1B will populate direction,
  /// breadth and dispersion before later Phase 1 modules add more aggregates.
  /// </summary>
  public sealed class MarketAnalysisSnapshot
  {
    public DateTimeOffset TimestampUtc { get; init; }
    public AnalyzerUniverseSnapshot Universe { get; init; }
    public IReadOnlyDictionary<string, double?> MarketFeatures { get; init; } = new Dictionary<string, double?>();
    public IReadOnlyDictionary<string, AssetFeatureSnapshot> Assets { get; init; } = new Dictionary<string, AssetFeatureSnapshot>(StringComparer.OrdinalIgnoreCase);
  }
}
