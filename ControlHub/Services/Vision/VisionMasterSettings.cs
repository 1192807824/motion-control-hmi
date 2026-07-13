namespace ControlHub.Services.Vision;

public sealed class VisionMasterSettings
{
    public string SolutionPath { get; set; } = "";

    public string ProcedureName { get; set; } = "流程1";

    public string PointXOutputName { get; set; } = "X";

    public string PointYOutputName { get; set; } = "Y";

    public string NPointCalibrationModuleName { get; set; } = "流程1.N点标定1";
}
