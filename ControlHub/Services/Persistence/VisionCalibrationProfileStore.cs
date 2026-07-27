using System.IO;
using System.Text.Json;

namespace ControlHub.Services.Persistence;

public sealed class VisionCalibrationProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public void Save(string filePath, VisionCalibrationProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(profile);
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, JsonSerializer.Serialize(profile, JsonOptions));
    }

    public VisionCalibrationProfile Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("吸嘴标定配置文件不存在。", fullPath);
        }

        VisionCalibrationProfile profile;
        try
        {
            profile = JsonSerializer.Deserialize<VisionCalibrationProfile>(File.ReadAllText(fullPath))
                ?? throw new InvalidDataException("吸嘴标定配置文件内容为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("吸嘴标定配置文件格式不正确。", exception);
        }

        if (profile.Version != 1 ||
            !IsSupportedAxisPair(profile.XHardwareAxisNo, profile.YHardwareAxisNo) ||
            Math.Abs(profile.PulsesPerVisionUnit - 10_000d) > 0.000001d ||
            !double.IsFinite(profile.StepXPulses) ||
            profile.StepXPulses <= 0 ||
            !double.IsFinite(profile.StepYPulses) ||
            profile.StepYPulses <= 0 ||
            !double.IsFinite(profile.VelocityPulsesPerSecond) ||
            profile.VelocityPulsesPerSecond <= 0 ||
            profile.SettleMilliseconds < 0 ||
            (!string.Equals(profile.MovePriority, "X", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(profile.MovePriority, "Y", StringComparison.OrdinalIgnoreCase)) ||
            (profile.NozzleDotPositionRecorded &&
             !AreFinite(profile.NozzleDotPositionXPulses, profile.NozzleDotPositionYPulses)) ||
            !profile.Nozzle1Calibrated ||
            !profile.Nozzle2Calibrated ||
            !AreFinite(
                profile.Nozzle1OffsetXPulses,
                profile.Nozzle1OffsetYPulses,
                profile.Nozzle2OffsetXPulses,
                profile.Nozzle2OffsetYPulses))
        {
            throw new InvalidDataException("配置不是有效的第一套XY双吸嘴标定文件。");
        }

        var calibrationPath = profile.CalibrationFilePath?.Trim() ?? "";
        if (!File.Exists(calibrationPath))
        {
            var portablePath = Path.Combine(
                Path.GetDirectoryName(fullPath) ?? "",
                Path.GetFileName(calibrationPath));
            if (File.Exists(portablePath))
            {
                profile.CalibrationFilePath = portablePath;
            }
        }

        if (!File.Exists(profile.CalibrationFilePath))
        {
            throw new FileNotFoundException(
                "配置引用的九点标定XML不存在。请把JSON与XML放在同一文件夹，或重新标定。",
                profile.CalibrationFilePath);
        }

        return profile;
    }

    private static bool AreFinite(params double[] values)
    {
        return values.All(double.IsFinite);
    }

    private static bool IsSupportedAxisPair(int xHardwareAxisNo, int yHardwareAxisNo)
    {
        return (xHardwareAxisNo == 1 && yHardwareAxisNo == 2) ||
               (xHardwareAxisNo == 3 && yHardwareAxisNo == 4);
    }
}

public sealed class VisionCalibrationProfile
{
    public int Version { get; set; } = 1;

    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;

    public int XHardwareAxisNo { get; set; } = 1;

    public int YHardwareAxisNo { get; set; } = 2;

    public double PulsesPerVisionUnit { get; set; } = 10_000d;

    public string CalibrationFilePath { get; set; } = "";

    public double StepXPulses { get; set; } = 100_000d;

    public double StepYPulses { get; set; } = 100_000d;

    public double VelocityPulsesPerSecond { get; set; } = 200_000d;

    public int SettleMilliseconds { get; set; } = 300;

    public string MovePriority { get; set; } = "X";

    public bool NozzleDotPositionRecorded { get; set; }

    public double NozzleDotPositionXPulses { get; set; }

    public double NozzleDotPositionYPulses { get; set; }

    public bool Nozzle1Calibrated { get; set; }

    public double Nozzle1OffsetXPulses { get; set; }

    public double Nozzle1OffsetYPulses { get; set; }

    public bool Nozzle2Calibrated { get; set; }

    public double Nozzle2OffsetXPulses { get; set; }

    public double Nozzle2OffsetYPulses { get; set; }
}
