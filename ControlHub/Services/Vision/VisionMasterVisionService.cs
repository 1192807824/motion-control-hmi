using System.Diagnostics;
using System.IO;
using ControlHub.Models;
using IMVSNPointCalibModuCs;
using VM.Core;
using VM.PlatformSDKCS;
using VMControls.Interface;

namespace ControlHub.Services.Vision;

public sealed class VisionMasterVisionService : IVisionService
{
    private VmProcedure? _procedure;
    private bool _disposed;

    public bool IsLoaded => _procedure is not null;

    public IVmModule? RenderModuleSource => _procedure;

    public VisionRunResult LoadSolution(VisionMasterSettings settings)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(settings);

        var solutionPath = settings.SolutionPath?.Trim() ?? "";
        var procedureName = settings.ProcedureName?.Trim() ?? "";
        if (!File.Exists(solutionPath))
        {
            throw new FileNotFoundException("未找到 VisionMaster 方案文件。", solutionPath);
        }

        if (string.IsNullOrWhiteSpace(procedureName))
        {
            throw new InvalidOperationException("请填写 VisionMaster 流程名称。 ");
        }

        ReleaseSolution();
        VmSolution.Load(solutionPath, "");
        _procedure = VmSolution.Instance[procedureName] as VmProcedure
            ?? throw new InvalidOperationException($"方案中未找到流程“{procedureName}”。");

        return new VisionRunResult
        {
            IsOk = true,
            Message = $"方案已加载：{Path.GetFileName(solutionPath)} / {procedureName}",
            Outputs =
            [
                new VisionOutputItem { Name = "方案", Type = "File", Value = solutionPath },
                new VisionOutputItem { Name = "流程", Type = "Procedure", Value = procedureName }
            ]
        };
    }

    public VisionRunResult RunOnce(VisionMasterSettings settings)
    {
        var measurement = CapturePoint(settings);
        return new VisionRunResult
        {
            IsOk = true,
            RunTimeMs = measurement.RunTimeMs,
            Message = $"流程执行完成，图像点 ({measurement.ImageX:0.###}, {measurement.ImageY:0.###})",
            Outputs =
            [
                new VisionOutputItem
                {
                    Name = settings.PointXOutputName,
                    Type = "Float",
                    Value = measurement.ImageX.ToString("0.######")
                },
                new VisionOutputItem
                {
                    Name = settings.PointYOutputName,
                    Type = "Float",
                    Value = measurement.ImageY.ToString("0.######")
                }
            ]
        };
    }

    public VisionPointMeasurement CapturePoint(VisionMasterSettings settings)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(settings);
        var procedure = _procedure
            ?? throw new InvalidOperationException("请先加载 VisionMaster 方案。 ");

        var stopwatch = Stopwatch.StartNew();
        procedure.Run();
        stopwatch.Stop();

        var imageX = ReadFloatOutput(procedure, settings.PointXOutputName, "X");
        var imageY = ReadFloatOutput(procedure, settings.PointYOutputName, "Y");
        if (!double.IsFinite(imageX) || !double.IsFinite(imageY))
        {
            throw new InvalidOperationException("VisionMaster 输出的图像点不是有效数值。 ");
        }

        var moduleRunTime = procedure.ModuResult.ModuRunTime;
        return new VisionPointMeasurement(
            imageX,
            imageY,
            moduleRunTime > 0 ? moduleRunTime : stopwatch.Elapsed.TotalMilliseconds);
    }

    public VisionCalibrationFileResult GenerateNinePointCalibrationFile(
        VisionMasterSettings settings,
        IReadOnlyList<NinePointCalibrationSample> samples,
        string filePath)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(samples);

        if (_procedure is null)
        {
            throw new InvalidOperationException("请先加载 VisionMaster 方案。 ");
        }

        if (samples.Count != 9)
        {
            throw new InvalidOperationException($"生成九点标定文件需要 9 个点，当前只有 {samples.Count} 个。 ");
        }

        var configuredOutputPath = filePath?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(configuredOutputPath))
        {
            throw new InvalidOperationException("请指定标定文件保存路径。 ");
        }

        var outputPath = Path.GetFullPath(configuredOutputPath);
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new InvalidOperationException("标定文件路径无效。 ");
        }

        Directory.CreateDirectory(outputDirectory);

        var moduleName = settings.NPointCalibrationModuleName?.Trim() ?? "";
        var calibrationTool = VmSolution.Instance[moduleName] as IMVSNPointCalibModuTool
            ?? throw new InvalidOperationException($"方案中未找到 N 点标定模块“{moduleName}”，请填写模块完整路径。 ");

        var imagePoints = samples
            .Select(sample => new PointF((float)sample.ImageX, (float)sample.ImageY))
            .ToList();
        var physicalPoints = samples
            .Select(sample => new PointF((float)sample.ActualMachineX, (float)sample.ActualMachineY))
            .ToList();

        var parameters = calibrationTool.ModuParams;
        var clearResult = parameters.DoClearPoint();
        if (clearResult != 0)
        {
            throw new InvalidOperationException($"N 点标定模块清空旧点位失败，SDK 返回码：{clearResult}。 ");
        }

        parameters.CalibPointGet = NPointCalibParam.CalibPointGetEnum.ManualInput;
        parameters.CalibPointTotalNum = samples.Count;
        parameters.RotPointTotalNum = 0;
        parameters.UseRelativeCoordinates = false;
        parameters.ImagePoint = imagePoints;
        parameters.PhysicalPoint = physicalPoints;
        parameters.ImageRotateAngle = [];
        parameters.WorldRotateAngle = [];

        calibrationTool.Run();
        var result = calibrationTool.ModuResult;
        if (result.ModuStatus != 0 || result.CalibStatus != 0 || result.CalibErrStatus != 0)
        {
            throw new InvalidOperationException(
                $"N 点标定计算失败：模块状态={result.ModuStatus}，" +
                $"标定状态={result.CalibStatus}，误差评估状态={result.CalibErrStatus}。 ");
        }

        var saveResult = parameters.DoSaveFile(outputPath);
        if (saveResult != 0)
        {
            throw new InvalidOperationException($"VisionMaster 生成标定文件失败，SDK 返回码：{saveResult}。 ");
        }

        if (!File.Exists(outputPath))
        {
            throw new IOException($"VisionMaster 未在指定位置生成标定文件：{outputPath}");
        }

        return new VisionCalibrationFileResult(
            outputPath,
            result.ModuStatus,
            result.CalibStatus,
            result.CalibErrStatus,
            result.TransError,
            result.TransWorldError,
            result.PixelPrecision);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ReleaseSolution();
        _disposed = true;
    }

    private static double ReadFloatOutput(VmProcedure procedure, string configuredName, string coordinateName)
    {
        var outputName = configuredName?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(outputName))
        {
            throw new InvalidOperationException($"请填写图像点 {coordinateName} 的流程输出名称。 ");
        }

        var output = procedure.ModuResult.GetOutputFloat(outputName);
        if (output.nValueNum < 1 || output.pFloatVal is null || output.pFloatVal.Length < 1)
        {
            throw new InvalidOperationException($"流程输出“{outputName}”不存在或没有浮点值。 ");
        }

        return output.pFloatVal[0];
    }

    private void ReleaseSolution()
    {
        _procedure = null;
        try
        {
            VmSolution.Instance?.Dispose();
        }
        catch
        {
            // A subsequent SDK load will report the actionable VisionMaster error.
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
