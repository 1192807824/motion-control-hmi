using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ControlHub.Views.Controls;

namespace ControlHub.Views.Pages;

public partial class VisualCalibrationPage : UserControl
{
    private bool _startRequested;
    private bool _shutdown;

    public VisualCalibrationPage()
    {
        InitializeComponent();
    }

    public async Task EnsureStartedAsync()
    {
        if (_startRequested || _shutdown)
        {
            return;
        }

        _startRequested = true;
        await VisionHost.StartAsync();
    }

    public void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        VisionHost.Shutdown();
    }

    private async void RestartHost_Click(object sender, RoutedEventArgs e)
    {
        RestartHostButton.IsEnabled = false;
        HostPlaceholder.Visibility = Visibility.Visible;
        SetHostStatus("正在重启 64 位标定工作台…", HostStatus.Starting);
        await VisionHost.RestartAsync();
    }

    private void VisionHost_Started(object? sender, EventArgs e)
    {
        HostPlaceholder.Visibility = Visibility.Collapsed;
        RestartHostButton.IsEnabled = true;
        SetHostStatus("标定工作台窗口已启动", HostStatus.Ready);
    }

    private void VisionHost_Failed(object? sender, VisionMasterHostFailedEventArgs e)
    {
        HostPlaceholder.Visibility = Visibility.Visible;
        RestartHostButton.IsEnabled = true;
        SetHostStatus($"工作台启动失败：{e.Message}", HostStatus.Error);
    }

    private void VisionHost_Exited(object? sender, EventArgs e)
    {
        if (_shutdown)
        {
            return;
        }

        HostPlaceholder.Visibility = Visibility.Visible;
        RestartHostButton.IsEnabled = true;
        SetHostStatus("标定工作台已退出，可点击重启。", HostStatus.Error);
    }

    private void SetHostStatus(string message, HostStatus status)
    {
        HostStatusText.Text = message;
        HostStatusIndicator.Fill = new SolidColorBrush(status switch
        {
            HostStatus.Ready => Color.FromRgb(57, 197, 107),
            HostStatus.Error => Color.FromRgb(217, 13, 22),
            _ => Color.FromRgb(224, 162, 26)
        });
    }

    private enum HostStatus
    {
        Starting,
        Ready,
        Error
    }
}
