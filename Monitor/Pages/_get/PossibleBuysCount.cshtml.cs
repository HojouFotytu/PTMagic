using System;

namespace Monitor.Pages
{
  // The compact PBL count is refreshed separately from DashboardTop.
  public class PossibleBuysCountModel : _Internal.BasePageModelSecureAJAX
  {
    public int? Count { get; private set; }

    public void OnGet()
    {
      base.Init();
      if (HttpContext.Response.StatusCode >= 300) return;
      Count = this.PtDataObject.PossibleBuyCount;
    }
  }
}
