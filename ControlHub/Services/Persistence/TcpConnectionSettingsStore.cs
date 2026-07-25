using System.IO;
using System.Text.Json;
using ControlHub.Services.Devices;

namespace ControlHub.Services.Persistence;

public sealed class TcpConnectionSettingsStore
{
    private readonly string _filePath = Path.Combine(AppContext.BaseDirectory, "tcp-connection-settings.json");

    public TcpConnectionSettings Load()
    {
        if (!File.Exists(_filePath))
        {
            return new TcpConnectionSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<TcpConnectionSettings>(File.ReadAllText(_filePath))
                ?? new TcpConnectionSettings();
        }
        catch (IOException)
        {
            return new TcpConnectionSettings();
        }
        catch (JsonException)
        {
            return new TcpConnectionSettings();
        }
    }

    public void Save(TcpConnectionSettings settings)
    {
        File.WriteAllText(_filePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }
}
