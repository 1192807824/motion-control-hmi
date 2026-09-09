using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlashCap;
using Microsoft.Win32;
using ShapeEllipse = System.Windows.Shapes.Ellipse;

namespace ControlHub.Views.Pages;

public partial class UsbMicroscopePage : UserControl
{
    private const double MinimumZoomScale = 1.0;
    private const double MaximumZoomScale = 8.0;
    private const double ZoomStep = 1.15;

    private readonly CaptureDevices _captureDevices = new();
    private CaptureDevice? _captureDevice;
    private HomePage? _homeController;
    private ContentControl? _homePreviewHost;
    private ContentControl _activePreviewHost;
    private BitmapSource? _latestFrame;
    private bool _refreshing;
    private bool _connecting;
    private bool _shutdown;
    private bool _loadedOnce;
    private bool _circlePointSelectionEnabled;
    private bool _circleMoveActive;
    private bool _hasCircle;
    private bool _rectanglePointSelectionEnabled;
    private bool _rectangleMoveActive;
    private bool _hasRectangle;
    private bool _ddRotationRunning;
    private readonly List<Point> _circleFitPointsPixels = [];
    private readonly List<Point> _rectangleFitPointsPixels = [];
    private readonly List<ShapeEllipse> _selectionPointMarkers = [];
    private Point _circleMoveStartView;
    private double _circleMoveStartCenterPixelX;
    private double _circleMoveStartCenterPixelY;
    private double _circleCenterPixelX;
    private double _circleCenterPixelY;
    private double _circleRadiusPixels;
    private Point _rectangleMoveStartView;
    private Point[] _rectangleMoveStartCornersPixels = [];
    private Point[] _rectangleCornersPixels = [];
    private bool _rectangleIsSquare;
    private double _zoomScale = MinimumZoomScale;
    private double _zoomOffsetX;
    private double _zoomOffsetY;
    private long _receivedFrames;

    public UsbMicroscopePage()
    {
        InitializeComponent();
        _activePreviewHost = DefaultPreviewHost;
    }

    public event EventHandler? PreviewChanged;

    public BitmapSource? LatestPreviewFrame => _latestFrame;

    public bool IsMicroscopeConnected => _captureDevice is not null;

    public string PreviewStatus =>
        _captureDevice is not null && _latestFrame is not null
            ? FrameStatusText.Text
            : ConnectionStatusText.Text;

    public void AttachHomeController(HomePage homeController)
    {
        _homeController = homeController ?? throw new ArgumentNullException(nameof(homeController));
        UpdateControls();
    }

    public async Task ActivateAsync()
    {
        if (!_loadedOnce && !_shutdown)
        {
            await RefreshDevicesAsync();
        }
    }

    public void AttachHomePreviewHost(ContentControl previewHost)
    {
        _homePreviewHost = previewHost ?? throw new ArgumentNullException(nameof(previewHost));
        UseHomePreview();
    }

    public void UseHomePreview() => MovePreviewTo(_homePreviewHost ?? DefaultPreviewHost);

    public void UseDefaultPreview() => MovePreviewTo(DefaultPreviewHost);

    private void MovePreviewTo(ContentControl host)
    {
        if (ReferenceEquals(_activePreviewHost, host))
        {
            return;
        }

        // 两个页面共用同一个预览和标注工作区；切换页面不改变采集连接。
        _circleMoveActive = false;
        _rectangleMoveActive = false;
        AnnotationCanvas.ReleaseMouseCapture();
        _activePreviewHost.Content = null;
        host.Content = MicroscopeWorkspace;
        _activePreviewHost = host;
    }

    public void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        var captureDevice = _captureDevice;
        _captureDevice = null;
        if (captureDevice is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await captureDevice.StopAsync();
                }
                catch
                {
                    // 程序关闭时设备可能已被系统移除。
                }

                try
                {
                    await captureDevice.DisposeAsync();
                }
                catch
                {
                    // 关闭阶段不再向已销毁的窗口报告设备释放错误。
                }
            });
        }
    }

    private async void UsbMicroscopePage_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_loadedOnce)
        {
            await RefreshDevicesAsync();
        }
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDevicesAsync();
    }

    private async Task RefreshDevicesAsync()
    {
        if (_refreshing || _shutdown)
        {
            return;
        }

        _refreshing = true;
        UpdateControls();
        SetStatus("正在检测 USB 显微镜…", MicroscopeStatus.Working);

        try
        {
            if (_captureDevice is not null)
            {
                await DisconnectAsync("正在刷新显微镜列表…");
            }

            var allDevices = _captureDevices.EnumerateDescriptors()
                .Where(descriptor => descriptor.Characteristics.Any(
                    characteristics => characteristics.PixelFormat != FlashCap.PixelFormats.Unknown))
                .ToArray();
            var directShowDevices = allDevices
                .Where(descriptor => descriptor.DeviceType == DeviceTypes.DirectShow)
                .ToArray();
            var availableDevices = directShowDevices.Length > 0 ? directShowDevices : allDevices;

            DeviceComboBox.ItemsSource = availableDevices
                .Select(descriptor => new MicroscopeDeviceItem(descriptor))
                .ToArray();
            DeviceComboBox.SelectedIndex = availableDevices.Length > 0 ? 0 : -1;
            _loadedOnce = true;

            if (availableDevices.Length == 0)
            {
                SetStatus("未检测到 USB 显微镜，请检查 USB 连接和驱动", MicroscopeStatus.Error);
                FrameStatusText.Text = "未发现兼容的 USB 显微镜";
            }
            else
            {
                SetStatus($"检测到 {availableDevices.Length} 个兼容设备，请选择 USB 显微镜", MicroscopeStatus.Ready);
                FrameStatusText.Text = "显微镜已就绪，等待连接";
            }
        }
        catch (Exception exception)
        {
            DeviceComboBox.ItemsSource = null;
            ModeComboBox.ItemsSource = null;
            SetStatus($"显微镜检测失败：{exception.Message}", MicroscopeStatus.Error);
        }
        finally
        {
            _refreshing = false;
            UpdateControls();
        }
    }

    private void Device_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceComboBox.SelectedItem is not MicroscopeDeviceItem device)
        {
            ModeComboBox.ItemsSource = null;
            UpdateControls();
            return;
        }

        var modes = device.Descriptor.Characteristics
            .Where(characteristics => characteristics.PixelFormat != FlashCap.PixelFormats.Unknown)
            .GroupBy(characteristics => characteristics.ToString(), StringComparer.Ordinal)
            .Select(group => new MicroscopeModeItem(group.First()))
            .ToArray();
        ModeComboBox.ItemsSource = modes;
        ModeComboBox.SelectedIndex = modes.Length > 0 ? 0 : -1;
        UpdateControls();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_connecting || _captureDevice is not null || _shutdown)
        {
            return;
        }

        if (DeviceComboBox.SelectedItem is not MicroscopeDeviceItem device ||
            ModeComboBox.SelectedItem is not MicroscopeModeItem mode)
        {
            SetStatus("请先选择显微镜设备和分辨率", MicroscopeStatus.Error);
            return;
        }

        _connecting = true;
        _latestFrame = null;
        _receivedFrames = 0;
        ClearAnnotations();
        ResetZoom();
        PreviewImage.Source = null;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        FrameStatusText.Text = "正在等待显微镜画面…";
        SetStatus($"正在连接 {device.Name}…", MicroscopeStatus.Working);
        UpdateControls();

        CaptureDevice? openedDevice = null;
        try
        {
            openedDevice = await device.Descriptor.OpenAsync(
                mode.Characteristics,
                OnPixelBufferArrivedAsync);
            await openedDevice.StartAsync();
            _captureDevice = openedDevice;
            openedDevice = null;
            SetStatus($"显微镜已连接：{device.Name}", MicroscopeStatus.Connected);
        }
        catch (Exception exception)
        {
            if (openedDevice is not null)
            {
                try
                {
                    await openedDevice.DisposeAsync();
                }
                catch
                {
                    // 保留最初的连接失败原因。
                }
            }

            SetStatus($"显微镜连接失败：{exception.Message}", MicroscopeStatus.Error);
        }
        finally
        {
            _connecting = false;
            UpdateControls();
        }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        await DisconnectAsync("已断开显微镜");
    }

    private async Task DisconnectAsync(string statusText)
    {
        var captureDevice = _captureDevice;
        _captureDevice = null;
        UpdateControls();

        if (captureDevice is not null)
        {
            Exception? releaseException = null;
            try
            {
                await captureDevice.StopAsync();
            }
            catch (Exception exception)
            {
                // USB 被拔出时 StopAsync 可能失败，仍需继续释放句柄。
                releaseException = exception;
            }

            try
            {
                await captureDevice.DisposeAsync();
            }
            catch (Exception exception)
            {
                releaseException ??= exception;
            }

            if (releaseException is not null)
            {
                statusText = $"显微镜已断开（设备释放提示：{releaseException.Message}）";
            }
        }

        _latestFrame = null;
        ClearAnnotations();
        ResetZoom();
        PreviewImage.Source = null;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        FrameStatusText.Text = "尚未接收画面";
        SetStatus(statusText, MicroscopeStatus.Ready);
        UpdateControls();
    }

    private async Task OnPixelBufferArrivedAsync(PixelBufferScope bufferScope)
    {
        byte[] imageData;
        long frameIndex;
        TimeSpan timestamp;
        try
        {
            imageData = bufferScope.Buffer.CopyImage();
            frameIndex = bufferScope.Buffer.FrameIndex;
            timestamp = bufferScope.Buffer.Timestamp;
        }
        finally
        {
            bufferScope.ReleaseNow();
        }

        BitmapFrame frame;
        try
        {
            using var stream = new MemoryStream(imageData, writable: false);
            frame = BitmapFrame.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            frame.Freeze();
        }
        catch
        {
            return;
        }

        if (_shutdown || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        var receivedFrames = Interlocked.Increment(ref _receivedFrames);
        await Dispatcher.InvokeAsync(() =>
        {
            if (_captureDevice is null || _shutdown)
            {
                return;
            }

            _latestFrame = frame;
            PreviewImage.Source = frame;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            UpdateCircleOverlayFromPixels();
            UpdateRectangleOverlayFromPixels();
            UpdateControls();

            if (receivedFrames == 1 || receivedFrames % 10 == 0)
            {
                var fps = timestamp.TotalSeconds > 0
                    ? frameIndex / timestamp.TotalSeconds
                    : 0;
                FrameStatusText.Text = $"实时画面  {frame.PixelWidth}×{frame.PixelHeight}  {fps:0.0} FPS";
            }

            PreviewChanged?.Invoke(this, EventArgs.Empty);
        }, DispatcherPriority.Render);
    }

    private async void DeviceProperties_Click(object sender, RoutedEventArgs e)
    {
        if (_captureDevice is not { HasPropertyPage: true } captureDevice)
        {
            return;
        }

        try
        {
            var window = Window.GetWindow(this);
            if (window is null)
            {
                return;
            }

            var handle = new WindowInteropHelper(window).Handle;
            await captureDevice.ShowPropertyPageAsync(handle);
        }
        catch (Exception exception)
        {
            SetStatus($"显微镜参数打开失败：{exception.Message}", MicroscopeStatus.Error);
        }
    }

    private void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (_latestFrame is not { } frame)
        {
            SetStatus("当前没有可保存的显微镜画面", MicroscopeStatus.Error);
            return;
        }

        var defaultDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "显微镜截图");
        Directory.CreateDirectory(defaultDirectory);

        var dialog = new SaveFileDialog
        {
            Title = "保存显微镜照片",
            InitialDirectory = defaultDirectory,
            FileName = $"显微镜_{DateTime.Now:yyyyMMdd_HHmmss}.jpg",
            Filter = "JPEG 图片 (*.jpg)|*.jpg|PNG 图片 (*.png)|*.png|BMP 图片 (*.bmp)|*.bmp",
            DefaultExt = ".jpg",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            BitmapEncoder encoder = Path.GetExtension(dialog.FileName).ToLowerInvariant() switch
            {
                ".png" => new PngBitmapEncoder(),
                ".bmp" => new BmpBitmapEncoder(),
                _ => new JpegBitmapEncoder { QualityLevel = 95 }
            };
            encoder.Frames.Add(BitmapFrame.Create(CreateAnnotatedFrame(frame)));
            using var output = File.Create(dialog.FileName);
            encoder.Save(output);
            SetStatus($"照片已保存：{Path.GetFileName(dialog.FileName)}", MicroscopeStatus.Connected);
        }
        catch (Exception exception)
        {
            SetStatus($"照片保存失败：{exception.Message}", MicroscopeStatus.Error);
        }
    }

    private void DrawCircle_Click(object sender, RoutedEventArgs e)
    {
        if (_latestFrame is null)
        {
            SetStatus("请先连接显微镜并等待实时画面", MicroscopeStatus.Error);
            return;
        }

        if (_circlePointSelectionEnabled)
        {
            if (!_hasCircle)
            {
                SetStatus("至少需要 3 个有效边缘点才能完成圆形拟合", MicroscopeStatus.Error);
                return;
            }

            FinishCirclePointSelection();
            return;
        }

        ClearAnnotations();
        SetCirclePointSelectionMode(true);
        SetStatus("请沿物体圆形边缘依次点击，至少选择 3 个点", MicroscopeStatus.Connected);
    }

    private void DrawRectangle_Click(object sender, RoutedEventArgs e)
    {
        if (_latestFrame is null)
        {
            SetStatus("请先连接显微镜并等待实时画面", MicroscopeStatus.Error);
            return;
        }

        ClearAnnotations();
        SetRectanglePointSelectionMode(true);
        SetStatus("请依次点击矩形或正方形的 4 个角点，点选顺序不限", MicroscopeStatus.Connected);
    }

    private void UndoPoint_Click(object sender, RoutedEventArgs e)
    {
        if (_circlePointSelectionEnabled && _circleFitPointsPixels.Count > 0)
        {
            _circleFitPointsPixels.RemoveAt(_circleFitPointsPixels.Count - 1);
            RemoveLastSelectionPointMarker();
            if (_circleFitPointsPixels.Count >= 3 && TryFitCircle(
                    _circleFitPointsPixels,
                    out var centerX,
                    out var centerY,
                    out var radius))
            {
                ApplyFittedCircle(centerX, centerY, radius);
            }
            else
            {
                _hasCircle = false;
                CircleAnnotationEllipse.Visibility = Visibility.Collapsed;
            }

            UpdateCirclePointSelectionStatus();
        }
        else if (_rectanglePointSelectionEnabled && _rectangleFitPointsPixels.Count > 0)
        {
            _rectangleFitPointsPixels.RemoveAt(_rectangleFitPointsPixels.Count - 1);
            RemoveLastSelectionPointMarker();
            _hasRectangle = false;
            _rectangleCornersPixels = [];
            RectangleAnnotationPolygon.Visibility = Visibility.Collapsed;
            UpdateRectanglePointSelectionStatus();
        }

        UpdateControls();
    }

    private void ClearAnnotation_Click(object sender, RoutedEventArgs e)
    {
        ClearAnnotations();
        SetStatus("图形标记已清除", _captureDevice is null
            ? MicroscopeStatus.Ready
            : MicroscopeStatus.Connected);
    }

    private void AnnotationCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_latestFrame is null)
        {
            return;
        }

        var imageRect = GetDisplayedImageRect();
        var point = e.GetPosition(AnnotationCanvas);
        if (imageRect.IsEmpty || !imageRect.Contains(point))
        {
            if (_circlePointSelectionEnabled)
            {
                SetStatus("请在显微镜图像范围内选择圆边缘点", MicroscopeStatus.Error);
            }
            else if (_rectanglePointSelectionEnabled)
            {
                SetStatus("请在显微镜图像范围内选择矩形角点", MicroscopeStatus.Error);
            }
            return;
        }

        if (_circlePointSelectionEnabled)
        {
            AddCircleFitPoint(point, imageRect);
            e.Handled = true;
            return;
        }

        if (_rectanglePointSelectionEnabled)
        {
            AddRectangleFitPoint(point, imageRect);
            e.Handled = true;
            return;
        }

        if (_hasCircle && IsPointInsideDisplayedCircle(point))
        {
            _circleMoveActive = true;
            _circleMoveStartView = point;
            _circleMoveStartCenterPixelX = _circleCenterPixelX;
            _circleMoveStartCenterPixelY = _circleCenterPixelY;
            AnnotationCanvas.CaptureMouse();
            AnnotationCanvas.Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }

        if (_hasRectangle && IsPointInsideDisplayedRectangle(point))
        {
            _rectangleMoveActive = true;
            _rectangleMoveStartView = point;
            _rectangleMoveStartCornersPixels = [.. _rectangleCornersPixels];
            AnnotationCanvas.CaptureMouse();
            AnnotationCanvas.Cursor = Cursors.SizeAll;
            e.Handled = true;
        }
    }

    private void AnnotationCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_circleMoveActive && !_rectangleMoveActive)
        {
            if (!_circlePointSelectionEnabled && !_rectanglePointSelectionEnabled)
            {
                var point = e.GetPosition(AnnotationCanvas);
                AnnotationCanvas.Cursor = (_hasCircle && IsPointInsideDisplayedCircle(point)) ||
                                          (_hasRectangle && IsPointInsideDisplayedRectangle(point))
                    ? Cursors.SizeAll
                    : Cursors.Arrow;
            }
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed || _latestFrame is null)
        {
            return;
        }

        var imageRect = GetDisplayedImageRect();
        if (imageRect.IsEmpty)
        {
            return;
        }

        var scale = imageRect.Width / _latestFrame.PixelWidth;
        var current = e.GetPosition(AnnotationCanvas);
        if (_circleMoveActive)
        {
            var nextCenterX = _circleMoveStartCenterPixelX + (current.X - _circleMoveStartView.X) / scale;
            var nextCenterY = _circleMoveStartCenterPixelY + (current.Y - _circleMoveStartView.Y) / scale;
            if (_circleRadiusPixels * 2 <= _latestFrame.PixelWidth)
            {
                nextCenterX = Math.Clamp(
                    nextCenterX,
                    _circleRadiusPixels,
                    _latestFrame.PixelWidth - _circleRadiusPixels);
            }

            if (_circleRadiusPixels * 2 <= _latestFrame.PixelHeight)
            {
                nextCenterY = Math.Clamp(
                    nextCenterY,
                    _circleRadiusPixels,
                    _latestFrame.PixelHeight - _circleRadiusPixels);
            }

            _circleCenterPixelX = nextCenterX;
            _circleCenterPixelY = nextCenterY;
            UpdateCircleOverlayFromPixels();
        }
        else if (_rectangleMoveActive && _rectangleMoveStartCornersPixels.Length == 4)
        {
            var deltaX = (current.X - _rectangleMoveStartView.X) / scale;
            var deltaY = (current.Y - _rectangleMoveStartView.Y) / scale;
            var minimumX = _rectangleMoveStartCornersPixels.Min(point => point.X);
            var maximumX = _rectangleMoveStartCornersPixels.Max(point => point.X);
            var minimumY = _rectangleMoveStartCornersPixels.Min(point => point.Y);
            var maximumY = _rectangleMoveStartCornersPixels.Max(point => point.Y);
            deltaX = Math.Clamp(deltaX, -minimumX, _latestFrame.PixelWidth - maximumX);
            deltaY = Math.Clamp(deltaY, -minimumY, _latestFrame.PixelHeight - maximumY);
            _rectangleCornersPixels = _rectangleMoveStartCornersPixels
                .Select(point => new Point(point.X + deltaX, point.Y + deltaY))
                .ToArray();
            UpdateRectangleOverlayFromPixels();
        }
        e.Handled = true;
    }

    private void AnnotationCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_circleMoveActive && !_rectangleMoveActive)
        {
            return;
        }

        var movedCircle = _circleMoveActive;
        _circleMoveActive = false;
        _rectangleMoveActive = false;
        AnnotationCanvas.ReleaseMouseCapture();
        AnnotationCanvas.Cursor = Cursors.SizeAll;
        if (movedCircle)
        {
            SetStatus(
                $"圆已移动：圆心({_circleCenterPixelX:0}, {_circleCenterPixelY:0})，半径{_circleRadiusPixels:0}像素",
                MicroscopeStatus.Connected);
        }
        else
        {
            var (width, height) = GetRectangleDimensions(_rectangleCornersPixels);
            SetStatus(
                $"{(_rectangleIsSquare ? "正方形" : "矩形")}已移动：{width:0} × {height:0} 像素",
                MicroscopeStatus.Connected);
        }
        e.Handled = true;
    }

    private void AnnotationCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ClampZoomOffsets();
        ApplyZoomTransform();
        UpdateCircleOverlayFromPixels();
        UpdateRectangleOverlayFromPixels();
        UpdateSelectionPointMarkers();
    }

    private void PreviewSurface_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_latestFrame is null || e.Delta == 0)
        {
            return;
        }

        var oldScale = _zoomScale;
        var scaleFactor = e.Delta > 0 ? ZoomStep : 1 / ZoomStep;
        var newScale = Math.Clamp(oldScale * scaleFactor, MinimumZoomScale, MaximumZoomScale);
        if (Math.Abs(newScale - oldScale) < 0.0001)
        {
            e.Handled = true;
            return;
        }

        var pointer = e.GetPosition(PreviewSurface);
        if (newScale <= MinimumZoomScale + 0.0001)
        {
            _zoomScale = MinimumZoomScale;
            _zoomOffsetX = 0;
            _zoomOffsetY = 0;
        }
        else
        {
            var imagePointX = (pointer.X - _zoomOffsetX) / oldScale;
            var imagePointY = (pointer.Y - _zoomOffsetY) / oldScale;
            _zoomScale = newScale;
            _zoomOffsetX = pointer.X - imagePointX * newScale;
            _zoomOffsetY = pointer.Y - imagePointY * newScale;
            ClampZoomOffsets();
        }

        ApplyZoomTransform();
        UpdateCircleOverlayFromPixels();
        UpdateRectangleOverlayFromPixels();
        UpdateSelectionPointMarkers();
        e.Handled = true;
    }

    private void ResetZoom_Click(object sender, RoutedEventArgs e)
    {
        ResetZoom();
    }

    private void ResetZoom()
    {
        _zoomScale = MinimumZoomScale;
        _zoomOffsetX = 0;
        _zoomOffsetY = 0;
        ApplyZoomTransform();
        UpdateCircleOverlayFromPixels();
        UpdateRectangleOverlayFromPixels();
        UpdateSelectionPointMarkers();
    }

    private void ApplyZoomTransform()
    {
        if (ZoomContent is null)
        {
            return;
        }

        ZoomContent.RenderTransform = new MatrixTransform(
            _zoomScale,
            0,
            0,
            _zoomScale,
            _zoomOffsetX,
            _zoomOffsetY);

        if (CircleAnnotationEllipse is not null)
        {
            CircleAnnotationEllipse.StrokeThickness = 3 / _zoomScale;
        }

        if (RectangleAnnotationPolygon is not null)
        {
            RectangleAnnotationPolygon.StrokeThickness = 3 / _zoomScale;
        }

        foreach (var marker in _selectionPointMarkers)
        {
            marker.Width = 10 / _zoomScale;
            marker.Height = 10 / _zoomScale;
            marker.StrokeThickness = 1.5 / _zoomScale;
        }

        if (ResetZoomButton is not null)
        {
            ResetZoomButton.Content = $"{_zoomScale * 100:0}%";
        }
    }

    private void ClampZoomOffsets()
    {
        var baseRect = GetBaseImageRect();
        if (baseRect.IsEmpty)
        {
            return;
        }

        _zoomOffsetX = ClampZoomOffset(
            _zoomOffsetX,
            baseRect.X,
            baseRect.Width,
            AnnotationCanvas.ActualWidth,
            _zoomScale);
        _zoomOffsetY = ClampZoomOffset(
            _zoomOffsetY,
            baseRect.Y,
            baseRect.Height,
            AnnotationCanvas.ActualHeight,
            _zoomScale);
    }

    private static double ClampZoomOffset(
        double offset,
        double imageStart,
        double imageLength,
        double viewportLength,
        double scale)
    {
        var scaledLength = imageLength * scale;
        if (scaledLength <= viewportLength)
        {
            return (viewportLength - scaledLength) / 2 - imageStart * scale;
        }

        var minimumOffset = viewportLength - (imageStart + imageLength) * scale;
        var maximumOffset = -imageStart * scale;
        return Math.Clamp(offset, minimumOffset, maximumOffset);
    }

    private void AddCircleFitPoint(Point viewPoint, Rect imageRect)
    {
        if (_latestFrame is null || imageRect.IsEmpty)
        {
            return;
        }

        var scale = imageRect.Width / _latestFrame.PixelWidth;
        var pixelPoint = new Point(
            (viewPoint.X - imageRect.X) / scale,
            (viewPoint.Y - imageRect.Y) / scale);
        _circleFitPointsPixels.Add(pixelPoint);
        AddSelectionPointMarker(pixelPoint);

        if (_circleFitPointsPixels.Count >= 3)
        {
            if (TryFitCircle(
                    _circleFitPointsPixels,
                    out var centerX,
                    out var centerY,
                    out var radius))
            {
                ApplyFittedCircle(centerX, centerY, radius);
            }
            else
            {
                _hasCircle = false;
                CircleAnnotationEllipse.Visibility = Visibility.Collapsed;
            }
        }

        UpdateCirclePointSelectionStatus();
        UpdateControls();
    }

    private void UpdateCirclePointSelectionStatus()
    {
        var pointCount = _circleFitPointsPixels.Count;
        DrawCircleButton.Content = _hasCircle ? $"完成点选({pointCount})" : $"点选中({pointCount}/3)";
        if (_hasCircle)
        {
            SetStatus(
                $"已用 {pointCount} 个点拟合圆；可继续加点提高精度，或点击“完成点选”",
                MicroscopeStatus.Connected);
        }
        else if (pointCount >= 3)
        {
            SetStatus("所选点接近直线，无法拟合圆；请撤销或继续选择其他边缘点", MicroscopeStatus.Error);
        }
        else
        {
            SetStatus($"已选择 {pointCount} 个点，还需至少 {3 - pointCount} 个点", MicroscopeStatus.Connected);
        }
    }

    private void FinishCirclePointSelection()
    {
        _circlePointSelectionEnabled = false;
        _circleFitPointsPixels.Clear();
        RemoveAllSelectionPointMarkers();
        AnnotationCanvas.IsHitTestVisible = true;
        AnnotationCanvas.Cursor = Cursors.Arrow;
        DrawCircleButton.Content = "重新点选";
        SetStatus("圆形拟合完成；按住圆内部即可拖动圆心", MicroscopeStatus.Connected);
        UpdateControls();
    }

    private void ApplyFittedCircle(double centerX, double centerY, double radius)
    {
        _circleCenterPixelX = centerX;
        _circleCenterPixelY = centerY;
        _circleRadiusPixels = radius;
        _hasCircle = true;
        UpdateCircleOverlayFromPixels();
    }

    private void AddRectangleFitPoint(Point viewPoint, Rect imageRect)
    {
        if (_latestFrame is null || imageRect.IsEmpty || _rectangleFitPointsPixels.Count >= 4)
        {
            return;
        }

        var scale = imageRect.Width / _latestFrame.PixelWidth;
        var pixelPoint = new Point(
            (viewPoint.X - imageRect.X) / scale,
            (viewPoint.Y - imageRect.Y) / scale);
        _rectangleFitPointsPixels.Add(pixelPoint);
        AddSelectionPointMarker(pixelPoint);

        if (_rectangleFitPointsPixels.Count == 4)
        {
            if (TryFitRectangle(
                    _rectangleFitPointsPixels,
                    out var corners,
                    out var isSquare))
            {
                _rectangleCornersPixels = corners;
                _rectangleIsSquare = isSquare;
                _hasRectangle = true;
                UpdateRectangleOverlayFromPixels();
                FinishRectanglePointSelection();
                return;
            }

            SetStatus("这 4 个点无法组成有效矩形，请撤销后重新选择角点", MicroscopeStatus.Error);
        }

        UpdateRectanglePointSelectionStatus();
        UpdateControls();
    }

    private void UpdateRectanglePointSelectionStatus()
    {
        var pointCount = _rectangleFitPointsPixels.Count;
        DrawRectangleButton.Content = $"点选中({pointCount}/4)";
        if (pointCount < 4)
        {
            SetStatus($"已选择 {pointCount} 个角点，还需 {4 - pointCount} 个点", MicroscopeStatus.Connected);
        }
    }

    private void FinishRectanglePointSelection()
    {
        _rectanglePointSelectionEnabled = false;
        _rectangleFitPointsPixels.Clear();
        RemoveAllSelectionPointMarkers();
        AnnotationCanvas.IsHitTestVisible = true;
        AnnotationCanvas.Cursor = Cursors.Arrow;
        DrawRectangleButton.Content = "重新点选矩形";
        var (width, height) = GetRectangleDimensions(_rectangleCornersPixels);
        SetStatus(
            $"{(_rectangleIsSquare ? "正方形" : "矩形")}生成完成：{width:0} × {height:0} 像素；按住内部可拖动",
            MicroscopeStatus.Connected);
        UpdateControls();
    }

    private static bool TryFitRectangle(
        IReadOnlyList<Point> points,
        out Point[] corners,
        out bool isSquare)
    {
        corners = [];
        isSquare = false;
        if (points.Count != 4)
        {
            return false;
        }

        var bestArea = double.PositiveInfinity;
        var bestCosine = 0d;
        var bestSine = 0d;
        var bestMinimumU = 0d;
        var bestMaximumU = 0d;
        var bestMinimumV = 0d;
        var bestMaximumV = 0d;

        for (var first = 0; first < points.Count - 1; first++)
        {
            for (var second = first + 1; second < points.Count; second++)
            {
                var deltaX = points[second].X - points[first].X;
                var deltaY = points[second].Y - points[first].Y;
                var length = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
                if (length < 2)
                {
                    continue;
                }

                var cosine = deltaX / length;
                var sine = deltaY / length;
                var minimumU = double.PositiveInfinity;
                var maximumU = double.NegativeInfinity;
                var minimumV = double.PositiveInfinity;
                var maximumV = double.NegativeInfinity;
                foreach (var point in points)
                {
                    var u = point.X * cosine + point.Y * sine;
                    var v = -point.X * sine + point.Y * cosine;
                    minimumU = Math.Min(minimumU, u);
                    maximumU = Math.Max(maximumU, u);
                    minimumV = Math.Min(minimumV, v);
                    maximumV = Math.Max(maximumV, v);
                }

                var width = maximumU - minimumU;
                var height = maximumV - minimumV;
                var area = width * height;
                if (width < 2 || height < 2 || !double.IsFinite(area) || area >= bestArea)
                {
                    continue;
                }

                bestArea = area;
                bestCosine = cosine;
                bestSine = sine;
                bestMinimumU = minimumU;
                bestMaximumU = maximumU;
                bestMinimumV = minimumV;
                bestMaximumV = maximumV;
            }
        }

        if (!double.IsFinite(bestArea))
        {
            return false;
        }

        var fittedWidth = bestMaximumU - bestMinimumU;
        var fittedHeight = bestMaximumV - bestMinimumV;
        var aspectRatio = Math.Max(fittedWidth, fittedHeight) / Math.Min(fittedWidth, fittedHeight);
        isSquare = aspectRatio <= 1.12;
        if (isSquare)
        {
            var centerU = (bestMinimumU + bestMaximumU) / 2;
            var centerV = (bestMinimumV + bestMaximumV) / 2;
            var halfSide = (fittedWidth + fittedHeight) / 4;
            bestMinimumU = centerU - halfSide;
            bestMaximumU = centerU + halfSide;
            bestMinimumV = centerV - halfSide;
            bestMaximumV = centerV + halfSide;
        }

        Point FromAxes(double u, double v)
        {
            return new Point(
                u * bestCosine - v * bestSine,
                u * bestSine + v * bestCosine);
        }

        corners =
        [
            FromAxes(bestMinimumU, bestMinimumV),
            FromAxes(bestMaximumU, bestMinimumV),
            FromAxes(bestMaximumU, bestMaximumV),
            FromAxes(bestMinimumU, bestMaximumV)
        ];
        return corners.All(point => double.IsFinite(point.X) && double.IsFinite(point.Y));
    }

    private static (double Width, double Height) GetRectangleDimensions(IReadOnlyList<Point> corners)
    {
        if (corners.Count != 4)
        {
            return (0, 0);
        }

        var width = (Distance(corners[0], corners[1]) + Distance(corners[2], corners[3])) / 2;
        var height = (Distance(corners[1], corners[2]) + Distance(corners[3], corners[0])) / 2;
        return (width, height);
    }

    private static double Distance(Point first, Point second)
    {
        var deltaX = second.X - first.X;
        var deltaY = second.Y - first.Y;
        return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
    }

    private static bool TryFitCircle(
        IReadOnlyList<Point> points,
        out double centerX,
        out double centerY,
        out double radius)
    {
        centerX = 0;
        centerY = 0;
        radius = 0;
        if (points.Count < 3)
        {
            return false;
        }

        var meanX = points.Average(point => point.X);
        var meanY = points.Average(point => point.Y);
        var matrix = new double[3, 3];
        var vector = new double[3];
        foreach (var point in points)
        {
            var x = point.X - meanX;
            var y = point.Y - meanY;
            var squared = x * x + y * y;
            matrix[0, 0] += x * x;
            matrix[0, 1] += x * y;
            matrix[0, 2] += x;
            matrix[1, 0] += x * y;
            matrix[1, 1] += y * y;
            matrix[1, 2] += y;
            matrix[2, 0] += x;
            matrix[2, 1] += y;
            matrix[2, 2] += 1;
            vector[0] -= x * squared;
            vector[1] -= y * squared;
            vector[2] -= squared;
        }

        if (!TrySolveThreeByThree(matrix, vector, out var solution))
        {
            return false;
        }

        var localCenterX = -solution[0] / 2;
        var localCenterY = -solution[1] / 2;
        var radiusSquared = localCenterX * localCenterX +
                            localCenterY * localCenterY -
                            solution[2];
        if (!double.IsFinite(radiusSquared) || radiusSquared <= 4)
        {
            return false;
        }

        centerX = localCenterX + meanX;
        centerY = localCenterY + meanY;
        radius = Math.Sqrt(radiusSquared);
        return double.IsFinite(centerX) && double.IsFinite(centerY) && double.IsFinite(radius);
    }

    private static bool TrySolveThreeByThree(double[,] matrix, double[] vector, out double[] solution)
    {
        solution = new double[3];
        var augmented = new double[3, 4];
        var largestCoefficient = 0d;
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                augmented[row, column] = matrix[row, column];
                largestCoefficient = Math.Max(largestCoefficient, Math.Abs(matrix[row, column]));
            }
            augmented[row, 3] = vector[row];
        }

        var pivotTolerance = Math.Max(1e-9, largestCoefficient * 1e-10);
        for (var pivotColumn = 0; pivotColumn < 3; pivotColumn++)
        {
            var pivotRow = pivotColumn;
            for (var row = pivotColumn + 1; row < 3; row++)
            {
                if (Math.Abs(augmented[row, pivotColumn]) > Math.Abs(augmented[pivotRow, pivotColumn]))
                {
                    pivotRow = row;
                }
            }

            if (Math.Abs(augmented[pivotRow, pivotColumn]) <= pivotTolerance)
            {
                return false;
            }

            if (pivotRow != pivotColumn)
            {
                for (var column = pivotColumn; column < 4; column++)
                {
                    (augmented[pivotColumn, column], augmented[pivotRow, column]) =
                        (augmented[pivotRow, column], augmented[pivotColumn, column]);
                }
            }

            var pivot = augmented[pivotColumn, pivotColumn];
            for (var column = pivotColumn; column < 4; column++)
            {
                augmented[pivotColumn, column] /= pivot;
            }

            for (var row = 0; row < 3; row++)
            {
                if (row == pivotColumn)
                {
                    continue;
                }

                var factor = augmented[row, pivotColumn];
                for (var column = pivotColumn; column < 4; column++)
                {
                    augmented[row, column] -= factor * augmented[pivotColumn, column];
                }
            }
        }

        for (var row = 0; row < 3; row++)
        {
            solution[row] = augmented[row, 3];
            if (!double.IsFinite(solution[row]))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsPointInsideDisplayedCircle(Point point)
    {
        if (!_hasCircle || _latestFrame is null)
        {
            return false;
        }

        var imageRect = GetDisplayedImageRect();
        if (imageRect.IsEmpty)
        {
            return false;
        }

        var scale = imageRect.Width / _latestFrame.PixelWidth;
        var centerX = imageRect.X + _circleCenterPixelX * scale;
        var centerY = imageRect.Y + _circleCenterPixelY * scale;
        var deltaX = point.X - centerX;
        var deltaY = point.Y - centerY;
        var radius = _circleRadiusPixels * scale;
        return deltaX * deltaX + deltaY * deltaY <= radius * radius;
    }

    private bool IsPointInsideDisplayedRectangle(Point point)
    {
        if (!_hasRectangle || _latestFrame is null || _rectangleCornersPixels.Length != 4)
        {
            return false;
        }

        var imageRect = GetDisplayedImageRect();
        if (imageRect.IsEmpty)
        {
            return false;
        }

        var scale = imageRect.Width / _latestFrame.PixelWidth;
        var corners = _rectangleCornersPixels
            .Select(corner => new Point(
                imageRect.X + corner.X * scale,
                imageRect.Y + corner.Y * scale))
            .ToArray();
        double? expectedSign = null;
        for (var index = 0; index < corners.Length; index++)
        {
            var start = corners[index];
            var end = corners[(index + 1) % corners.Length];
            var cross = (end.X - start.X) * (point.Y - start.Y) -
                        (end.Y - start.Y) * (point.X - start.X);
            if (Math.Abs(cross) < 0.001)
            {
                continue;
            }

            var sign = Math.Sign(cross);
            expectedSign ??= sign;
            if (sign != expectedSign)
            {
                return false;
            }
        }

        return expectedSign.HasValue;
    }

    private void AddSelectionPointMarker(Point pixelPoint)
    {
        var marker = new ShapeEllipse
        {
            Width = 10 / _zoomScale,
            Height = 10 / _zoomScale,
            Fill = new SolidColorBrush(Color.FromRgb(255, 214, 64)),
            Stroke = new SolidColorBrush(Color.FromRgb(30, 35, 40)),
            StrokeThickness = 1.5 / _zoomScale,
            IsHitTestVisible = false
        };
        _selectionPointMarkers.Add(marker);
        AnnotationCanvas.Children.Add(marker);
        PositionSelectionPointMarker(marker, pixelPoint);
    }

    private void PositionSelectionPointMarker(ShapeEllipse marker, Point pixelPoint)
    {
        if (_latestFrame is null)
        {
            return;
        }

        var imageRect = GetDisplayedImageRect();
        if (imageRect.IsEmpty)
        {
            return;
        }

        var scale = imageRect.Width / _latestFrame.PixelWidth;
        Canvas.SetLeft(marker, imageRect.X + pixelPoint.X * scale - marker.Width / 2);
        Canvas.SetTop(marker, imageRect.Y + pixelPoint.Y * scale - marker.Height / 2);
    }

    private void UpdateSelectionPointMarkers()
    {
        IReadOnlyList<Point> points = _rectanglePointSelectionEnabled
            ? _rectangleFitPointsPixels
            : _circleFitPointsPixels;
        for (var index = 0; index < Math.Min(
                 points.Count,
                 _selectionPointMarkers.Count); index++)
        {
            PositionSelectionPointMarker(_selectionPointMarkers[index], points[index]);
        }
    }

    private void RemoveLastSelectionPointMarker()
    {
        if (_selectionPointMarkers.Count == 0)
        {
            return;
        }

        var marker = _selectionPointMarkers[^1];
        _selectionPointMarkers.RemoveAt(_selectionPointMarkers.Count - 1);
        AnnotationCanvas.Children.Remove(marker);
    }

    private void RemoveAllSelectionPointMarkers()
    {
        foreach (var marker in _selectionPointMarkers)
        {
            AnnotationCanvas.Children.Remove(marker);
        }
        _selectionPointMarkers.Clear();
    }

    private void UpdateCirclePreview(Point center, double radius)
    {
        var diameter = radius * 2;
        CircleAnnotationEllipse.Width = diameter;
        CircleAnnotationEllipse.Height = diameter;
        Canvas.SetLeft(CircleAnnotationEllipse, center.X - radius);
        Canvas.SetTop(CircleAnnotationEllipse, center.Y - radius);
    }

    private void UpdateCircleOverlayFromPixels()
    {
        if (!_hasCircle || _latestFrame is null || AnnotationCanvas is null)
        {
            return;
        }

        var imageRect = GetDisplayedImageRect();
        if (imageRect.IsEmpty || _latestFrame.PixelWidth <= 0)
        {
            CircleAnnotationEllipse.Visibility = Visibility.Collapsed;
            return;
        }

        var scale = imageRect.Width / _latestFrame.PixelWidth;
        var center = new Point(
            imageRect.X + _circleCenterPixelX * scale,
            imageRect.Y + _circleCenterPixelY * scale);
        UpdateCirclePreview(center, _circleRadiusPixels * scale);
        CircleAnnotationEllipse.Visibility = Visibility.Visible;
    }

    private void UpdateRectangleOverlayFromPixels()
    {
        if (!_hasRectangle ||
            _latestFrame is null ||
            _rectangleCornersPixels.Length != 4 ||
            AnnotationCanvas is null)
        {
            return;
        }

        var imageRect = GetDisplayedImageRect();
        if (imageRect.IsEmpty || _latestFrame.PixelWidth <= 0)
        {
            RectangleAnnotationPolygon.Visibility = Visibility.Collapsed;
            return;
        }

        var scale = imageRect.Width / _latestFrame.PixelWidth;
        RectangleAnnotationPolygon.Points = new PointCollection(
            _rectangleCornersPixels.Select(point => new Point(
                imageRect.X + point.X * scale,
                imageRect.Y + point.Y * scale)));
        RectangleAnnotationPolygon.Visibility = Visibility.Visible;
    }

    private Rect GetDisplayedImageRect()
    {
        return GetBaseImageRect();
    }

    private Rect GetBaseImageRect()
    {
        if (_latestFrame is null ||
            _latestFrame.PixelWidth <= 0 ||
            _latestFrame.PixelHeight <= 0 ||
            AnnotationCanvas.ActualWidth <= 0 ||
            AnnotationCanvas.ActualHeight <= 0)
        {
            return Rect.Empty;
        }

        var scale = Math.Min(
            AnnotationCanvas.ActualWidth / _latestFrame.PixelWidth,
            AnnotationCanvas.ActualHeight / _latestFrame.PixelHeight);
        var width = _latestFrame.PixelWidth * scale;
        var height = _latestFrame.PixelHeight * scale;
        return new Rect(
            (AnnotationCanvas.ActualWidth - width) / 2,
            (AnnotationCanvas.ActualHeight - height) / 2,
            width,
            height);
    }

    private void SetCirclePointSelectionMode(bool enabled)
    {
        _circlePointSelectionEnabled = enabled;
        AnnotationCanvas.IsHitTestVisible = enabled || _hasCircle || _hasRectangle;
        AnnotationCanvas.Cursor = enabled
            ? Cursors.Cross
            : _hasCircle || _hasRectangle ? Cursors.SizeAll : Cursors.Arrow;
        DrawCircleButton.Content = enabled ? "点选中(0/3)" : _hasCircle ? "重新点选" : "点选圆";
        UpdateControls();
    }

    private void SetRectanglePointSelectionMode(bool enabled)
    {
        _rectanglePointSelectionEnabled = enabled;
        AnnotationCanvas.IsHitTestVisible = enabled || _hasCircle || _hasRectangle;
        AnnotationCanvas.Cursor = enabled
            ? Cursors.Cross
            : _hasCircle || _hasRectangle ? Cursors.SizeAll : Cursors.Arrow;
        DrawRectangleButton.Content = enabled ? "点选中(0/4)" : _hasRectangle ? "重新点选矩形" : "点选矩形";
        UpdateControls();
    }

    private void ClearAnnotations()
    {
        _circleMoveActive = false;
        _rectangleMoveActive = false;
        _hasCircle = false;
        _hasRectangle = false;
        _circleRadiusPixels = 0;
        _circleFitPointsPixels.Clear();
        _rectangleFitPointsPixels.Clear();
        _rectangleCornersPixels = [];
        _rectangleMoveStartCornersPixels = [];
        RemoveAllSelectionPointMarkers();
        if (AnnotationCanvas is not null)
        {
            AnnotationCanvas.ReleaseMouseCapture();
            AnnotationCanvas.IsHitTestVisible = false;
            AnnotationCanvas.Cursor = Cursors.Arrow;
        }

        if (CircleAnnotationEllipse is not null)
        {
            CircleAnnotationEllipse.Visibility = Visibility.Collapsed;
        }

        if (RectangleAnnotationPolygon is not null)
        {
            RectangleAnnotationPolygon.Visibility = Visibility.Collapsed;
            RectangleAnnotationPolygon.Points.Clear();
        }

        _circlePointSelectionEnabled = false;
        _rectanglePointSelectionEnabled = false;
        if (DrawCircleButton is not null)
        {
            DrawCircleButton.Content = "点选圆";
        }

        if (DrawRectangleButton is not null)
        {
            DrawRectangleButton.Content = "点选矩形";
        }

        if (UndoPointButton is not null)
        {
            UndoPointButton.IsEnabled = false;
        }

        UpdateControls();
    }

    private BitmapSource CreateAnnotatedFrame(BitmapSource frame)
    {
        var drawCircle = _hasCircle && _circleRadiusPixels > 0;
        var drawRectangle = _hasRectangle && _rectangleCornersPixels.Length == 4;
        if (!drawCircle && !drawRectangle)
        {
            return frame;
        }

        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(frame, new Rect(0, 0, frame.PixelWidth, frame.PixelHeight));
            var displayedRect = GetDisplayedImageRect();
            var displayScale = displayedRect.IsEmpty || frame.PixelWidth <= 0
                ? 1d
                : displayedRect.Width / frame.PixelWidth * _zoomScale;
            var strokeThickness = Math.Max(2d, 3d / displayScale);
            if (drawCircle)
            {
                var circlePen = new Pen(
                    new SolidColorBrush(Color.FromRgb(255, 59, 66)),
                    strokeThickness);
                circlePen.Freeze();
                drawing.DrawEllipse(
                    null,
                    circlePen,
                    new Point(_circleCenterPixelX, _circleCenterPixelY),
                    _circleRadiusPixels,
                    _circleRadiusPixels);
            }

            if (drawRectangle)
            {
                var rectanglePen = new Pen(
                    new SolidColorBrush(Color.FromRgb(53, 213, 255)),
                    strokeThickness);
                rectanglePen.Freeze();
                for (var index = 0; index < _rectangleCornersPixels.Length; index++)
                {
                    drawing.DrawLine(
                        rectanglePen,
                        _rectangleCornersPixels[index],
                        _rectangleCornersPixels[(index + 1) % _rectangleCornersPixels.Length]);
                }
            }
        }

        var dpiX = frame.DpiX > 0 ? frame.DpiX : 96;
        var dpiY = frame.DpiY > 0 ? frame.DpiY : 96;
        var rendered = new RenderTargetBitmap(
            frame.PixelWidth,
            frame.PixelHeight,
            dpiX,
            dpiY,
            System.Windows.Media.PixelFormats.Pbgra32);
        rendered.Render(visual);
        rendered.Freeze();
        return rendered;
    }

    private async void RotateDdOnce_Click(object sender, RoutedEventArgs e)
    {
        if (_ddRotationRunning || _homeController is null || _captureDevice is null)
        {
            return;
        }

        _ddRotationRunning = true;
        SetStatus("DD 马达正在转动 22500 脉冲…", MicroscopeStatus.Working);
        UpdateControls();
        try
        {
            var result = await _homeController.RotateDdOnceAsync(CancellationToken.None);
            SetStatus(
                $"DD 马达转动完成，当前位置 {result.FeedbackPosition:0.###} pulse",
                MicroscopeStatus.Connected);
        }
        catch (Exception exception)
        {
            SetStatus($"DD 马达转动失败：{exception.Message}", MicroscopeStatus.Error);
        }
        finally
        {
            _ddRotationRunning = false;
            UpdateControls();
        }
    }

    private void UpdateControls()
    {
        var connected = _captureDevice is not null;
        var busy = _refreshing || _connecting;
        DeviceComboBox.IsEnabled = !connected && !busy;
        ModeComboBox.IsEnabled = !connected && !busy;
        RefreshDevicesButton.IsEnabled = !busy;
        ConnectButton.IsEnabled = !connected && !busy &&
                                  DeviceComboBox.SelectedItem is MicroscopeDeviceItem &&
                                  ModeComboBox.SelectedItem is MicroscopeModeItem;
        DisconnectButton.IsEnabled = connected && !busy;
        DevicePropertiesButton.IsEnabled = connected && _captureDevice?.HasPropertyPage == true;
        CaptureButton.IsEnabled = connected && _latestFrame is not null;
        ResetZoomButton.IsEnabled = connected && _latestFrame is not null;
        DrawCircleButton.IsEnabled = connected && _latestFrame is not null;
        DrawRectangleButton.IsEnabled = connected && _latestFrame is not null;
        UndoPointButton.IsEnabled = (_circlePointSelectionEnabled && _circleFitPointsPixels.Count > 0) ||
                                    (_rectanglePointSelectionEnabled && _rectangleFitPointsPixels.Count > 0);
        ClearAnnotationButton.IsEnabled = _hasCircle ||
                                          _hasRectangle ||
                                          _circlePointSelectionEnabled ||
                                          _rectanglePointSelectionEnabled ||
                                          _circleFitPointsPixels.Count > 0 ||
                                          _rectangleFitPointsPixels.Count > 0;
        RotateDdButton.IsEnabled = connected &&
                                   _latestFrame is not null &&
                                   _homeController is not null &&
                                   !_ddRotationRunning;
    }

    private void SetStatus(string message, MicroscopeStatus status)
    {
        ConnectionStatusText.Text = message;
        AnnotationStatusText.Text = message;
        ConnectionStatusIndicator.Fill = new SolidColorBrush(status switch
        {
            MicroscopeStatus.Connected => Color.FromRgb(57, 197, 107),
            MicroscopeStatus.Ready => Color.FromRgb(88, 165, 255),
            MicroscopeStatus.Working => Color.FromRgb(224, 162, 26),
            MicroscopeStatus.Error => Color.FromRgb(217, 13, 22),
            _ => Color.FromRgb(111, 129, 144)
        });
        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class MicroscopeDeviceItem
    {
        public MicroscopeDeviceItem(CaptureDeviceDescriptor descriptor)
        {
            Descriptor = descriptor;
            Name = descriptor.Name;
        }

        public CaptureDeviceDescriptor Descriptor { get; }
        public string Name { get; }
    }

    private sealed class MicroscopeModeItem
    {
        public MicroscopeModeItem(VideoCharacteristics characteristics)
        {
            Characteristics = characteristics;
            Description = characteristics.ToString();
        }

        public VideoCharacteristics Characteristics { get; }
        public string Description { get; }
    }

    private enum MicroscopeStatus
    {
        Ready,
        Working,
        Connected,
        Error
    }
}
