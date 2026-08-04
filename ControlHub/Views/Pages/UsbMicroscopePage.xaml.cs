using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlashCap;
using Microsoft.Win32;

namespace ControlHub.Views.Pages;

public partial class UsbMicroscopePage : UserControl
{
    private readonly CaptureDevices _captureDevices = new();
    private CaptureDevice? _captureDevice;
    private BitmapSource? _latestFrame;
    private bool _refreshing;
    private bool _connecting;
    private bool _shutdown;
    private bool _loadedOnce;
    private long _receivedFrames;

    public UsbMicroscopePage()
    {
        InitializeComponent();
    }

    public async Task ActivateAsync()
    {
        if (!_loadedOnce && !_shutdown)
        {
            await RefreshDevicesAsync();
        }
    }

    public async Task DeactivateAsync()
    {
        if (_captureDevice is not null)
        {
            await DisconnectAsync("已断开显微镜");
        }
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
            CaptureButton.IsEnabled = true;

            if (receivedFrames == 1 || receivedFrames % 10 == 0)
            {
                var fps = timestamp.TotalSeconds > 0
                    ? frameIndex / timestamp.TotalSeconds
                    : 0;
                FrameStatusText.Text = $"实时画面  {frame.PixelWidth}×{frame.PixelHeight}  {fps:0.0} FPS";
            }
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
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using var output = File.Create(dialog.FileName);
            encoder.Save(output);
            SetStatus($"照片已保存：{Path.GetFileName(dialog.FileName)}", MicroscopeStatus.Connected);
        }
        catch (Exception exception)
        {
            SetStatus($"照片保存失败：{exception.Message}", MicroscopeStatus.Error);
        }
    }

    private void ShowCrosshair_Changed(object sender, RoutedEventArgs e)
    {
        if (CrosshairOverlay is not null)
        {
            CrosshairOverlay.Visibility = ShowCrosshairCheckBox.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
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
    }

    private void SetStatus(string message, MicroscopeStatus status)
    {
        ConnectionStatusText.Text = message;
        ConnectionStatusIndicator.Fill = new SolidColorBrush(status switch
        {
            MicroscopeStatus.Connected => Color.FromRgb(57, 197, 107),
            MicroscopeStatus.Ready => Color.FromRgb(88, 165, 255),
            MicroscopeStatus.Working => Color.FromRgb(224, 162, 26),
            MicroscopeStatus.Error => Color.FromRgb(217, 13, 22),
            _ => Color.FromRgb(111, 129, 144)
        });
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
