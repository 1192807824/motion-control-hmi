using System.IO;
using System.Text.Json;

namespace ControlHub.Services.Persistence;

public sealed class VisualCalibrationSettingsStore
{
    private readonly string _filePath;

    public VisualCalibrationSettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ControlHub",
            "visual-calibration-settings.json");
    }

    public VisualCalibrationSettings Load()
    {
        if (!File.Exists(_filePath))
        {
            return new VisualCalibrationSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<VisualCalibrationSettings>(File.ReadAllText(_filePath))
                ?? new VisualCalibrationSettings();
        }
        catch (IOException)
        {
            return new VisualCalibrationSettings();
        }
        catch (JsonException)
        {
            return new VisualCalibrationSettings();
        }
    }

    public void Save(VisualCalibrationSettings settings)
    {
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

public sealed class VisualCalibrationSettings
{
    public double StepXPulses { get; set; } = 100_000;

    public double StepYPulses { get; set; } = 100_000;

    public double VelocityPulsesPerSecond { get; set; } = 100_000;

    public int SettleMilliseconds { get; set; } = 300;

    public string MovePriority { get; set; } = "X";

    public string CalibrationFilePath { get; set; } = "";

    public bool NozzleOffsetCalibrated { get; set; }

    public double NozzleOffsetXPulses { get; set; }

    public double NozzleOffsetYPulses { get; set; }

    public bool Nozzle2OffsetCalibrated { get; set; }

    public double Nozzle2OffsetXPulses { get; set; }

    public double Nozzle2OffsetYPulses { get; set; }

    public string ClickTargetTool { get; set; } = "Camera";
}
