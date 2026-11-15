using System;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Core.Main;
using Core.Helper;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Monitor._Internal;

namespace Monitor
{
  public class Startup
  {
    PTMagicConfiguration systemConfiguration = null;
    private readonly IWebHostEnvironment _hostingEnvironment;

    public Startup(IWebHostEnvironment hostingEnvironment)
    {
      _hostingEnvironment = hostingEnvironment;
    }

    // This method gets called by the runtime. Use this method to add services to the container.
    public void ConfigureServices(IServiceCollection services)
    {
      string monitorBasePath = _hostingEnvironment.ContentRootPath;

      IConfiguration config = new ConfigurationBuilder()
                .SetBasePath(monitorBasePath)
                .AddJsonFile("appsettings.json", false)
                .Build();

      string ptMagicBasePath = Configuration.MonitorPathResolver.ResolvePTMagicBasePath(
        config.GetValue<string>("PTMagicBasePath"), monitorBasePath);

      systemConfiguration = new PTMagicConfiguration(ptMagicBasePath);

      services.AddMvc(option =>
      {
        option.EnableEndpointRouting = false;
        option.Filters.Add(new FirstRaidDataFilter(ptMagicBasePath, systemConfiguration.GeneralSettings.Monitor.RootUrl));
      });
      services.AddAntiforgery(o => o.HeaderName = "XSRF-TOKEN");
      services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
      services.AddSingleton<LiveTcvService>(serviceProvider => new LiveTcvService(ptMagicBasePath, serviceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LiveTcvService>>()));
      services.AddSingleton<IHostedService>(serviceProvider => serviceProvider.GetRequiredService<LiveTcvService>());
      services.AddSingleton<TempFileCleanupService>(serviceProvider => new TempFileCleanupService(monitorBasePath, serviceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TempFileCleanupService>>()));
      services.AddSingleton<IHostedService>(serviceProvider => serviceProvider.GetRequiredService<TempFileCleanupService>());
      services.AddDistributedMemoryCache();
      services.AddSession(options =>
      {
        options.IdleTimeout = TimeSpan.FromSeconds(1800);
        options.Cookie.HttpOnly = true;
        options.Cookie.Name = "PTMagicMonitor" + systemConfiguration.GeneralSettings.Monitor.Port.ToString();
      });

      services.Configure<KestrelServerOptions>(options =>
      {
        options.AllowSynchronousIO = true;
      });

      // Remove the old tmp folder if it exists
      string oldTmpFolder = monitorBasePath + System.IO.Path.DirectorySeparatorChar + "wwwroot" + System.IO.Path.DirectorySeparatorChar + "assets" + System.IO.Path.DirectorySeparatorChar + "tmp" + System.IO.Path.DirectorySeparatorChar;
      if (System.IO.Directory.Exists(oldTmpFolder))
      {
        System.IO.Directory.Delete(oldTmpFolder, true);
      }
    }

    // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
      // Register global exception handler
      if (env.EnvironmentName == "Development")
      {
        app.UseBrowserLink();
        app.UseDeveloperExceptionPage();
      }
      else
      {
        app.UseExceptionHandler("/Error");
      }

      // Configure request pipeline
      app.UseStaticFiles();
      app.UseSession();
      app.UseMvcWithDefaultRoute();      

      // Open the browser
      if (systemConfiguration.GeneralSettings.Monitor.OpenBrowserOnStart) OpenBrowser("http://localhost:" + systemConfiguration.GeneralSettings.Monitor.Port.ToString());
    }

    public static void OpenBrowser(string url)
    {
      if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
      {
        Process.Start(new ProcessStartInfo("cmd", $"/c start {url}")); // Works ok on windows
      }
      else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
      {
        Process.Start("xdg-open", url);  // Works ok on linux
      }
      else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
      {
        Process.Start("open", url); // Not tested
      }
    }
  }

  // Only data-dependent views are gated. Login, Logs, settings, PT Data and
  // other independent Monitor routes remain accessible before the first raid.
  internal sealed class FirstRaidDataFilter : Microsoft.AspNetCore.Mvc.Filters.IAsyncPageFilter
  {
    private static readonly System.Collections.Generic.HashSet<string> RaidDependentPages =
      new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
      {
        "/MarketAnalyzer", "/StatusSummary", "/ManageSMS", "/DCACalculator",
        "/_get/DashboardTop", "/_get/DashboardBottom", "/_get/TickerWidgets",
        "/_get/BagDetails", "/_get/BagList", "/_get/BuyList"
      };

    private readonly string _summaryPath;
    private readonly string _logsPath;

    public FirstRaidDataFilter(string basePath, string rootUrl)
    {
      _summaryPath = Path.Combine(basePath, Constants.PTMagicPathData, "LastRuntimeSummary.json");
      _logsPath = (rootUrl ?? "/").TrimEnd('/') + "/Logs";
    }

    public System.Threading.Tasks.Task OnPageHandlerSelectionAsync(
      Microsoft.AspNetCore.Mvc.Filters.PageHandlerSelectedContext context) =>
      System.Threading.Tasks.Task.CompletedTask;

    public async System.Threading.Tasks.Task OnPageHandlerExecutionAsync(
      Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutingContext context,
      Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutionDelegate next)
    {
      string route = (context.ActionDescriptor as Microsoft.AspNetCore.Mvc.RazorPages.PageActionDescriptor)?.ViewEnginePath;
      if (!RaidDependentPages.Contains(route ?? "") || HasCompletedFirstRaid())
      {
        await next();
        return;
      }

      if (context.HandlerInstance is _Internal.BasePageModelSecure secure)
      {
        // Run the existing session/cookie security check before disclosing
        // anything from a protected Monitor page.
        secure.Init();
        if (context.HttpContext.Response.StatusCode >= 300
          && context.HttpContext.Response.StatusCode < 400)
        {
          context.Result = new Microsoft.AspNetCore.Mvc.EmptyResult();
          return;
        }
      }

      context.HttpContext.Response.Headers["Cache-Control"] = "no-store";
      context.HttpContext.Response.Headers["Refresh"] = "60";
      string safeLink = System.Net.WebUtility.HtmlEncode(_logsPath);
      context.Result = new Microsoft.AspNetCore.Mvc.ContentResult
      {
        ContentType = "text/html; charset=utf-8",
        Content = "<div class=\"card-box\" data-raid-waiting=\"true\" style=\"padding:20px;margin:12px 0\">"
          + "Data Unavailable until PTMagic completes its first raid. See <a href=\""
          + safeLink + "\">Logs</a>. This section will check again in one minute.</div>",
        StatusCode = 200
      };
    }

    private bool HasCompletedFirstRaid()
    {
      try
      {
        if (!File.Exists(_summaryPath)) return false;
        string json = File.ReadAllText(_summaryPath);
        var summary = Newtonsoft.Json.JsonConvert.DeserializeObject<Core.Main.DataObjects.PTMagicData.Summary>(json);
        return summary != null && summary.LastRuntime > Constants.confMinDate;
      }
      catch (IOException) { return false; }
      catch (Newtonsoft.Json.JsonException) { return false; }
    }
  }

}
