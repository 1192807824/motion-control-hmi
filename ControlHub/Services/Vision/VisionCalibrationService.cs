using System.IO;
using ControlHub.Services.Persistence;

namespace ControlHub.Services.Vision;

public enum VisionTargetTool
{
    Camera,
    Nozzle1,
    Nozzle2
}

public readonly record struct VisionMotionTarget(
    double X,
    double Y,
    VisionTargetTool Tool);

public sealed record VisionCalibrationSnapshot(
    string CalibrationFilePath,
    bool CalibrationFileExists,
    string CalibrationProfilePath,
    bool CalibrationProfileExists,
    bool Nozzle1Calibrated,
    double Nozzle1OffsetX,
    double Nozzle1OffsetY,
    bool Nozzle2Calibrated,
    double Nozzle2OffsetX,
    double Nozzle2OffsetY);

public sealed class VisionCalibrationService
{
    public const int FirstSetXHardwareAxisNo = 1;
    public const int FirstSetYHardwareAxisNo = 2;
    public const double PulsesPerVisionUnit = 10_000d;

    private readonly VisualCalibrationSettingsStore _store = new();

    private VisionCalibrationService()
    {
        Settings = _store.Load();
        Settings.ClickTargetTool = ToSettingsValue(ParseTargetTool(Settings.ClickTargetTool));
    }

    public static VisionCalibrationService Shared { get; } = new();

    public VisualCalibrationSettings Settings { get; }

    public event EventHandler? Changed;

    public void Save()
    {
        _store.Save(Settings);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public VisionCalibrationSnapshot GetSnapshot()
    {
        var path = Settings.CalibrationFilePath?.Trim() ?? "";
        var profilePath = Settings.CalibrationProfilePath?.Trim() ?? "";
        return new VisionCalibrationSnapshot(
            path,
            !string.IsNullOrWhiteSpace(path) && File.Exists(path),
            profilePath,
            !string.IsNullOrWhiteSpace(profilePath) && File.Exists(profilePath),
            Settings.NozzleOffsetCalibrated,
            Settings.NozzleOffsetXPulses,
            Settings.NozzleOffsetYPulses,
            Settings.Nozzle2OffsetCalibrated,
            Settings.Nozzle2OffsetXPulses,
            Settings.Nozzle2OffsetYPulses);
    }

    public bool IsToolCalibrated(VisionTargetTool tool)
    {
        return tool switch
        {
            VisionTargetTool.Camera => true,
            VisionTargetTool.Nozzle1 => Settings.NozzleOffsetCalibrated,
            VisionTargetTool.Nozzle2 => Settings.Nozzle2OffsetCalibrated,
            _ => false
        };
    }

    public VisionMotionTarget CalculateTarget(
        double cameraTargetX,
        double cameraTargetY,
        VisionTargetTool tool)
    {
        if (!double.IsFinite(cameraTargetX) || !double.IsFinite(cameraTargetY))
        {
            throw new ArgumentOutOfRangeException(nameof(cameraTargetX), "相机目标坐标必须是有效数值。");
        }

        var (offsetX, offsetY) = tool switch
        {
            VisionTargetTool.Camera => (0d, 0d),
            VisionTargetTool.Nozzle1 when Settings.NozzleOffsetCalibrated =>
                (Settings.NozzleOffsetXPulses, Settings.NozzleOffsetYPulses),
            VisionTargetTool.Nozzle2 when Settings.Nozzle2OffsetCalibrated =>
                (Settings.Nozzle2OffsetXPulses, Settings.Nozzle2OffsetYPulses),
            VisionTargetTool.Nozzle1 => throw new InvalidOperationException("吸嘴1偏移尚未标定。"),
            VisionTargetTool.Nozzle2 => throw new InvalidOperationException("吸嘴2偏移尚未标定。"),
            _ => throw new ArgumentOutOfRangeException(nameof(tool))
        };

        var targetX = cameraTargetX + offsetX;
        var targetY = cameraTargetY + offsetY;
        if (!double.IsFinite(targetX) || !double.IsFinite(targetY))
        {
            throw new InvalidOperationException("应用视觉工具偏移后的目标坐标无效。");
        }

        return new VisionMotionTarget(targetX, targetY, tool);
    }

    public static VisionTargetTool ParseTargetTool(string? value)
    {
        return value?.Trim() switch
        {
            "Nozzle" or "Nozzle1" => VisionTargetTool.Nozzle1,
            "Nozzle2" => VisionTargetTool.Nozzle2,
            _ => VisionTargetTool.Camera
        };
    }

    public static string ToSettingsValue(VisionTargetTool tool)
    {
        return tool switch
        {
            VisionTargetTool.Nozzle1 => "Nozzle1",
            VisionTargetTool.Nozzle2 => "Nozzle2",
            _ => "Camera"
        };
    }
}
