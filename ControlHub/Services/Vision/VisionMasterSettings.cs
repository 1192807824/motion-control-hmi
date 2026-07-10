namespace ControlHub.Services.Vision;

public sealed class VisionMasterSettings
{
    public string SdkDirectory { get; set; } = @"C:\Program Files\VisionMaster4.4.1";

    public string SolutionPath { get; set; } = @"D:\Vision\TrayInspect.sol";

    public string ProcedureName { get; set; } = "流程1";

    public string InputImageName { get; set; } = "ImageData";

    public string OutputImageName { get; set; } = "ImageData0";

    public int ContinuousRunIntervalMs { get; set; } = 500;

    public string CameraName { get; set; } = "HikRobot-1";

    public string CameraIp { get; set; } = "192.168.1.64";

    public double ExposureTimeUs { get; set; } = 8000;

    public double Gain { get; set; } = 1.0;

    public string TriggerMode { get; set; } = "Software";

    public string PixelFormat { get; set; } = "Mono8";
}
