using System;
using System.IO;

namespace Monitor.Configuration
{
  // Resolve deployment paths independently of the shell's working directory.
  internal static class MonitorPathResolver
  {
    internal static string FindMonitorBasePath()
    {
      string executableDirectory = AppContext.BaseDirectory;
      string currentDirectory = Directory.GetCurrentDirectory();
      string[] candidates =
      {
        executableDirectory,
        Path.Combine(executableDirectory, "Monitor"),
        currentDirectory,
        Path.Combine(currentDirectory, "Monitor")
      };

      foreach (string directory in candidates)
      {
        if (File.Exists(Path.Combine(directory, "appsettings.json")))
          return Path.GetFullPath(directory);
      }

      // Preserve the normal startup check's useful missing-file error.
      return Path.GetFullPath(executableDirectory);
    }

    internal static string ResolvePTMagicBasePath(string configuredPath, string monitorBasePath)
    {
      if (String.IsNullOrWhiteSpace(configuredPath))
        throw new InvalidOperationException("PTMagicBasePath must be set in Monitor/appsettings.json.");

      string absolutePath = Path.GetFullPath(configuredPath, monitorBasePath);
      return Path.EndsInDirectorySeparator(absolutePath)
        ? absolutePath : absolutePath + Path.DirectorySeparatorChar;
    }
  }
}
