using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ControlHub.Services.Motion;

namespace ControlHub.Views.Pages;

public partial class HomePage
{
    private readonly PressureDisplay[] _pressureDisplays = ZAxisPressureMonitor.Unavailable("未连接")
        .Select(reading => new PressureDisplay(reading)).ToArray();
    private DispatcherTimer? _pressureTimer;
    private CancellationTokenSource? _pressureCancellation;
    private bool _pressureReadPending;
    private long _pressureReadStarted;

    private void InitializePressureMonitor()
    {
        ZPressureItems.ItemsSource = _pressureDisplays;
        _pressureTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _pressureTimer.Tick += async (_, _) => await RefreshPressureMonitorAsync();
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
        if (_pressureCancellation is not null)
            return;
        _pressureCancellation = new CancellationTokenSource();
        _pressureTimer!.Start();
        _ = RefreshPressureMonitorAsync();
    }

    private void StopPressureMonitor()
    {
        _pressureTimer?.Stop();
        _pressureCancellation?.Cancel();
        _pressureCancellation?.Dispose();
        _pressureCancellation = null;
        ApplyPressureReadings(ZAxisPressureMonitor.Unavailable("待刷新"));
    }

    private async Task RefreshPressureMonitorAsync()
    {
        if (_pressureCancellation is null)
            return;
        ZPressureProtectionStatusText.Text = _motionController?.PressureSafetyStatus ?? "压力保护未连接";
        ZPressureProtectionStatusText.ToolTip = _motionController?.PressureSafetyDetails;
        if (_pressureReadPending)
        {
            if (Stopwatch.GetElapsedTime(_pressureReadStarted).TotalSeconds >= 1)
                ApplyPressureReadings(ZAxisPressureMonitor.Unavailable("读取超时", "反馈超过 1 秒未更新，已清空旧值。"));
            return;
        }
        var controller = _motionController;
        if (controller is null)
        {
            ApplyPressureReadings(ZAxisPressureMonitor.Unavailable("未连接"));
            return;
        }

        var token = _pressureCancellation.Token;
        _pressureReadPending = true;
        _pressureReadStarted = Stopwatch.GetTimestamp();
        try
        {
            // 后台读取，每次仅允许一个批次，避免慢接口阻塞界面或堆积采集任务。
            var readings = await Task.Run(() => controller.ReadZAxisPressures(token), token);
            if (!token.IsCancellationRequested && ReferenceEquals(controller, _motionController))
                ApplyPressureReadings(Stopwatch.GetElapsedTime(_pressureReadStarted).TotalSeconds >= 1
                    ? ZAxisPressureMonitor.Unavailable("读取超时", "本轮读取耗时超过 1 秒，已丢弃过期值。")
                    : readings);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested)
                ApplyPressureReadings(ZAxisPressureMonitor.Unavailable("读取失败", exception.Message));
        }
        finally
        {
            _pressureReadPending = false;
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
