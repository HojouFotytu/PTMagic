using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Monitor._Internal
{
  public static class LocalDataExport
  {
    public static int Run(string[] args, TextWriter output, TextWriter error,
      CancellationToken token, DateTime? clockUtc = null)
    {
      // The command is explicit and never launches Monitor or reads credentials.
      if (args.Length != 5 || args[0] != "--export-data" ||
        !Int32.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out int mib) ||
        !MultipartExportSelection.TryFromCode("data", args[2], mib,
          clockUtc ?? DateTime.UtcNow, out var selection))
      {
        error.WriteLine("Usage: dotnet Monitor.dll --export-data <instance-directory> <1d|7d|1w|1m|all> <maximum-MiB> <output-directory>");
        return 2;
      }
      string staging = null;
      try
      {
        string instance = Path.GetFullPath(args[1]);
        string data = Path.Combine(instance, "_data");
        string outputRoot = Path.GetFullPath(args[4]);
        if (!Directory.Exists(data))
          throw new DirectoryNotFoundException("Instance _data directory is unavailable.");
        string relative = Path.GetRelativePath(data, outputRoot);
        if (!Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
          throw new ArgumentException("Output directory must be outside the instance _data directory.");

        var members = StreamingDownloadArchive.CaptureData(data, selection.FromUtc,
          selection.ThroughUtc, token);
        if (members.Count == 0)
          throw new InvalidDataException("No source files match the requested export.");
        string instanceName = System.Text.RegularExpressions.Regex.Replace(
          new DirectoryInfo(instance).Name, @"[^a-zA-Z0-9_-]+", "-").Trim('-', '_');
        if (String.IsNullOrEmpty(instanceName)) instanceName = "Instance";
        if (instanceName.Length > 64) instanceName = instanceName.Substring(0, 64);
        string id = Guid.NewGuid().ToString("N");
        string prefix = "PTMagic-_data-" + instanceName + "-" +
          selection.ThroughUtc.ToString("yyyy-MM-ddTHH-mm-ssZ", CultureInfo.InvariantCulture) +
          "-" + selection.FileTag + "-" + id.Substring(0, 12);

        Directory.CreateDirectory(outputRoot);
        if ((File.GetAttributes(outputRoot) & FileAttributes.ReparsePoint) != 0)
          throw new IOException("Output directory must not be a symbolic link.");
        string final = Path.Combine(outputRoot, prefix);
        staging = final + ".incomplete";
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(staging);
        else Directory.CreateDirectory(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var batch = new MultipartZipBatch(staging, prefix, id, members, selection.MaxBytes, selection);
        var state = batch.Snapshot();
        output.WriteLine($"Export {id}: {selection.HistoryRange}, max {mib} MiB, {state.TotalSources} source files.");
        while (!state.Complete)
        {
          int previous = state.ProcessedSources;
          int partCount = state.Parts.Length;
          state = batch.PrepareNext(token);
          if (state.ProcessedSources <= previous)
            throw new IOException("Archive preparation did not advance.");
          foreach (var part in state.Parts.Skip(partCount))
            output.WriteLine($"{part.FileName}: {part.Bytes} bytes, {part.Files} files" +
              (part.ExceedsLimit ? " (OVERSIZE: " + part.OversizedEntry + ")" : ""));
        }
        token.ThrowIfCancellationRequested();
        if (state.Parts.Length == 0) throw new InvalidDataException("No complete records matched the export.");
        // The receipt is outside the ZIP files; extraction of the parts
        // still reconstructs only the original selected directory tree.
        File.WriteAllText(Path.Combine(staging, "export-manifest.json"),
          JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        Directory.Move(staging, final);
        staging = null;
        output.WriteLine($"Complete: {state.Parts.Length} ZIPs, {state.ProcessedSources}/{state.TotalSources} sources.");
        output.WriteLine("OUTPUT: " + final);
        return 0;
      }
      catch (OperationCanceledException)
      {
        error.WriteLine("Export cancelled.");
      }
      catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
        ex is ArgumentException || ex is InvalidOperationException || ex is JsonException)
      {
        error.WriteLine("Export failed: " + ex.Message);
      }
      if (staging != null) error.WriteLine("Incomplete export (do not treat as complete): " + staging);
      return 1;
    }
  }
}
