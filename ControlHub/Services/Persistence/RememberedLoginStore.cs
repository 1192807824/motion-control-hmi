using System.IO;
using System.Text.Json;

namespace ControlHub.Services.Persistence;

public sealed class RememberedLoginStore
{
    private readonly string _filePath = Path.Combine(AppContext.BaseDirectory, "remembered-login.json");
    private readonly string _legacyFilePath = Path.Combine(AppContext.BaseDirectory, "permission-session.json");

    public RememberedLogin? Load()
    {
        var sourcePath = File.Exists(_filePath) ? _filePath : _legacyFilePath;
        if (!File.Exists(sourcePath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RememberedLogin>(File.ReadAllText(sourcePath));
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(RememberedLogin login)
    {
        File.WriteAllText(_filePath, JsonSerializer.Serialize(login, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }
}

public sealed record RememberedLogin(string UserName, string Role);
