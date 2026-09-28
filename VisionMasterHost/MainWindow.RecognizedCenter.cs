using System.Globalization;
using System.IO;
using IMVSHPFeatureMatchModuCs;
using IMVSCalibTransformModuCs;
using ImageSourceModuleCs;
using VM.Core;

namespace VisionMasterHost;

public partial class MainWindow
{
    private string RunRecognizedCenter(IReadOnlyList<string> parts)
    {
        const string procedureName = "找芯片流程_011_测试";
        if (parts.Count != 2) throw new InvalidDataException("识别中心点移动参数无效。");
        var calibrationPath = DecodeAndValidateCalibrationFilePath(parts[1]);
        if (_busy || _calibrationSession is not null || _clickMoveEnabled)
            throw new InvalidOperationException("请先停止当前标定或点击图像移动。");
        var stage = "加载视觉方案";
        try
        {
            if (!_solutionLoaded) LoadFixedSolution();
            if (!_solutionLoaded) throw new InvalidOperationException(_fixedSolutionLoadError ?? "视觉方案未加载成功。");
            if (!string.Equals(_activeCalibrationProcedureName, _calibrationProcedureName, StringComparison.Ordinal))
                throw new InvalidOperationException("识别中心点移动仅允许在第一套视觉使用。");
            stage = "查找流程及107/108模块";
            // SDK对不存在的流程名会直接抛E0000001，先枚举给出可定位的错误。
            var procedures = VmSolution.Instance.GetAllProcedureList();
            var names = Enumerable.Range(0, (int)procedures.nNum)
                .Select(index => procedures.astProcessInfo[index].strProcessName).ToArray();
            if (!names.Contains(procedureName, StringComparer.Ordinal))
                throw new InvalidOperationException($"方案“{_loadedSolutionPath}”中未找到“{procedureName}”；已有流程：{string.Join("、", names)}。");
            var procedure = GetRequiredProcedure(procedureName);
            var match = ResolveModuleById<IMVSHPFeatureMatchModuTool>(procedure, procedureName, 108);
            var source = ResolveModuleById<ImageSourceModuleTool>(procedure, procedureName, 107);
            if (match.Root?.ID != procedure.ID || source.Root?.ID != procedure.ID)
                throw new InvalidOperationException("107图像源和108高精度匹配必须属于找芯片流程_011_测试。");
            stage = "停止连续采集";
            StopAllContinuousExecutionNoThrow();
            PrepareLiveRendererForCameraAcquisition();
            ApplySingleRenderLayout(showLive: true);
            LiveRenderTitleText.Text = "108 高精度匹配 · 识别中心点";
            _clickCenterPixelReady = false;
            SetBusy(true);
            stage = "执行找芯片流程_011_测试";
            procedure.Run(true);
            EnsureProcedureRunSucceeded(procedure, procedureName);
            stage = "读取108匹配框结果";
            var result = match.ModuResult;
            if (result is null) throw new InvalidOperationException("108未返回本次匹配结果，禁止移动。");
            var matchDetails = $"模块状态={result.ModuStatus}，匹配数量={result.MatchNum}，匹配框数量={result.MatchRect?.Count ?? 0}，错误码=0x{result.ErrorCode:X8}";
            WriteRecognizedCenterLog(stage, matchDetails);
            var matchError = RecognizedCenterValidation.GetMatchError(result.ModuStatus, result.MatchNum, result.MatchRect?.Count ?? 0);
            if (matchError is not null)
            {
                // NG只尝试显示本次107原图；渲染失败不能覆盖真正的匹配失败原因。
                TryShowRecognizedCenterImage(source);
                throw new InvalidOperationException($"{matchError}（{matchDetails}）；禁止移动。");
            }
            // 红框列是 MatchRect.CenterPoint，不能使用 MatchPoint（模板参考点）。
            var pixel = result.MatchRect![0].CenterPoint;
            stage = "读取107本次图像尺寸";
            // 图像源->颜色转换->108，尺寸不变。直接读取107输出，不依赖渲染控件缓存。
            var inputImage = source.ModuResult.ImageData;
            if (inputImage is null || inputImage.Width < 2 || inputImage.Height < 2 ||
                float.IsNaN(pixel.X) || float.IsInfinity(pixel.X) || float.IsNaN(pixel.Y) || float.IsInfinity(pixel.Y) ||
                pixel.X < 0 || pixel.Y < 0 || pixel.X > inputImage.Width - 1 || pixel.Y > inputImage.Height - 1)
                throw new InvalidDataException("108匹配框中心或本次图像尺寸无效。");
            var centerX = (float)((inputImage.Width - 1) / 2d);
            var centerY = (float)((inputImage.Height - 1) / 2d);
            var previewWarning = ShowRecognizedCenterPreview(procedureName, match);
            stage = "载入第一套标定文件";
            var transform = GetCalibrationTransformModule();
            transform.ModuParams.LoadCalibPath = calibrationPath;
            stage = "设置匹配中心与图像中心转换输入";
            transform.ModuParams.InputPoint = [pixel, new VM.PlatformSDKCS.PointF { X = centerX, Y = centerY }];
            stage = "执行标定坐标转换";
            transform.Run();
            stage = "读取标定坐标转换结果";
            var converted = transform.ModuResult;
            if (converted is null || converted.ModuStatus != 1 || converted.TransPoint is null || converted.TransPoint.Count != 2)
                throw new InvalidOperationException("标定转换未返回匹配框中心和本次图像中心的机械坐标。");
            var point = converted.TransPoint[0];
            var center = converted.TransPoint[1];
            var values = new[] { pixel.X, pixel.Y, point.X, point.Y, centerX, centerY, center.X, center.Y };
            if (values.Any(value => float.IsNaN(value) || float.IsInfinity(value)))
                throw new InvalidDataException("识别中心点转换结果无效。");
            SetStatus($"{procedureName}：匹配框中心 X={pixel.X:F3}，Y={pixel.Y:F3}{previewWarning}", StatusKind.Success);
            return string.Join("\t", values.Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
        }
        catch (Exception exception)
        {
            WriteRecognizedCenterLog(stage, exception.ToString());
            throw new RecognizedCenterStageException(stage, FormatException(exception), exception);
        }
        finally { SetBusy(false); }
    }

    private string ShowRecognizedCenterPreview(string procedureName, VmModule match)
    {
        try
        {
            var render = ResolveNamedModule<VmModule>(procedureName, "图像渲染1");
            if (TryShowRecognizedCenterImage(render)) return "";
        }
        catch (Exception exception) { WriteRecognizedCenterLog("查找可选图像渲染1", exception.Message); }
        return TryShowRecognizedCenterImage(match) ? "" : "；图像预览失败，请查看视觉日志";
    }

    private bool TryShowRecognizedCenterImage(VmModule module)
    {
        try
        {
            // 只在绑定前清理一次，不能在绑定有效结果后再次清空。
            VisionRenderControl.ModuleSource = null;
            VisionRenderControl.ClearDisplayView();
            BindInspectionResultModule(module);
            VisionRenderControl.UpdateVMResultShow();
            if (!HasCurrentVisionRenderImage()) throw new InvalidOperationException("渲染未返回本次图像。");
            ImagePlaceholder.Visibility = System.Windows.Visibility.Collapsed;
            return true;
        }
        catch (Exception exception)
        {
            ImagePlaceholder.Visibility = System.Windows.Visibility.Visible;
            WriteRecognizedCenterLog("显示识别结果", exception.ToString());
            return false;
        }
    }

    private void WriteRecognizedCenterLog(string stage, string details)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ControlHub", "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "vision-recognized-center.log"),
                $"{DateTime.Now:O} [{stage}] 方案={_loadedSolutionPath}{Environment.NewLine}{details}{Environment.NewLine}");
        }
        catch { /* 日志失败不得覆盖原始识别结果。 */ }
    }
}
