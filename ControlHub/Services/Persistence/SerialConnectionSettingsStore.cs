using System.IO;
using System.Text.Json;
using ControlHub.Services.Devices;

namespace ControlHub.Services.Persistence;

public sealed class SerialConnectionSettingsStore
{
    private readonly string _filePath = Path.Combine(AppContext.BaseDirectory, "serial-connection-settings.json");

    public SerialConnectionSettings Load()
    {
        if (!File.Exists(_filePath))
        {
            return new SerialConnectionSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<SerialConnectionSettings>(File.ReadAllText(_filePath))
                ?? new SerialConnectionSettings();
        }
        catch (IOException)
        {
            return new SerialConnectionSettings();
        }
        catch (JsonException)
        {
            return new SerialConnectionSettings();
        }
    }

    public void Save(SerialConnectionSettings settings)
    {
        File.WriteAllText(_filePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }
}
