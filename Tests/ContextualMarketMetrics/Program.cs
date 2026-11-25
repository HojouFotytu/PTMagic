using Core.Helper;
using Core.Main.DataObjects.PTMagicData;
using Core.MarketAnalyzer.MarketData;
using Microsoft.Extensions.Logging.Abstractions;
using M = Core.MarketAnalyzer.MarketData.ContextualMarketMetrics;

var pin = DateTimeOffset.Parse("2026-11-25T08:03:00Z");
var close = DateTimeOffset.Parse("2026-11-25T07:59:59.999Z");
var interval = TimeSpan.FromMinutes(5);
var names = new[] { "AAAUSDT", "BBBUSDT", "CCCUSDT", "DDDUSDT" };
var trend = new MarketTrend { Name = "Flow", TrendMinutes = 60, VolumeExpansionLookback = 1 };
var store = new MarketDataStore(30);
int checks = 0;
void Check(bool pass, string message)
{
  if (!pass) throw new Exception(message);
  checks++;
}
void Near(FeatureValueSnapshot sample, double value, string message)
{
  Check(sample.IsValid && sample.Value.HasValue && Math.Abs(sample.Value.Value - value) < 1e-10, message);
}
AlignedTakerFlowPeriod Period(double price, double ratio, double quote, DateTimeOffset? stamp = null) =>
  new() { TakerBuyRatio = new() { Value = ratio, IsValid = true, SourceDataTimestampUtc = stamp ?? close, SampleCount = 12 },
    PriceReturnPercent = price, QuoteVolume = quote };
var periods = new Dictionary<string,AlignedTakerFlowPeriod> {
  [names[0]] = Period(2,.90,100), [names[1]] = Period(-1,.30,200),
  [names[2]] = Period(-1,.60,50), [names[3]] = Period(2,.20,50)
};
var flowNames = new[] { M.NetConfirmedFlowPressure,
  M.BuyingPressureOnDeclines, M.BuyingPressureOnAdvances, M.MeanBuyerImbalanceExcess };
FlowPriceConfirmationResult Flow() => M.Calculate(trend, flowNames, names, names, store, interval, pin, "BinanceFutures", null, periods);
var result = Flow().GlobalMetrics;
Near(result[M.NetConfirmedFlowPressure], 0, "opposing confirmed dollars cancel despite very different returns and volumes");
Near(result[M.BuyingPressureOnDeclines], -17.5, "buyers absorbing declining prices offset confirmed selling");
Near(result[M.BuyingPressureOnAdvances], 12.5, "sellers leaning on increasing prices offset confirmed buying");
Near(result[M.MeanBuyerImbalanceExcess], .25, "positive taker imbalance averaged across all traded contracts");
periods[names[0]] = Period(2,.90,100,close.AddHours(-1));
Check(!Flow().GlobalMetrics[M.NetConfirmedFlowPressure].IsValid,"stale-complete prior hour cannot fill the present");
periods[names[0]] = new() { TakerBuyRatio = new() { IsNoTrades = true, SampleCount = 12, SourceDataTimestampUtc = close }, QuoteVolume = 0 };
result = Flow().GlobalMetrics;
Check(result[M.NetConfirmedFlowPressure].IsValid && result[M.NetConfirmedFlowPressure].SampleCount == 4,"confirmed no-trade observation counts toward dollar coverage");
Check(!result[M.MeanBuyerImbalanceExcess].IsValid,"no trades cannot imply known taker imbalance");

var ratios = new double[] { 0, 1, 3, 100 };
var previous = names.Select((s,i)=>(s,i)).ToDictionary(a=>a.s,
  a=>(IReadOnlyDictionary<string,FeatureValueSnapshot>)new Dictionary<string,FeatureValueSnapshot> {
    ["VolumeExpansion"] = new() { IsValid = true, Value = ratios[a.i], SourceDataTimestampUtc = close } });
FeatureValueSnapshot Volume() => M.Calculate(trend,new[]{M.MeanVolumeExpansionExcess},names,names,store,interval,pin,
  "BinanceFutures",null,null,previous).GlobalMetrics[M.MeanVolumeExpansionExcess];
Near(Volume(),(.5+99.0/101)/4,"quiet and baseline-level contracts remain in the denominator; one extreme contract contributes under one vote");
previous[names[0]] = new Dictionary<string,FeatureValueSnapshot> {["VolumeExpansion"] = new() { InvalidReason="Zero prior baseline" }};
Check(!Volume().IsValid && Volume().SampleCount==3,"unknown baseline differs from a valid zero current volume");

var day = new MarketTrend { Name="Day24", TrendMinutes=1440,
  Metrics=String.Join(",",new[] { M.FundingRateVsHistory,M.MedianFundingRateVsHistory,M.PositiveFundingPressure,M.NegativeFundingPressure }) };
var data = new FuturesFundingDataService();
var log = new LogHelper(NullLogger<LogHelper>.Instance);
var original = new Dictionary<string,List<FuturesFundingObservation>>();
foreach(var (symbol,sid) in names.Select((s,i)=>(s,i)))
{
  var rows = new List<FuturesFundingObservation>();
  decimal sign = sid % 2==0 ? 1 : -1;
  foreach(var (hours,raw) in new[]{(-34,0.0002m),(-26,0.0002m),(-18,0.0002m),(-14,0.0001m),(-10,0.0001m),(-2,sid switch {0 or 1 => 0.0004m, 2=>0.0002m,_=>0.0001m})})
    rows.Add(new() { Symbol=symbol, RateType="Regular", NativeRate=sign*raw, FundingTimeUtc=pin.AddHours(hours).AddMilliseconds(rows.Count % 4) });
  rows.Add(new() { Symbol=symbol, RateType="Special", NativeRate=0.9m, FundingTimeUtc=pin.AddHours(-1) });
  original[symbol]=rows;
}
await data.RefreshAsync(names,2880,pin,5,(s,from,to,limit,ct)=>Task.FromResult((IReadOnlyList<FuturesFundingObservation>)
  original[s].Where(r=>r.FundingTimeUtc>=from && r.FundingTimeUtc<=to).ToArray()),log);
var fundingNames=day.Metrics.Split(',');
FlowPriceConfirmationResult Funding(DateTimeOffset time, string exchange="BinanceFutures") =>
  M.Calculate(day,fundingNames,names,names,store,interval,time,exchange,data);
var funding = Funding(pin);
Near(funding.AssetMetrics[names[0]][M.FundingRateVsHistory],.06,"8h payment doubles relative to earlier 4h and 8h payments normalized to actual accrual minutes");
Near(funding.AssetMetrics[names[2]][M.FundingRateVsHistory],0,"unchanged daily carry despite schedule variation and publication jitter");
Near(funding.AssetMetrics[names[3]][M.FundingRateVsHistory],.03,"less negative remains a signed improvement");
Near(funding.GlobalMetrics[M.MedianFundingRateVsHistory],.015,"median includes improving negative-payer with correct sign");
Near(funding.GlobalMetrics[M.PositiveFundingPressure],.015,"only a currently positive payer with higher daily carry contributes");
Near(funding.GlobalMetrics[M.NegativeFundingPressure],.015,"negative payer with easing costs cannot enter the expensive negative tail");
Check(!Funding(pin.AddMinutes(5)).GlobalMetrics[M.PositiveFundingPressure].IsValid,"unverified future interval cannot reuse previous coverage");
Check(!Funding(pin,"Binance").AssetMetrics[names[0]][M.FundingRateVsHistory].IsValid,"settled USD-M metrics unavailable on spot");
Check(FuturesFundingDataService.RequiredHorizonMinutes(new[]{day})==2880,"contextual funding provisions twice the declared horizon even without legacy funding selections");
// Fixed-rate, individually denominated carry. Two contracts share the payment time;
 // a shorter or longer interval to the next expected settlement changes the projected count.
var carryStore = new MarketDataStore(100);
for (int j=0; j<names.Length; j++)
  for(int i=-60;i<0;i++)
  {
    var start = close.AddMilliseconds(1).AddMinutes(i*5);
    carryStore.UpsertCandle(new() { Symbol=names[j], Interval=interval, OpenTimeUtc=start,
      CloseTimeUtc=start.AddMinutes(5).AddMilliseconds(-1), Open=100, Close=100,
      Low=100-(j==1?2:1), High=100+(j==1?2:1), BaseVolume=1, QuoteVolume=100, TradeCount=1, IsClosed=true });
  }
var payments=new FuturesFundingDataService();
async Task LoadPayments(FuturesFundingDataService target, DateTimeOffset through)
{
  await target.RefreshAsync(names,2940,through,5,(symbol,from,to,limit,ct) => {
    int j=Array.IndexOf(names,symbol);
    // A/B are due in 30m and pay +1% per settlement, C is due in 2h, D is due in 30m and pays -1%.
    var latest=pin.AddMinutes(j==2 ? -360 : -450);
    var dates=Enumerable.Range(0,5).Select(i=>latest.AddHours(-8*i));
    return Task.FromResult((IReadOnlyList<FuturesFundingObservation>)dates.Where(t=>t>=from && t<=to)
      .OrderBy(t=>t).Select(t=>new FuturesFundingObservation { Symbol=symbol, RateType="Regular",
        NativeRate=j==3 ? -.01m:.01m, FundingTimeUtc=t }).ToArray());
  },log);
}
await LoadPayments(payments,pin);
var hour=new MarketTrend {Name="TradeHour", Metrics="ProjectedFundingCostInPriorATR,MedianProjectedFundingCostInPriorATR",TrendMinutes=60,ATRPeriod=1};
var projection = MarketTrendAnalyticsEngine.Calculate(hour,names,names,carryStore,interval,pin,
  exchangeName:"BinanceFutures",fundingData:payments);
Near(projection.AssetMetrics[names[0]][M.ProjectedFundingCostInPriorATR],.5,"persistent 1% payer costs half a 2% prior ATR when due");
Near(projection.AssetMetrics[names[1]][M.ProjectedFundingCostInPriorATR],.25,"same actual fee measured against this contract's 4% ATR");
Near(projection.AssetMetrics[names[2]][M.ProjectedFundingCostInPriorATR],0,"no settlement falls inside the next hour");
Near(projection.AssetMetrics[names[3]][M.ProjectedFundingCostInPriorATR],-.5,"short payer has negative signed burden");
Near(projection.MarketMetrics[M.MedianProjectedFundingCostInPriorATR],.125,"median uses individual costs and prior ATRs, including verified zero-carry intervals");
Check(FuturesFundingDataService.RequiredHorizonMinutes(new[]{hour})==2880,"short forecast independently requires verified two-day funding coverage");
var change = MarketTrendDeltaEngine.Calculate(hour,new[]{M.ProjectedFundingCostInPriorATR},projection,
  carryStore,interval,exchangeName:"BinanceFutures",fundingData:payments);
Near(change.AssetMetrics[names[0]][M.ProjectedFundingCostInPriorATR].Delta,.5,
  "a payment entering the forecast window registers even when the latest settlement has not changed");

var later=pin.AddMinutes(31);
var overdue=new FuturesFundingDataService();
await LoadPayments(overdue,later);
var late = M.Calculate(hour,hour.Metrics.Split(','),names,names,carryStore,interval,later,"BinanceFutures",overdue);
Check(!late.AssetMetrics[names[0]][M.ProjectedFundingCostInPriorATR].IsValid,"an overdue expected settlement cannot be projected from a stale cadence");
Console.WriteLine($"Contextual market measurements passed: {checks} assertions.");
