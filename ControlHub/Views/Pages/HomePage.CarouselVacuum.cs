using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace ControlHub.Views.Pages;

public partial class HomePage
{
    private string? _carouselUpperVacuumControlError;

    private bool CanControlCarouselUpperVacuum =>
        !_startSequenceRunning && !_oneKeyCollectRunning && !_oneKeyResetRunning &&
        !_presetPositionMoveRunning && !_drive5018Writing && !_preserveIoOnEmergencyStop;

    private void InitializeCarouselUpperVacuumControl()
    {
        var refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        refreshTimer.Tick += (_, _) => RefreshCarouselUpperVacuumControl();
        IsVisibleChanged += (_, _) =>
        {
            refreshTimer.IsEnabled = IsLoaded && IsVisible;
            if (refreshTimer.IsEnabled) RefreshCarouselUpperVacuumControl();
        };
        Loaded += (_, _) =>
        {
            refreshTimer.IsEnabled = IsVisible;
            RefreshCarouselUpperVacuumControl();
        };
        Unloaded += (_, _) => refreshTimer.Stop();
    }

    private void RefreshCarouselUpperVacuumControl()
    {
        if (CarouselUpperVacuumButton is null || CarouselUpperVacuumStateText is null) return;

        var state = "未连接";
        var vacuumEnabled = false;
        var readable = false;
        try
        {
            if (_motionController is { } controller &&
                controller.TryReadDigitalOutputHardwareBit(CarouselUpperVacuumOutputBit, out var outputHigh))
            {
                readable = true;
                vacuumEnabled = !outputHigh; // Y24低电平开启。
                state = vacuumEnabled ? "已开启 · 点击关闭" : "已关闭 · 点击开启";
                if (_preserveIoOnEmergencyStop) state = "急停中 · IO已冻结";
                else if (!CanControlCarouselUpperVacuum) state = "运行中 · 流程控制";
            }
        }
        catch (Exception exception)
        {
            state = $"读取失败：{exception.Message}";
        }

        CarouselUpperVacuumButton.IsEnabled = readable && CanControlCarouselUpperVacuum;
        CarouselUpperVacuumButton.Background = new SolidColorBrush(vacuumEnabled
            ? Color.FromRgb(22, 139, 80) : Color.FromRgb(36, 67, 88));
        CarouselUpperVacuumStateText.Text = _carouselUpperVacuumControlError ?? state;
        CarouselUpperVacuumButton.ToolTip = _carouselUpperVacuumControlError ??
            $"{state}；手动控制工位上方吸，自动运行时由流程控制。";
    }

    private void CarouselUpperVacuum_Click(object sender, RoutedEventArgs e)
    {
        if (!CanControlCarouselUpperVacuum) return;

        _carouselUpperVacuumControlError = null;
        try
        {
            var controller = _motionController
                ?? throw new InvalidOperationException("运动控制未连接。");
            if (!controller.TryReadDigitalOutputHardwareBit(CarouselUpperVacuumOutputBit, out var outputHigh))
                throw new InvalidOperationException("运动控制卡未连接。");

            // 从实际输出反转电平，避免其它IO入口改写后按钮状态失步。
            if (!controller.SetDigitalOutputHardwareBit(CarouselUpperVacuumOutputBit, !outputHigh))
                throw new InvalidOperationException("操作失败，请检查控制卡连接及急停状态。");
            if (!controller.TryReadDigitalOutputHardwareBit(CarouselUpperVacuumOutputBit, out var actualHigh) ||
                actualHigh != !outputHigh)
                throw new InvalidOperationException("输出回读不符，请检查IO状态。");
        }
        catch (Exception exception)
        {
            _carouselUpperVacuumControlError = exception.Message;
        }
        RefreshCarouselUpperVacuumControl();
    }
}
