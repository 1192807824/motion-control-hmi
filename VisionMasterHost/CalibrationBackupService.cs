using System.Globalization;
using System.IO;

namespace VisionMasterHost;

/// <summary>
/// 在九点标定文件被覆盖前保存不可变历史版本。
/// </summary>
internal static class CalibrationBackupService
{
    private const int MaximumBackupsPerFile = 50;

    public static string? BackupBeforeOverwrite(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("标定文件路径不能为空。", nameof(filePath));
        }

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        var sourceDirectory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("无法确定待备份标定文件的目录。");
        var baseName = Path.GetFileNameWithoutExtension(fullPath);
        var extension = Path.GetExtension(fullPath);
        var typeDirectoryName = $"{baseName}_{extension.TrimStart('.')}";
        var historyDirectory = Path.Combine(sourceDirectory, "历史备份", typeDirectoryName);
        Directory.CreateDirectory(historyDirectory);

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        var backupPath = CreateUniqueBackupPath(historyDirectory, baseName, extension, timestamp);
        File.Copy(fullPath, backupPath, overwrite: false);
        TrimOldBackups(historyDirectory, baseName, extension);
        return backupPath;
    }

    private static string CreateUniqueBackupPath(
        string historyDirectory,
        string baseName,
        string extension,
        string timestamp)
    {
        for (var suffix = 0; suffix < 1000; suffix++)
        {
            var suffixText = suffix == 0 ? "" : $"_{suffix:000}";
            var candidate = Path.Combine(
                historyDirectory,
                $"{baseName}_{timestamp}{suffixText}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("同一时刻产生的标定备份数量过多，无法生成唯一备份文件名。");
    }

    private static void TrimOldBackups(string historyDirectory, string baseName, string extension)
    {
        try
        {
            var pattern = $"{baseName}_*{extension}";
            var oldBackups = new DirectoryInfo(historyDirectory)
                .GetFiles(pattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Skip(MaximumBackupsPerFile);
            foreach (var oldBackup in oldBackups)
            {
                oldBackup.Delete();
            }
        }
        catch
        {
            // 当前备份已经成功。历史清理失败不应阻止本次标定文件保存。
        }
    }
}
