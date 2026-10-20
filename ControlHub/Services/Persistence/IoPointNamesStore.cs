using System.IO;
using System.Text;
using System.Text.Json;
using ControlHub.Services.Motion;

namespace ControlHub.Services.Persistence;

/// <summary>设备IO名称独立保存，不随默认运动配置或产品配方覆盖。</summary>
public sealed class IoPointNamesStore
{
    private readonly string _filePath;

    public IoPointNamesStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(AppContext.BaseDirectory, "io-point-names.json");
    }

    public Dictionary<string, string> LoadOrMigrate(IReadOnlyDictionary<string, string> legacyNames)
    {
        if (!File.Exists(_filePath) && !File.Exists(_filePath + ".bak"))
        {
            MotionCardOptions.ValidateIoPointNames(legacyNames);
            if (legacyNames.Count > 0) Save(legacyNames);
            return new Dictionary<string, string>(legacyNames);
        }

        try
        {
            return Read(_filePath);
        }
        catch (Exception primary) when (IsFileError(primary))
        {
            try
            {
                return Read(_filePath + ".bak");
            }
            catch (Exception backup) when (IsFileError(backup))
            {
                throw new InvalidDataException(
                    $"I/O名称读取失败，主文件及备份均不可用，请检查 {_filePath}：{primary.Message}", primary);
            }
        }
    }

    public void Save(IReadOnlyDictionary<string, string> names)
    {
        MotionCardOptions.ValidateIoPointNames(names);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_filePath))!);
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath,
                JsonSerializer.Serialize(names, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            using (var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.Write, FileShare.Read))
                stream.Flush(flushToDisk: true);
            if (File.Exists(_filePath))
                File.Replace(temporaryPath, _filePath, _filePath + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(temporaryPath, _filePath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static Dictionary<string, string> Read(string path)
    {
        var names = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"{Path.GetFileName(path)} 内容为空。");
        MotionCardOptions.ValidateIoPointNames(names);
        return names;
    }

    private static bool IsFileError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException;
}
