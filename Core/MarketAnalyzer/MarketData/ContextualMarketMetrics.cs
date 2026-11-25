using System;
using System.Collections.Generic;
using System.Linq;
using Core.Main.DataObjects.PTMagicData;

namespace Core.MarketAnalyzer.MarketData
{
  /// <summary>Continuous measurements on the pinned MarketTrend populations and observation time.</summary>
  public static class ContextualMarketMetrics
  {
    public const string NetConfirmedFlowPressure = "NetConfirmedFlowPressure";
    public const string BuyingPressureOnDeclines = "BuyingPressureOnDeclines";
    public const string BuyingPressureOnAdvances = "BuyingPressureOnAdvances";
    public const string MeanBuyerImbalanceExcess = "MeanBuyerImbalanceExcess";
    public const string MeanVolumeExpansionExcess = "MeanVolumeExpansionExcess";
    public const string FundingRateVsHistory = "FundingRateVsHistory";
    public const string MedianFundingRateVsHistory = "MedianFundingRateVsHistory";
    public const string PositiveFundingPressure = "PositiveFundingPressure";
    public const string NegativeFundingPressure = "NegativeFundingPressure";

    public const string ProjectedFundingCostInPriorATR = "ProjectedFundingCostInPriorATR";
    public const string MedianProjectedFundingCostInPriorATR = "MedianProjectedFundingCostInPriorATR";

    private static readonly string[] TakerNames = { NetConfirmedFlowPressure,
      BuyingPressureOnDeclines, BuyingPressureOnAdvances, MeanBuyerImbalanceExcess };
    private static readonly string[] GlobalFundingNames = { MedianFundingRateVsHistory, PositiveFundingPressure, NegativeFundingPressure };

    public static bool IsTaker(string metric) => TakerNames.Contains(metric, StringComparer.OrdinalIgnoreCase);
    public static bool IsProjectedFunding(string metric) => String.Equals(metric, ProjectedFundingCostInPriorATR, StringComparison.OrdinalIgnoreCase)
      || String.Equals(metric, MedianProjectedFundingCostInPriorATR, StringComparison.OrdinalIgnoreCase);
    public static bool IsGlobalFunding(string metric) => GlobalFundingNames.Contains(metric, StringComparer.OrdinalIgnoreCase)
      || String.Equals(metric, MedianProjectedFundingCostInPriorATR, StringComparison.OrdinalIgnoreCase);
    public static int FundingHistoryMinutes(int trendMinutes, string metric) => IsProjectedFunding(metric)
      ? Math.Max(2 * 24 * 60, checked(2 * trendMinutes)) : checked(2 * trendMinutes);
    public static bool IsFunding(string metric) => IsProjectedFunding(metric) || IsGlobalFunding(metric) || String.Equals(metric, FundingRateVsHistory, StringComparison.OrdinalIgnoreCase);
    public static bool NeedsFunding(IEnumerable<string> metrics) => metrics != null && metrics.Any(IsFunding);
    public static bool NeedsTaker(IEnumerable<string> metrics) => metrics != null && metrics.Any(IsTaker);
    public static bool IsMetric(string metric) => IsFunding(metric) || IsTaker(metric) || String.Equals(metric, MeanVolumeExpansionExcess, StringComparison.OrdinalIgnoreCase);

    public static FlowPriceConfirmationResult Calculate(MarketTrend trend, IReadOnlyCollection<string> requested,
      IEnumerable<string> globalSymbols, IEnumerable<string> assetSymbols, MarketDataStore store, TimeSpan interval,
      DateTimeOffset asOf, string exchange, FuturesFundingDataService fundingData,
      IReadOnlyDictionary<string, AlignedTakerFlowPeriod> alignedPeriods = null,
      IReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> existingAssetMetrics = null)
    {
      var selected = new HashSet<string>(requested ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
      var global = new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
      var assets = new Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>>(StringComparer.OrdinalIgnoreCase);
      if (selected.Any(IsMetric))
      {
        string[] symbols = Normalize(globalSymbols);
        if (selected.Any(IsTaker))
        {
          if (alignedPeriods == null)
          {
            var captured = new Dictionary<string, AlignedTakerFlowPeriod>(StringComparer.OrdinalIgnoreCase);
            TakerFlowFeatureEngine.Calculate(Array.Empty<string>(), new[] { MarketTrendMetrics.MedianTakerBuyRatio },
              store, interval, trend.TrendMinutes, asOf, symbols, captured);
            alignedPeriods = captured;
          }
          AddFlow(trend, selected, symbols, alignedPeriods, asOf, global);
        }
        if (selected.Contains(MeanVolumeExpansionExcess))
          global[MeanVolumeExpansionExcess] = VolumeExcess(trend, symbols, existingAssetMetrics, store, interval, asOf);
        if (selected.Any(IsProjectedFunding))
          AddProjectedFunding(trend, selected, symbols, Normalize(assetSymbols), store, interval, fundingData, asOf, exchange, global, assets);
        if (selected.Any(metric => IsFunding(metric) && !IsProjectedFunding(metric)))
          AddFunding(trend.TrendMinutes, selected, symbols, Normalize(assetSymbols), fundingData, asOf, exchange, global, assets);
      }
      return new FlowPriceConfirmationResult { GlobalMetrics = global, AssetMetrics = assets };
    }

    private static string[] Normalize(IEnumerable<string> symbols) => (symbols ?? Array.Empty<string>())
      .Where(s => !String.IsNullOrWhiteSpace(s)).Select(s => s.Trim().ToUpperInvariant())
      .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static DateTimeOffset PeriodClose(DateTimeOffset asOf, int minutes)
    {
      long duration = checked((long)minutes * 60000);
      return DateTimeOffset.FromUnixTimeMilliseconds(asOf.ToUnixTimeMilliseconds() / duration * duration - 1);
    }

    private static FeatureValueSnapshot Sample(double value, DateTimeOffset asOf, DateTimeOffset? source,
      int count, string reason = "")
    {
      bool valid = Double.IsFinite(value) && source.HasValue;
      return new FeatureValueSnapshot { Value = valid ? value : null, IsValid = valid, CalculatedAtUtc = asOf,
        SourceDataTimestampUtc = source, SampleCount = count, InvalidReason = valid ? "" : reason };
    }

    private static void AddFlow(MarketTrend trend, HashSet<string> selected, string[] symbols,
      IReadOnlyDictionary<string, AlignedTakerFlowPeriod> periods, DateTimeOffset asOf,
      Dictionary<string, FeatureValueSnapshot> output)
    {
      var close = PeriodClose(asOf, trend.TrendMinutes);
      double total = 0, buyerConfirm = 0, sellerConfirm = 0, buyerDiverge = 0, sellerDiverge = 0, buyExcess = 0;
      int observed = 0, traded = 0;
      foreach (string symbol in symbols)
      {
        if (!periods.TryGetValue(symbol, out var period)) continue;
        var ratio = period?.TakerBuyRatio;
        if (ratio == null || ratio.IsStale || ratio.SourceDataTimestampUtc != close) continue;
        if (ratio.IsNoTrades && !ratio.IsValid && !ratio.Value.HasValue && ratio.SampleCount > 0 && period.QuoteVolume == 0)
        {
          observed++;
          continue;
        }
        double buy = ratio.Value ?? Double.NaN, quote = period.QuoteVolume ?? Double.NaN;
        double price = period.PriceReturnPercent ?? Double.NaN;
        if (!ratio.IsValid || !Double.IsFinite(price) || !Double.IsFinite(quote) || quote <= 0 ||
            !Double.IsFinite(buy) || buy < 0 || buy > 1) continue;
        observed++;
        traded++;
        total += quote;
        double imbalance = 2 * buy - 1;
        buyExcess += Math.Max(0, imbalance);
        double signedQuote = quote * imbalance;
        if (price > 0)
        {
          buyerConfirm += Math.Max(signedQuote, 0);
          sellerDiverge += Math.Max(-signedQuote, 0);
        }
        else if (price < 0)
        {
          sellerConfirm += Math.Max(-signedQuote, 0);
          buyerDiverge += Math.Max(signedQuote, 0);
        }
      }
      foreach (string name in TakerNames.Where(selected.Contains))
      {
        double value = name switch {
          MeanBuyerImbalanceExcess => traded > 0 ? buyExcess / traded : Double.NaN,
          NetConfirmedFlowPressure => buyerConfirm - sellerConfirm,
          BuyingPressureOnDeclines => buyerDiverge - sellerConfirm,
          _ => buyerConfirm - sellerDiverge
        };
        bool equalWeighted = name == MeanBuyerImbalanceExcess;
        // No-trade observations verify zero dollars but cannot define a per-contract taker ratio.
        int validCount = equalWeighted ? traded : observed;
        if (!equalWeighted) value = total > 0 && double.IsFinite(total) ? 100 * (value / total) : Double.NaN;
        output[name] = MarketMetricCoverage.Apply(Sample(value, asOf, close, validCount,
          "Complete aligned traded quote volume is unavailable."), validCount, symbols.Length, asOf);
      }
    }

    private static FeatureValueSnapshot VolumeExcess(MarketTrend trend, string[] symbols,
      IReadOnlyDictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> existing,
      MarketDataStore store, TimeSpan interval, DateTimeOffset asOf)
    {
      var close = PeriodClose(asOf, trend.TrendMinutes);
      int valid = 0;
      double sum = 0;
      foreach (string symbol in symbols)
      {
        FeatureValueSnapshot expansion = null;
        if (existing != null && existing.TryGetValue(symbol, out var metrics))
          metrics.TryGetValue(MarketTrendMetrics.VolumeExpansion, out expansion);
        if (expansion == null)
          expansion = TrendPeriodMetricsCalculator.Calculate(store.GetSnapshot(symbol, interval, asOf),
            trend.TrendMinutes, asOf, trend.ATRPeriod, trend.VolumeExpansionLookback, false, true, false).VolumeExpansion;
        double x = expansion?.Value ?? Double.NaN;
        if (expansion?.IsValid != true || expansion.IsStale || expansion.SourceDataTimestampUtc != close ||
            !Double.IsFinite(x) || x < 0) continue;
        // x = current quote volume / strictly prior mean. Bound each member's contribution:
        // a thin baseline cannot grant one contract the weight of many expanding contracts.
        sum += x > 1 ? (x - 1) / (x + 1) : 0;
        valid++;
      }
      return MarketMetricCoverage.Apply(Sample(valid > 0 ? sum / valid : Double.NaN, asOf, close, valid,
        "No complete positive prior volume baseline."), valid, symbols.Length, asOf);
    }

    private static void AddFunding(int minutes, HashSet<string> selected, string[] globalSymbols, string[] assetSymbols,
      FuturesFundingDataService data, DateTimeOffset asOf, string exchange,
      Dictionary<string, FeatureValueSnapshot> global,
      Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> asset)
    {
      bool aggregate = GlobalFundingNames.Any(selected.Contains), publishAsset = selected.Contains(FundingRateVsHistory);
      var globalSet = new HashSet<string>(globalSymbols, StringComparer.OrdinalIgnoreCase);
      var assets = new HashSet<string>(assetSymbols, StringComparer.OrdinalIgnoreCase);
      var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      if (aggregate) required.UnionWith(globalSet);
      if (publishAsset) required.UnionWith(assets);
      var deviations = new List<double>();
      double positive = 0, negative = 0;
      DateTimeOffset? oldest = null;
      foreach (string symbol in required)
      {
        IReadOnlyList<FuturesFundingObservation> history = null;
        if (String.Equals(exchange, "BinanceFutures", StringComparison.OrdinalIgnoreCase) && data != null)
          data.TrySnapshot(symbol, checked(2 * minutes), asOf, out history);
        var sample = SettlementDeviation(history, minutes, asOf, out var dailyRate);
        if (publishAsset && assets.Contains(symbol))
          PublishAsset(asset, symbol, FundingRateVsHistory, sample);
        if (!aggregate || !globalSet.Contains(symbol) || !sample.IsValid) continue;
        double change = sample.Value.Value;
        deviations.Add(change);
        if (dailyRate > 0) positive += Math.Max(change, 0);
        if (dailyRate < 0) negative += Math.Max(-change, 0);
        if (!oldest.HasValue || sample.SourceDataTimestampUtc < oldest) oldest = sample.SourceDataTimestampUtc;
      }
      foreach (string name in GlobalFundingNames.Where(selected.Contains))
      {
        int count = deviations.Count;
        double value = count == 0 ? Double.NaN : name == MedianFundingRateVsHistory ? Median(deviations) :
          (name == PositiveFundingPressure ? positive : negative) / count;
        global[name] = MarketMetricCoverage.Apply(Sample(value, asOf, oldest, count,
          "No comparable, verified prior regular settlement intervals."), count, globalSymbols.Length, asOf);
      }
    }

    private static FeatureValueSnapshot SettlementDeviation(IReadOnlyList<FuturesFundingObservation> history,
      int minutes, DateTimeOffset asOf, out double current)
    {
      current = Double.NaN;
      var regular = (history ?? Array.Empty<FuturesFundingObservation>()).Where(r => r.IsRegular)
        .OrderBy(r => r.FundingTimeUtc).ToArray();
      bool unknown = history == null || history.Any(r => !r.IsRegular && !String.Equals(r.RateType, "Special", StringComparison.OrdinalIgnoreCase));
      if (unknown || regular.Length < 3 || regular[^1].FundingTimeUtc <= asOf.AddMinutes(-minutes))
        return Sample(Double.NaN, asOf, null, regular.Length,
          "Requires coverage of two trend horizons, the latest Regular settlement within the current horizon, and at least two complete observed settlement intervals.");
      var previous = new List<double>(regular.Length - 2);
      for (int i = 1; i < regular.Length; i++)
      {
        // Compare elapsed settlement minutes. Millisecond publishing jitter inside a
        // scheduled minute must not make identical rates on identical schedules reprice.
        long elapsed = regular[i].FundingTimeUtc.ToUnixTimeMilliseconds() / 60000
          - regular[i - 1].FundingTimeUtc.ToUnixTimeMilliseconds() / 60000;
        if (elapsed <= 0) return Sample(Double.NaN, asOf, regular[i].FundingTimeUtc, i, "Overlapping regular settlement minutes.");
        double rate = (double)regular[i].NativeRate * (100.0 * 1440.0 / elapsed);
        if (i == regular.Length - 1) current = rate;
        else previous.Add(rate);
      }
      return Sample(current - Median(previous), asOf, regular[^1].FundingTimeUtc, regular.Length - 1,
        "Funding daily-rate comparison is nonfinite.");
    }

    private static void PublishAsset(Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> output,
      string symbol, string metric, FeatureValueSnapshot sample)
    {
      var values = output.TryGetValue(symbol, out var earlier) ? new Dictionary<string, FeatureValueSnapshot>(earlier,
        StringComparer.OrdinalIgnoreCase) : new Dictionary<string, FeatureValueSnapshot>(StringComparer.OrdinalIgnoreCase);
      values[metric] = sample;
      output[symbol] = values;
    }

    private static void AddProjectedFunding(MarketTrend trend, HashSet<string> selected, string[] globalSymbols,
      string[] assetSymbols, MarketDataStore store, TimeSpan interval, FuturesFundingDataService data,
      DateTimeOffset asOf, string exchange, Dictionary<string, FeatureValueSnapshot> global,
      Dictionary<string, IReadOnlyDictionary<string, FeatureValueSnapshot>> asset)
    {
      bool aggregate = selected.Contains(MedianProjectedFundingCostInPriorATR);
      bool publish = selected.Contains(ProjectedFundingCostInPriorATR);
      var globalSet = new HashSet<string>(globalSymbols, StringComparer.OrdinalIgnoreCase);
      var assetSet = new HashSet<string>(assetSymbols, StringComparer.OrdinalIgnoreCase);
      var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      if (aggregate) required.UnionWith(globalSet);
      if (publish) required.UnionWith(assetSet);
      int historyMinutes = FundingHistoryMinutes(trend.TrendMinutes, ProjectedFundingCostInPriorATR);
      var referenceAsOf = asOf.AddMinutes(-trend.TrendMinutes);
      var costs = new List<double>();
      DateTimeOffset? oldest = null;
      foreach (string symbol in required)
      {
        IReadOnlyList<FuturesFundingObservation> history = null;
        if (String.Equals(exchange, "BinanceFutures", StringComparison.OrdinalIgnoreCase) && data != null)
          data.TrySnapshot(symbol, historyMinutes, asOf, out history);
        var scheduled = ProjectRegularPayments(history, trend.TrendMinutes, asOf);
        var cost = scheduled;
        if (scheduled.IsValid)
        {
          var atr = TrendPeriodMetricsCalculator.Calculate(store.GetSnapshot(symbol, interval, referenceAsOf),
            trend.TrendMinutes, referenceAsOf, trend.ATRPeriod, trend.VolumeExpansionLookback, true, false, false).ATRP;
          double prior = atr?.Value ?? Double.NaN;
          bool valid = atr?.IsValid == true && !atr.IsStale && atr.SourceDataTimestampUtc.HasValue && prior > 0 && Double.IsFinite(prior);
          // Verified funding coverage reaches the current raid even between settlements.
          // An unchanged last payment may enter the forecast window as the clock advances.
          // Use the closed prior ATR endpoint as the older boundary of the two verified inputs.
          cost = Sample(valid ? scheduled.Value.Value / prior : Double.NaN, asOf, atr?.SourceDataTimestampUtc, scheduled.SampleCount,
            "Requires a positive source-verified prior-window ATR percentage for the same contract.");
        }
        if (publish && assetSet.Contains(symbol)) PublishAsset(asset, symbol, ProjectedFundingCostInPriorATR, cost);
        if (!aggregate || !globalSet.Contains(symbol) || !cost.IsValid) continue;
        costs.Add(cost.Value.Value);
        if (!oldest.HasValue || cost.SourceDataTimestampUtc < oldest) oldest = cost.SourceDataTimestampUtc;
      }
      if (aggregate)
        global[MedianProjectedFundingCostInPriorATR] = MarketMetricCoverage.Apply(
          Sample(costs.Count == 0 ? Double.NaN : Median(costs), asOf, oldest, costs.Count,
            "No verified scheduled funding cost and positive prior ATR pairs."), costs.Count, globalSymbols.Length, asOf);
    }

    private static FeatureValueSnapshot ProjectRegularPayments(IReadOnlyList<FuturesFundingObservation> history,
      int minutes, DateTimeOffset asOf)
    {
      var regular = (history ?? Array.Empty<FuturesFundingObservation>()).Where(r => r.IsRegular)
        .OrderBy(r => r.FundingTimeUtc).ToArray();
      if (regular.Length < 2 || history.Any(r => !r.IsRegular && !String.Equals(r.RateType, "Special", StringComparison.OrdinalIgnoreCase)))
        return Sample(Double.NaN, asOf, null, regular.Length, "No verified pair of regular settlements for projecting cadence.");
      var last = regular[^1];
      // Forecast only from previously settled observations. Both the next rate and schedule may change.
      long latest = last.FundingTimeUtc.ToUnixTimeMilliseconds() / 60000;
      long previous = regular[^2].FundingTimeUtc.ToUnixTimeMilliseconds() / 60000;
      long pin = asOf.ToUnixTimeMilliseconds() / 60000;
      long cadence = latest - previous;
      if (cadence <= 0 || latest > pin || pin - latest >= cadence)
        return Sample(Double.NaN, asOf, last.FundingTimeUtc, regular.Length, "The last observed regular cadence is invalid or overdue.");
      long payments = (pin - latest + minutes) / cadence;
      return Sample((double)last.NativeRate * 100.0 * payments, asOf, last.FundingTimeUtc, regular.Length,
        "Nonfinite projected funding cost.");
    }

    private static double Median(List<double> values)
    {
      values.Sort();
      int mid = values.Count / 2;
      return values.Count % 2 != 0 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }
  }
}
