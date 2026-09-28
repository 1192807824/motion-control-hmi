using System.Globalization;
using System.IO;
using IMVSHPFeatureMatchModuCs;
using IMVSCalibTransformModuCs;
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
        if (!_solutionLoaded) LoadFixedSolution();
        if (!string.Equals(_activeCalibrationProcedureName, _calibrationProcedureName, StringComparison.Ordinal))
            throw new InvalidOperationException("识别中心点移动仅允许在第一套视觉使用。");
        var procedure = GetRequiredProcedure(procedureName);
        var match = ResolveModuleById<IMVSHPFeatureMatchModuTool>(procedure, procedureName, 108);
        VmModule render = match;
        try { render = ResolveNamedModule<VmModule>(procedureName, "图像渲染1"); }
        catch (InvalidOperationException) { /* 未配置独立渲染模块时，直接显示108的匹配叠加结果。 */ }
        StopAllContinuousExecutionNoThrow();
        PrepareLiveRendererForCameraAcquisition();
        ApplySingleRenderLayout(showLive: true);
        LiveRenderTitleText.Text = "108 高精度匹配 · 识别中心点";
        _clickCenterPixelReady = false;
        SetBusy(true);
        try
        {
            procedure.Run(true);
            // 先直接刷新108，图像尺寸必须来自本次匹配使用的画面。
            BindInspectionResultModule(match);
            VisionRenderControl.ClearDisplayView();
            VisionRenderControl.UpdateVMResultShow();
            ImagePlaceholder.Visibility = System.Windows.Visibility.Collapsed;
            EnsureProcedureRunSucceeded(procedure, procedureName);
            var result = match.ModuResult;
            if (result is null || result.ModuStatus != 1 || result.MatchNum != 1 || result.MatchRect is null || result.MatchRect.Count != 1)
                throw new InvalidOperationException("108高精度匹配必须返回唯一一个有效匹配框；未识别或多目标时禁止移动。");
            // 红框列是 MatchRect.CenterPoint，不能使用 MatchPoint（模板参考点）。
            var pixel = result.MatchRect[0].CenterPoint;
            var inputImage = VisionRenderControl.ImageSource;
            if (inputImage is null || inputImage.Width < 2 || inputImage.Height < 2 ||
                float.IsNaN(pixel.X) || float.IsInfinity(pixel.X) || float.IsNaN(pixel.Y) || float.IsInfinity(pixel.Y) ||
                pixel.X < 0 || pixel.Y < 0 || pixel.X > inputImage.Width - 1 || pixel.Y > inputImage.Height - 1)
                throw new InvalidDataException("108匹配框中心或本次图像尺寸无效。");
            var centerX = (float)((inputImage.Width - 1) / 2d);
            var centerY = (float)((inputImage.Height - 1) / 2d);
            var transform = _standaloneTransformModule ??= new IMVSCalibTransformModuTool();
            transform.ModuParams.LoadCalibPath = calibrationPath;
            transform.ModuParams.InputPoint = [pixel, new VM.PlatformSDKCS.PointF { X = centerX, Y = centerY }];
            transform.Run();
            var converted = transform.ModuResult;
            if (converted is null || converted.ModuStatus != 1 || converted.TransPoint is null || converted.TransPoint.Count != 2)
                throw new InvalidOperationException("标定转换未返回匹配框中心和本次图像中心的机械坐标。");
            var point = converted.TransPoint[0];
            var center = converted.TransPoint[1];
            var values = new[] { pixel.X, pixel.Y, point.X, point.Y, centerX, centerY, center.X, center.Y };
            if (values.Any(value => float.IsNaN(value) || float.IsInfinity(value)))
                throw new InvalidDataException("识别中心点转换结果无效。");
            // 优先显示图像渲染1；未配置时保留108自身的匹配叠加结果。
            if (!ReferenceEquals(render, match))
            {
                BindInspectionResultModule(render);
                RefreshInspectionDisplayNoThrow();
            }
            SetStatus($"{procedureName}：匹配框中心 X={pixel.X:F3}，Y={pixel.Y:F3}", StatusKind.Success);
            return string.Join("\t", values.Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
        }
        finally { SetBusy(false); }
    }
}
