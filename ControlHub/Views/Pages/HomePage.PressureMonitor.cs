using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ControlHub.Services.Motion;

namespace ControlHub.Views.Pages;

public partial class HomePage
{
    private void LoadPressureThresholdInput()
    {
        ZPressureThresholdTextBox.Text = (_motionController?.ZPressureEmergencyStopThreshold ??
            MotionCardOptions.DefaultZPressureEmergencyStopThreshold).ToString(CultureInfo.InvariantCulture);
        SaveZPressureThresholdButton.IsEnabled = _motionController is not null;
        ZPressureThresholdSettingsStatusText.Foreground = Brushes.LightGreen;
        ZPressureThresholdSettingsStatusText.Text = $"当前生效阈值：{ZPressureThresholdTextBox.Text}；四轴共用，保存后生效。";
    }

    private void SaveZPressureThreshold_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(ZPressureThresholdTextBox.Text.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var threshold))
                throw new ArgumentException("请输入 1–32766 的整数阈值。");
            var controller = _motionController ?? throw new InvalidOperationException("运动控制配置尚未连接。");
            controller.SaveZPressureEmergencyStopThreshold(threshold);
            ZPressureThresholdTextBox.Text = threshold.ToString(CultureInfo.InvariantCulture);
            ZPressureThresholdSettingsStatusText.Foreground = Brushes.LightGreen;
            ZPressureThresholdSettingsStatusText.Text = $"已保存并生效：任一Z轴原始值 > {threshold} 全轴急停；重启后保留。";
            ZPressureProtectionStatusText.Text = controller.PressureSafetyStatus;
            ZPressureProtectionStatusText.ToolTip = controller.PressureSafetyDetails;
        }
        catch (Exception exception)
        {
            ZPressureThresholdSettingsStatusText.Foreground = Brushes.OrangeRed;
            ZPressureThresholdSettingsStatusText.Text = $"未应用：{exception.Message} 当前阈值仍为 {_motionController?.ZPressureEmergencyStopThreshold.ToString() ?? "未知"}。";
        }
    }

    private readonly PressureDisplay[] _pressureDisplays = ZAxisPressureMonitor.Unavailable("未连接")
        .Select(reading => new PressureDisplay(reading)).ToArray();
    private DispatcherTimer? _pressureTimer;

    private void InitializePressureMonitor()
    {
        ZPressureItems.ItemsSource = _pressureDisplays;
        // 生产异步续体使用 Normal；Background 显示刷新可能被持续延后。
        // 此高优先级回调仅复制四个缓存值，不等待控制卡、线程池或磁盘。
        _pressureTimer = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _pressureTimer.Tick += (_, _) => RefreshPressureMonitor();
        Loaded += (_, _) => UpdatePressureMonitorVisibility();
        IsVisibleChanged += (_, _) => UpdatePressureMonitorVisibility();
        Unloaded += (_, _) => StopPressureMonitor();
    }

    private void UpdatePressureMonitorVisibility()
    {
        if (!IsLoaded || !IsVisible)
        {
            StopPressureMonitor();
            return;
        }
        if (_pressureTimer!.IsEnabled)
            return;
        _pressureTimer!.Start();
        RefreshPressureMonitor();
    }

    private void StopPressureMonitor()
    {
        _pressureTimer?.Stop();
        ApplyPressureReadings(ZAxisPressureMonitor.Unavailable("待刷新"));
    }

    private void RefreshPressureMonitor()
    {
        if (_pressureTimer?.IsEnabled != true)
            return;
        ZPressureProtectionStatusText.Text = _motionController?.PressureSafetyStatus ?? "压力保护未连接";
        ZPressureProtectionStatusText.ToolTip = _motionController?.PressureSafetyDetails;
        var controller = _motionController;
        if (controller is null)
        {
            ApplyPressureReadings(ZAxisPressureMonitor.Unavailable("未连接"));
            return;
        }

        try
        {
            // ReadZAxisPressures 仅访问内存缓存；反馈是否过期由采集时间判定，
            // 不再把UI/线程池排队时间误当成控制卡读取超时。
            ApplyPressureReadings(controller.ReadZAxisPressures());
        }
        catch (Exception exception)
        {
            ApplyPressureReadings(ZAxisPressureMonitor.Unavailable("读取失败", exception.Message));
        }
    }

    private void ApplyPressureReadings(ZAxisPressureReading[] readings)
    {
        for (var index = 0; index < _pressureDisplays.Length; index++)
            _pressureDisplays[index].Update(readings[index]);
    }

    private sealed class PressureDisplay(ZAxisPressureReading reading) : INotifyPropertyChanged
    {
        public string Label => $"{reading.Label} · 轴 {reading.AxisNo}";
        public string Value => reading.RawValue?.ToString(CultureInfo.InvariantCulture) ?? "—";
        public string Status => reading.Status;
        public string Detail => reading.Detail;
        public Brush ValueBrush => reading.RawValue.HasValue ? Brushes.LightSkyBlue : Brushes.SlateGray;
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Update(ZAxisPressureReading next)
        {
            if (reading == next) return;
            reading = next;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }
}
