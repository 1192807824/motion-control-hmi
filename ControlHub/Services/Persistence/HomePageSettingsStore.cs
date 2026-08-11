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
    public double? FirstSetTeachingCenterX { get; set; }

    public double? FirstSetTeachingCenterY { get; set; }

    public double? FirstSetTeachingPressPositionX { get; set; }

    public double? FirstSetTeachingPressPositionY { get; set; }

    public double? PresetPosition1X { get; set; }

    public double? PresetPosition1Y { get; set; }

    public double? PresetPosition2X { get; set; }

    public double? PresetPosition2Y { get; set; }

    public double? LowerCameraPhotoPosition1X { get; set; }

    public double? LowerCameraPhotoPosition1Y { get; set; }

    public double? LowerCameraPhotoPosition2X { get; set; }

    public double? LowerCameraPhotoPosition2Y { get; set; }

    public double? Axis0RelativePulse { get; set; }

    public double? FirstSetPickupZPosition { get; set; }

    public double? FirstSetDropZPosition { get; set; }

    public double? FirstSetSafeZPosition { get; set; }

    public double? SecondSetPickupZPosition { get; set; }

    public double? SecondSetDropZPosition { get; set; }

    public double? SecondSetSafeZPosition { get; set; }

    public double? FirstSetNozzle1PickupZPosition { get; set; }

    public double? FirstSetNozzle1DropZPosition { get; set; }

    public double? FirstSetNozzle1SafeZPosition { get; set; }

    public double? FirstSetNozzle2PickupZPosition { get; set; }

    public double? FirstSetNozzle2DropZPosition { get; set; }

    public double? FirstSetNozzle2SafeZPosition { get; set; }

    public double? SecondSetNozzle1PickupZPosition { get; set; }

    public double? SecondSetNozzle1DropZPosition { get; set; }

    public double? SecondSetNozzle1SafeZPosition { get; set; }

    public double? SecondSetNozzle2PickupZPosition { get; set; }

    public double? SecondSetNozzle2DropZPosition { get; set; }

    public double? SecondSetNozzle2SafeZPosition { get; set; }

    public double? NozzleZVelocity { get; set; }

    public int? VacuumPickupDwellMilliseconds { get; set; }

    public int? VacuumBreakPulseMilliseconds { get; set; }

    public double? SecondSetPickupPosition1X { get; set; }

    public double? SecondSetPickupPosition1Y { get; set; }

    public double? SecondSetPickupPosition2X { get; set; }

    public double? SecondSetPickupPosition2Y { get; set; }

    public double? Bin0PositionX { get; set; }

    public double? Bin0PositionY { get; set; }

    public double? Bin1PositionX { get; set; }

    public double? Bin1PositionY { get; set; }

    public double? Bin2PositionX { get; set; }

    public double? Bin2PositionY { get; set; }

    public double? Bin3PositionX { get; set; }

    public double? Bin3PositionY { get; set; }

    public Dictionary<int, ProductionAxisMotionSettings> ProductionAxisMotionSettings { get; set; } = [];

    public Dictionary<int, TestStationSettings> TestStationSettings { get; set; } = [];
}

public sealed class ProductionAxisMotionSettings
{
    public double RunVelocity { get; set; }

    public double StartVelocity { get; set; }

    public double StopVelocity { get; set; }

    public double AccelerationMilliseconds { get; set; } = 100;

    public double DecelerationMilliseconds { get; set; } = 100;

    public double STimeMilliseconds { get; set; }

    public double DecelerationStopMilliseconds { get; set; } = 100;
}

public sealed class TestStationSettings
{
    public double PressPosition { get; set; }

    public double WaitPosition { get; set; }
}
