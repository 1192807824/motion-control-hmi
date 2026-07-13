using VMControls.Interface;

namespace ControlHub.Services.Vision;

public interface IVisionService : IDisposable
{
    bool IsLoaded { get; }

    IVmModule? RenderModuleSource { get; }

    VisionRunResult LoadSolution(VisionMasterSettings settings);

    VisionRunResult RunOnce(VisionMasterSettings settings);

    VisionPointMeasurement CapturePoint(VisionMasterSettings settings);

    VisionCalibrationFileResult GenerateNinePointCalibrationFile(
        VisionMasterSettings settings,
        IReadOnlyList<NinePointCalibrationSample> samples,
        string filePath);
}
