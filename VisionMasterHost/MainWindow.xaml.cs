using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IMVSBlobFindModuCs;
using IMVSCalibTransformModuCs;
using IMVSNPointCalibModuCs;
using Microsoft.Win32;
using VM.Core;
using VM.PlatformSDKCS;
using VMControls.Interface;

namespace VisionMasterHost;

public partial class MainWindow : Window
{
    private readonly VisionCalibrationSettings _settings = VisionCalibrationSettings.Load();
    private readonly string? _commandPipeName;
    private readonly string? _eventPipeName;
    private readonly CancellationTokenSource _commandPipeCancellation = new();
    private readonly object _commandPipeSync = new();
    private VmProcedure? _previewProcedure;
    private VmProcedure? _calibrationProcedure;
    private VmModule? _displayedModule;
    private VmModule? _crosshairModule;
    private NamedPipeServerStream? _activeCommandPipe;
    private Task? _commandPipeTask;
    private VisionCalibrationSession? _calibrationSession;
    private bool _solutionLoaded;
    private bool _closed;
    private bool _sdkAvailable = true;
    private bool _busy;
    private bool _clickMoveEnabled;
    private bool _clickTransformBusy;
    private bool _clickCenterPixelReady;
    private float _clickCenterPixelX;
    private float _clickCenterPixelY;
    private int _clickImagePixelWidth;
    private int _clickImagePixelHeight;
    private string _clickCalibrationPath = "";

    public MainWindow(
        bool embedded,
        string? commandPipeName = null,
        string? eventPipeName = null)
    {
        InitializeComponent();
        _commandPipeName = string.IsNullOrWhiteSpace(commandPipeName) ? null : commandPipeName;
        _eventPipeName = string.IsNullOrWhiteSpace(eventPipeName) ? null : eventPipeName;
        if (embedded)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
        }
    }

    public void ReportSdkInitializationFailure(Exception exception)
    {
        _sdkAvailable = false;
        var vmException = FindVmException(exception);
        var message = vmException?.errorCode == unchecked((int)0xE0000700)
            ? "未检测到 VisionMaster 加密狗或授权状态异常（0xE0000700）。"
            : FormatException(exception);
        SdkErrorTextBlock.Text = message;
        SdkErrorPanel.Visibility = Visibility.Visible;
        VisionRenderControl.IsEnabled = false;
        CalibrationRenderControl.IsEnabled = false;
        CenterCrosshair.Visibility = Visibility.Collapsed;
        ImagePlaceholder.Visibility = Visibility.Collapsed;
        CalibrationImagePlaceholder.Visibility = Visibility.Collapsed;
        UpdateCommandState();
        SetStatus(message, StatusKind.Error);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        StartCommandPipeServer();
        if (!_sdkAvailable)
        {
            return;
        }

        try
        {
            VisionRenderControl.SetRenderToolbarVisible(true);
            CalibrationRenderControl.SetRenderToolbarVisible(true);
        }
        catch (Exception exception)
        {
            ReportSdkInitializationFailure(exception);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.SolutionPath))
        {
            return;
        }

        SolutionPathTextBox.Text = _settings.SolutionPath;
        if (!File.Exists(_settings.SolutionPath))
        {
            SetStatus("上次使用的方案文件不存在，请重新选择。", StatusKind.Error);
            return;
        }

        await LoadSolutionAsync(_settings.SolutionPath, autoRestore: true);
    }

    private void StartCommandPipeServer()
    {
        if (_commandPipeTask is not null || string.IsNullOrWhiteSpace(_commandPipeName))
        {
            return;
        }

        _commandPipeTask = RunCommandPipeServerAsync(_commandPipeCancellation.Token);
    }

    private async Task RunCommandPipeServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _commandPipeName!,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                lock (_commandPipeSync)
                {
                    _activeCommandPipe = pipe;
                }

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using (var reader = new StreamReader(
                           pipe,
                           new UTF8Encoding(false),
                           false,
                           1024,
                           leaveOpen: true))
                using (var writer = new StreamWriter(
                           pipe,
                           new UTF8Encoding(false),
                           1024,
                           leaveOpen: true)
                       {
                           AutoFlush = true
                       })
                {
                    var command = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (command is null)
                    {
                        continue;
                    }

                    var response = await Dispatcher.InvokeAsync(
                        () => ExecuteCalibrationCommandSafely(command));
                    await writer.WriteLineAsync(response).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // The client may disconnect between a command and its reply; accept the next command.
            }
            finally
            {
                lock (_commandPipeSync)
                {
                    if (ReferenceEquals(_activeCommandPipe, pipe))
                    {
                        _activeCommandPipe = null;
                    }
                }

                pipe?.Dispose();
            }
        }
    }

    private string ExecuteCalibrationCommandSafely(string command)
    {
        try
        {
            return EncodePipeResponse(success: true, ExecuteCalibrationCommand(command));
        }
        catch (Exception exception)
        {
            return EncodePipeResponse(success: false, FormatException(exception));
        }
    }

    private string ExecuteCalibrationCommand(string command)
    {
        var parts = command.Split('\t');
        return parts[0] switch
        {
            "SET_CENTER" => SetCalibrationCenter(parts),
            "PREPARE" => PrepareNinePointCalibration(parts),
            "CAPTURE" => CaptureNinePointCalibration(parts),
            "COMPLETE" => CompleteNinePointCalibration(),
            "ABORT" => AbortNinePointCalibration(),
            "SET_CLICK_MODE" => SetClickMoveMode(parts),
            "TRANSFORM_PIXEL" => TransformPixel(parts),
            "RUN_RECTANGLE_BLOB" => RunRectangleBlobInspection(parts),
            _ => throw new InvalidOperationException($"不支持的视觉标定命令：{parts[0]}")
        };
    }

    /// <summary>
    /// 单次运行当前实时流程。流程中的图像采集模块先触发拍照，随后由 Blob 分析模块
    /// 输出候选目标；这里取矩形度最高、面积最大的两个有效 Blob，并返回其像素质心。
    /// </summary>
    private string RunRectangleBlobInspection(IReadOnlyList<string> parts)
    {
        if (parts.Count != 1)
        {
            throw new InvalidDataException("Blob检测命令不需要附加参数。");
        }

        if (_busy || _calibrationSession is not null)
        {
            throw new InvalidOperationException("视觉标定正在执行，暂不允许拍照检测。");
        }

        if (!_solutionLoaded ||
            _previewProcedure is null ||
            PreviewProcedureComboBox.SelectedItem is not string procedureName)
        {
            throw new InvalidOperationException("请先加载视觉方案并选择实时流程。");
        }

        var blobModule = ResolveBlobFindModule(procedureName);
        var previewWasRunning = _previewProcedure.ContinuousRunEnable;
        if (previewWasRunning)
        {
            // 连续预览必须先暂停，才能保证下面读取到的是本次拍照对应的结果。
            _previewProcedure.ContinuousRunEnable = false;
        }

        try
        {
            _previewProcedure.Run(true);
            VisionRenderControl.UpdateVMResultShow();

            var result = blobModule.ModuResult;
            if (result.ModuStatus != 1)
            {
                throw new InvalidOperationException("Blob分析模块返回NG，请检查相机取像、阈值和检测区域。");
            }

            var points = result.CentroidPoint;
            if (points is null || points.Count < 2 || result.BlobNum < 2)
            {
                throw new InvalidOperationException(
                    $"Blob分析只找到 {Math.Max(0, result.BlobNum)} 个目标，需要找到两个矩形。");
            }

            var candidates = new List<RectangleBlobCandidate>();
            var candidateCount = Math.Min(result.BlobNum, points.Count);
            for (var index = 0; index < candidateCount; index++)
            {
                var point = points[index];
                if (float.IsNaN(point.X) || float.IsInfinity(point.X) ||
                    float.IsNaN(point.Y) || float.IsInfinity(point.Y))
                {
                    continue;
                }

                var rectangularity = result.Rectangularity is not null && index < result.Rectangularity.Count
                    ? result.Rectangularity[index]
                    : 0f;
                var area = result.Area is not null && index < result.Area.Count
                    ? result.Area[index]
                    : 0f;
                var blobRect = result.BlobRect is not null && index < result.BlobRect.Count
                    ? result.BlobRect[index]
                    : null;
                candidates.Add(new RectangleBlobCandidate(
                    point.X,
                    point.Y,
                    rectangularity,
                    area,
                    blobRect?.RectPoint.X ?? (int)Math.Round(point.X),
                    blobRect?.RectPoint.Y ?? (int)Math.Round(point.Y),
                    blobRect?.RectWidth ?? 1,
                    blobRect?.RectHeight ?? 1));
            }

            if (candidates.Count < 2)
            {
                throw new InvalidOperationException("Blob分析未返回两个有效矩形质心。");
            }

            // 先选出最像矩形的两个目标，再按 X、Y 排序，保证矩形1/2编号稳定。
            var selected = candidates
                .OrderByDescending(candidate => candidate.Rectangularity)
                .ThenByDescending(candidate => candidate.Area)
                .Take(2)
                .OrderBy(candidate => candidate.PixelX)
                .ThenBy(candidate => candidate.PixelY)
                .ToArray();

            // 保存本次相机原图。主程序会在主页加载它，并使用下面返回的 Blob 框和质心
            // 叠加检测标记；这样无需把同一个 VisionMaster 窗口嵌入两个页面。
            var inspectionImage = SaveInspectionImage();
            SetStatus(
                $"拍照及Blob分析完成：矩形1({selected[0].PixelX:0.###}, {selected[0].PixelY:0.###})，" +
                $"矩形2({selected[1].PixelX:0.###}, {selected[1].PixelY:0.###})",
                StatusKind.Success);
            return string.Join(
                "\t",
                selected[0].PixelX.ToString("R", CultureInfo.InvariantCulture),
                selected[0].PixelY.ToString("R", CultureInfo.InvariantCulture),
                selected[0].Left.ToString(CultureInfo.InvariantCulture),
                selected[0].Top.ToString(CultureInfo.InvariantCulture),
                selected[0].Width.ToString(CultureInfo.InvariantCulture),
                selected[0].Height.ToString(CultureInfo.InvariantCulture),
                selected[1].PixelX.ToString("R", CultureInfo.InvariantCulture),
                selected[1].PixelY.ToString("R", CultureInfo.InvariantCulture),
                selected[1].Left.ToString(CultureInfo.InvariantCulture),
                selected[1].Top.ToString(CultureInfo.InvariantCulture),
                selected[1].Width.ToString(CultureInfo.InvariantCulture),
                selected[1].Height.ToString(CultureInfo.InvariantCulture),
                inspectionImage.PixelWidth.ToString(CultureInfo.InvariantCulture),
                inspectionImage.PixelHeight.ToString(CultureInfo.InvariantCulture),
                inspectionImage.FilePath);
        }
        finally
        {
            if (previewWasRunning && !_closed)
            {
                _previewProcedure.ContinuousRunEnable = true;
            }

            UpdateCommandState();
        }
    }

    /// <summary>
    /// 将当前结果对应的原始相机图保存到共享临时目录，并读取 BMP 像素尺寸。
    /// 文件由主程序成功加载到内存后删除。
    /// </summary>
    private InspectionImageFile SaveInspectionImage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ControlHubVision");
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, $"blob-{Guid.NewGuid():N}.bmp");
        try
        {
            VisionRenderControl.SaveOriginalImage(imagePath);
            using var stream = new FileStream(
                imagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 26 || reader.ReadUInt16() != 0x4D42)
            {
                throw new InvalidDataException("VisionMaster 保存的Blob检测图不是有效 BMP 图像。");
            }

            stream.Position = 18;
            var pixelWidth = Math.Abs((long)reader.ReadInt32());
            var pixelHeight = Math.Abs((long)reader.ReadInt32());
            if (pixelWidth <= 0 || pixelHeight <= 0 ||
                pixelWidth > int.MaxValue || pixelHeight > int.MaxValue)
            {
                throw new InvalidDataException("VisionMaster 保存的Blob检测图尺寸无效。");
            }

            return new InspectionImageFile(imagePath, (int)pixelWidth, (int)pixelHeight);
        }
        catch
        {
            try
            {
                File.Delete(imagePath);
            }
            catch
            {
            }

            throw;
        }
    }

    private string TransformPixel(IReadOnlyList<string> parts)
    {
        if (parts.Count != 4)
        {
            throw new InvalidDataException("像素坐标转换参数不正确。");
        }

        var pixelX = ParseFiniteDouble(parts[1], "像素X");
        var pixelY = ParseFiniteDouble(parts[2], "像素Y");
        if (pixelX < 0 || pixelY < 0)
        {
            throw new InvalidDataException("像素坐标不能小于0。");
        }

        string calibrationPath;
        try
        {
            calibrationPath = Encoding.UTF8.GetString(Convert.FromBase64String(parts[3]));
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("标定文件路径格式不正确。", exception);
        }

        if (_busy || _calibrationSession is not null)
        {
            throw new InvalidOperationException("视觉标定正在执行，暂不允许像素坐标转换。");
        }

        if (!_solutionLoaded || CalibrationProcedureComboBox.SelectedItem is not string procedureName)
        {
            throw new InvalidOperationException("请先加载视觉方案并选择标定流程。");
        }

        var fullPath = Path.GetFullPath(calibrationPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("标定文件不存在。", fullPath);
        }

        var previewWasRunning = _previewProcedure?.ContinuousRunEnable == true;
        if (previewWasRunning)
        {
            _previewProcedure!.ContinuousRunEnable = false;
        }

        try
        {
            var (centerPixelX, centerPixelY) = GetClickCenterPixel();
            if (pixelX >= _clickImagePixelWidth || pixelY >= _clickImagePixelHeight)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pixelX),
                    $"像素坐标超出当前图像范围：宽{_clickImagePixelWidth}，高{_clickImagePixelHeight}。");
            }

            var transformModule = ResolveCalibrationTransformModule(procedureName);
            transformModule.ModuParams.LoadCalibPath = fullPath;
            transformModule.ModuParams.InputPoint =
            [
                new VM.PlatformSDKCS.PointF { X = (float)pixelX, Y = (float)pixelY },
                new VM.PlatformSDKCS.PointF { X = centerPixelX, Y = centerPixelY }
            ];
            transformModule.Run();
            var result = transformModule.ModuResult;
            if (result.ModuStatus != 1 || result.TransPoint is null || result.TransPoint.Count < 2)
            {
                throw new InvalidOperationException("标定转换模块未返回像素点和图像中心的机械坐标。");
            }

            var transformedPoint = result.TransPoint[0];
            var transformedCenter = result.TransPoint[1];
            SetStatus(
                $"像素({pixelX}, {pixelY})已按{Path.GetFileName(fullPath)}完成标定转换。",
                StatusKind.Success);
            return string.Join(
                "\t",
                transformedPoint.X.ToString("R", CultureInfo.InvariantCulture),
                transformedPoint.Y.ToString("R", CultureInfo.InvariantCulture),
                centerPixelX.ToString("R", CultureInfo.InvariantCulture),
                centerPixelY.ToString("R", CultureInfo.InvariantCulture),
                transformedCenter.X.ToString("R", CultureInfo.InvariantCulture),
                transformedCenter.Y.ToString("R", CultureInfo.InvariantCulture));
        }
        finally
        {
            if (previewWasRunning && _previewProcedure is not null)
            {
                _previewProcedure.ContinuousRunEnable = true;
            }

            UpdateCommandState();
        }
    }

    private string SetClickMoveMode(IReadOnlyList<string> parts)
    {
        if (parts.Count != 3 || (parts[1] != "0" && parts[1] != "1"))
        {
            throw new InvalidDataException("点击移动配置参数不正确。");
        }

        var enabled = parts[1] == "1";
        string calibrationPath;
        try
        {
            calibrationPath = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("标定文件路径格式不正确。", exception);
        }

        if (!enabled)
        {
            _clickMoveEnabled = false;
            DetachCrosshairModule();
            _clickCenterPixelReady = false;
            _clickCalibrationPath = calibrationPath;
            VisionRenderControl.SetRenderToolbarVisible(true);
            CenterCrosshair.Visibility = Visibility.Visible;
            UpdateCommandState();
            SetStatus("点击视觉移动已关闭。", StatusKind.Ready);
            return "点击视觉移动已关闭。";
        }

        if (_eventPipeName is null)
        {
            throw new InvalidOperationException("当前视觉窗口没有运动回传通道。");
        }

        if (!_solutionLoaded || CalibrationProcedureComboBox.SelectedItem is not string procedureName)
        {
            throw new InvalidOperationException("请先加载视觉方案并选择标定流程。");
        }

        var fullPath = Path.GetFullPath(calibrationPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("标定文件不存在。", fullPath);
        }

        var transformModule = ResolveCalibrationTransformModule(procedureName);
        transformModule.ModuParams.LoadCalibPath = fullPath;
        _clickCalibrationPath = fullPath;
        _clickCenterPixelReady = false;
        _ = GetClickCenterPixel();
        VisionRenderControl.SetRenderToolbarVisible(true);
        _clickMoveEnabled = true;
        CenterCrosshair.Visibility = Visibility.Collapsed;
        AttachCrosshairModule();
        DrawImageCenterCrosshair();
        UpdateCommandState();
        SetStatus($"点击移动已启用：{Path.GetFileName(fullPath)}", StatusKind.Success);
        return $"已引用标定文件：{fullPath}。点击图像后将把该点移到绿色十字中心。";
    }

    private string SetCalibrationCenter(IReadOnlyList<string> parts)
    {
        if (parts.Count != 3)
        {
            throw new InvalidDataException("写入标定中心的参数数量不正确。");
        }

        if (!_solutionLoaded ||
            _calibrationProcedure is null ||
            CalibrationProcedureComboBox.SelectedItem is not string procedureName)
        {
            throw new InvalidOperationException("请先加载方案并选择标定流程。");
        }

        var centerX = ParseFiniteDouble(parts[1], "基准点X");
        var centerY = ParseFiniteDouble(parts[2], "基准点Y");
        var previewWasRunning = _previewProcedure?.ContinuousRunEnable == true;
        if (previewWasRunning)
        {
            _previewProcedure!.ContinuousRunEnable = false;
        }

        try
        {
            var nPointModule = ResolveNPointCalibrationModule(procedureName);
            var parameters = nPointModule.ModuParams;
            parameters.BasePointX = centerX;
            parameters.BasePointY = centerY;
            if (Math.Abs(parameters.BasePointX - centerX) > 0.000001 ||
                Math.Abs(parameters.BasePointY - centerY) > 0.000001)
            {
                throw new InvalidOperationException("N点标定模块未接受新的基准点参数。");
            }

            SetStatus(
                $"已写入并读回 {procedureName}.N点标定1：基准点 X={parameters.BasePointX:0.####}，" +
                $"Y={parameters.BasePointY:0.####}",
                StatusKind.Success);
            return
                $"已写入并读回 {procedureName}.N点标定1：基准点 X={parameters.BasePointX:0.####}，" +
                $"Y={parameters.BasePointY:0.####}。" +
                "独立打开的 VisionMaster 编辑器不会同步刷新宿主进程内存参数。";
        }
        finally
        {
            if (previewWasRunning)
            {
                _previewProcedure!.ContinuousRunEnable = true;
            }

            UpdateCommandState();
        }
    }

    private string PrepareNinePointCalibration(IReadOnlyList<string> parts)
    {
        if (parts.Count != 7)
        {
            throw new InvalidDataException("准备九点标定的参数数量不正确。");
        }

        if (!_solutionLoaded ||
            _calibrationProcedure is null ||
            CalibrationProcedureComboBox.SelectedItem is not string procedureName)
        {
            throw new InvalidOperationException("请先在视觉组件中加载标定方案并选择标定流程。");
        }

        var centerX = ParseFiniteDouble(parts[1], "基准点X");
        var centerY = ParseFiniteDouble(parts[2], "基准点Y");
        var offsetX = ParsePositiveDouble(parts[3], "间距X");
        var offsetY = ParsePositiveDouble(parts[4], "间距Y");
        var xFirst = parts[5] switch
        {
            "X" => true,
            "Y" => false,
            _ => throw new InvalidDataException("移动优先参数只能是 X 或 Y。")
        };
        string calibrationPath;
        try
        {
            calibrationPath = Path.GetFullPath(
                Encoding.UTF8.GetString(Convert.FromBase64String(parts[6])));
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("标定文件路径格式不正确。", exception);
        }

        if (!string.Equals(Path.GetExtension(calibrationPath), ".xml", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("标定文件必须使用 .xml 扩展名。");
        }

        var calibrationDirectory = Path.GetDirectoryName(calibrationPath)
            ?? throw new InvalidDataException("无法确定标定文件的保存目录。");

        var nPointModule = ResolveNPointCalibrationModule(procedureName);
        try
        {
            var parameters = nPointModule.ModuParams;
            parameters.CalibPointGet = NPointCalibParam.CalibPointGetEnum.TriggerAcquisition;
            parameters.CalibPointTotalNum = 9;
            parameters.RotPointTotalNum = 0;
            parameters.TeachEnable = false;
            parameters.BasePointX = centerX;
            parameters.BasePointY = centerY;
            parameters.MoveAlignX = offsetX;
            parameters.MoveAlignY = offsetY;
            parameters.MoveFirstType = xFirst
                ? NPointCalibParam.MoveFirstTypeEnum.XFirst
                : NPointCalibParam.MoveFirstTypeEnum.YFirst;
            parameters.ChangeDirectionMoveTime = 3;
            parameters.UseRelativeCoordinates = false;

            Directory.CreateDirectory(calibrationDirectory);
            parameters.CalibPathName = calibrationPath;
            parameters.RefreshFileEnable = true;
            parameters.DoClearPoint();

            _calibrationSession = new VisionCalibrationSession(nPointModule, calibrationPath);
            ClearCalibrationRenderer();
            BindCalibrationModule(nPointModule);
            SetBusy(true);
            SetStatus(
                $"九点标定已准备：基准({centerX:0.####}, {centerY:0.####})，" +
                $"偏移({offsetX:0.####}, {offsetY:0.####})，{(xFirst ? "X" : "Y")}优先",
                StatusKind.Busy);
            return $"已准备九点标定：{calibrationPath}";
        }
        catch
        {
            _calibrationSession = null;
            ClearCalibrationRenderer();
            SetBusy(false);
            throw;
        }
    }

    private string CaptureNinePointCalibration(IReadOnlyList<string> parts)
    {
        if (parts.Count != 2 ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pointNumber) ||
            pointNumber is < 1 or > 9)
        {
            throw new InvalidDataException("采集点号必须在 1 到 9 之间。");
        }

        var session = _calibrationSession
            ?? throw new InvalidOperationException("尚未准备九点标定参数。");
        if (pointNumber != session.NextPointNumber)
        {
            throw new InvalidOperationException(
                $"采集顺序错误：当前应采集第 {session.NextPointNumber} 点，不是第 {pointNumber} 点。");
        }

        if (_calibrationProcedure is null)
        {
            throw new InvalidOperationException("当前 VisionMaster 流程已失效。");
        }

        var stopwatch = Stopwatch.StartNew();
        RunCalibrationProcedureOnce();
        stopwatch.Stop();
        CalibrationRenderControl.UpdateVMResultShow();

        var result = session.Module.ModuResult;
        if (result.ModuStatus != 1)
        {
            throw new InvalidOperationException(
                $"第 {pointNumber} 点 N点标定模块返回 NG，请检查相机取像和圆查找结果。");
        }

        session.NextPointNumber++;
        SetStatus(
            $"第 {pointNumber}/9 点已采集，流程用时 {stopwatch.Elapsed.TotalMilliseconds:0.0} ms",
            StatusKind.Busy);
        return $"第 {pointNumber}/9 点 VisionMaster 流程执行完成。";
    }

    private string CompleteNinePointCalibration()
    {
        var session = _calibrationSession
            ?? throw new InvalidOperationException("尚未准备九点标定参数。");
        if (session.NextPointNumber != 10)
        {
            throw new InvalidOperationException(
                $"九点数据尚未采集完成，当前已完成 {session.NextPointNumber - 1}/9 点。");
        }

        var result = session.Module.ModuResult;
        if (result.ModuStatus != 1 || result.CalibStatus != 1)
        {
            throw new InvalidOperationException("九点已采集，但 VisionMaster 未生成有效标定结果。");
        }

        if (result.CalibErrStatus != 0)
        {
            throw new InvalidOperationException("标定矩阵已生成，但标定误差评估未通过。");
        }

        session.Module.ModuParams.DoSaveFile(session.CalibrationPath);
        BindCalibrationModule(session.Module);
        _calibrationSession = null;
        var message =
            $"九点标定成功，像素精度 {result.PixelPrecision:0.######}，" +
            $"标定文件：{session.CalibrationPath}；N点标定1结果已显示在右侧";
        SetBusy(false);
        SetStatus(message, StatusKind.Success);
        return message;
    }

    private string AbortNinePointCalibration()
    {
        if (_calibrationSession is { } session)
        {
            session.Module.ModuParams.DoClearPoint();
            _calibrationSession = null;
        }

        ClearCalibrationRenderer();
        var message = "九点标定已取消，本次未完成的标定点已清空。";
        SetBusy(false);
        SetStatus(message, StatusKind.Ready);
        return message;
    }

    private static double ParseFiniteDouble(string value, string name)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ||
            double.IsNaN(result) ||
            double.IsInfinity(result))
        {
            throw new InvalidDataException($"{name}必须是有效数值。");
        }

        return result;
    }

    private static double ParsePositiveDouble(string value, string name)
    {
        var result = ParseFiniteDouble(value, name);
        if (result <= 0)
        {
            throw new InvalidDataException($"{name}必须大于 0。");
        }

        return result;
    }

    private static string EncodePipeResponse(bool success, string message)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(message ?? ""));
        return $"{(success ? "OK" : "ERR")}\t{encoded}";
    }

    private static IMVSNPointCalibModuTool ResolveNPointCalibrationModule(string procedureName)
    {
        var procedure = VmSolution.Instance[procedureName] as VmProcedure
            ?? throw new InvalidOperationException($"方案中未找到流程“{procedureName}”。");
        var moduleList = procedure.GetProcedureModuleList();
        for (var index = 0; index < moduleList.nNum; index++)
        {
            var info = moduleList.astModuleInfo[index];
            var displayName = info.strDisplayName?.Trim() ?? "";
            var moduleName = info.strModuleName?.Trim() ?? "";
            if (!string.Equals(moduleName, "IMVSNPointCalibModu", StringComparison.Ordinal) &&
                !displayName.Contains("N点标定"))
            {
                continue;
            }

            foreach (var candidate in new[]
                     {
                         $"{procedureName}.{displayName}",
                         $"{procedureName}.{moduleName}",
                         displayName,
                         moduleName
                     }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
            {
                try
                {
                    if (VmSolution.Instance[candidate] is IMVSNPointCalibModuTool module)
                    {
                        return module;
                    }
                }
                catch
                {
                }
            }
        }

        throw new InvalidOperationException("当前流程中未找到“N点标定”模块。");
    }

    private static IMVSCalibTransformModuTool ResolveCalibrationTransformModule(string procedureName)
    {
        var procedure = VmSolution.Instance[procedureName] as VmProcedure
            ?? throw new InvalidOperationException($"方案中未找到流程“{procedureName}”。");
        var moduleList = procedure.GetProcedureModuleList();
        for (var index = 0; index < moduleList.nNum; index++)
        {
            var info = moduleList.astModuleInfo[index];
            var displayName = info.strDisplayName?.Trim() ?? "";
            var moduleName = info.strModuleName?.Trim() ?? "";
            if (!string.Equals(moduleName, "IMVSCalibTransformModu", StringComparison.Ordinal) &&
                !displayName.Contains("标定转换"))
            {
                continue;
            }

            foreach (var candidate in new[]
                     {
                         $"{procedureName}.{displayName}",
                         $"{procedureName}.{moduleName}",
                         displayName,
                         moduleName
                     }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
            {
                try
                {
                    if (VmSolution.Instance[candidate] is IMVSCalibTransformModuTool module)
                    {
                        return module;
                    }
                }
                catch
                {
                }
            }
        }

        throw new InvalidOperationException("当前标定流程中未找到“标定转换”模块。");
    }

    /// <summary>
    /// 在当前实时流程中定位海康 Blob分析模块。显示名和模块类型名都参与匹配，
    /// 以兼容用户给模块改名以及不同 VisionMaster 方案的命名方式。
    /// </summary>
    private static IMVSBlobFindModuTool ResolveBlobFindModule(string procedureName)
    {
        var procedure = VmSolution.Instance[procedureName] as VmProcedure
            ?? throw new InvalidOperationException($"方案中未找到流程“{procedureName}”。");
        var moduleList = procedure.GetProcedureModuleList();
        for (var index = 0; index < moduleList.nNum; index++)
        {
            var info = moduleList.astModuleInfo[index];
            var displayName = info.strDisplayName?.Trim() ?? "";
            var moduleName = info.strModuleName?.Trim() ?? "";
            if (!string.Equals(moduleName, "IMVSBlobFindModu", StringComparison.Ordinal) &&
                !displayName.Contains("Blob分析"))
            {
                continue;
            }

            foreach (var candidate in new[]
                     {
                         $"{procedureName}.{displayName}",
                         $"{procedureName}.{moduleName}",
                         displayName,
                         moduleName
                     }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
            {
                try
                {
                    if (VmSolution.Instance[candidate] is IMVSBlobFindModuTool module)
                    {
                        return module;
                    }
                }
                catch
                {
                    // 部分 SDK 模块类型名不是可查询路径，继续尝试下一个候选名。
                }
            }
        }

        throw new InvalidOperationException(
            "当前实时流程中未找到“Blob分析”模块，请先在 VisionMaster 流程中添加并配置图像采集和Blob分析。");
    }

    private async void VisionRenderControl_OnMouseLeftButtonDownPixelChanged(int pixelX, int pixelY)
    {
        if (!_clickMoveEnabled || _clickTransformBusy || _closed)
        {
            return;
        }

        _clickTransformBusy = true;
        try
        {
            if (_busy || _calibrationSession is not null)
            {
                throw new InvalidOperationException("视觉标定正在执行，暂不允许点击移动。");
            }

            if (!_solutionLoaded || CalibrationProcedureComboBox.SelectedItem is not string procedureName)
            {
                throw new InvalidOperationException("请先加载视觉方案并选择标定流程。");
            }

            if (string.IsNullOrWhiteSpace(_clickCalibrationPath) || !File.Exists(_clickCalibrationPath))
            {
                throw new FileNotFoundException("当前引用的标定文件不存在。", _clickCalibrationPath);
            }

            var previewWasRunning = _previewProcedure?.ContinuousRunEnable == true;
            if (previewWasRunning)
            {
                _previewProcedure!.ContinuousRunEnable = false;
            }

            VM.PlatformSDKCS.PointF transformedPoint;
            VM.PlatformSDKCS.PointF transformedCenter;
            float centerPixelX;
            float centerPixelY;
            try
            {
                (centerPixelX, centerPixelY) = GetClickCenterPixel();
                var transformModule = ResolveCalibrationTransformModule(procedureName);
                transformModule.ModuParams.LoadCalibPath = _clickCalibrationPath;
                transformModule.ModuParams.InputPoint =
                [
                    new VM.PlatformSDKCS.PointF
                    {
                        X = pixelX,
                        Y = pixelY
                    },
                    new VM.PlatformSDKCS.PointF
                    {
                        X = centerPixelX,
                        Y = centerPixelY
                    }
                ];
                transformModule.Run();
                var result = transformModule.ModuResult;
                if (result.ModuStatus != 1 || result.TransPoint is null || result.TransPoint.Count < 2)
                {
                    throw new InvalidOperationException("标定转换模块未返回点击点和中心点的机械坐标。");
                }

                transformedPoint = result.TransPoint[0];
                transformedCenter = result.TransPoint[1];
            }
            finally
            {
                if (previewWasRunning && _previewProcedure is not null)
                {
                    _previewProcedure.ContinuousRunEnable = true;
                }

                UpdateCommandState();
            }

            SetStatus(
                $"点击({pixelX}, {pixelY}) → 十字中心({centerPixelX:0.##}, {centerPixelY:0.##})",
                StatusKind.Success);
            await SendHostEventAsync(string.Join(
                "\t",
                "CLICK_TARGET",
                pixelX.ToString(CultureInfo.InvariantCulture),
                pixelY.ToString(CultureInfo.InvariantCulture),
                transformedPoint.X.ToString("R", CultureInfo.InvariantCulture),
                transformedPoint.Y.ToString("R", CultureInfo.InvariantCulture),
                centerPixelX.ToString("R", CultureInfo.InvariantCulture),
                centerPixelY.ToString("R", CultureInfo.InvariantCulture),
                transformedCenter.X.ToString("R", CultureInfo.InvariantCulture),
                transformedCenter.Y.ToString("R", CultureInfo.InvariantCulture)));
        }
        catch (Exception exception)
        {
            var message = FormatException(exception);
            SetStatus($"点击坐标转换失败：{message}", StatusKind.Error);
            try
            {
                await SendHostEventAsync(
                    $"CLICK_ERROR\t{Convert.ToBase64String(Encoding.UTF8.GetBytes(message))}");
            }
            catch
            {
            }
        }
        finally
        {
            _clickTransformBusy = false;
        }
    }

    private (float X, float Y) GetClickCenterPixel()
    {
        if (_clickCenterPixelReady)
        {
            return (_clickCenterPixelX, _clickCenterPixelY);
        }

        var temporaryImagePath = Path.Combine(
            Path.GetTempPath(),
            $"ControlHub-click-center-{Guid.NewGuid():N}.bmp");
        try
        {
            VisionRenderControl.SaveOriginalImage(temporaryImagePath);
            using var stream = new FileStream(
                temporaryImagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 26 || reader.ReadUInt16() != 0x4D42)
            {
                throw new InvalidDataException("VisionMaster 保存的原图不是有效 BMP 图像。");
            }

            stream.Position = 18;
            var pixelWidth = Math.Abs((long)reader.ReadInt32());
            var pixelHeight = Math.Abs((long)reader.ReadInt32());
            if (pixelWidth <= 0 || pixelHeight <= 0)
            {
                throw new InvalidDataException("当前图像尺寸无效。");
            }

            _clickCenterPixelX = (float)((pixelWidth - 1) / 2d);
            _clickCenterPixelY = (float)((pixelHeight - 1) / 2d);
            _clickImagePixelWidth = checked((int)pixelWidth);
            _clickImagePixelHeight = checked((int)pixelHeight);
            _clickCenterPixelReady = true;
            return (_clickCenterPixelX, _clickCenterPixelY);
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            throw new InvalidOperationException("无法读取当前图像中心，请确认实时画面已经正常出图。", exception);
        }
        finally
        {
            try
            {
                File.Delete(temporaryImagePath);
            }
            catch
            {
            }
        }
    }

    private void DrawImageCenterCrosshair()
    {
        if (!_clickMoveEnabled || !_clickCenterPixelReady || _closed)
        {
            return;
        }

        const string crosshairColor = "#00E676";
        const double crosshairThickness = 1d;
        var horizontal = new VMControls.WPF.LineEx(
            new Point(0, _clickCenterPixelY),
            new Point(_clickImagePixelWidth - 1, _clickCenterPixelY),
            1d,
            crosshairColor,
            crosshairThickness,
            false,
            "图像中心");
        var vertical = new VMControls.WPF.LineEx(
            new Point(_clickCenterPixelX, 0),
            new Point(_clickCenterPixelX, _clickImagePixelHeight - 1),
            1d,
            crosshairColor,
            crosshairThickness,
            false,
            "图像中心");
        VisionRenderControl.DrawShape(horizontal);
        VisionRenderControl.DrawShape(vertical);
    }

    private void QueueImageCenterCrosshair()
    {
        if (!_clickMoveEnabled || !_clickCenterPixelReady || _closed)
        {
            return;
        }

        const string crosshairColor = "#00E676";
        const double crosshairThickness = 1d;
        var horizontal = new VMControls.WPF.LineEx(
            new Point(0, _clickCenterPixelY),
            new Point(_clickImagePixelWidth - 1, _clickCenterPixelY),
            1d,
            crosshairColor,
            crosshairThickness,
            false,
            "图像中心");
        var vertical = new VMControls.WPF.LineEx(
            new Point(_clickCenterPixelX, 0),
            new Point(_clickCenterPixelX, _clickImagePixelHeight - 1),
            1d,
            crosshairColor,
            crosshairThickness,
            false,
            "图像中心");
        VisionRenderControl.AddShape(horizontal);
        VisionRenderControl.AddShape(vertical);
    }

    private void AttachCrosshairModule()
    {
        DetachCrosshairModule();
        if (_displayedModule is null)
        {
            return;
        }

        _crosshairModule = _displayedModule;
        try
        {
            _crosshairModule.EnableResultCallback();
            _crosshairModule.ModuleResultCallBackArrived += CrosshairModule_ModuleResultCallBackArrived;
        }
        catch
        {
            _crosshairModule = null;
        }
    }

    private void DetachCrosshairModule()
    {
        if (_crosshairModule is null)
        {
            return;
        }

        try
        {
            _crosshairModule.ModuleResultCallBackArrived -= CrosshairModule_ModuleResultCallBackArrived;
        }
        catch
        {
        }

        _crosshairModule = null;
    }

    private void CrosshairModule_ModuleResultCallBackArrived(object? sender, EventArgs e)
    {
        QueueImageCenterCrosshair();
    }

    private async Task SendHostEventAsync(string message)
    {
        if (string.IsNullOrWhiteSpace(_eventPipeName))
        {
            return;
        }

        using var pipe = new NamedPipeClientStream(
            ".",
            _eventPipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2_000).ConfigureAwait(false);
        using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(false),
            1024,
            leaveOpen: true)
        {
            AutoFlush = true
        };
        await writer.WriteLineAsync(message).ConfigureAwait(false);
    }

    private async void ChooseSolution_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 VisionMaster 标定方案",
            Filter = "VisionMaster 方案 (*.sol)|*.sol|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = GetInitialSolutionDirectory()
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        SolutionPathTextBox.Text = dialog.FileName;
        await LoadSolutionAsync(dialog.FileName, autoRestore: false);
    }

    private async Task LoadSolutionAsync(string solutionPath, bool autoRestore)
    {
        if (!File.Exists(solutionPath))
        {
            SetStatus("请选择有效的 VisionMaster .sol 方案文件。", StatusKind.Error);
            return;
        }

        SetBusy(true);
        SetStatus(autoRestore ? "正在恢复上次标定方案…" : "正在加载标定方案…", StatusKind.Busy);
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.Background);

        var previousSolutionClosed = false;
        try
        {
            CloseCurrentSolution();
            previousSolutionClosed = true;
            VmSolution.Load(Path.GetFullPath(solutionPath), "");
            _solutionLoaded = true;

            var procedureNames = GetProcedureNames();
            if (procedureNames.Count == 0)
            {
                throw new InvalidOperationException("方案中没有可用流程。 ");
            }

            _settings.SolutionPath = Path.GetFullPath(solutionPath);
            SolutionPathTextBox.Text = _settings.SolutionPath;
            PreviewProcedureComboBox.ItemsSource = procedureNames;
            CalibrationProcedureComboBox.ItemsSource = procedureNames;
            CalibrationProcedureComboBox.SelectedItem =
                SelectPreferredCalibrationProcedure(procedureNames);
            PreviewProcedureComboBox.SelectedItem =
                SelectPreferredPreviewProcedure(procedureNames);
            SaveSettingsNoThrow();
            UpdateCommandState();
            if (_previewProcedure?.ContinuousRunEnable == true)
            {
                SetStatus(
                    autoRestore
                        ? $"已自动加载：{Path.GetFileName(solutionPath)}；实时与标定流程已分离"
                        : $"方案已加载：{Path.GetFileName(solutionPath)}；实时与标定流程已分离",
                    StatusKind.Success);
            }
        }
        catch (Exception exception)
        {
            if (previousSolutionClosed)
            {
                CloseCurrentSolutionNoThrow();
            }

            SetStatus(
                $"{(autoRestore ? "自动加载" : "加载")}失败：{FormatException(exception)}",
                StatusKind.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void PreviewProcedureComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_solutionLoaded || PreviewProcedureComboBox.SelectedItem is not string procedureName)
        {
            return;
        }

        try
        {
            StopPreviewProcedureNoThrow();
            _previewProcedure = VmSolution.Instance[procedureName] as VmProcedure
                ?? throw new InvalidOperationException($"方案中未找到流程“{procedureName}”。");
            _settings.PreviewProcedureName = procedureName;
            PopulateImageSteps(procedureName, _previewProcedure);
            SaveSettingsNoThrow();
            if (TryStartLivePreview(out var previewError))
            {
                SetStatus($"{procedureName} 实时预览已启动", StatusKind.Success);
            }
            else
            {
                SetStatus($"流程已加载，但实时预览启动失败：{previewError}", StatusKind.Error);
            }
        }
        catch (Exception exception)
        {
            _previewProcedure = null;
            ImageStepComboBox.ItemsSource = null;
            ClearRenderer();
            UpdateCommandState();
            SetStatus($"实时流程加载失败：{FormatException(exception)}", StatusKind.Error);
        }
    }

    private void CalibrationProcedureComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_solutionLoaded || CalibrationProcedureComboBox.SelectedItem is not string procedureName)
        {
            return;
        }

        try
        {
            _calibrationProcedure = VmSolution.Instance[procedureName] as VmProcedure
                ?? throw new InvalidOperationException($"方案中未找到流程“{procedureName}”。");
            _settings.CalibrationProcedureName = procedureName;
            var nPointModule = ResolveNPointCalibrationModule(procedureName);
            ClearCalibrationRenderer();
            BindCalibrationModule(nPointModule);
            SaveSettingsNoThrow();
            UpdateCommandState();
            SetStatus($"标定流程已选择：{procedureName}", StatusKind.Ready);
        }
        catch (Exception exception)
        {
            _calibrationProcedure = null;
            ClearCalibrationRenderer();
            UpdateCommandState();
            SetStatus($"标定流程加载失败：{FormatException(exception)}", StatusKind.Error);
        }
    }

    private void PopulateImageSteps(string procedureName, VmProcedure procedure)
    {
        var options = GetImageStepOptions(procedureName, procedure);
        ImageStepComboBox.ItemsSource = options;
        ImageStepComboBox.SelectedItem =
            options.FirstOrDefault(option =>
                string.Equals(option.DisplayName, "图像源1", StringComparison.Ordinal))
            ?? options.FirstOrDefault(option => option.IsImageSource)
            ?? options.FirstOrDefault(option =>
                option.DisplayName.Contains("图像采集"))
            ?? options.FirstOrDefault(option =>
                string.Equals(option.ModuleKey, _settings.ImageModuleKey, StringComparison.Ordinal))
            ?? options.FirstOrDefault(option => option.DisplayName.Contains("N点标定"))
            ?? options.FirstOrDefault(option => option.DisplayName.Contains("标定"))
            ?? options.LastOrDefault();
    }

    private static IReadOnlyList<VisionModuleOption> GetImageStepOptions(
        string procedureName,
        VmProcedure procedure)
    {
        var options = new List<VisionModuleOption>();
        var moduleList = procedure.GetProcedureModuleList();
        for (var index = 0; index < moduleList.nNum; index++)
        {
            var info = moduleList.astModuleInfo[index];
            var displayName = info.strDisplayName?.Trim() ?? "";
            var moduleName = info.strModuleName?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            var option = ResolveModuleOption(procedureName, displayName, moduleName);
            if (option is not null
                && options.All(existing =>
                    !string.Equals(existing.ModuleKey, option.ModuleKey, StringComparison.Ordinal)))
            {
                options.Add(option);
            }
        }

        if (options.Count == 0)
        {
            options.Add(new VisionModuleOption(
                $"{procedureName}（流程图像）",
                procedureName,
                procedure,
                isImageSource: false));
        }

        return options;
    }

    private static VisionModuleOption? ResolveModuleOption(
        string procedureName,
        string displayName,
        string moduleName)
    {
        var candidates = new[]
        {
            $"{procedureName}.{displayName}",
            $"{procedureName}.{moduleName}",
            moduleName,
            displayName
        };

        foreach (var candidate in candidates
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.Ordinal))
        {
            try
            {
                if (VmSolution.Instance[candidate] is IVmModule module)
                {
                    var isImageSource =
                        displayName.Contains("图像源") ||
                        displayName.Contains("图像采集") ||
                        moduleName.IndexOf("ImageSource", StringComparison.OrdinalIgnoreCase) >= 0;
                    return new VisionModuleOption(displayName, candidate, module, isImageSource);
                }
            }
            catch
            {
                // Some SDK module names are type names rather than lookup paths.
            }
        }

        return null;
    }

    private void ImageStepComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_solutionLoaded || ImageStepComboBox.SelectedItem is not VisionModuleOption option)
        {
            return;
        }

        try
        {
            BindImageStep(option, persistSelection: true);
            SetStatus($"当前图像：{PreviewProcedureComboBox.SelectedItem} / {option.DisplayName}", StatusKind.Ready);
        }
        catch (Exception exception)
        {
            ClearRenderer();
            SetStatus($"图像步骤绑定失败：{FormatException(exception)}", StatusKind.Error);
        }
    }

    private void RunOnce_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProcedureReady())
        {
            return;
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            _previewProcedure!.Run(true);
            stopwatch.Stop();
            VisionRenderControl.UpdateVMResultShow();
            SetStatus(
                $"运行完成，用时 {stopwatch.Elapsed.TotalMilliseconds:0.0} ms",
                StatusKind.Success);
        }
        catch (Exception exception)
        {
            SetStatus($"运行失败：{FormatException(exception)}", StatusKind.Error);
        }
    }

    private void ContinuousRun_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProcedureReady())
        {
            return;
        }

        try
        {
            if (TryStartLivePreview(out var previewError))
            {
                SetStatus("实时预览已启动", StatusKind.Success);
            }
            else
            {
                SetStatus($"实时预览启动失败：{previewError}", StatusKind.Error);
            }
        }
        catch (Exception exception)
        {
            SetStatus($"连续运行失败：{FormatException(exception)}", StatusKind.Error);
        }
    }

    private void StopRun_Click(object sender, RoutedEventArgs e)
    {
        if (_previewProcedure is null)
        {
            return;
        }

        try
        {
            _previewProcedure.ContinuousRunEnable = false;
            UpdateCommandState();
            SetStatus("实时预览已暂停", StatusKind.Ready);
        }
        catch (Exception exception)
        {
            SetStatus($"停止流程失败：{FormatException(exception)}", StatusKind.Error);
        }
    }

    private string SelectPreferredPreviewProcedure(IReadOnlyList<string> procedureNames)
    {
        return procedureNames.FirstOrDefault(name =>
                   string.Equals(name, _settings.PreviewProcedureName, StringComparison.Ordinal))
               ?? procedureNames.FirstOrDefault(name =>
                   string.Equals(name, "实时相机", StringComparison.Ordinal))
               ?? procedureNames.FirstOrDefault(name => name.Contains("实时"))
               ?? procedureNames.FirstOrDefault(name => name.Contains("图像采集"))
               ?? procedureNames.FirstOrDefault(name => name.Contains("相机"))
               ?? procedureNames.FirstOrDefault(name => !name.Contains("标定"))
               ?? procedureNames[0];
    }

    private string SelectPreferredCalibrationProcedure(IReadOnlyList<string> procedureNames)
    {
        return procedureNames.FirstOrDefault(name =>
                   string.Equals(name, _settings.CalibrationProcedureName, StringComparison.Ordinal))
               ?? procedureNames.FirstOrDefault(name =>
                   string.Equals(name, "标定流程", StringComparison.Ordinal))
               ?? procedureNames.FirstOrDefault(name => name.Contains("标定"))
               ?? procedureNames[0];
    }

    private static IReadOnlyList<string> GetProcedureNames()
    {
        var procedureList = VmSolution.Instance.GetAllProcedureList();
        var names = new List<string>(checked((int)procedureList.nNum));
        for (var index = 0; index < procedureList.nNum; index++)
        {
            var name = procedureList.astProcessInfo[index].strProcessName?.Trim();
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name!);
            }
        }

        return names;
    }

    private bool EnsureProcedureReady()
    {
        if (_solutionLoaded && _previewProcedure is not null)
        {
            return true;
        }

        SetStatus("请先选择方案、实时流程和预览图像。", StatusKind.Error);
        return false;
    }

    private void CloseCurrentSolution()
    {
        DetachCrosshairModule();
        _clickMoveEnabled = false;
        StopPreviewProcedureNoThrow();
        ClearRenderer();

        if (_solutionLoaded)
        {
            VmSolution.Instance?.CloseSolution();
        }

        _previewProcedure = null;
        _calibrationProcedure = null;
        _solutionLoaded = false;
        PreviewProcedureComboBox.ItemsSource = null;
        CalibrationProcedureComboBox.ItemsSource = null;
        ImageStepComboBox.ItemsSource = null;
        UpdateCommandState();
    }

    private void CloseCurrentSolutionNoThrow()
    {
        DetachCrosshairModule();
        _clickMoveEnabled = false;
        StopPreviewProcedureNoThrow();
        ClearRenderer();
        try
        {
            VmSolution.Instance?.CloseSolution();
        }
        catch
        {
        }

        _previewProcedure = null;
        _calibrationProcedure = null;
        _solutionLoaded = false;
        PreviewProcedureComboBox.ItemsSource = null;
        CalibrationProcedureComboBox.ItemsSource = null;
        ImageStepComboBox.ItemsSource = null;
        UpdateCommandState();
    }

    private void ClearRenderer()
    {
        _displayedModule = null;
        try
        {
            VisionRenderControl.ModuleSource = null;
            VisionRenderControl.ClearDisplayView();
        }
        catch
        {
        }

        ClearCalibrationRenderer();

        if (_sdkAvailable)
        {
            CenterCrosshair.Visibility = Visibility.Collapsed;
            ImagePlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void ClearCalibrationRenderer()
    {
        try
        {
            CalibrationRenderControl.ModuleSource = null;
            CalibrationRenderControl.ClearDisplayView();
        }
        catch
        {
        }

        if (_sdkAvailable)
        {
            CalibrationImagePlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void BindCalibrationModule(IMVSNPointCalibModuTool module)
    {
        CalibrationRenderControl.ModuleSource = module;
        try
        {
            CalibrationRenderControl.UpdateVMResultShow();
        }
        catch
        {
            // The module has no render result until the first calibration capture.
        }

        CalibrationImagePlaceholder.Visibility = Visibility.Collapsed;
    }

    private void StopPreviewProcedureNoThrow()
    {
        if (_previewProcedure is null)
        {
            return;
        }

        try
        {
            _previewProcedure.ContinuousRunEnable = false;
        }
        catch
        {
        }
    }

    private void RunCalibrationProcedureOnce()
    {
        if (_calibrationProcedure is null)
        {
            throw new InvalidOperationException("当前 VisionMaster 标定流程已失效。");
        }

        var resumePreview = _previewProcedure?.ContinuousRunEnable == true;
        if (resumePreview)
        {
            _previewProcedure!.ContinuousRunEnable = false;
        }

        try
        {
            _calibrationProcedure.Run(true);
        }
        finally
        {
            if (resumePreview && !_closed && _previewProcedure is not null)
            {
                _previewProcedure.ContinuousRunEnable = true;
            }
        }
    }

    private bool TryStartLivePreview(out string errorMessage)
    {
        if (!_solutionLoaded || _previewProcedure is null)
        {
            errorMessage = "视觉方案或流程尚未加载";
            return false;
        }

        if (_calibrationSession is not null)
        {
            errorMessage = "九点标定正在执行";
            return false;
        }

        try
        {
            if (ImageStepComboBox.SelectedItem is VisionModuleOption option)
            {
                BindImageStep(option, persistSelection: false);
            }

            _previewProcedure.ContinuousRunEnable = true;
            UpdateCommandState();
            errorMessage = "";
            return true;
        }
        catch (Exception exception)
        {
            UpdateCommandState();
            errorMessage = FormatException(exception);
            return false;
        }
    }

    private void BindImageStep(VisionModuleOption option, bool persistSelection)
    {
        DetachCrosshairModule();
        _clickCenterPixelReady = false;
        _clickImagePixelWidth = 0;
        _clickImagePixelHeight = 0;
        _displayedModule = option.Module as VmModule;
        VisionRenderControl.ModuleSource = option.Module;
        try
        {
            VisionRenderControl.UpdateVMResultShow();
        }
        catch
        {
            // A newly loaded image source may not have a render result until its first frame.
        }

        ImagePlaceholder.Visibility = Visibility.Collapsed;
        CenterCrosshair.Visibility = _clickMoveEnabled
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (_clickMoveEnabled)
        {
            _ = GetClickCenterPixel();
            AttachCrosshairModule();
            DrawImageCenterCrosshair();
        }
        if (!persistSelection)
        {
            return;
        }

        _settings.ImageModuleKey = option.ModuleKey;
        SaveSettingsNoThrow();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateCommandState();
    }

    private void UpdateCommandState()
    {
        if (!_sdkAvailable)
        {
            CommandBar.IsEnabled = false;
            return;
        }

        CommandBar.IsEnabled = true;
        var previewReady = _solutionLoaded && _previewProcedure is not null;
        var calibrationReady = _solutionLoaded && _calibrationProcedure is not null;
        var continuousRunning = previewReady && _previewProcedure!.ContinuousRunEnable;
        ChooseSolutionButton.IsEnabled = !_busy && !continuousRunning && !_clickMoveEnabled;
        PreviewProcedureComboBox.IsEnabled = !_busy && _solutionLoaded && !continuousRunning && !_clickMoveEnabled;
        CalibrationProcedureComboBox.IsEnabled = !_busy && _solutionLoaded && !continuousRunning && !_clickMoveEnabled;
        ImageStepComboBox.IsEnabled = !_busy && previewReady && !continuousRunning && !_clickMoveEnabled;
        RunOnceButton.IsEnabled = !_busy && previewReady && !continuousRunning;
        ContinuousRunButton.IsEnabled = !_busy && previewReady && !continuousRunning;
        StopRunButton.IsEnabled = !_busy && continuousRunning;
        if (!calibrationReady && _solutionLoaded)
        {
            CalibrationProcedureComboBox.ToolTip = "请选择包含N点标定模块的流程";
        }
    }

    private string? GetInitialSolutionDirectory()
    {
        if (string.IsNullOrWhiteSpace(_settings.SolutionPath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(_settings.SolutionPath);
        return Directory.Exists(directory) ? directory : null;
    }

    private void SaveSettingsNoThrow()
    {
        try
        {
            _settings.Save();
        }
        catch
        {
            // The vision workflow remains usable even if local preferences cannot be persisted.
        }
    }

    private void SetStatus(string message, StatusKind kind)
    {
        StatusTextBlock.Text = message;
        StatusIndicator.Fill = new SolidColorBrush(kind switch
        {
            StatusKind.Success => Color.FromRgb(57, 197, 107),
            StatusKind.Error => Color.FromRgb(217, 13, 22),
            StatusKind.Busy => Color.FromRgb(13, 110, 232),
            _ => Color.FromRgb(224, 162, 26)
        });
    }

    private static string FormatException(Exception exception)
    {
        var vmException = FindVmException(exception) ?? VmSolution.GetVmException(exception);
        return vmException is null
            ? exception.Message
            : $"{vmException.Message}（错误码 0x{vmException.errorCode:X8}）";
    }

    private static VmException? FindVmException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is VmException vmException)
            {
                return vmException;
            }
        }

        return null;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        DetachCrosshairModule();
        _commandPipeCancellation.Cancel();
        lock (_commandPipeSync)
        {
            try
            {
                _activeCommandPipe?.Dispose();
            }
            catch
            {
            }

            _activeCommandPipe = null;
        }

        StopPreviewProcedureNoThrow();
        try
        {
            VisionRenderControl.Dispose();
        }
        catch
        {
        }

        try
        {
            CalibrationRenderControl.Dispose();
        }
        catch
        {
        }

        try
        {
            VmSolution.Instance?.CloseSolution();
        }
        catch
        {
        }

        try
        {
            VmSolution.Instance?.Dispose();
        }
        catch
        {
        }
    }

    private sealed class VisionModuleOption
    {
        public VisionModuleOption(
            string displayName,
            string moduleKey,
            IVmModule module,
            bool isImageSource)
        {
            DisplayName = displayName;
            ModuleKey = moduleKey;
            Module = module;
            IsImageSource = isImageSource;
        }

        public string DisplayName { get; }

        public string ModuleKey { get; }

        public IVmModule Module { get; }

        public bool IsImageSource { get; }
    }

    private sealed class VisionCalibrationSession
    {
        public VisionCalibrationSession(
            IMVSNPointCalibModuTool module,
            string calibrationPath)
        {
            Module = module;
            CalibrationPath = calibrationPath;
        }

        public IMVSNPointCalibModuTool Module { get; }

        public string CalibrationPath { get; }

        public int NextPointNumber { get; set; } = 1;
    }

    private sealed class RectangleBlobCandidate
    {
        public RectangleBlobCandidate(
            float pixelX,
            float pixelY,
            float rectangularity,
            float area,
            int left,
            int top,
            int width,
            int height)
        {
            PixelX = pixelX;
            PixelY = pixelY;
            Rectangularity = rectangularity;
            Area = area;
            Left = left;
            Top = top;
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
        }

        public float PixelX { get; }

        public float PixelY { get; }

        public float Rectangularity { get; }

        public float Area { get; }

        public int Left { get; }

        public int Top { get; }

        public int Width { get; }

        public int Height { get; }
    }

    private sealed class InspectionImageFile
    {
        public InspectionImageFile(string filePath, int pixelWidth, int pixelHeight)
        {
            FilePath = filePath;
            PixelWidth = pixelWidth;
            PixelHeight = pixelHeight;
        }

        public string FilePath { get; }

        public int PixelWidth { get; }

        public int PixelHeight { get; }
    }

    private enum StatusKind
    {
        Ready,
        Busy,
        Success,
        Error
    }
}
