using System;
using System.Collections.Generic;
using System.Linq;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>Ranks one observation against the 24 strictly earlier equivalent windows, on this raid's frozen universe.</summary>
  public static class MarketContextComparisons
  {
    public const int ReferenceCount = 24;
    private enum Basis { Breadth, ExpansionBreadth, Expansion, Correlation, Efficiency, BuyerFlow, SellerFlow, Imbalance }
    private sealed record Comparison(Basis Basis, bool Change = false, bool Asset = false)
    {
      public bool Aligned => Basis != Basis.Breadth && Basis != Basis.Efficiency;
    }
    private static readonly Dictionary<string, Comparison> Definitions = new(StringComparer.OrdinalIgnoreCase)
    {
      ["BreadthChangePercentile"] = new(Basis.Breadth, true),
      ["VolumeExpansionBreadthChangePercentile"] = new(Basis.ExpansionBreadth, true),
      ["MedianVolumeExpansionPercentile"] = new(Basis.Expansion),
      ["MedianCorrelationToBTCPercentile"] = new(Basis.Correlation),
      ["MedianCorrelationToBTCChangePercentile"] = new(Basis.Correlation, true),
      ["MedianPricePathEfficiencyPercentile"] = new(Basis.Efficiency),
      ["MedianPricePathEfficiencyChangePercentile"] = new(Basis.Efficiency, true),
      ["BuyerFlowPriceConfirmationVolumeSharePercentile"] = new(Basis.BuyerFlow),
      ["SellerFlowPriceConfirmationVolumeSharePercentile"] = new(Basis.SellerFlow),
      ["TakerImbalancePercentile"] = new(Basis.Imbalance, Asset: true),
      ["TakerImbalanceChangePercentile"] = new(Basis.Imbalance, true, true),
      ["PricePathEfficiencyChangePercentile"] = new(Basis.Efficiency, true, true)
    };
    public static bool IsMetric(string name) => name != null && Definitions.ContainsKey(name);

    // periods plus one optional reference close; the caller accounts for a forming candle,
    // incomplete UTC period and any requested outer Delta in the normal retention contract.
    public static void Describe(MarketTrend trend, string metric, out long periods, out bool aligned, out bool referenceClose)
    {
      var definition = Definitions[metric];
      periods = ReferenceCount + 1 + (definition.Change ? 1 : 0);
      aligned = definition.Aligned;
      referenceClose = definition.Basis is Basis.Breadth or Basis.Efficiency or Basis.Correlation;
      if (definition.Basis is Basis.Expansion or Basis.ExpansionBreadth)
        periods += trend.VolumeExpansionLookback;
      if (definition.Basis == Basis.Correlation)
        periods += trend.CorrelationLookback - 1;
    }

    private readonly record struct Period(double PriceReturn, double Efficiency, double Quote,
      double TakerBuy, double PriceDirection)
    {
      public static Period Missing => new(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
    }

    public static Phase1DFeatureResult Calculate(MarketTrend trend, IReadOnlyCollection<string> requested,
      string[] globalSymbols, string[] assetSymbols, MarketDataStore store, TimeSpan interval,
      DateTimeOffset asOf, string benchmarkSymbol, bool needsConversion)
    {
      var selectors = Definitions.Where(pair => requested.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)).ToArray();
      var global = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
      var assets = new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(StringComparer.OrdinalIgnoreCase);
      if (selectors.Length == 0) return new Phase1DFeatureResult { GlobalMetrics = global, AssetMetrics = assets };
      long step = (long)interval.TotalMilliseconds;
      long window = checked((long)trend.TrendMinutes * 60000);
      int candles = checked((int)(window / step));
      long last = asOf.ToUnixTimeMilliseconds() / step * step - 1;
      long alignedLast = asOf.ToUnixTimeMilliseconds() / window * window - 1;

      var counts = new Dictionary<bool, int> { [true] = 0, [false] = 0 };
      foreach (var selector in selectors)
      {
        Describe(trend, selector.Key, out long periods, out bool aligned, out _);
        counts[aligned] = Math.Max(counts[aligned], checked((int)periods));
      }
      bool usesEfficiency = selectors.Any(pair => pair.Value.Basis == Basis.Efficiency);
      bool usesCorrelation = selectors.Any(pair => pair.Value.Basis == Basis.Correlation);
      bool useGlobal = selectors.Any(pair => !pair.Value.Asset);
      bool useAssets = selectors.Any(pair => pair.Value.Asset);
      var globalSet = new HashSet<string>(globalSymbols, StringComparer.OrdinalIgnoreCase);
      var assetSet = new HashSet<string>(assetSymbols, StringComparer.OrdinalIgnoreCase);
      var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      if (useGlobal) symbols.UnionWith(globalSet);
      if (useAssets) symbols.UnionWith(assetSet);

      // Fetch once per symbol. Window summaries hold only the quantities used to compute
      // rank inputs. Never build a past market universe or recursively calculate whole raids.
      string quoteCurrency = benchmarkSymbol.StartsWith("BTC", StringComparison.OrdinalIgnoreCase)
        ? benchmarkSymbol.Substring(3) : "USDT";
      bool identityBenchmark = quoteCurrency.Equals("BTC", StringComparison.OrdinalIgnoreCase);
      MarketDataSeriesSnapshot conversion = null;
      Dictionary<long, double> fx = null;
      if (needsConversion && (usesEfficiency || usesCorrelation || selectors.Any(pair => pair.Value.Basis == Basis.Breadth)))
      {
        string reference = MarketTrendFiatConversion.ConversionSymbol(quoteCurrency);
        if (!string.IsNullOrEmpty(reference)) conversion = store.GetSnapshot(reference, interval, asOf);
        fx = new Dictionary<long, double>();
        if (conversion != null && !conversion.IsStale)
          foreach (var candle in conversion.ClosedCandles)
            if (candle.Close > 0 && double.IsFinite(candle.Close))
              fx[candle.CloseTimeUtc.ToUnixTimeMilliseconds()] = candle.Close;
      }
      Period[] benchmark = Array.Empty<Period>();
      MarketDataSeriesSnapshot benchmarkSnapshot = null;
      if (usesCorrelation)
      {
        // BTC/BTC itself is constant in market currency. Its Fiat reference return
        // equals the BTC/USDT conversion; real crypto-quoted assets use aligned FX closes.
        if (!identityBenchmark) benchmarkSnapshot = store.GetSnapshot(benchmarkSymbol, interval, asOf);
        benchmark = Periods(identityBenchmark ? conversion : benchmarkSnapshot, alignedLast, step, candles,
          counts[true], false, identityBenchmark ? null : fx);
      }
      var aggregate = selectors.Where(pair => !pair.Value.Asset).Select(pair => pair.Value.Basis).Distinct()
        .ToDictionary(basis => basis, basis => new PopulationWindows(ReferenceCount + 2, basis));
      foreach (string symbol in symbols)
      {
        bool isBenchmark = usesCorrelation && !identityBenchmark && symbol.Equals(benchmarkSymbol, StringComparison.OrdinalIgnoreCase);
        var snapshot = isBenchmark ? benchmarkSnapshot : store.GetSnapshot(symbol, interval, asOf);
        Period[] rolling = Periods(snapshot, last, step, candles, counts[false], usesEfficiency, fx, rolling: true);
        Period[] aligned = isBenchmark ? benchmark : Periods(snapshot, alignedLast, step, candles, counts[true], false, fx);
        var local = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
        var measures = new Dictionary<Basis, double[]>();
        foreach (var selector in selectors)
        {
          Comparison c = selector.Value;
          if (c.Asset ? !assetSet.Contains(symbol) : !globalSet.Contains(symbol)) continue;
          Period[] bars = c.Aligned ? aligned : rolling;
          if (!measures.TryGetValue(c.Basis, out var observations))
          {
            observations = new double[ReferenceCount + 2];
            for (int i = 0; i < observations.Length; i++)
              observations[i] = Measure(c.Basis, i, bars, benchmark, trend);
            measures[c.Basis] = observations;
          }
          if (c.Asset)
            local[selector.Key] = Ranked(observations, c.Change, asOf, c.Aligned ? alignedLast : last);
        }
        if (local.Count > 0) assets[symbol] = local;
        if (!globalSet.Contains(symbol)) continue;
        foreach (var pair in aggregate)
        {
          if (!measures.TryGetValue(pair.Key, out var observations)) continue;
          for (int i = 0; i < observations.Length; i++)
            pair.Value.Add(i, observations[i], i < aligned.Length ? aligned[i].Quote : double.NaN);
        }
      }
      foreach (var selector in selectors.Where(pair => !pair.Value.Asset))
      {
        var c = selector.Value;
        var windows = aggregate[c.Basis];
        int used = ReferenceCount + (c.Change ? 2 : 1);
        var series = new double[used];
        int minimumValid = globalSet.Count;
        for (int i = 0; i < used; i++)
        {
          series[i] = windows.Value(i, globalSet.Count);
          minimumValid = Math.Min(minimumValid, windows.Count(i));
        }
        var rank = Ranked(series, c.Change, asOf, c.Aligned ? alignedLast : last);
        global[selector.Key] = MarketMetricCoverage.Apply(rank, minimumValid, globalSet.Count, asOf);
      }
      return new Phase1DFeatureResult { GlobalMetrics = global, AssetMetrics = assets };
    }

    // Match ordinary rolling-return / price-path endpoints and UTC-aligned volume/flow/correlation endpoints.
    private static Period[] Periods(MarketDataSeriesSnapshot snapshot, long endpoint, long step, int candles,
      int count, bool efficiency, Dictionary<long, double> fx, bool rolling = false)
    {
      if (count == 0) return Array.Empty<Period>();
      var result = new Period[count];
      Array.Fill(result, Period.Missing);
      if (snapshot == null || snapshot.IsStale) return result;
      double ConvertAt(long time) => fx == null ? 1 : fx.TryGetValue(time, out double value) ? value : double.NaN;
      var all = snapshot.ClosedCandles;
      int cursor = all.Count - 1;
      for (int i = 0; i < count; i++)
      {
        long end = endpoint - (long)i * candles * step;
        while (cursor >= 0 && all[cursor].CloseTimeUtc.ToUnixTimeMilliseconds() > end) cursor--;
        int first = cursor - candles + 1;
        if (cursor < 0) break;
        if (all[cursor].CloseTimeUtc.ToUnixTimeMilliseconds() != end) continue;
        if (first < 0 || all[first].CloseTimeUtc.ToUnixTimeMilliseconds() != end - (candles - 1L) * step)
        {
          // Population breadth uses the two pinned price endpoints. An interior gap
          // cannot supply path, flow or correlation, but it does not erase an observed return.
          if (rolling)
          {
            long target = end - candles * step;
            int index = cursor - 1;
            while (index >= 0 && all[index].CloseTimeUtc.ToUnixTimeMilliseconds() > target) index--;
            if (index >= 0 && all[index].CloseTimeUtc.ToUnixTimeMilliseconds() == target)
            {
              double before = all[index].Close * ConvertAt(target);
              double after = all[cursor].Close * ConvertAt(end);
              if (before > 0 && after > 0)
                result[i] = Period.Missing with { PriceReturn = after / before - 1 };
            }
          }
          continue;
        }
        int reference = first - 1;
        double anchor = reference >= 0 && all[reference].CloseTimeUtc.ToUnixTimeMilliseconds() == end - candles * step
          ? all[reference].Close : double.NaN;
        double quote = 0, buy = 0, travel = 0, netLog = 0;
        double anchorFx = ConvertAt(end - candles * step);
        double previous = anchor, previousFx = anchorFx;
        double finalFx = ConvertAt(end);
        bool complete = true, completeFx = true;
        for (int j = first; j <= cursor; j++)
        {
          MarketCandle candle = all[j];
          if (candle.CloseTimeUtc.ToUnixTimeMilliseconds() != end - (cursor - j) * step)
          {
            complete = false;
            break;
          }
          if (!rolling && fx != null && !double.IsFinite(ConvertAt(end - (cursor - j) * step))) completeFx = false;
          double q = candle.QuoteVolume;
          quote += q >= 0 ? q : double.NaN;
          double b = candle.TakerBuyQuoteVolume ?? double.NaN;
          buy += b >= 0 && b <= q ? b : double.NaN;
          if (efficiency)
          {
            double move = previous > 0 && candle.Close > 0 ? Math.Log(candle.Close) - Math.Log(previous) : double.NaN;
            if (fx != null)
            {
              double rate = ConvertAt(end - (cursor - j) * step);
              move += Math.Log(rate) - Math.Log(previousFx);
              previousFx = rate;
            }
            travel += Math.Abs(move);
            netLog += move;
          }
          previous = candle.Close;
        }
        if (!complete) continue;
        double close = all[cursor].Close;
        double priceReturn = completeFx && anchor > 0 && close > 0 ? (close * finalFx) / (anchor * anchorFx) - 1 : double.NaN;
        double path = travel > 0 && double.IsFinite(travel) && anchor > 0 && close > 0
          ? Math.Min(1, Math.Abs(netLog) / travel) : double.NaN;
        double direction = double.IsFinite(all[first].Open) && double.IsFinite(close) && all[first].Open > 0 && close > 0 ? Math.Sign(close - all[first].Open) : double.NaN;
        result[i] = new Period(priceReturn, path, quote, buy, direction);
        cursor = first - 1;
      }
      return result;
    }

    private static double Measure(Basis basis, int i, Period[] bars, Period[] benchmark, MarketTrend trend)
    {
      if (i >= bars.Length) return double.NaN;
      Period bar = bars[i];
      switch (basis)
      {
        case Basis.Breadth:
          return double.IsFinite(bar.PriceReturn) ? (bar.PriceReturn > 0 ? 100 : 0) : double.NaN;
        case Basis.Efficiency:
          return bar.Efficiency;
        case Basis.Expansion:
        case Basis.ExpansionBreadth:
          int length = trend.VolumeExpansionLookback;
          if (length < 1 || i + length >= bars.Length) return double.NaN;
          double total = 0;
          for (int j = i + 1; j <= i + length; j++) total += bars[j].Quote;
          double expansion = total > 0 && double.IsFinite(total) ? bar.Quote / (total / length) : double.NaN;
          if (basis == Basis.Expansion || !double.IsFinite(expansion)) return expansion;
          return expansion > trend.VolumeExpansionBreadthThreshold ? 100 : 0;
        case Basis.Correlation:
          return Correlation(i, bars, benchmark, trend.CorrelationLookback);
        case Basis.Imbalance:
          return bar.Quote > 0 ? 2 * bar.TakerBuy / bar.Quote - 1 : double.NaN;
        case Basis.BuyerFlow:
        case Basis.SellerFlow:
          if (!double.IsFinite(bar.TakerBuy) || !double.IsFinite(bar.Quote) || !double.IsFinite(bar.PriceDirection))
            return double.NaN;
          double net = 2 * bar.TakerBuy - bar.Quote;
          int sign = basis == Basis.BuyerFlow ? 1 : -1;
          return sign * bar.PriceDirection > 0 && sign * net > 0 ? Math.Abs(net) : 0;
        default:
          throw new ArgumentOutOfRangeException(nameof(basis));
      }
    }

    private static double Correlation(int i, Period[] bars, Period[] benchmark, int length)
    {
      if (length < SynchronizedReturnCorrelationCalculator.MinimumLookback ||
          length > SynchronizedReturnCorrelationCalculator.MaximumLookback ||
          i + length > Math.Min(bars.Length, benchmark.Length)) return double.NaN;
      // Preserve native chronological summation and the near-constant variance
      // policy from SynchronizedReturnCorrelationCalculator without making 25 return-vector copies.
      double meanX = 0, meanY = 0;
      for (int k = i + length - 1; k >= i; k--)
      {
        meanX += bars[k].PriceReturn;
        meanY += benchmark[k].PriceReturn;
      }
      meanX /= length;
      meanY /= length;
      double scaleX = 0, scaleY = 0, maxX = 0, maxY = 0;
      for (int k = i; k < i + length; k++)
      {
        double x = bars[k].PriceReturn, y = benchmark[k].PriceReturn;
        scaleX = Math.Max(scaleX, Math.Abs(x - meanX));
        scaleY = Math.Max(scaleY, Math.Abs(y - meanY));
        maxX = Math.Max(maxX, Math.Abs(x));
        maxY = Math.Max(maxY, Math.Abs(y));
      }
      if (!(scaleX > Math.Max(1e-15, maxX * 1e-12)) ||
          !(scaleY > Math.Max(1e-15, maxY * 1e-12))) return double.NaN;
      double xx = 0, yy = 0, xy = 0;
      for (int k = i + length - 1; k >= i; k--)
      {
        double x = (bars[k].PriceReturn - meanX) / scaleX;
        double y = (benchmark[k].PriceReturn - meanY) / scaleY;
        xx += x * x;
        yy += y * y;
        xy += x * y;
      }
      return Math.Clamp(xy / Math.Sqrt(xx * yy), -1, 1);
    }

    private sealed class PopulationWindows
    {
      private readonly Basis basis;
      private readonly List<double>[] valid;
      private readonly double[] weights;
      private readonly double[] sums;
      private readonly int[] counts;
      private bool Median => basis is Basis.Correlation or Basis.Efficiency or Basis.Expansion;
      public PopulationWindows(int length, Basis basis)
      {
        this.basis = basis;
        valid = Median ? Enumerable.Range(0, length).Select(_ => new List<double>()).ToArray() : null;
        sums = new double[length];
        counts = new int[length];
        weights = new double[length];
      }
      public void Add(int index, double value, double weight)
      {
        if (!double.IsFinite(value)) return;
        counts[index]++;
        if (Median) valid[index].Add(value);
        else
        {
          sums[index] += value;
          weights[index] += weight;
        }
      }
      public int Count(int index) => counts[index];
      public double Value(int index, int population)
      {
        if (!MarketMetricCoverage.IsReady(counts[index], population)) return double.NaN;
        if (basis is Basis.BuyerFlow or Basis.SellerFlow)
          return weights[index] > 0 ? sums[index] / weights[index] * 100 : double.NaN;
        if (basis is Basis.Breadth or Basis.ExpansionBreadth) return sums[index] / counts[index];
        var values = valid[index];
        values.Sort();
        int mid = values.Count / 2;
        return values.Count % 2 == 0 ? (values[mid - 1] + values[mid]) / 2 : values[mid];
      }
    }

    private static FeatureValueSnapshot Ranked(double[] series, bool change, DateTimeOffset asOf, long endpoint)
    {
      double Value(int i) => change ? series[i] - series[i + 1] : series[i];
      double current = Value(0);
      int lower = 0, tied = 0, valid = 0;
      for (int i = 1; i <= ReferenceCount; i++)
      {
        double previous = Value(i);
        if (!double.IsFinite(previous)) continue;
        valid++;
        if (previous < current) lower++;
        else if (previous == current) tied++;
      }
      bool ready = double.IsFinite(current) && valid == ReferenceCount;
      return new FeatureValueSnapshot
      {
        IsValid = ready, Value = ready ? 100.0 * (lower + tied * 0.5) / ReferenceCount : null,
        SourceDataTimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(endpoint), CalculatedAtUtc = asOf,
        SampleCount = valid,
        InvalidReason = ready ? "" : "Requires a valid current observation and 24 complete earlier equivalent observations."
      };
    }
  }
}
