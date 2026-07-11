using System.IO;
using System.Text;
using System.Text.Json;

namespace ControlHub.Services.Persistence;

public sealed class AxisSettingsStore
{
    private readonly string _filePath;
    private readonly string _legacyFilePath;

    public AxisSettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(AppContext.BaseDirectory, "axis-settings.json");
        _legacyFilePath = Path.Combine(Path.GetDirectoryName(_filePath) ?? AppContext.BaseDirectory, "axis-names.json");
    }

    public IReadOnlyDictionary<int, AxisSettings> Load()
    {
        return LoadWithDiagnostics().Settings;
    }

    public AxisSettingsLoadResult LoadWithDiagnostics()
    {
        var sourcePath = File.Exists(_filePath) ? _filePath : _legacyFilePath;
        if (!File.Exists(sourcePath))
        {
            return new AxisSettingsLoadResult(new Dictionary<int, AxisSettings>(), null);
        }

        try
        {
            return new AxisSettingsLoadResult(ReadSettings(sourcePath), null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            if (TryReadLegacyNames(sourcePath, out var legacySettings))
            {
                return new AxisSettingsLoadResult(
                    legacySettings,
                    $"已从旧版轴名称配置 {Path.GetFileName(sourcePath)} 恢复；下次保存会迁移为 axis-settings.json。");
            }

            var backupPath = _filePath + ".bak";
            if (File.Exists(backupPath) && TryReadSettings(backupPath, out var backupSettings))
            {
                return new AxisSettingsLoadResult(
                    backupSettings,
                    $"轴参数主文件读取失败，已从备份 {Path.GetFileName(backupPath)} 恢复。原因：{exception.Message}");
            }

            return new AxisSettingsLoadResult(
                new Dictionary<int, AxisSettings>(),
                $"轴参数文件读取失败，已使用默认值。文件：{sourcePath}；原因：{exception.Message}");
        }
    }

    public void Save(IEnumerable<Models.AxisStatus> axes)
    {
        var axisArray = axes.ToArray();
        foreach (var axis in axisArray)
        {
            if (!double.IsFinite(axis.JogSpeed) || axis.JogSpeed < 0)
            {
                throw new InvalidDataException($"轴 {axis.AxisNo} 的运行速度必须是大于或等于 0 的有限数值。");
            }

            if (!double.IsFinite(axis.JogDistance))
            {
                throw new InvalidDataException($"轴 {axis.AxisNo} 的移动距离/目标位置必须是有限数值。");
            }
        }

        var settings = axisArray.ToDictionary(
            axis => axis.AxisNo,
            axis => new AxisSettings(axis.Name, axis.JogSpeed, axis.JogDistance, ConfigurationVersion: 2));
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            using (var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_filePath))
            {
                File.Replace(temporaryPath, _filePath, _filePath + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _filePath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static IReadOnlyDictionary<int, AxisSettings> ReadSettings(string path)
    {
        var settings = JsonSerializer.Deserialize<Dictionary<int, AxisSettings>>(File.ReadAllText(path))
                       ?? throw new InvalidDataException($"{Path.GetFileName(path)} 内容为空。");
        if (settings.Count == 0 || settings.Values.All(item => item.ConfigurationVersion is >= 2))
        {
            return settings;
        }

        return settings.ToDictionary(
            item => Math.Max(0, item.Key - 1),
            item => item.Value);
    }

    private static bool TryReadSettings(string path, out IReadOnlyDictionary<int, AxisSettings> settings)
    {
        try
        {
            settings = ReadSettings(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            settings = new Dictionary<int, AxisSettings>();
            return false;
        }
    }

    private static bool TryReadLegacyNames(string sourcePath, out IReadOnlyDictionary<int, AxisSettings> settings)
    {
        try
        {
            var names = JsonSerializer.Deserialize<Dictionary<int, string>>(File.ReadAllText(sourcePath));
            if (names is null)
            {
                settings = new Dictionary<int, AxisSettings>();
                return false;
            }

            settings = names.ToDictionary(
                item => Math.Max(0, item.Key - 1),
                item => new AxisSettings(item.Value, null, null));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            settings = new Dictionary<int, AxisSettings>();
            return false;
        }
    }
}

public sealed record AxisSettingsLoadResult(
    IReadOnlyDictionary<int, AxisSettings> Settings,
    string? Warning);

public sealed record AxisSettings(
    string? Name,
    double? JogSpeed,
    double? JogDistance,
    int? ConfigurationVersion = null);
