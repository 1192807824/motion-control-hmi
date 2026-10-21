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
            var settings = JsonSerializer.Deserialize<TcpConnectionSettings>(File.ReadAllText(_filePath))
                ?? new TcpConnectionSettings();

            MigrateLegacyGenericTcpDefaults(settings);
            return settings;
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

    private static void MigrateLegacyGenericTcpDefaults(TcpConnectionSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.LastSuccessfulConnectionSignature)
            || !string.Equals(settings.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || settings.Port != 5000)
        {
            return;
        }

        settings.Host = "192.168.1.101";
        settings.Port = 5025;
        settings.NewLine = "\\n";
        settings.ManualSendText = "*IDN?";
        settings.AppendNewLine = true;
    }
}
