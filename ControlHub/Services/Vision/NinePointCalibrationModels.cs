namespace ControlHub.Services.Vision;

public sealed record VisionPointMeasurement(
    double ImageX,
    double ImageY,
    double RunTimeMs);

public sealed class NinePointCalibrationSample
{
    public required int Index { get; init; }

    public required double TargetMachineX { get; init; }

    public required double TargetMachineY { get; init; }

    public required double ActualMachineX { get; init; }

    public required double ActualMachineY { get; init; }

    public required double ImageX { get; init; }

    public required double ImageY { get; init; }
}

public sealed record VisionCalibrationFileResult(
    string FilePath,
    int ModuleStatus,
    int CalibrationStatus,
    int CalibrationErrorStatus,
    double TranslationError,
    double TranslationWorldError,
    double PixelPrecision);
