using System;
using System.Security.Cryptography;
using System.Text;
using Core.Main.DataObjects;
using Newtonsoft.Json;

namespace Monitor._Internal
{
  public static class SalesDataVersionHelper
  {
    public static string GetVersion(ProfitTrailerData ptData)
    {
      if (ptData == null)
      {
        return "";
      }

      string snapshot = JsonConvert.SerializeObject(new
      {
        Stats = ptData.Stats,
        DailyPNL = ptData.DailyPNL,
        DailyTCV = ptData.DailyTCV,
        MonthlyStats = ptData.MonthlyStats,
        ProfitablePairs = ptData.ProfitablePairs,
        DailyStats = ptData.DailyStats
      }, Formatting.None);

      return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)));
    }
  }
}
