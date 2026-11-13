using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace Monitor.Pages
{
  public class PTDataModel : _Internal.BasePageModelSecure
  {
    [BindProperty] public int MaximumHistoryRecords { get; set; } = 0;
    [BindProperty] public bool IncludeBuys { get; set; } = true;
    [BindProperty] public bool IncludeSales { get; set; } = true;
    [BindProperty] public bool IncludeCurrentPositions { get; set; } = true;
    [BindProperty] public bool IncludeFunding { get; set; } = false;

    public string ValidationMessage { get; private set; } = "";
    public bool ExportEnabled => PTMagicConfiguration?.GeneralSettings?.Monitor?.IsPasswordProtected == true;

    public IActionResult OnGet()
    {
      base.Init();
      if (!IsLoggedIn(HttpContext)) return RedirectToPage("/Login");
      return Page();
    }

    public async Task<IActionResult> OnPostDownloadAsync(CancellationToken cancellationToken)
    {
      base.Init();
      if (!IsLoggedIn(HttpContext)) return Forbid();
      // A password-protected Monitor is required because ZIPs contain private account data.
      if (!ExportEnabled) return Forbid();

      var selection = new _Internal.PTDataExportSelection
      {
        MaximumHistoryRecords = MaximumHistoryRecords,
        IncludeBuys = IncludeBuys,
        IncludeSales = IncludeSales,
        IncludeCurrentPositions = IncludeCurrentPositions,
        IncludeFunding = IncludeFunding
      };
      if (!selection.ValidLimit || !selection.AnySelected)
      {
        ValidationMessage = "Choose a valid history amount and at least one dataset.";
        return Page();
      }

      try
      {
        var zip = await _Internal.PTDataExportService.GenerateAsync(PTMagicConfiguration,
          selection, cancellationToken).ConfigureAwait(false);
        string fileName = "PT-Data-" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ssZ") + ".zip";
        return File(zip, "application/zip", fileName);
      }
      catch (InvalidOperationException)
      {
        ValidationMessage = "PT Data export could not start. Another export may still be active or the PT connection configuration is unavailable.";
      }
      catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
      {
        ValidationMessage = "PT Data export exceeded its time limit. Choose a smaller selection and try again.";
      }
      catch (Exception) when (!cancellationToken.IsCancellationRequested)
      {
        ValidationMessage = "PT Data export failed. Check the local Monitor/PT connection and try again.";
      }
      return Page();
    }
  }
}
