using System.IO;
using ControlHub.Services.Persistence;

namespace ControlHub.Services.Vision;

public enum VisionTargetTool
{
    Camera,
    Nozzle1,
    Nozzle2
}

public enum VisionCalibrationAxisSet
{
    First,
    Second
}

public readonly record struct VisionCalibrationAxisPair(
    int XHardwareAxisNo,
    int YHardwareAxisNo);

public readonly record struct VisionMotionTarget(
    double X,
    double Y,
    VisionTargetTool Tool,
    double RotationDegrees = 0d);

public readonly record struct DualNozzleMechanicalTargets(
    VisionMotionTarget Nozzle1,
    VisionMotionTarget Nozzle2);

public sealed record VisionCalibrationSnapshot(
    VisionCalibrationAxisSet AxisSet,
    string AxisSetDisplayName,
    int XHardwareAxisNo,
    int YHardwareAxisNo,
    string CalibrationFilePath,
    bool CalibrationFileExists,
    string CalibrationProfilePath,
    bool CalibrationProfileExists,
    double VelocityPulsesPerSecond,
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
    public const int SecondSetXHardwareAxisNo = 3;
    public const int SecondSetYHardwareAxisNo = 4;
    public const double PulsesPerVisionUnit = 10_000d;

    private readonly VisualCalibrationSettingsStore _store = new();

    private VisionCalibrationService()
    {
        Settings = _store.Load();
        Settings.ActiveAxisSet = ToSettingsValue(ParseAxisSet(Settings.ActiveAxisSet));
        Settings.ClickTargetTool = ToSettingsValue(ParseTargetTool(Settings.ClickTargetTool));
        Settings.SecondClickTargetTool = ToSettingsValue(ParseTargetTool(Settings.SecondClickTargetTool));
    }

    public static VisionCalibrationService Shared { get; } = new();

    public VisualCalibrationSettings Settings { get; }

    public event EventHandler? Changed;

    public VisionCalibrationAxisSet ActiveAxisSet
    {
        get => ParseAxisSet(Settings.ActiveAxisSet);
        set => Settings.ActiveAxisSet = ToSettingsValue(value);
    }

    public VisionCalibrationAxisPair ActiveAxisPair => GetAxisPair(ActiveAxisSet);

    public void Save()
    {
        _store.Save(Settings);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public VisualCalibrationSettings CaptureRecipeSettings()
    {
        return ProductRecipeStore.Clone(Settings);
    }

    public void ApplyRecipeSettings(VisualCalibrationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var source = ProductRecipeStore.Clone(settings);
        foreach (var property in typeof(VisualCalibrationSettings).GetProperties()
                     .Where(property => property.CanRead && property.CanWrite))
        {
            property.SetValue(Settings, property.GetValue(source));
        }

        Settings.ActiveAxisSet = ToSettingsValue(ParseAxisSet(Settings.ActiveAxisSet));
        Settings.ClickTargetTool = ToSettingsValue(ParseTargetTool(Settings.ClickTargetTool));
        Settings.SecondClickTargetTool = ToSettingsValue(ParseTargetTool(Settings.SecondClickTargetTool));
        Save();
    }

    public string? BackupSettingsBeforeOverwrite()
    {
        return _store.BackupBeforeOverwrite();
    }

    public VisionCalibrationSnapshot GetSnapshot()
    {
        var axisSet = ActiveAxisSet;
        var axes = GetAxisPair(axisSet);
        var path = GetCalibrationFilePath(axisSet);
        var profilePath = GetCalibrationProfilePath(axisSet);
        var offsets = GetNozzleOffsets(axisSet);
        return new VisionCalibrationSnapshot(
            axisSet,
            GetAxisSetDisplayName(axisSet),
            axes.XHardwareAxisNo,
            axes.YHardwareAxisNo,
            path,
            !string.IsNullOrWhiteSpace(path) && File.Exists(path),
            profilePath,
            !string.IsNullOrWhiteSpace(profilePath) && File.Exists(profilePath),
            GetVelocity(axisSet),
            offsets.Nozzle1Calibrated,
            offsets.Nozzle1OffsetX,
            offsets.Nozzle1OffsetY,
            offsets.Nozzle2Calibrated,
            offsets.Nozzle2OffsetX,
            offsets.Nozzle2OffsetY);
    }

    public bool IsToolCalibrated(VisionTargetTool tool)
    {
        var offsets = GetNozzleOffsets(ActiveAxisSet);
        return tool switch
        {
            VisionTargetTool.Camera => true,
            VisionTargetTool.Nozzle1 => offsets.Nozzle1Calibrated,
            VisionTargetTool.Nozzle2 => offsets.Nozzle2Calibrated,
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
            throw new ArgumentOutOfRangeException(nameof(cameraTargetX), "Camera target coordinates must be finite.");
        }

        var offsets = GetNozzleOffsets(ActiveAxisSet);
        var (offsetX, offsetY) = tool switch
        {
            VisionTargetTool.Camera => (0d, 0d),
            VisionTargetTool.Nozzle1 when offsets.Nozzle1Calibrated =>
                (offsets.Nozzle1OffsetX, offsets.Nozzle1OffsetY),
            VisionTargetTool.Nozzle2 when offsets.Nozzle2Calibrated =>
                (offsets.Nozzle2OffsetX, offsets.Nozzle2OffsetY),
            VisionTargetTool.Nozzle1 => throw new InvalidOperationException("Nozzle 1 offset is not calibrated."),
            VisionTargetTool.Nozzle2 => throw new InvalidOperationException("Nozzle 2 offset is not calibrated."),
            _ => throw new ArgumentOutOfRangeException(nameof(tool))
        };

        var targetX = cameraTargetX + offsetX;
        var targetY = cameraTargetY + offsetY;
        if (!double.IsFinite(targetX) || !double.IsFinite(targetY))
        {
            throw new InvalidOperationException("Target coordinates are invalid after applying tool offset.");
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

    public static VisionCalibrationAxisSet ParseAxisSet(string? value)
    {
        return value?.Trim() switch
        {
            "Second" or "Axis34" or "2" => VisionCalibrationAxisSet.Second,
            _ => VisionCalibrationAxisSet.First
        };
    }

    public static string ToSettingsValue(VisionCalibrationAxisSet axisSet)
    {
        return axisSet == VisionCalibrationAxisSet.Second ? "Second" : "First";
    }

    public static string GetAxisSetDisplayName(VisionCalibrationAxisSet axisSet)
    {
        return axisSet == VisionCalibrationAxisSet.Second ? "第二套轴 3/4" : "第一套轴 1/2";
    }

    public static VisionCalibrationAxisPair GetAxisPair(VisionCalibrationAxisSet axisSet)
    {
        return axisSet == VisionCalibrationAxisSet.Second
            ? new VisionCalibrationAxisPair(SecondSetXHardwareAxisNo, SecondSetYHardwareAxisNo)
            : new VisionCalibrationAxisPair(FirstSetXHardwareAxisNo, FirstSetYHardwareAxisNo);
    }

    public string GetCalibrationFilePath(VisionCalibrationAxisSet axisSet)
    {
        return (axisSet == VisionCalibrationAxisSet.Second
            ? Settings.SecondCalibrationFilePath
            : Settings.CalibrationFilePath)?.Trim() ?? "";
    }

    public string GetCalibrationProfilePath(VisionCalibrationAxisSet axisSet)
    {
        return (axisSet == VisionCalibrationAxisSet.Second
            ? Settings.SecondCalibrationProfilePath
            : Settings.CalibrationProfilePath)?.Trim() ?? "";
    }

    public double GetVelocity(VisionCalibrationAxisSet axisSet)
    {
        return axisSet == VisionCalibrationAxisSet.Second
            ? Settings.SecondVelocityPulsesPerSecond
            : Settings.VelocityPulsesPerSecond;
    }

    private (
        bool Nozzle1Calibrated,
        double Nozzle1OffsetX,
        double Nozzle1OffsetY,
        bool Nozzle2Calibrated,
        double Nozzle2OffsetX,
        double Nozzle2OffsetY) GetNozzleOffsets(VisionCalibrationAxisSet axisSet)
    {
        return axisSet == VisionCalibrationAxisSet.Second
            ? (
                Settings.SecondNozzleOffsetCalibrated,
                Settings.SecondNozzleOffsetXPulses,
                Settings.SecondNozzleOffsetYPulses,
                Settings.SecondNozzle2OffsetCalibrated,
                Settings.SecondNozzle2OffsetXPulses,
                Settings.SecondNozzle2OffsetYPulses)
            : (
                Settings.NozzleOffsetCalibrated,
                Settings.NozzleOffsetXPulses,
                Settings.NozzleOffsetYPulses,
                Settings.Nozzle2OffsetCalibrated,
                Settings.Nozzle2OffsetXPulses,
                Settings.Nozzle2OffsetYPulses);
    }
}
