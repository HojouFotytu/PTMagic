﻿using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Core.Main;
using Core.Helper;
using Core.Main.DataObjects;
using Core.Main.DataObjects.PTMagicData;

namespace Monitor.Pages
{
  public class SalesAnalyzer : _Internal.BasePageModelSecure
  {
    public ProfitTrailerData PTData = null;
    public string TradesChartDataJSON = "";
    public string ProfitChartDataJSON = "";
    public string BalanceChartDataJSON = "";
    public IEnumerable<KeyValuePair<string, double>> TopMarkets = null;
    public DateTime MinSellLogDate = Constants.confMinDate;
    public Dictionary<DateTime, double> DailyGains = new Dictionary<DateTime, double>();
    public Dictionary<DateTime, double> MonthlyGains = new Dictionary<DateTime, double>();
    public DateTimeOffset DateTimeNow = Constants.confMinDate;
    public double totalCurrentValue = 0;
    public void OnGet()
    {
      base.Init();

      BindData();
      BuildTCV();
    }
    private void BindData()
    {
      PTData = this.PtDataObject;

      // Convert local offset time to UTC
      TimeSpan offsetTimeSpan = TimeSpan.Parse(PTMagicConfiguration.GeneralSettings.Application.TimezoneOffset.Replace("+", ""));
      DateTimeNow = DateTimeOffset.UtcNow.ToOffset(offsetTimeSpan);

      BuildTopMarkets();
      BuildSalesChartData();
    }
    private void BuildTopMarkets()
    {
      var markets = PTData.SellLog.GroupBy(m => m.Market);
      Dictionary<string, double> topMarketsDic = new Dictionary<string, double>();
      foreach (var market in markets)
      {
        double totalProfit = 0;
        totalProfit = PTData.SellLog.FindAll(m => m.Market == market.Key).Sum(m => m.Profit);
        topMarketsDic.Add(market.Key, totalProfit);
      }
      TopMarkets = new SortedDictionary<string, double>(topMarketsDic).OrderByDescending(m => m.Value).Take(PTMagicConfiguration.GeneralSettings.Monitor.MaxTopMarkets);
    }
    private void BuildSalesChartData()
    {
      if (PTData.SellLog.Count > 0)
      {
        MinSellLogDate = PTData.SellLog.OrderBy(sl => sl.SoldDate).First().SoldDate.Date;
        DateTime graphStartDate = DateTimeNow.DateTime.Date.AddDays(-1850);
        if (MinSellLogDate > graphStartDate) graphStartDate = MinSellLogDate;

        int tradeDayIndex = 0;
        string tradesPerDayJSON = "";
        string profitPerDayJSON = "";
        string balancePerDayJSON = "";
        double balance = 0.0;
        for (DateTime salesDate = graphStartDate; salesDate <= DateTimeNow.DateTime.Date; salesDate = salesDate.AddDays(1))
        {
          if (tradeDayIndex > 0)
          {
            tradesPerDayJSON += ",\n";
            profitPerDayJSON += ",\n";
            balancePerDayJSON += ",\n";
          }
          double profit = 0;
          int trades = PTData.SellLog.FindAll(t => t.SoldDate.Date == salesDate.Date).Count;
          profit = PTData.SellLog.FindAll(t => t.SoldDate.Date == salesDate.Date).Sum(t => t.Profit);
          double profitFiat = Math.Round(profit * Summary.MainMarketPrice, 2);
          balance += profitFiat; 
          tradesPerDayJSON += "{x: new Date('" + salesDate.Date.ToString("yyyy-MM-dd") + "'), y: " + trades + "}";
          profitPerDayJSON += "{x: new Date('" + salesDate.Date.ToString("yyyy-MM-dd") + "'), y: " + profitFiat.ToString("0.00", new System.Globalization.CultureInfo("en-US")) + "}";
          balancePerDayJSON += "{x: new Date('" + salesDate.Date.ToString("yyyy-MM-dd") + "'), y: " + balance.ToString("0.00", new System.Globalization.CultureInfo("en-US")) + "}";
          tradeDayIndex++;
        }
        TradesChartDataJSON = "[";
        TradesChartDataJSON += "{";
        TradesChartDataJSON += "key: 'Sales',";
        TradesChartDataJSON += "color: '" + Constants.ChartLineColors[0] + "',";
        TradesChartDataJSON += "values: [" + tradesPerDayJSON + "]";
        TradesChartDataJSON += "}";
        TradesChartDataJSON += "]";

        ProfitChartDataJSON = "[";
        ProfitChartDataJSON += "{";
        ProfitChartDataJSON += "key: 'Profit in " + Summary.MainFiatCurrency + "',";
        ProfitChartDataJSON += "color: '" + Constants.ChartLineColors[1] + "',";
        ProfitChartDataJSON += "values: [" + profitPerDayJSON + "]";
        ProfitChartDataJSON += "}";
        ProfitChartDataJSON += "]";

        BalanceChartDataJSON = "[";
        BalanceChartDataJSON += "{";
        BalanceChartDataJSON += "key: 'Profit in " + Summary.MainFiatCurrency + "',";
        BalanceChartDataJSON += "color: '" + Constants.ChartLineColors[1] + "',";
        BalanceChartDataJSON += "values: [" + balancePerDayJSON + "]";
        BalanceChartDataJSON += "}";
        BalanceChartDataJSON += "]";
        
        for (DateTime salesDate = DateTimeNow.DateTime.Date; salesDate >= MinSellLogDate; salesDate = salesDate.AddDays(-1))
        {
          List<SellLogData> salesDateSales = PTData.SellLog.FindAll(sl => sl.SoldDate.Date == salesDate);
          double salesDateProfit;
          salesDateProfit = salesDateSales.Sum(sl => sl.Profit);
          double salesDateStartBalance = PTData.GetSnapshotBalance(salesDate);
          double salesDateGain = Math.Round(salesDateProfit / salesDateStartBalance * 100, 2);
          DailyGains.Add(salesDate, salesDateGain);
        }
        DateTime minSellLogMonthDate = new DateTime(MinSellLogDate.Year, MinSellLogDate.Month, 1).Date;
        DateTime salesMonthStartDate = new DateTime(DateTimeNow.DateTime.Year, DateTimeNow.DateTime.Month, 1).Date;
        for (DateTime salesMonthDate = salesMonthStartDate.Date; salesMonthDate >= minSellLogMonthDate; salesMonthDate = salesMonthDate.AddMonths(-1))
        {
          List<Core.Main.DataObjects.PTMagicData.SellLogData> salesMonthSales = PTData.SellLog.FindAll(sl => sl.SoldDate.Date.Month == salesMonthDate.Month && sl.SoldDate.Date.Year == salesMonthDate.Year);
          double salesDateProfit;
          salesDateProfit = salesMonthSales.Sum(sl => sl.Profit);
          double salesDateStartBalance = PTData.GetSnapshotBalance(salesMonthDate);
          double salesDateGain = Math.Round(salesDateProfit / salesDateStartBalance * 100, 2);
          MonthlyGains.Add(salesMonthDate, salesDateGain);
        }
      }
    }
    private void BuildTCV()
    {
      double AvailableBalance = PTData.GetCurrentBalance();
      foreach (Core.Main.DataObjects.PTMagicData.DCALogData dcaLogEntry in PTData.DCALog)
      {
        double leverage = dcaLogEntry.Leverage;
        if (leverage == 0)
        {
          leverage = 1;
        }
        totalCurrentValue = totalCurrentValue + ((dcaLogEntry.Amount * dcaLogEntry.CurrentPrice) / leverage);
      }
      totalCurrentValue = totalCurrentValue + AvailableBalance;
    }
  }
}
