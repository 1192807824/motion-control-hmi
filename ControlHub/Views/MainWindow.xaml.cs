using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using ControlHub.Services.Persistence;
using ControlHub.ViewModels;
using ControlHub.Views.Dialogs;

namespace ControlHub.Views;

public partial class MainWindow : Window
{
    private readonly RememberedLoginStore _rememberedLoginStore = new();
    private readonly SemaphoreSlim _navigationGate = new(1, 1);
    private readonly DispatcherTimer _clockTimer;
    private readonly MainWindowViewModel _viewModel;
    private RememberedLogin? _rememberedLogin;
    private bool _motionShutdownPrepared;

    public MainWindow()
    {
        InitializeComponent();

        VisualCalibrationContent.AttachMotionController(MotionPage);
        HomeContent.AttachMotionController(MotionPage);
        HomeContent.AttachVisionCalibrationController(VisualCalibrationContent);

        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
        LoadRememberedLogin();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => _viewModel.NowText = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");
        _clockTimer.Start();
    }

    private void PermissionLogin_Click(object sender, RoutedEventArgs e)
    {
        var loginDialog = new LoginDialog(_rememberedLogin?.UserName, _rememberedLogin?.Role)
        {
            Owner = this
        };

        if (loginDialog.ShowDialog() == true)
        {
            _rememberedLogin = new RememberedLogin(loginDialog.LoginUserName, loginDialog.LoginRole);
            _rememberedLoginStore.Save(_rememberedLogin);
            _viewModel.UserPermissionText = $"用户权限：{loginDialog.LoginRole}";
        }
    }

    private async void MotionMenu_Click(object sender, RoutedEventArgs e)
    {
        await NavigateAsync(ShowMotionPage, enterCalibration: false);
    }

    private async void HomeMenu_Click(object sender, RoutedEventArgs e)
    {
        await NavigateAsync(ShowHomePage, enterCalibration: false);
    }

    private async void VisualCalibrationMenu_Click(object sender, RoutedEventArgs e)
    {
        await NavigateAsync(ShowVisualCalibrationPage, enterCalibration: true);
    }

    private async void ConnectionMenu_Click(object sender, RoutedEventArgs e)
    {
        await NavigateAsync(ShowConnectionConfigPage, enterCalibration: false);
    }

    private async Task NavigateAsync(Action showPage, bool enterCalibration)
    {
        await _navigationGate.WaitAsync();
        try
        {
            if (HomeContent.IsOperationRunning)
            {
                return;
            }

            if (enterCalibration)
            {
                showPage();
                await VisualCalibrationContent.EnterCalibrationAsync();
            }
            else
            {
                await VisualCalibrationContent.LeaveCalibrationAsync();
                showPage();
            }
        }
        catch (Exception exception)
        {
            // 释放视觉宿主失败时保持当前页面，不继续切页；否则遗留的实时方案
            // 仍可能占用相机，主页再次单拍就会出现“设备忙/使能失败”。
            MessageBox.Show(
                this,
                exception.Message,
                "视觉模式切换失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _navigationGate.Release();
        }
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void ToggleWindowSize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            Width = Math.Min(1680, SystemParameters.WorkArea.Width);
            Height = Math.Min(950, SystemParameters.WorkArea.Height);
            Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
            Top = SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - Height) / 2;
            return;
        }

        EnterFullScreen();
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void LoadRememberedLogin()
    {
        _rememberedLogin = _rememberedLoginStore.Load();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_motionShutdownPrepared && !MotionPage.TryShutdown(out var failureMessage))
        {
            e.Cancel = true;
            MessageBox.Show(
                this,
                failureMessage,
                "无法安全关闭程序",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        _motionShutdownPrepared = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        VisualCalibrationContent.Shutdown();
        ConnectionConfigContent.Shutdown();
        _clockTimer.Stop();
        base.OnClosed(e);
    }

    private void ShowMotionPage()
    {
        HomeMenuButton.Style = (Style)Resources["MenuButton"];
        MotionMenuButton.Style = (Style)Resources["ActiveMenuButton"];
        VisualCalibrationMenuButton.Style = (Style)Resources["MenuButton"];
        ConnectionMenuButton.Style = (Style)Resources["MenuButton"];
        HomeContent.Visibility = Visibility.Collapsed;
        MotionPage.Visibility = Visibility.Visible;
        VisualCalibrationContent.Visibility = Visibility.Collapsed;
        ConnectionConfigContent.Visibility = Visibility.Collapsed;
    }

    private void ShowHomePage()
    {
        HomeMenuButton.Style = (Style)Resources["ActiveMenuButton"];
        MotionMenuButton.Style = (Style)Resources["MenuButton"];
        VisualCalibrationMenuButton.Style = (Style)Resources["MenuButton"];
        ConnectionMenuButton.Style = (Style)Resources["MenuButton"];
        HomeContent.Visibility = Visibility.Visible;
        MotionPage.Visibility = Visibility.Collapsed;
        VisualCalibrationContent.Visibility = Visibility.Collapsed;
        ConnectionConfigContent.Visibility = Visibility.Collapsed;
    }

    private void ShowVisualCalibrationPage()
    {
        HomeMenuButton.Style = (Style)Resources["MenuButton"];
        MotionMenuButton.Style = (Style)Resources["MenuButton"];
        VisualCalibrationMenuButton.Style = (Style)Resources["ActiveMenuButton"];
        ConnectionMenuButton.Style = (Style)Resources["MenuButton"];
        HomeContent.Visibility = Visibility.Collapsed;
        MotionPage.Visibility = Visibility.Collapsed;
        VisualCalibrationContent.Visibility = Visibility.Visible;
        ConnectionConfigContent.Visibility = Visibility.Collapsed;
    }

    private void ShowConnectionConfigPage()
    {
        HomeMenuButton.Style = (Style)Resources["MenuButton"];
        MotionMenuButton.Style = (Style)Resources["MenuButton"];
        VisualCalibrationMenuButton.Style = (Style)Resources["MenuButton"];
        ConnectionMenuButton.Style = (Style)Resources["ActiveMenuButton"];
        HomeContent.Visibility = Visibility.Collapsed;
        MotionPage.Visibility = Visibility.Collapsed;
        VisualCalibrationContent.Visibility = Visibility.Collapsed;
        ConnectionConfigContent.Visibility = Visibility.Visible;
    }

    private void EnterFullScreen()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Left = SystemParameters.WorkArea.Left;
        Top = SystemParameters.WorkArea.Top;
        Width = SystemParameters.WorkArea.Width;
        Height = SystemParameters.WorkArea.Height;
        WindowState = WindowState.Maximized;
    }
}
