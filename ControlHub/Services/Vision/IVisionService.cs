namespace ControlHub.Services.Vision;

public interface IVisionService : IDisposable
{
    bool IsLoaded { get; }

    VisionRunResult LoadSolution(VisionMasterSettings settings);

    VisionRunResult RunOnce(VisionMasterSettings settings);

    void StartContinuous(VisionMasterSettings settings);

    void StopContinuous();
}
