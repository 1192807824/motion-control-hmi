using System.IO;
using System.Text.Json;
using ControlHub.Services.Devices;

namespace ControlHub.Services.Persistence;

public sealed class VibrationFeederSettingsStore
{
    private readonly string _filePath = Path.Combine(AppContext.BaseDirectory, "vibration-feeder-settings.json");

    public VibrationFeederSettings Load()
    {
        if (!File.Exists(_filePath))
        {
            return new VibrationFeederSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<VibrationFeederSettings>(File.ReadAllText(_filePath))
                ?? new VibrationFeederSettings();
        }
        catch (IOException)
        {
            return new VibrationFeederSettings();
        }
        catch (JsonException)
        {
            return new VibrationFeederSettings();
        }
    }

    public void Save(VibrationFeederSettings settings)
    {
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
}
