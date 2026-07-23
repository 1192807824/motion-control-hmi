using System.IO;
using System.Text.Json;

namespace ControlHub.Services.Persistence;

public sealed class HomePageSettingsStore
{
    private readonly string _filePath;

    public HomePageSettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ControlHub",
            "home-page-settings.json");
    }

    public HomePageSettings Load()
    {
        if (!File.Exists(_filePath))
        {
            return new HomePageSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<HomePageSettings>(File.ReadAllText(_filePath))
                ?? new HomePageSettings();
        }
        catch (IOException)
        {
            return new HomePageSettings();
        }
        catch (JsonException)
        {
            return new HomePageSettings();
        }
    }

    public void Save(HomePageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_filePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }
}

public sealed class HomePageSettings
{
    public double? PresetPosition1X { get; set; }

    public double? PresetPosition1Y { get; set; }

    public double? PresetPosition2X { get; set; }

    public double? PresetPosition2Y { get; set; }

    public double? Axis0RelativePulse { get; set; }

    public double? FirstSetPickupZPosition { get; set; }

    public double? FirstSetDropZPosition { get; set; }

    public double? FirstSetSafeZPosition { get; set; }

    public double? SecondSetPickupZPosition { get; set; }

    public double? SecondSetDropZPosition { get; set; }

    public double? SecondSetSafeZPosition { get; set; }

    public double? NozzleZVelocity { get; set; }

    public double? SecondSetPickupPosition1X { get; set; }

    public double? SecondSetPickupPosition1Y { get; set; }

    public double? SecondSetPickupPosition2X { get; set; }

    public double? SecondSetPickupPosition2Y { get; set; }
}
