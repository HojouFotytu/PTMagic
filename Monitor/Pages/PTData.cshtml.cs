using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace Monitor.Pages
{
  public class PTDataModel : _Internal.BasePageModelSecure
  {
    [BindProperty] public string HistoryRange { get; set; } = "all";
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
      if (!IsLoggedIn(HttpContext)) return StatusCode(403);
      // This Monitor uses session-based authentication, not an ASP.NET authentication scheme.
      // Return HTTP 403 directly rather than invoking an unregistered forbid handler.
      if (!ExportEnabled) return StatusCode(403);

      var selection = new _Internal.PTDataExportSelection
      {
        HistoryRange = HistoryRange,
        IncludeBuys = IncludeBuys,
        IncludeSales = IncludeSales,
        IncludeCurrentPositions = IncludeCurrentPositions,
        IncludeFunding = IncludeFunding
      };
      if (!selection.ValidHistoryRange || !selection.AnySelected)
      {
        ValidationMessage = "Choose a valid history range and at least one dataset.";
        return Page();
      }

      try
      {
        var zip = await _Internal.PTDataExportService.GenerateAsync(PTMagicConfiguration,
          selection, cancellationToken).ConfigureAwait(false);
        string instanceName = PTMagicConfiguration.GeneralSettings.Application.InstanceName ?? "";
        // Keep filenames portable across Windows/Linux and avoid path/control characters.
        string safeInstanceName = System.Text.RegularExpressions.Regex
          .Replace(instanceName.Trim(), @"[^a-zA-Z0-9_-]+", "-")
          .Trim('-', '_');
        if (safeInstanceName.Length > 64) safeInstanceName = safeInstanceName.Substring(0, 64).TrimEnd('-', '_');
        if (String.IsNullOrEmpty(safeInstanceName)) safeInstanceName = "Instance";
        string fileName = "PT-Data-" + safeInstanceName + "-" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ssZ") + ".zip";
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
