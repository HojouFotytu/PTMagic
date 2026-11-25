using System;
using System.Collections.Generic;
using System.Linq;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>
  /// Event-anchored comparisons on a fixed equal-initial-value basket. All
  /// constituents must have an unbroken close path across the entire window.
  /// The common turning points are identified once and reused for both legs.
  /// </summary>
  internal static class MarketLegMetrics
  {
    private static readonly string[] Drawdown = { MarketTrendMetrics.DrawdownSizeInATR,
      MarketTrendMetrics.DrawdownRecoveryPercent, MarketTrendMetrics.DrawdownParticipationRecoveryPercent,
      MarketTrendMetrics.DrawdownLeaderOverlapPercent, MarketTrendMetrics.DrawdownLeaderOverlapExcess };
    private static readonly string[] Rally = { MarketTrendMetrics.RallySizeInATR,
      MarketTrendMetrics.RallyRetracementPercent, MarketTrendMetrics.RallyParticipationReversalPercent,
      MarketTrendMetrics.RallyLeaderOverlapPercent, MarketTrendMetrics.RallyLeaderOverlapExcess };

    internal static bool IsMetric(string metric) => Drawdown.Contains(metric, StringComparer.OrdinalIgnoreCase)
      || Rally.Contains(metric, StringComparer.OrdinalIgnoreCase);

    internal static Dictionary<string, FeatureValueSnapshot> Calculate(MarketTrend trend,
      IReadOnlyCollection<string> requested, string[] symbols, MarketDataStore store, TimeSpan interval,
      DateTimeOffset asOf, MarketDataSeriesSnapshot conversion, bool needsConversion)
    {
      var output = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
      int count = checked((int)(TimeSpan.FromMinutes(trend.TrendMinutes).Ticks / interval.Ticks) + 1);
      long intervalMs = checked((long)interval.TotalMilliseconds);
      long endpoint = asOf.ToUnixTimeMilliseconds() / intervalMs * intervalMs - 1;
      var sourceTime = DateTimeOffset.FromUnixTimeMilliseconds(endpoint);
      var fx = needsConversion ? Closes(conversion, count, endpoint, intervalMs, null) : null;
      var paths = new List<double[]>(symbols.Length);
      if (!needsConversion || fx != null)
      {
        foreach (string symbol in symbols)
        {
          var path = Closes(store.GetSnapshot(symbol, interval, asOf), count, endpoint, intervalMs, fx);
          if (path != null) paths.Add(path);
        }
      }
      bool ready = MarketMetricCoverage.IsReady(paths.Count, symbols.Length);
      double[] index = null;
      if (ready)
      {
        index = new double[count];
        foreach (var path in paths)
          for (int i = 0; i < count; i++) index[i] += path[i] / path[0] / paths.Count;
        ready = index.All(value => double.IsFinite(value) && value > 0);
      }
      foreach (var family in new[] { Drawdown, Rally })
      {
        if (!family.Any(metric => requested.Contains(metric, StringComparer.OrdinalIgnoreCase))) continue;
        int direction = family == Drawdown ? -1 : 1;
        var leg = ready ? FindLeg(index, direction) : (Start: 0, Turn: 0);
        bool found = leg.Turn > leg.Start;
        var values = found ? Measure(index, paths, leg.Start, leg.Turn, direction) : null;
        for (int i = 0; i < family.Length; i++)
        {
          if (!requested.Contains(family[i], StringComparer.OrdinalIgnoreCase)) continue;
          bool valid = found && double.IsFinite(values[i]);
          var feature = new FeatureValueSnapshot
          {
            IsValid = valid, Value = valid ? values[i] : null,
            CalculatedAtUtc = asOf, SourceDataTimestampUtc = sourceTime,
            InvalidReason = valid ? "" : !ready ? "Insufficient aligned complete constituent paths or invalid basket."
              : !found ? "No originating directional excursion within this window."
              : "The originating leg has no measurable denominator."
          };
          output[family[i]] = MarketMetricCoverage.Apply(feature, paths.Count, symbols.Length, asOf);
        }
      }
      return output;
    }

    private static double[] Closes(MarketDataSeriesSnapshot series, int count, long endpoint,
      long intervalMs, double[] fx)
    {
      if (series == null || series.IsStale || series.ClosedSampleCount < count) return null;
      int start = series.ClosedSampleCount - count;
      long first = endpoint - (count - 1L) * intervalMs;
      var path = new double[count];
      for (int i = 0; i < count; i++)
      {
        var candle = series.ClosedCandles[start + i];
        if (candle.CloseTimeUtc.ToUnixTimeMilliseconds() != first + i * intervalMs) return null;
        double close = candle.Close * (fx == null ? 1 : fx[i]);
        if (!double.IsFinite(close) || close <= 0) return null;
        path[i] = close;
      }
      return path;
    }

    private static (int Start, int Turn) FindLeg(double[] index, int direction)
    {
      int start = 0, turn = 0;
      var completed = (Start: 0, Turn: 0);
      for (int i = 1; i < index.Length; i++)
      {
        // Drawdown searches from a high to its subsequent low; rally reverses
        // both comparisons. A reclaimed origin retires this excursion.
        if (direction * (index[i] - index[start]) <= 0)
        {
          if (turn > start) completed = (start, turn);
          start = turn = i;
        }
        else if (direction * (index[i] - index[turn]) >= 0)
        {
          turn = i;
        }
      }
      return turn > start ? (start, turn) : completed;
    }

    private static double[] Measure(double[] index, List<double[]> paths, int start, int turn, int direction)
    {
      int end = index.Length - 1;
      double distance = direction * (index[turn] - index[start]);
      double size = 100.0 * distance / index[start]; // converted to ATR units by caller
      double recovered = 100.0 * -direction * (index[end] - index[turn]) / distance;
      if (end == turn) return new[] { size, 0.0, 0.0, 0.0, double.NaN };

      var origin = new double[paths.Count];
      var response = new double[paths.Count];
      for (int i = 0; i < paths.Count; i++)
      {
        origin[i] = direction * (paths[i][turn] / paths[i][start] - 1);
        response[i] = -direction * (paths[i][end] / paths[i][turn] - 1);
      }
      int originalParticipants = origin.Count(value => value > 0);
      double participation = originalParticipants > 0
        ? 100.0 * response.Count(value => value > 0) / originalParticipants : double.NaN;

      // Up to the strongest quarter of the fixed cohort on each leg; positive
      // movers only, with all ties at the boundary admitted. Denominator is the
      // number of response leaders, so zero precisely means no returning leaders.
      int cutoff = (paths.Count - 1) / 4;
      double originalCut = origin.OrderByDescending(value => value).ElementAt(cutoff);
      double responseCut = response.OrderByDescending(value => value).ElementAt(cutoff);
      int originalLeaders = origin.Count(value => value > 0 && value >= originalCut);
      int leaders = 0, shared = 0;
      for (int i = 0; i < response.Length; i++)
      {
        if (response[i] <= 0 || response[i] < responseCut) continue;
        leaders++;
        if (origin[i] > 0 && origin[i] >= originalCut) shared++;
      }
      double overlap = leaders == 0 ? 0 : 100.0 * shared / leaders;
      double excess = leaders == 0 ? double.NaN : overlap - 100.0 * originalLeaders / paths.Count;
      return new[] { size, recovered, participation, overlap, excess };
    }
  }
}
