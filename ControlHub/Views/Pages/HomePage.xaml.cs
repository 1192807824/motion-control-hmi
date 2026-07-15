using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using ControlHub.Services.Vision;
using ControlHub.Views.Controls;
using ShapeLine = System.Windows.Shapes.Line;
using ShapeRectangle = System.Windows.Shapes.Rectangle;

namespace ControlHub.Views.Pages;

public partial class HomePage : UserControl
{
    private readonly VisionCalibrationService _visionCalibration = VisionCalibrationService.Shared;
    private MotionControlPage? _motionController;
    private VisualCalibrationPage? _visualCalibrationController;
    private bool _coordinateTransformRunning;
    private bool _startSequenceRunning;

    public HomePage()
    {
        InitializeComponent();
        _visionCalibration.Changed += VisionCalibration_Changed;
        RefreshVisionCalibrationStatus();
        RefreshFirstSetCalibrationDetails();
    }

    public void AttachMotionController(MotionControlPage motionController)
    {
        _motionController = motionController ?? throw new ArgumentNullException(nameof(motionController));
        UpdateHomeCommandState();
    }

    public void AttachVisionCalibrationController(VisualCalibrationPage visualCalibrationController)
    {
        _visualCalibrationController = visualCalibrationController
            ?? throw new ArgumentNullException(nameof(visualCalibrationController));
        UpdateHomeCommandState();
    }

    /// <summary>
    /// 主页生产逻辑读取的视觉标定快照，包括标定文件和两个吸嘴的偏移。
    /// </summary>
    public VisionCalibrationSnapshot VisionCalibration => _visionCalibration.GetSnapshot();

    /// <summary>
    /// 主页正在换算或执行“回中心→拍照→Blob”时禁止切换视觉模式，避免中途释放相机或宿主。
    /// </summary>
    public bool IsOperationRunning => _coordinateTransformRunning || _startSequenceRunning;

    /// <summary>
    /// 将视觉计算出的相机轴坐标转换为相机/吸嘴1/吸嘴2的实际轴目标。
    /// </summary>
    public VisionMotionTarget CalculateVisionTarget(
        double cameraTargetX,
        double cameraTargetY,
        VisionTargetTool targetTool)
    {
        return _visionCalibration.CalculateTarget(cameraTargetX, cameraTargetY, targetTool);
    }

    /// <summary>
    /// 从视觉标定页的共享配置中加载第一套 XY 标定文件。
    /// </summary>
    private VisionCalibrationSnapshot GetFirstSetCalibrationFileSnapshot()
    {
        var snapshot = _visionCalibration.GetSnapshot();
        if (string.IsNullOrWhiteSpace(snapshot.CalibrationFilePath))
        {
            throw new InvalidOperationException("第一套 XY 尚未设置标定文件。");
        }

        if (!snapshot.CalibrationFileExists)
        {
            throw new FileNotFoundException(
                "第一套 XY 标定文件不存在，请先在视觉标定页完成九点标定。",
                snapshot.CalibrationFilePath);
        }

        return snapshot;
    }

    /// <summary>
    /// 加载需要同时使用标定文件和双吸嘴偏移的完整配置。
    /// </summary>
    private VisionCalibrationSnapshot GetFirstSetCalibrationSnapshot()
    {
        var snapshot = GetFirstSetCalibrationFileSnapshot();
        if (!snapshot.CalibrationProfileExists)
        {
            throw new FileNotFoundException(
                "第一套 XY 配置尚未保存，请先在视觉标定页完成双吸嘴验证并保存配置。",
                snapshot.CalibrationProfilePath);
        }

        return snapshot;
    }

    /// <summary>
    /// 主页开始按钮当前执行三个顺序步骤：
    /// 1. 第一套 XY 回到九点标定中心；2. 海康相机拍照；3. Blob分析并显示两个矩形质心。
    /// 本阶段仍不执行取料、吸嘴或后续摆盘动作。
    /// </summary>
    private async void StartProduction_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_startSequenceRunning || _coordinateTransformRunning)
        {
            return;
        }

        try
        {
            var motionController = _motionController
                ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
            var visualCalibrationController = _visualCalibrationController
                ?? throw new InvalidOperationException("主页尚未连接视觉标定组件。");
            var velocity = _visionCalibration.Settings.VelocityPulsesPerSecond;
            if (!double.IsFinite(velocity) || velocity <= 0)
            {
                throw new InvalidOperationException("第一套 XY 的移动速度配置无效。");
            }

            _startSequenceRunning = true;
            UpdateHomeCommandState();
            ClearBlobInspectionResult();
            SetStartProductionStatus("步骤1/3：正在读取第一套 XY 标定中心…", Color.FromRgb(242, 181, 68));

            var center = ReadFirstSetCalibrationCenter();

            // 当前反馈位置只用于估算本次移动所需的超时时间；读取本身不会使能或移动轴。
            var current = motionController.CaptureCalibrationFeedback(
                VisionCalibrationService.FirstSetXHardwareAxisNo,
                VisionCalibrationService.FirstSetYHardwareAxisNo);
            var timeoutMilliseconds = CalculateStartMoveTimeout(
                current.ActualX,
                current.ActualY,
                center.X,
                center.Y,
                velocity);

            SetStartProductionStatus(
                $"步骤1/3：第一套 XY 正在回标定中心 X={center.X:0.###}，Y={center.Y:0.###}…",
                Color.FromRgb(242, 181, 68));
            var actual = await motionController.MoveCalibrationAxesToAsync(
                VisionCalibrationService.FirstSetXHardwareAxisNo,
                VisionCalibrationService.FirstSetYHardwareAxisNo,
                center.X,
                center.Y,
                velocity,
                positionTolerance: 10d,
                moveTimeoutMilliseconds: timeoutMilliseconds,
                cancellationToken: CancellationToken.None);

            // XY 确认到位后才按需启动视觉宿主；主页不会加载 VisionMaster 方案，
            // 而是用 MVS SDK 直接拍一帧，再由本地 C# Blob 分析同一张原图。
            SetStartProductionStatus(
                $"步骤2/3：XY已到位({actual.ActualX:0.###}, {actual.ActualY:0.###})，" +
                "海康拍照 → 步骤3/3 Blob分析中…",
                Color.FromRgb(242, 181, 68));
            var blobResult = await visualCalibrationController.RunRectangleBlobInspectionAsync(
                CancellationToken.None);
            SetBlobInspectionResult(blobResult);

            SetStartProductionStatus(
                "步骤3完成：已找到两个矩形并显示像素质心 XY",
                Color.FromRgb(73, 209, 125));
        }
        catch (Exception exception)
        {
            SetStartProductionStatus(
                $"开始流程失败：{exception.Message}",
                Color.FromRgb(242, 122, 128));
        }
        finally
        {
            _startSequenceRunning = false;
            UpdateHomeCommandState();
        }
    }

    /// <summary>
    /// 直接读取九点标定 XML 中的九个机械坐标。规则九点阵列的平均值就是记录标定时
    /// 的 XY 中心，因此第一步不需要启动视觉方案，也不会在移动前占用或触发相机。
    /// </summary>
    private (double X, double Y) ReadFirstSetCalibrationCenter()
    {
        var snapshot = GetFirstSetCalibrationFileSnapshot();
        XDocument document;
        try
        {
            document = XDocument.Load(snapshot.CalibrationFilePath);
        }
        catch (Exception exception)
        {
            throw new InvalidDataException("无法读取第一套 XY 九点标定文件。", exception);
        }

        ValidateNPointCalibration(document, "第一套 XY 标定文件");

        var worldPointList = document
            .Descendants()
            .FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, "CalibPointFListParam", StringComparison.Ordinal) &&
                string.Equals(
                    (string?)element.Attribute("ParamName"),
                    "WorldPointLst",
                    StringComparison.Ordinal));
        var worldPoints = worldPointList?
            .Elements()
            .Where(element => string.Equals(element.Name.LocalName, "PointF", StringComparison.Ordinal))
            .Select(element => new
            {
                X = ParseCalibrationCoordinate(element, "X"),
                Y = ParseCalibrationCoordinate(element, "Y")
            })
            .ToArray();
        if (worldPoints is not { Length: 9 })
        {
            throw new InvalidDataException("第一套 XY 标定文件中未找到完整的 9 个机械标定点。");
        }

        // 标定文件使用 VisionMaster 机械单位，控制卡使用 pulse；相机中心不叠加吸嘴偏移。
        var centerX = worldPoints.Average(point => point.X) *
            VisionCalibrationService.PulsesPerVisionUnit;
        var centerY = worldPoints.Average(point => point.Y) *
            VisionCalibrationService.PulsesPerVisionUnit;
        if (!double.IsFinite(centerX) || !double.IsFinite(centerY))
        {
            throw new InvalidOperationException("第一套 XY 标定中心坐标无效。");
        }

        return (centerX, centerY);
    }

    private static void ValidateNPointCalibration(XDocument document, string displayName)
    {
        var calibrationType = ReadCalibrationParameter(document, "CalibType");
        if (!string.Equals(calibrationType, "NPointCalib", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{displayName}不是有效的 N 点标定结果。");
        }

        var errorStatus = ReadCalibrationParameter(document, "CalibErrStatus");
        if (!string.Equals(errorStatus, "0", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{displayName}的 CalibErrStatus 不是 0，禁止用于机械移动。");
        }
    }

    private static string? ReadCalibrationParameter(XDocument document, string parameterName)
    {
        return document
            .Descendants()
            .FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, "CalibParam", StringComparison.Ordinal) &&
                string.Equals(
                    (string?)element.Attribute("ParamName"),
                    parameterName,
                    StringComparison.Ordinal))?
            .Elements()
            .FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, "ParamValue", StringComparison.Ordinal))?
            .Value
            .Trim();
    }

    private static double ParseCalibrationCoordinate(XElement point, string coordinateName)
    {
        var value = point
            .Elements()
            .FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, coordinateName, StringComparison.Ordinal))
            ?.Value;
        if (!double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var coordinate) ||
            !double.IsFinite(coordinate))
        {
            throw new InvalidDataException($"九点标定文件中的机械坐标 {coordinateName} 无效。");
        }

        return coordinate;
    }

    /// <summary>
    /// 把相机图像中的像素坐标转换成第一套 XY 两个吸嘴各自的机械目标坐标。
    /// 此方法只读取当前轴坐标，不下发任何运动命令。
    /// </summary>
    public async Task<DualNozzleMechanicalTargets> ConvertPixelToMechanicalTargetsAsync(
        double pixelX,
        double pixelY,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(pixelX) || !double.IsFinite(pixelY) || pixelX < 0 || pixelY < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelX), "像素坐标必须是大于等于0的有效数字。");
        }

        var snapshot = EnsureFirstSetToolsReady();
        var visualCalibrationController = _visualCalibrationController
            ?? throw new InvalidOperationException("主页尚未连接视觉标定组件。");
        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");

        var transformed = await visualCalibrationController.TransformPixelAsync(
            pixelX,
            pixelY,
            snapshot.CalibrationFilePath,
            cancellationToken);
        var current = motionController.CaptureCalibrationFeedback(
            VisionCalibrationService.FirstSetXHardwareAxisNo,
            VisionCalibrationService.FirstSetYHardwareAxisNo);
        var cameraTargetX = current.ActualX +
            (transformed.CenterTransformedX - transformed.TransformedX) *
            VisionCalibrationService.PulsesPerVisionUnit;
        var cameraTargetY = current.ActualY +
            (transformed.CenterTransformedY - transformed.TransformedY) *
            VisionCalibrationService.PulsesPerVisionUnit;

        return new DualNozzleMechanicalTargets(
            CalculateVisionTarget(cameraTargetX, cameraTargetY, VisionTargetTool.Nozzle1),
            CalculateVisionTarget(cameraTargetX, cameraTargetY, VisionTargetTool.Nozzle2));
    }

    private void VisionCalibration_Changed(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshVisionCalibrationStatus);
            return;
        }

        RefreshVisionCalibrationStatus();
        RefreshFirstSetCalibrationDetails();
    }

    private void RefreshVisionCalibrationStatus()
    {
        var snapshot = _visionCalibration.GetSnapshot();
        string statusText;
        Color statusColor;

        if (!snapshot.CalibrationFileExists)
        {
            statusText = "标定文件缺失";
            statusColor = Color.FromRgb(242, 122, 128);
        }
        else if (!snapshot.CalibrationProfileExists)
        {
            statusText = "第一套XY配置未保存";
            statusColor = Color.FromRgb(242, 181, 68);
        }
        else if (snapshot.Nozzle1Calibrated && snapshot.Nozzle2Calibrated)
        {
            statusText = "双吸嘴标定就绪";
            statusColor = Color.FromRgb(57, 197, 107);
        }
        else if (snapshot.Nozzle1Calibrated || snapshot.Nozzle2Calibrated)
        {
            statusText = snapshot.Nozzle1Calibrated ? "吸嘴1标定就绪" : "吸嘴2标定就绪";
            statusColor = Color.FromRgb(242, 181, 68);
        }
        else
        {
            statusText = "相机标定就绪";
            statusColor = Color.FromRgb(61, 163, 255);
        }

        VisionCalibrationStatusText.Text = statusText;
        VisionCalibrationStatusIndicator.Fill = new SolidColorBrush(statusColor);
    }

    private async void ConvertBothNozzles_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_coordinateTransformRunning || _startSequenceRunning)
        {
            return;
        }

        try
        {
            var pixelX = ParsePixelCoordinate(PixelXTextBox.Text, "像素 X");
            var pixelY = ParsePixelCoordinate(PixelYTextBox.Text, "像素 Y");

            _coordinateTransformRunning = true;
            UpdateHomeCommandState();
            SetCoordinateTransformStatus($"正在换算像素({pixelX}, {pixelY})…", true);

            var targets = await ConvertPixelToMechanicalTargetsAsync(pixelX, pixelY);
            Nozzle1MechanicalCoordinateText.Text = FormatMechanicalCoordinate(targets.Nozzle1);
            Nozzle2MechanicalCoordinateText.Text = FormatMechanicalCoordinate(targets.Nozzle2);
            SetCoordinateTransformStatus("换算完成；仅显示坐标，未下发运动命令。", true);
        }
        catch (Exception exception)
        {
            Nozzle1MechanicalCoordinateText.Text = "X = —　Y = —";
            Nozzle2MechanicalCoordinateText.Text = "X = —　Y = —";
            SetCoordinateTransformStatus($"换算失败：{exception.Message}", false);
        }
        finally
        {
            _coordinateTransformRunning = false;
            UpdateHomeCommandState();
        }
    }

    private VisionCalibrationSnapshot EnsureFirstSetToolsReady()
    {
        var snapshot = GetFirstSetCalibrationSnapshot();
        if (!snapshot.Nozzle1Calibrated || !snapshot.Nozzle2Calibrated)
        {
            throw new InvalidOperationException("第一套 XY 的两个吸嘴偏移尚未全部标定。");
        }

        return snapshot;
    }

    private void RefreshFirstSetCalibrationDetails()
    {
        if (FirstSetCalibrationDetailsText is null)
        {
            return;
        }

        var snapshot = _visionCalibration.GetSnapshot();
        var profileName = snapshot.CalibrationProfileExists
            ? Path.GetFileName(snapshot.CalibrationProfilePath)
            : "配置未保存";
        var nozzle1 = snapshot.Nozzle1Calibrated ? "嘴1√" : "嘴1×";
        var nozzle2 = snapshot.Nozzle2Calibrated ? "嘴2√" : "嘴2×";
        FirstSetCalibrationDetailsText.Text = $"轴1=X / 轴2=Y　{profileName}　{nozzle1}　{nozzle2}";
        FirstSetCalibrationDetailsText.ToolTip =
            $"标定文件：{snapshot.CalibrationFilePath}\n" +
            $"XY配置：{snapshot.CalibrationProfilePath}\n" +
            $"吸嘴1偏移：X={snapshot.Nozzle1OffsetX:0.###}，Y={snapshot.Nozzle1OffsetY:0.###} pulse\n" +
            $"吸嘴2偏移：X={snapshot.Nozzle2OffsetX:0.###}，Y={snapshot.Nozzle2OffsetY:0.###} pulse";
    }

    private void UpdateHomeCommandState()
    {
        if (ConvertBothNozzlesButton is null || StartProductionButton is null)
        {
            return;
        }

        var controllersReady =
            _motionController is not null &&
            _visualCalibrationController is not null;
        var commandsIdle = !_coordinateTransformRunning && !_startSequenceRunning;
        ConvertBothNozzlesButton.IsEnabled = controllersReady && commandsIdle;
        StartProductionButton.IsEnabled = controllersReady && commandsIdle;
        PixelXTextBox.IsEnabled = commandsIdle;
        PixelYTextBox.IsEnabled = commandsIdle;
    }

    private void SetStartProductionStatus(string message, Color color)
    {
        StartProductionHintText.Text = message;
        StartProductionHintText.ToolTip = message;
        StartProductionHintText.Foreground = new SolidColorBrush(color);
    }

    private void ClearBlobInspectionResult()
    {
        BlobRectangle1CenterText.Text = "矩形1质心(px)：X = —　Y = —";
        BlobRectangle2CenterText.Text = "矩形2质心(px)：X = —　Y = —";
        BlobInspectionImage.Source = null;
        BlobInspectionOverlayCanvas.Children.Clear();
        BlobInspectionImageViewbox.Visibility = Visibility.Collapsed;
        BlobInspectionImagePlaceholder.Visibility = Visibility.Visible;
        BlobInspectionImageStatusText.Text = "等待拍照";
        BlobInspectionImageStatusText.Foreground = new SolidColorBrush(Color.FromRgb(98, 181, 255));
    }

    private void SetBlobInspectionResult(VisionRectangleBlobResult result)
    {
        BlobRectangle1CenterText.Text =
            $"矩形1质心(px)：X = {result.Rectangle1.X:0.###}　Y = {result.Rectangle1.Y:0.###}";
        BlobRectangle2CenterText.Text =
            $"矩形2质心(px)：X = {result.Rectangle2.X:0.###}　Y = {result.Rectangle2.Y:0.###}";
        ShowBlobInspectionImage(result);
    }

    /// <summary>
    /// 加载本次单拍保存的相机图，并在同一像素坐标系中叠加两个 Blob 框和质心。
    /// BitmapCacheOption.OnLoad 会把文件完整读入内存，因此加载后即可删除临时文件。
    /// </summary>
    private void ShowBlobInspectionImage(VisionRectangleBlobResult result)
    {
        if (string.IsNullOrWhiteSpace(result.ImagePath) || !File.Exists(result.ImagePath))
        {
            throw new FileNotFoundException("本次 Blob 检测图不存在。", result.ImagePath);
        }

        BitmapImage bitmap;
        try
        {
            using var stream = new FileStream(
                result.ImagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
        }
        finally
        {
            try
            {
                File.Delete(result.ImagePath);
            }
            catch
            {
                // 图片已经完整载入内存；临时文件清理失败不影响本次结果显示。
            }
        }

        var imageWidth = bitmap.PixelWidth > 0 ? bitmap.PixelWidth : result.ImageWidth;
        var imageHeight = bitmap.PixelHeight > 0 ? bitmap.PixelHeight : result.ImageHeight;
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            throw new InvalidDataException("本次 Blob 检测图尺寸无效。");
        }

        BlobInspectionImageSurface.Width = imageWidth;
        BlobInspectionImageSurface.Height = imageHeight;
        BlobInspectionOverlayCanvas.Width = imageWidth;
        BlobInspectionOverlayCanvas.Height = imageHeight;
        BlobInspectionImage.Source = bitmap;
        BlobInspectionOverlayCanvas.Children.Clear();
        DrawBlobOverlay(result.Rectangle1, 1, Color.FromRgb(0, 230, 118), imageWidth, imageHeight);
        DrawBlobOverlay(result.Rectangle2, 2, Color.FromRgb(64, 196, 255), imageWidth, imageHeight);

        BlobInspectionImagePlaceholder.Visibility = Visibility.Collapsed;
        BlobInspectionImageViewbox.Visibility = Visibility.Visible;
        BlobInspectionImageStatusText.Text = $"已检测 · {imageWidth}×{imageHeight}";
        BlobInspectionImageStatusText.Foreground = new SolidColorBrush(Color.FromRgb(73, 209, 125));
    }

    private void DrawBlobOverlay(
        VisionBlobRectangle blob,
        int number,
        Color color,
        double imageWidth,
        double imageHeight)
    {
        var left = Math.Clamp(blob.Left, 0d, Math.Max(0d, imageWidth - 1d));
        var top = Math.Clamp(blob.Top, 0d, Math.Max(0d, imageHeight - 1d));
        var width = Math.Clamp(blob.Width, 1d, Math.Max(1d, imageWidth - left));
        var height = Math.Clamp(blob.Height, 1d, Math.Max(1d, imageHeight - top));
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var strokeThickness = Math.Max(2d, Math.Min(imageWidth, imageHeight) / 500d);

        var rectangle = new ShapeRectangle
        {
            Width = width,
            Height = height,
            Stroke = brush,
            StrokeThickness = strokeThickness
        };
        Canvas.SetLeft(rectangle, left);
        Canvas.SetTop(rectangle, top);
        BlobInspectionOverlayCanvas.Children.Add(rectangle);

        var crosshairRadius = Math.Max(8d, Math.Min(imageWidth, imageHeight) / 80d);
        AddBlobCrosshairLine(
            blob.X - crosshairRadius,
            blob.Y,
            blob.X + crosshairRadius,
            blob.Y,
            brush,
            strokeThickness);
        AddBlobCrosshairLine(
            blob.X,
            blob.Y - crosshairRadius,
            blob.X,
            blob.Y + crosshairRadius,
            brush,
            strokeThickness);

        var label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(190, 5, 20, 30)),
            BorderBrush = brush,
            BorderThickness = new Thickness(strokeThickness),
            Padding = new Thickness(5d, 2d, 5d, 2d),
            Child = new TextBlock
            {
                Text = $"矩形{number}  X={blob.X:0.0}  Y={blob.Y:0.0}",
                Foreground = brush,
                FontSize = Math.Max(12d, Math.Min(imageWidth, imageHeight) / 55d),
                FontWeight = FontWeights.Bold
            }
        };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, Math.Max(0d, top - Math.Min(imageWidth, imageHeight) / 28d));
        BlobInspectionOverlayCanvas.Children.Add(label);
    }

    private void AddBlobCrosshairLine(
        double x1,
        double y1,
        double x2,
        double y2,
        Brush brush,
        double strokeThickness)
    {
        BlobInspectionOverlayCanvas.Children.Add(new ShapeLine
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = brush,
            StrokeThickness = strokeThickness
        });
    }

    /// <summary>
    /// 按当前距离和配置速度估算超时，额外预留5秒用于加减速和状态刷新。
    /// </summary>
    private static int CalculateStartMoveTimeout(
        double currentX,
        double currentY,
        double targetX,
        double targetY,
        double velocity)
    {
        var longestDistance = Math.Max(
            Math.Abs(targetX - currentX),
            Math.Abs(targetY - currentY));
        var estimatedMilliseconds = longestDistance / velocity * 1000d + 5000d;
        return (int)Math.Clamp(Math.Ceiling(estimatedMilliseconds), 10_000d, 120_000d);
    }

    private void SetCoordinateTransformStatus(string message, bool success)
    {
        CoordinateTransformStatusText.Text = message;
        CoordinateTransformStatusText.Foreground = new SolidColorBrush(success
            ? Color.FromRgb(73, 209, 125)
            : Color.FromRgb(242, 181, 68));
    }

    private static string FormatMechanicalCoordinate(VisionMotionTarget target)
    {
        return $"X = {target.X:0.###}　Y = {target.Y:0.###}";
    }

    private static double ParseFiniteCoordinate(string? value, string fieldName)
    {
        if (!TryParseCoordinate(value, out var parsed))
        {
            throw new ArgumentException($"{fieldName}必须是有效数字。");
        }

        return parsed;
    }

    private static double ParsePixelCoordinate(string? value, string fieldName)
    {
        var parsed = ParseFiniteCoordinate(value, fieldName);
        if (parsed < 0)
        {
            throw new ArgumentException($"{fieldName}必须大于等于0。");
        }

        return parsed;
    }

    private static bool TryParseCoordinate(string? value, out double parsed)
    {
        return (double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.CurrentCulture,
                    out parsed) ||
                double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out parsed)) &&
               double.IsFinite(parsed);
    }
}
