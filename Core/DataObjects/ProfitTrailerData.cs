﻿using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using Core.Main.DataObjects.PTMagicData;

namespace Core.Main.DataObjects
{

  public class ProfitTrailerData
  {
    private SummaryData _summary = null;
    private Properties _properties = null;
    private List<SellLogData> _sellLog = new List<SellLogData>();
    private List<DCALogData> _dcaLog = new List<DCALogData>();
    private List<BuyLogData> _buyLog = new List<BuyLogData>();
    private string _ptmBasePath = "";
    private PTMagicConfiguration _systemConfiguration = null;
    private TransactionData _transactionData = null;
    private DateTime _buyLogRefresh = DateTime.UtcNow, _sellLogRefresh = DateTime.UtcNow, _dcaLogRefresh = DateTime.UtcNow, _summaryRefresh = DateTime.UtcNow, _propertiesRefresh = DateTime.UtcNow;
    private volatile object _buyLock = new object(), _sellLock = new object(), _dcaLock = new object(), _summaryLock = new object(), _propertiesLock = new object();
    private TimeSpan? _offsetTimeSpan = null;

    // Constructor
    public ProfitTrailerData(PTMagicConfiguration systemConfiguration)
    {
      _systemConfiguration = systemConfiguration;
    }

    // Get a time span for the UTC offset from the settings
    private TimeSpan OffsetTimeSpan
    {
      get
      {
        if (!_offsetTimeSpan.HasValue)
        {
          // Get offset for settings.
          _offsetTimeSpan = TimeSpan.Parse(_systemConfiguration.GeneralSettings.Application.TimezoneOffset.Replace("+", ""));
        }

        return _offsetTimeSpan.Value;
      }
    }

    // Get the time with the settings UTC offset applied
    private DateTimeOffset LocalizedTime
    {
      get
      {
        return DateTimeOffset.UtcNow.ToOffset(OffsetTimeSpan);
      }
    }

    public SummaryData Summary
    {
      get
      {
        if (_summary == null || (DateTime.UtcNow > _summaryRefresh))
        {
          lock (_summaryLock)
          {
            // Thread double locking
            if (_summary == null || (DateTime.UtcNow > _summaryRefresh))
            {
              _summary = BuildSummaryData(GetDataFromProfitTrailer("api/v2/data/misc"));
              _summaryRefresh = DateTime.UtcNow.AddSeconds(_systemConfiguration.GeneralSettings.Monitor.RefreshSeconds - 1);
            }
          }
        }

        return _summary;
      }
    }
    public Properties Properties
    {
      get
      {
        if (_properties == null || (DateTime.UtcNow > _propertiesRefresh))
        {
          lock (_propertiesLock)
          {
            // Thread double locking
            if (_properties == null || (DateTime.UtcNow > _propertiesRefresh))
            {
              _properties = BuildProptertiesData(GetDataFromProfitTrailer("api/v2/data/properties"));
              _propertiesRefresh = DateTime.UtcNow.AddSeconds(_systemConfiguration.GeneralSettings.Monitor.RefreshSeconds - 1);
            }
          }
        }

        return _properties;
      }
    }
    public List<SellLogData> SellLog
    {
      get
      {
        if (_sellLog == null || (DateTime.UtcNow > _sellLogRefresh))
        {
          lock (_sellLock)
          {
            // Thread double locking
            if (_sellLog == null || (DateTime.UtcNow > _sellLogRefresh))
            {
              _sellLog.Clear();

              // Page through the sales data summarizing it.
              bool exitLoop = false;
              int pageIndex = 1;

              while (!exitLoop)
              {
                var sellDataPage = GetDataFromProfitTrailer("/api/v2/data/sales?perPage=5000&sort=SOLDDATE&sortDirection=ASCENDING&page=" + pageIndex);
                if (sellDataPage != null && sellDataPage.data.Count > 0)
                {
                  // Add sales data page to collection
                  this.BuildSellLogData(sellDataPage);
                  pageIndex++;
                }
                else
                {
                  // All data retrieved
                  exitLoop = true;
                }
              }
              
              // Update sell log refresh time
              _sellLogRefresh = DateTime.UtcNow.AddSeconds(_systemConfiguration.GeneralSettings.Monitor.RefreshSeconds - 1);
            }
          }
        }

        return _sellLog;
      }
    }

    public List<SellLogData> SellLogToday
    {
      get
      {
        return SellLog.FindAll(sl => sl.SoldDate.Date == LocalizedTime.DateTime.Date);
      }
    }

    public List<SellLogData> SellLogYesterday
    {
      get
      {
        return SellLog.FindAll(sl => sl.SoldDate.Date == LocalizedTime.DateTime.AddDays(-1).Date);
      }
    }

    public List<SellLogData> SellLogLast7Days
    {
      get
      {
        return SellLog.FindAll(sl => sl.SoldDate.Date >= LocalizedTime.DateTime.AddDays(-7).Date);
      }
    }

    public List<SellLogData> SellLogLast30Days
    {
      get
      {
        return SellLog.FindAll(sl => sl.SoldDate.Date >= LocalizedTime.DateTime.AddDays(-30).Date);
      }
    }

    public List<DCALogData> DCALog
    {
      get
      {
        if (_dcaLog == null || (DateTime.UtcNow > _dcaLogRefresh))
        {
          lock (_dcaLock)
          {
            // Thread double locking
            if (_dcaLog == null || (DateTime.UtcNow > _dcaLogRefresh))
            {
              dynamic dcaData = null, pairsData = null, pendingData = null, watchData = null;
              _dcaLog.Clear();

              Parallel.Invoke(() =>
              {
                dcaData = GetDataFromProfitTrailer("/api/v2/data/dca", true);
              },
              () =>
              {
                pairsData = GetDataFromProfitTrailer("/api/v2/data/pairs", true);
              },
              () =>
              {
                pendingData = GetDataFromProfitTrailer("/api/v2/data/pending", true);

              },
              () =>
              {
                watchData = GetDataFromProfitTrailer("/api/v2/data/watchmode", true);
              });

              this.BuildDCALogData(dcaData, pairsData, pendingData, watchData);
              _dcaLogRefresh = DateTime.UtcNow.AddSeconds(_systemConfiguration.GeneralSettings.Monitor.BagAnalyzerRefreshSeconds - 1);
            }
          }
        }

        return _dcaLog;
      }
    }

    public List<BuyLogData> BuyLog
    {
      get
      {
        if (_buyLog == null || (DateTime.UtcNow > _buyLogRefresh))
        {
          lock (_buyLock)
          {
            // Thread double locking
            if (_buyLog == null || (DateTime.UtcNow > _buyLogRefresh))
            {
              _buyLog.Clear();
              this.BuildBuyLogData(GetDataFromProfitTrailer("/api/v2/data/pbl", true));
              _buyLogRefresh = DateTime.UtcNow.AddSeconds(_systemConfiguration.GeneralSettings.Monitor.BuyAnalyzerRefreshSeconds - 1);
            }
          }
        }

        return _buyLog;
      }
    }

    public TransactionData TransactionData
    {
      get
      {
        if (_transactionData == null) _transactionData = new TransactionData(_ptmBasePath);
        return _transactionData;
      }
    }

    public double GetCurrentBalance()
    {
      return
      (this.Summary.Balance);
    }
    public double GetPairsBalance()
    {
      return
      (this.Summary.PairsValue);
    }
    public double GetDCABalance()
    {
      return
      (this.Summary.DCAValue);
    }
    public double GetPendingBalance()
    {
      return
      (this.Summary.PendingValue);
    }
    public double GetDustBalance()
    {
      return
      (this.Summary.DustValue);
    }

    public double GetSnapshotBalance(DateTime snapshotDateTime)
    {
      double result = _systemConfiguration.GeneralSettings.Application.StartBalance;

      result += this.SellLog.FindAll(sl => sl.SoldDate.Date < snapshotDateTime.Date).Sum(sl => sl.Profit);
      result += this.TransactionData.Transactions.FindAll(t => t.UTCDateTime < snapshotDateTime).Sum(t => t.Amount);

      // Calculate holdings for snapshot date
      result += this.DCALog.FindAll(pairs => pairs.FirstBoughtDate <= snapshotDateTime).Sum(pairs => pairs.CurrentValue);

      return result;
    }

    private dynamic GetDataFromProfitTrailer(string callPath, bool arrayReturned = false)
    {
      string rawBody = "";
      string url = string.Format("{0}{1}{2}token={3}", _systemConfiguration.GeneralSettings.Application.ProfitTrailerMonitorURL, 
      callPath,
      callPath.Contains("?") ? "&" : "?", 
      _systemConfiguration.GeneralSettings.Application.ProfitTrailerServerAPIToken);

      // Get the data from PT
      Debug.WriteLine(String.Format("{0} - Calling '{1}'", DateTime.UtcNow, url));
      HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
      request.AutomaticDecompression = DecompressionMethods.GZip;
      request.KeepAlive = true;

      WebResponse response = request.GetResponse();

      using (Stream dataStream = response.GetResponseStream())
      {
        StreamReader reader = new StreamReader(dataStream);
        rawBody = reader.ReadToEnd();
        reader.Close();
      }

      response.Close();

      // Parse the JSON and build the data sets
      if (!arrayReturned)
      {
        return JObject.Parse(rawBody);
      }
      else
      {
        return JArray.Parse(rawBody);
      }
    }

    private SummaryData BuildSummaryData(dynamic PTData)
    {
      return new SummaryData()
      {
        Market = PTData.market,
        Balance = PTData.realBalance,
        PairsValue = PTData.totalPairsCurrentValue,
        DCAValue = PTData.totalDCACurrentValue,
        PendingValue = PTData.totalPendingCurrentValue,
        DustValue = PTData.totalDustCurrentValue
      };
    }
    private Properties BuildProptertiesData(dynamic PTProperties)
    {
      return new Properties()
      {
        Currency = PTProperties.currency,
        Shorting = PTProperties.shorting,
        Margin = PTProperties.margin,
        UpTime = PTProperties.upTime,
        Port = PTProperties.port,
        IsLeverageExchange = PTProperties.isLeverageExchange,
        BaseUrl = PTProperties.baseUrl
      };
    }
    private void BuildSellLogData(dynamic rawSellLogData)
    {
      foreach (var rsld in rawSellLogData.data)
      {
        SellLogData sellLogData = new SellLogData();
        sellLogData.SoldAmount = rsld.soldAmount;
        sellLogData.BoughtTimes = rsld.boughtTimes;
        sellLogData.Market = rsld.market;
        sellLogData.ProfitPercent = rsld.profit;
        sellLogData.SoldPrice = rsld.currentPrice;
        sellLogData.AverageBuyPrice = rsld.avgPrice;
        sellLogData.TotalCost = rsld.totalCost;
        sellLogData.Profit = rsld.profitCurrency;


        //Convert Unix Timestamp to Datetime
        System.DateTime dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
        dtDateTime = dtDateTime.AddSeconds((double)rsld.soldDate).ToUniversalTime();

        // Profit Trailer sales are saved in UTC
        DateTimeOffset ptSoldDate = DateTimeOffset.Parse(dtDateTime.Year.ToString() + "-" + dtDateTime.Month.ToString("00") + "-" + dtDateTime.Day.ToString("00") + "T" + dtDateTime.Hour.ToString("00") + ":" + dtDateTime.Minute.ToString("00") + ":" + dtDateTime.Second.ToString("00"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

        // Convert UTC sales time to local offset time
        ptSoldDate = ptSoldDate.ToOffset(OffsetTimeSpan);

        sellLogData.SoldDate = ptSoldDate.DateTime;

        _sellLog.Add(sellLogData);
      }
    }

    private void BuildDCALogData(dynamic rawDCALogData, dynamic rawPairsLogData, dynamic rawPendingLogData, dynamic rawWatchModeLogData)
    {
      // Parse DCA data
      _dcaLog.AddRange(ParsePairsData(rawDCALogData, true));

      // Parse Pairs data
      _dcaLog.AddRange(ParsePairsData(rawPairsLogData, false));

      // Parse pending pairs data
      _dcaLog.AddRange(ParsePairsData(rawPendingLogData, false));

      // Parse watch only pairs data
      _dcaLog.AddRange(ParsePairsData(rawWatchModeLogData, false));

    }

    // Parse the pairs data from PT to our own common data structure.
    private List<DCALogData> ParsePairsData(dynamic pairsData, bool processBuyStrategies)
    {
      List<DCALogData> pairs = new List<DCALogData>();

      foreach (var pair in pairsData)
      {
        DCALogData dcaLogData = new DCALogData();
        dcaLogData.Amount = pair.totalAmount;
        dcaLogData.BoughtTimes = pair.boughtTimes;
        dcaLogData.Market = pair.market;
        dcaLogData.ProfitPercent = pair.profit;
        dcaLogData.AverageBuyPrice = pair.avgPrice;
        dcaLogData.TotalCost = pair.totalCost;
        dcaLogData.BuyTriggerPercent = pair.buyProfit;
        dcaLogData.CurrentLowBBValue = pair.bbLow == null ? 0 : pair.bbLow;
        dcaLogData.CurrentHighBBValue = pair.highBb == null ? 0 : pair.highBb;
        dcaLogData.BBTrigger = pair.bbTrigger == null ? 0 : pair.bbTrigger;
        dcaLogData.CurrentPrice = pair.currentPrice;
        dcaLogData.SellTrigger = pair.triggerValue == null ? 0 : pair.triggerValue;
        dcaLogData.PercChange = pair.percChange;
        dcaLogData.Leverage = pair.leverage == null ? 0 : pair.leverage;
        dcaLogData.BuyStrategy = pair.buyStrategy == null ? "" : pair.buyStrategy;
        dcaLogData.SellStrategy = pair.sellStrategy == null ? "" : pair.sellStrategy;
        dcaLogData.IsTrailing = false;

        // See if they are using PT 2.5 (buyStrategiesData) or 2.4 (buyStrategies)
        var buyStrats = pair.buyStrategies != null ? pair.buyStrategies : pair.buyStrategiesData.data;
        if (buyStrats != null && processBuyStrategies)
        {
          foreach (var bs in buyStrats)
          {
            Strategy buyStrategy = new Strategy();
            buyStrategy.Type = bs.type;
            buyStrategy.Name = bs.name;
            buyStrategy.EntryValue = bs.entryValue;
            buyStrategy.EntryValueLimit = bs.entryValueLimit;
            buyStrategy.TriggerValue = bs.triggerValue;
            buyStrategy.CurrentValue = bs.currentValue;
            buyStrategy.CurrentValuePercentage = bs.currentValuePercentage;
            buyStrategy.Decimals = bs.decimals;
            buyStrategy.IsTrailing = bs.trailing;
            buyStrategy.IsTrue = bs.strategyResult;

            dcaLogData.BuyStrategies.Add(buyStrategy);
          }
        }

        // See if they are using PT 2.5 (sellStrategiesData) or 2.4 (sellStrategies)
        var sellStrats = pair.sellStrategies != null ? pair.sellStrategies : pair.sellStrategiesData.data;
        if (sellStrats != null)
        {
          foreach (var ss in sellStrats)
          {
            Strategy sellStrategy = new Strategy();
            sellStrategy.Type = ss.type;
            sellStrategy.Name = ss.name;
            sellStrategy.EntryValue = ss.entryValue;
            sellStrategy.EntryValueLimit = ss.entryValueLimit;
            sellStrategy.TriggerValue = ss.triggerValue;
            sellStrategy.CurrentValue = ss.currentValue;
            sellStrategy.CurrentValuePercentage = ss.currentValuePercentage;
            sellStrategy.Decimals = ss.decimals;
            sellStrategy.IsTrailing = ss.trailing;
            sellStrategy.IsTrue = ss.strategyResult;

            dcaLogData.SellStrategies.Add(sellStrategy);

            // Find the target percentage gain to sell.
            if (sellStrategy.Name.Contains("GAIN", StringComparison.InvariantCultureIgnoreCase))
            {
              if (!dcaLogData.TargetGainValue.HasValue || dcaLogData.TargetGainValue.Value > sellStrategy.EntryValue)
              {
                // Set the target sell percentage
                dcaLogData.TargetGainValue = sellStrategy.EntryValue;
              }
            }
          }
        }

        // Calculate current value
        dcaLogData.CurrentValue = dcaLogData.CurrentPrice * dcaLogData.Amount;

        // Convert Unix Timestamp to Datetime
        System.DateTime rdldDateTime = new DateTime(1970, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
        rdldDateTime = rdldDateTime.AddSeconds((double)pair.firstBoughtDate).ToUniversalTime();

        // Profit Trailer bought times are saved in UTC
        if (pair.firstBoughtDate > 0)
        {
          DateTimeOffset ptFirstBoughtDate = DateTimeOffset.Parse(rdldDateTime.Year.ToString() + "-" + rdldDateTime.Month.ToString("00") + "-" + rdldDateTime.Day.ToString("00") + "T" + rdldDateTime.Hour.ToString("00") + ":" + rdldDateTime.Minute.ToString("00") + ":" + rdldDateTime.Second.ToString("00"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

          // Convert UTC bought time to local offset time
          ptFirstBoughtDate = ptFirstBoughtDate.ToOffset(OffsetTimeSpan);

          dcaLogData.FirstBoughtDate = ptFirstBoughtDate.DateTime;
        }
        else
        {
          dcaLogData.FirstBoughtDate = Constants.confMinDate;
        }

        _dcaLog.Add(dcaLogData);
      }

      return pairs;
    }

    private void BuildBuyLogData(dynamic rawBuyLogData)
    {
      foreach (var rbld in rawBuyLogData)
      {
        BuyLogData buyLogData = new BuyLogData() { IsTrailing = false, IsTrue = false, IsSom = false, TrueStrategyCount = 0 };
        buyLogData.Market = rbld.market;
        buyLogData.ProfitPercent = rbld.profit;
        buyLogData.CurrentPrice = rbld.currentPrice;
        buyLogData.PercChange = rbld.percChange;
        buyLogData.Volume24h = rbld.volume;

        if (rbld.positive != null)
        {
          buyLogData.IsTrailing = ((string)(rbld.positive)).IndexOf("trailing", StringComparison.InvariantCultureIgnoreCase) > -1;
          buyLogData.IsTrue = ((string)(rbld.positive)).IndexOf("true", StringComparison.InvariantCultureIgnoreCase) > -1;
        }
        else
        {
          // Parse buy strategies

          // See if they are using PT 2.5 (buyStrategiesData) or 2.4 (buyStrategies)
          var buyStrats = rbld.buyStrategies != null ? rbld.buyStrategies : rbld.buyStrategiesData.data;

          if (buyStrats != null)
          {
            foreach (var bs in buyStrats)
            {
              Strategy buyStrategy = new Strategy();
              buyStrategy.Type = bs.type;
              buyStrategy.Name = bs.name;
              buyStrategy.EntryValue = bs.entryValue;
              buyStrategy.EntryValueLimit = bs.entryValueLimit;
              buyStrategy.TriggerValue = bs.triggerValue;
              buyStrategy.CurrentValue = bs.currentValue;
              buyStrategy.CurrentValuePercentage = bs.currentValuePercentage;
              buyStrategy.Decimals = bs.decimals;
              buyStrategy.IsTrailing = bs.trailing;
              buyStrategy.IsTrue = bs.strategyResult;

              // Is SOM?
              buyLogData.IsSom = buyLogData.IsSom || buyStrategy.Name.Contains("som enabled", StringComparison.OrdinalIgnoreCase);

              // Is the pair trailing?
              buyLogData.IsTrailing = buyLogData.IsTrailing || buyStrategy.IsTrailing;
              buyLogData.IsTrue = buyLogData.IsTrue || buyStrategy.IsTrue;

              // True status strategy count total
              buyLogData.TrueStrategyCount += buyStrategy.IsTrue ? 1 : 0;

              // Add
              buyLogData.BuyStrategies.Add(buyStrategy);
            }
          }
        }

        _buyLog.Add(buyLogData);
      }
    }
  }
}
