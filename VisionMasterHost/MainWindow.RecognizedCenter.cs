using System.Globalization;
using System.IO;
using ImageSourceModuleCs;
using ShellModuleCs;
using VM.Core;

namespace VisionMasterHost;

public partial class MainWindow
{
    private string RunRecognizedCenter(IReadOnlyList<string> parts)
    {
        var procedureName = _recognizedCenterProcedureName;
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
            stage = "查找流程及图像源1/脚本1";
            // SDK对不存在的流程名会直接抛E0000001，先枚举给出可定位的错误。
            var procedures = VmSolution.Instance.GetAllProcedureList();
            var names = Enumerable.Range(0, (int)procedures.nNum)
                .Select(index => procedures.astProcessInfo[index].strProcessName).ToArray();
            if (!names.Contains(procedureName, StringComparer.Ordinal))
                throw new InvalidOperationException($"方案“{_loadedSolutionPath}”中未找到“{procedureName}”；已有流程：{string.Join("、", names)}。");
            var procedure = GetRequiredProcedure(procedureName);
            // 模块编号会随复制/导入变化，只按配方流程内的模块名称查找。
            var script = ResolveNamedModule<ShellModuleTool>(procedureName, InspectionScriptModuleName);
            var source = ResolveNamedModule<ImageSourceModuleTool>(procedureName, CalibrationImageSourceName);
            if (script.Root?.ID != procedure.ID || source.Root?.ID != procedure.ID)
                throw new InvalidOperationException($"图像源1和脚本1必须属于所选流程“{procedureName}”，不能使用其他流程的同名模块。");
            stage = "停止连续采集";
            StopAllContinuousExecutionNoThrow();
            PrepareLiveRendererForCameraAcquisition();
            ApplySingleRenderLayout(showLive: true);
            LiveRenderTitleText.Text = "识别中心点 · 脚本1坐标 / 本流程匹配预览";
            _clickCenterPixelReady = false;
            SetBusy(true);
            stage = $"执行{procedureName}";
            procedure.Run(true);
            EnsureProcedureRunSucceeded(procedure, procedureName);
            stage = "读取脚本1的X/Y/R结果";
            var result = script.ModuResult;
            if (result is null) throw new InvalidOperationException("脚本1未返回本次结果，禁止移动。");
            WriteRecognizedCenterLog(stage, $"流程={procedureName}，脚本1 ID={script.ID}，图像源1 ID={source.ID}，模块状态={result.ModuStatus}，错误码=0x{result.ErrorCode:X8}");
            // 与生产找芯片共用读取方式：优先X/Y/R浮点输出，兼容ResultShow中的X:...,Y:...,R:...。
            // 匹配模块只负责预览，不用它的状态、MatchNum或MatchRect决定移动坐标。
            var rows = result.ModuStatus == 1 ? ReadAllScriptResults(result) : new List<RectangleBlobCandidate>();
            var scriptError = RecognizedCenterValidation.GetScriptError(result.ModuStatus, rows.Count);
            if (scriptError is not null)
            {
                // NG只尝试显示本次原图；渲染失败不能覆盖真正的匹配失败原因。
                TryShowRecognizedCenterImage(source);
                throw new InvalidOperationException($"{scriptError}（模块状态={result.ModuStatus}，结果数量={rows.Count}）；禁止移动。");
            }
            var row = rows[0];
            var pixel = new VM.PlatformSDKCS.PointF { X = row.PixelX, Y = row.PixelY };
            WriteRecognizedCenterLog(stage, FormattableString.Invariant($"脚本1：X={pixel.X:R},Y={pixel.Y:R},R={row.RotationDegrees:R}；仅使用XY进行标定转换。"));
            stage = "读取图像源1本次图像尺寸";
            // 直接读取所选流程的图像源输出，不依赖渲染控件缓存。
            var inputImage = source.ModuResult.ImageData;
            if (inputImage is null || inputImage.Width < 2 || inputImage.Height < 2 ||
                float.IsNaN(pixel.X) || float.IsInfinity(pixel.X) || float.IsNaN(pixel.Y) || float.IsInfinity(pixel.Y) ||
                pixel.X < 0 || pixel.Y < 0 || pixel.X > inputImage.Width - 1 || pixel.Y > inputImage.Height - 1)
                throw new InvalidDataException("脚本1的XY超出本次图像范围或图像尺寸无效，请确认脚本输出为像素坐标。");
            var centerX = (float)((inputImage.Width - 1) / 2d);
            var centerY = (float)((inputImage.Height - 1) / 2d);
            var previewWarning = ShowRecognizedCenterPreview(procedureName, procedure, source);
            stage = "载入第一套标定文件";
            var transform = GetCalibrationTransformModule();
            transform.ModuParams.LoadCalibPath = calibrationPath;
            stage = "设置脚本坐标与图像中心转换输入";
            transform.ModuParams.InputPoint = [pixel, new VM.PlatformSDKCS.PointF { X = centerX, Y = centerY }];
            stage = "执行标定坐标转换";
            transform.Run();
            stage = "读取标定坐标转换结果";
            var converted = transform.ModuResult;
            if (converted is null || converted.ModuStatus != 1 || converted.TransPoint is null || converted.TransPoint.Count != 2)
                throw new InvalidOperationException("标定转换未返回脚本坐标和本次图像中心的机械坐标。");
            var point = converted.TransPoint[0];
            var center = converted.TransPoint[1];
            var values = new[] { pixel.X, pixel.Y, point.X, point.Y, centerX, centerY, center.X, center.Y };
            if (values.Any(value => float.IsNaN(value) || float.IsInfinity(value)))
                throw new InvalidDataException("识别中心点转换结果无效。");
            SetStatus($"脚本1：X={pixel.X:F3}，Y={pixel.Y:F3}，R={row.RotationDegrees:F3}°（仅移动XY）{previewWarning}", StatusKind.Success);
            return string.Join("\t", values.Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
        }
        catch (Exception exception)
        {
            WriteRecognizedCenterLog(stage, exception.ToString());
            throw new RecognizedCenterStageException(stage, FormatException(exception), exception);
        }
        finally { SetBusy(false); }
    }

    private string ShowRecognizedCenterPreview(string procedureName, VmProcedure procedure, ImageSourceModuleTool source)
    {
        try
        {
            var render = ResolveNamedModule<VmModule>(procedureName, "图像渲染1");
            if (render.Root?.ID == procedure.ID && TryShowRecognizedCenterImage(render)) return "";
        }
        catch (Exception exception) { WriteRecognizedCenterLog("查找可选图像渲染1", exception.Message); }
        try
        {
            // 预览也不依赖旧的108编号。只在本流程存在唯一匹配模块时使用它。
            var matches = GetImageStepOptions(procedureName, procedure)
                .Where(option => option.DisplayName.Contains("匹配"))
                .Select(option => option.Module).OfType<VmModule>()
                .Where(module => module.Root?.ID == procedure.ID)
                .GroupBy(module => module.ID).Select(group => group.First()).ToArray();
            if (matches.Length == 1 && TryShowRecognizedCenterImage(matches[0])) return "";
        }
        catch (Exception exception) { WriteRecognizedCenterLog("显示本流程匹配预览", exception.ToString()); }
        if (TryShowRecognizedCenterImage(source)) return "；显示本次原图";
        return "；图像预览失败，请查看视觉日志";
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
