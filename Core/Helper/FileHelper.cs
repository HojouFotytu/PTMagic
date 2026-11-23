using System;
using System.IO;
using Core.Main;

namespace Core.Helper
{
  public static class FileHelper
  {
    public static void WriteTextToFile(string folderPath, string fileName, string text)
    {
      FileHelper.WriteTextToFile(folderPath, fileName, text, Constants.confMinDate, Constants.confMinDate);
    }

    public static void WriteTextToFile(string folderPath, string fileName, string text, DateTime creationTime, DateTime lastWriteTime)
    {
      if (!Directory.Exists(folderPath))
      {
        Directory.CreateDirectory(folderPath);
      }

      File.WriteAllText(folderPath + fileName, text);

      if (creationTime != Constants.confMinDate)
      {
        File.SetCreationTimeUtc(folderPath + fileName, creationTime);
      }

      if (lastWriteTime != Constants.confMinDate)
      {
        File.SetLastWriteTimeUtc(folderPath + fileName, lastWriteTime);
      }
    }

    public static void CreateBackup(string filePath, string backupFolder)
    {
      FileHelper.CreateBackup(filePath, backupFolder, "");
    }

    public static void CreateBackup(string filePath, string backupFolder, string backupFileName)
    {
      if (!Directory.Exists(backupFolder))
      {
        Directory.CreateDirectory(backupFolder);
      }

      FileInfo file = new FileInfo(filePath);

      string backupFilePath = backupFolder + DateTime.Now.ToString("yyyy-MM-dd_HH.mm.ss") + "_" + file.Name;
      if (!backupFileName.Equals(""))
      {
        backupFilePath = backupFolder + backupFileName;
      }

      File.Copy(file.FullName, backupFilePath, true);
    }

    public static void CleanupFilesMinutes(string folderPath, int maxMinutes)
    {
      if (!Directory.Exists(folderPath))
      {
        Directory.CreateDirectory(folderPath);
      }

      DirectoryInfo folder = new DirectoryInfo(folderPath);
      foreach (FileInfo file in folder.GetFiles())
      {
        DateTime maxAge = DateTime.UtcNow.AddMinutes(-maxMinutes);

        if (file.LastWriteTimeUtc < maxAge)
        {
          File.Delete(file.FullName);
        }
      }
    }

    // Only these legacy market snapshots are historical files; MarketInfo and
    // exchange metadata in the same directory must not be purged.
    public static void CleanupMarketDataFiles(string folderPath, int maxHours)
    {
      if (maxHours < 0) throw new ArgumentOutOfRangeException(nameof(maxHours));
      if (!Directory.Exists(folderPath)) return;
      DateTime cutoffUtc = DateTime.UtcNow.AddHours(-(maxHours + 1));
      foreach (FileInfo file in new DirectoryInfo(folderPath).EnumerateFiles("MarketData_*.json"))
      {
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
        if (file.LastWriteTimeUtc < cutoffUtc) file.Delete();
      }
    }

    public static void CleanupFiles(string folderPath, int maxHours)
    {
      if (!Directory.Exists(folderPath))
      {
        Directory.CreateDirectory(folderPath);
      }

      DirectoryInfo folder = new DirectoryInfo(folderPath);
      foreach (FileInfo file in folder.GetFiles())
      {
        DateTime maxAge = DateTime.UtcNow.AddHours(-(maxHours + 1));

        if (file.LastWriteTimeUtc < maxAge)
        {
          File.Delete(file.FullName);
        }
      }
    }
  }
}
