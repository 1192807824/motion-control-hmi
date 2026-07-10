using System.IO;
using System.Text.Json;

namespace ControlHub.Services.Persistence;

public sealed class AxisSettingsStore
{
    private readonly string _filePath = Path.Combine(AppContext.BaseDirectory, "axis-settings.json");
    private readonly string _legacyFilePath = Path.Combine(AppContext.BaseDirectory, "axis-names.json");

    public IReadOnlyDictionary<int, AxisSettings> Load()
    {
        var sourcePath = File.Exists(_filePath) ? _filePath : _legacyFilePath;
        if (!File.Exists(sourcePath))
        {
            return new Dictionary<int, AxisSettings>();
        }

        try
        {
            var json = File.ReadAllText(sourcePath);
            return JsonSerializer.Deserialize<Dictionary<int, AxisSettings>>(json)
                ?? new Dictionary<int, AxisSettings>();
        }
        catch (IOException)
        {
            return new Dictionary<int, AxisSettings>();
        }
        catch (JsonException)
        {
            return LoadLegacyNames(sourcePath);
        }
    }

    public void Save(IEnumerable<Models.AxisStatus> axes)
    {
        var settings = axes.ToDictionary(
            axis => axis.AxisNo,
            axis => new AxisSettings(axis.Name, axis.JogSpeed, axis.JogDistance));
        var directory = Path.GetDirectoryName(_filePath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_filePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static IReadOnlyDictionary<int, AxisSettings> LoadLegacyNames(string sourcePath)
    {
        try
        {
            var json = File.ReadAllText(sourcePath);
            var names = JsonSerializer.Deserialize<Dictionary<int, string>>(json);
            return names?.ToDictionary(
                item => item.Key,
                item => new AxisSettings(item.Value, null, null))
                ?? new Dictionary<int, AxisSettings>();
        }
        catch (IOException)
        {
            return new Dictionary<int, AxisSettings>();
        }
        catch (JsonException)
        {
            return new Dictionary<int, AxisSettings>();
        }
    }
}

public sealed record AxisSettings(string? Name, double? JogSpeed, double? JogDistance);
