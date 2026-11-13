using System;
using System.IO;
using System.Threading;
using System.Reflection;
using Core.Helper;
using Microsoft.Extensions.DependencyInjection;


[assembly: AssemblyVersion("2.10.0")]
[assembly: AssemblyProduct("PT Magic")]

namespace PTMagic
{
  class ProgramT
  {
    // Create a logger
    private static LogHelper _log;

    // Main class entry
    public static void Main(string[] args)
    {
      // Keep an explicitly configured working directory (development/systemd).
      // For a deployed executable launched elsewhere, find settings next to the binary.
      if (!HasSettings(Directory.GetCurrentDirectory()) && HasSettings(AppContext.BaseDirectory))
      {
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
      }

      _log = ServiceHelper.BuildLoggerService().GetRequiredService<LogHelper>();

      // Register a global exception handler
      AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(GlobalUnhandledExceptionHandler);

      // Init PTMagic
      Core.Main.PTMagic ptMagic = new Core.Main.PTMagic(_log);
      ptMagic.CurrentVersion = Assembly.GetExecutingAssembly().GetName().Version;

      // Start process
      ptMagic.StartProcess();

      // Keep the app running
      for (; ; )
      {
        Thread.Sleep(10000);
      }
    }

    private static bool HasSettings(string directory)
    {
      return File.Exists(Path.Combine(directory, "settings.general.json"))
        && File.Exists(Path.Combine(directory, "settings.analyzer.json"));
    }

    // Global unhandled exception handler
    private static void GlobalUnhandledExceptionHandler(object sender, UnhandledExceptionEventArgs args)
    {
      Exception e = (Exception)args.ExceptionObject;

      if (args.IsTerminating)
      {
        _log.DoLogCritical("Unhandled fatal exception occurred: ", e);
      }
      else
      {
        _log.DoLogError("Unhandled fatal exception occurred: " + e.ToString());
      }
    }
  }
}