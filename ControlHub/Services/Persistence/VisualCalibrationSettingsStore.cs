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

    public string? BackupBeforeOverwrite()
    {
        return CalibrationBackupService.BackupBeforeOverwrite(_filePath);
    }
}

public sealed class VisualCalibrationSettings
{
    public string ActiveAxisSet { get; set; } = "First";

    public string ActiveCalibrationMode { get; set; } = "";

    public double StepXPulses { get; set; } = 100_000;

    public double StepYPulses { get; set; } = 100_000;

    public double VelocityPulsesPerSecond { get; set; } = 200_000;

    public int SettleMilliseconds { get; set; } = 300;

    public string MovePriority { get; set; } = "X";

    public string CalibrationFilePath { get; set; } = "";

    public string CalibrationProfilePath { get; set; } = "";

    public bool NozzleOffsetCalibrated { get; set; }

    public double NozzleOffsetXPulses { get; set; }

    public double NozzleOffsetYPulses { get; set; }

    public bool Nozzle2OffsetCalibrated { get; set; }

    public double Nozzle2OffsetXPulses { get; set; }

    public double Nozzle2OffsetYPulses { get; set; }

    public string ClickTargetTool { get; set; } = "Camera";

    public double SecondStepXPulses { get; set; } = 100_000;

    public double SecondStepYPulses { get; set; } = 100_000;

    public double SecondVelocityPulsesPerSecond { get; set; } = 200_000;

    public int SecondSettleMilliseconds { get; set; } = 300;

    public string SecondMovePriority { get; set; } = "X";

    public string SecondCalibrationFilePath { get; set; } = "";

    public string SecondCalibrationProfilePath { get; set; } = "";

    public bool SecondNozzleOffsetCalibrated { get; set; }

    public double SecondNozzleOffsetXPulses { get; set; }

    public double SecondNozzleOffsetYPulses { get; set; }

    public bool SecondNozzle2OffsetCalibrated { get; set; }

    public double SecondNozzle2OffsetXPulses { get; set; }

    public double SecondNozzle2OffsetYPulses { get; set; }

    public string SecondClickTargetTool { get; set; } = "Camera";

    public double LowerCameraStepXPulses { get; set; } = 100_000;

    public double LowerCameraStepYPulses { get; set; } = 100_000;

    public double LowerCameraVelocityPulsesPerSecond { get; set; } = 200_000;

    public int LowerCameraSettleMilliseconds { get; set; } = 300;

    public string LowerCameraMovePriority { get; set; } = "X";

    public int LowerCameraActiveNozzle { get; set; } = 1;

    public double? LowerCameraArrivalPosition1X { get; set; }

    public double? LowerCameraArrivalPosition1Y { get; set; }

    public double? LowerCameraArrivalPosition2X { get; set; }

    public double? LowerCameraArrivalPosition2Y { get; set; }

    public string LowerCameraCalibrationFilePath { get; set; } = "";

    public string LowerCameraNozzle1CalibrationFilePath { get; set; } = "";

    public string LowerCameraNozzle2CalibrationFilePath { get; set; } = "";

    public string LowerCameraNozzle1TeachDataFilePath { get; set; } = "";

    public string LowerCameraNozzle2TeachDataFilePath { get; set; } = "";

    public bool LowerCameraNozzle1RotationCenterCalibrated { get; set; }

    public double LowerCameraNozzle1RotationCenterX { get; set; }

    public double LowerCameraNozzle1RotationCenterY { get; set; }

    public bool LowerCameraNozzle2RotationCenterCalibrated { get; set; }

    public double LowerCameraNozzle2RotationCenterX { get; set; }

    public double LowerCameraNozzle2RotationCenterY { get; set; }
}
