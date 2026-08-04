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
    private readonly DispatcherTimer _clockTimer;
    private readonly MainWindowViewModel _viewModel;
    private RememberedLogin? _rememberedLogin;
    private bool _motionShutdownPrepared;

    public MainWindow()
    {
        InitializeComponent();

        VisualCalibrationContent.AttachMotionController(MotionPage);
        HomeContent.AttachMotionController(MotionPage);
        UsbMicroscopeContent.AttachHomeController(HomeContent);
        HomeContent.AttachVisionCalibrationController(VisualCalibrationContent);
        HomeContent.AttachConnectionConfigController(ConnectionConfigContent);
        ParameterSettingsContent.AttachSettingsContent(HomeContent.DetachParameterSettingsPanel());

        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
        LoadRememberedLogin();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => _viewModel.NowText = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");
        _clockTimer.Start();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 初始页仍是主页；启动时先把 VisionMasterHost 拉起，让固定方案提前加载。
        // 标定页打开时只切换界面，获取实时画面由按钮手动触发。
        await Dispatcher.InvokeAsync(
            () => VisualCalibrationContent.UpdateLayout(),
            DispatcherPriority.ContextIdle);
        await VisualCalibrationContent.EnsureStartedAsync();
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
        await HomeContent.DeactivateProductionAsync();
        await UsbMicroscopeContent.DeactivateAsync();
        if (!await VisualCalibrationContent.DeactivateCalibrationViewAsync())
        {
            return;
        }
        ShowMotionPage();
    }

    private async void HomeMenu_Click(object sender, RoutedEventArgs e)
    {
        await UsbMicroscopeContent.DeactivateAsync();
        if (!await VisualCalibrationContent.DeactivateCalibrationViewAsync())
        {
            return;
        }
        ShowHomePage();
    }

    private async void VisualCalibrationMenu_Click(object sender, RoutedEventArgs e)
    {
        await HomeContent.DeactivateProductionAsync();
        await UsbMicroscopeContent.DeactivateAsync();
        ShowVisualCalibrationPage();
        try
        {
            await VisualCalibrationContent.ActivateCalibrationViewAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "标定界面开启失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void UsbMicroscopeMenu_Click(object sender, RoutedEventArgs e)
    {
        await HomeContent.DeactivateProductionAsync();
        if (!await VisualCalibrationContent.DeactivateCalibrationViewAsync())
        {
            return;
        }

        ShowUsbMicroscopePage();
        await UsbMicroscopeContent.ActivateAsync();
    }

    private async void ConnectionMenu_Click(object sender, RoutedEventArgs e)
    {
        await HomeContent.DeactivateProductionAsync();
        await UsbMicroscopeContent.DeactivateAsync();
        if (!await VisualCalibrationContent.DeactivateCalibrationViewAsync())
        {
            return;
        }
        ShowConnectionConfigPage();
    }

    private async void ParameterSettingsMenu_Click(object sender, RoutedEventArgs e)
    {
        await HomeContent.DeactivateProductionAsync();
        await UsbMicroscopeContent.DeactivateAsync();
        if (!await VisualCalibrationContent.DeactivateCalibrationViewAsync())
        {
            return;
        }

        ShowParameterSettingsPage();
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
        HomeContent.RequestProductionStopNoWait();
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
        UsbMicroscopeContent.Shutdown();
        ConnectionConfigContent.Shutdown();
        _clockTimer.Stop();
        base.OnClosed(e);
    }

    private void ShowMotionPage()
    {
        HomeMenuButton.Style = (Style)Resources["MenuButton"];
        MotionMenuButton.Style = (Style)Resources["ActiveMenuButton"];
        VisualCalibrationMenuButton.Style = (Style)Resources["MenuButton"];
        UsbMicroscopeMenuButton.Style = (Style)Resources["MenuButton"];
        ConnectionMenuButton.Style = (Style)Resources["MenuButton"];
        ParameterSettingsMenuButton.Style = (Style)Resources["MenuButton"];
        HomeContent.Visibility = Visibility.Collapsed;
        MotionPage.Visibility = Visibility.Visible;
        VisualCalibrationContent.Visibility = Visibility.Collapsed;
        UsbMicroscopeContent.Visibility = Visibility.Collapsed;
        ConnectionConfigContent.Visibility = Visibility.Collapsed;
        ParameterSettingsContent.Visibility = Visibility.Collapsed;
    }

    private void ShowHomePage()
    {
        HomeMenuButton.Style = (Style)Resources["ActiveMenuButton"];
        MotionMenuButton.Style = (Style)Resources["MenuButton"];
        VisualCalibrationMenuButton.Style = (Style)Resources["MenuButton"];
        UsbMicroscopeMenuButton.Style = (Style)Resources["MenuButton"];
        ConnectionMenuButton.Style = (Style)Resources["MenuButton"];
        ParameterSettingsMenuButton.Style = (Style)Resources["MenuButton"];
        HomeContent.Visibility = Visibility.Visible;
        MotionPage.Visibility = Visibility.Collapsed;
        VisualCalibrationContent.Visibility = Visibility.Collapsed;
        UsbMicroscopeContent.Visibility = Visibility.Collapsed;
        ConnectionConfigContent.Visibility = Visibility.Collapsed;
        ParameterSettingsContent.Visibility = Visibility.Collapsed;
        HomeContent.RefreshVisionInspectionDisplay(VisualCalibrationContent);
    }

    private void ShowVisualCalibrationPage()
    {
        VisualCalibrationContent.UseDefaultVisionDisplay();
        HomeMenuButton.Style = (Style)Resources["MenuButton"];
        MotionMenuButton.Style = (Style)Resources["MenuButton"];
        VisualCalibrationMenuButton.Style = (Style)Resources["ActiveMenuButton"];
        UsbMicroscopeMenuButton.Style = (Style)Resources["MenuButton"];
        ConnectionMenuButton.Style = (Style)Resources["MenuButton"];
        ParameterSettingsMenuButton.Style = (Style)Resources["MenuButton"];
        HomeContent.Visibility = Visibility.Collapsed;
        MotionPage.Visibility = Visibility.Collapsed;
        VisualCalibrationContent.Visibility = Visibility.Visible;
        UsbMicroscopeContent.Visibility = Visibility.Collapsed;
        ConnectionConfigContent.Visibility = Visibility.Collapsed;
        ParameterSettingsContent.Visibility = Visibility.Collapsed;
        VisualCalibrationContent.RefreshVisionDisplay();
    }

    private void ShowUsbMicroscopePage()
    {
        HomeMenuButton.Style = (Style)Resources["MenuButton"];
        MotionMenuButton.Style = (Style)Resources["MenuButton"];
        VisualCalibrationMenuButton.Style = (Style)Resources["MenuButton"];
        UsbMicroscopeMenuButton.Style = (Style)Resources["ActiveMenuButton"];
        ConnectionMenuButton.Style = (Style)Resources["MenuButton"];
        ParameterSettingsMenuButton.Style = (Style)Resources["MenuButton"];
        HomeContent.Visibility = Visibility.Collapsed;
        MotionPage.Visibility = Visibility.Collapsed;
        VisualCalibrationContent.Visibility = Visibility.Collapsed;
        UsbMicroscopeContent.Visibility = Visibility.Visible;
        ConnectionConfigContent.Visibility = Visibility.Collapsed;
        ParameterSettingsContent.Visibility = Visibility.Collapsed;
    }

    private void ShowConnectionConfigPage()
    {
        HomeMenuButton.Style = (Style)Resources["MenuButton"];
        MotionMenuButton.Style = (Style)Resources["MenuButton"];
        VisualCalibrationMenuButton.Style = (Style)Resources["MenuButton"];
        UsbMicroscopeMenuButton.Style = (Style)Resources["MenuButton"];
        ConnectionMenuButton.Style = (Style)Resources["ActiveMenuButton"];
        ParameterSettingsMenuButton.Style = (Style)Resources["MenuButton"];
        HomeContent.Visibility = Visibility.Collapsed;
        MotionPage.Visibility = Visibility.Collapsed;
        VisualCalibrationContent.Visibility = Visibility.Collapsed;
        UsbMicroscopeContent.Visibility = Visibility.Collapsed;
        ConnectionConfigContent.Visibility = Visibility.Visible;
        ParameterSettingsContent.Visibility = Visibility.Collapsed;
    }

    private void ShowParameterSettingsPage()
    {
        HomeMenuButton.Style = (Style)Resources["MenuButton"];
        MotionMenuButton.Style = (Style)Resources["MenuButton"];
        VisualCalibrationMenuButton.Style = (Style)Resources["MenuButton"];
        UsbMicroscopeMenuButton.Style = (Style)Resources["MenuButton"];
        ConnectionMenuButton.Style = (Style)Resources["MenuButton"];
        ParameterSettingsMenuButton.Style = (Style)Resources["ActiveMenuButton"];
        HomeContent.Visibility = Visibility.Collapsed;
        MotionPage.Visibility = Visibility.Collapsed;
        VisualCalibrationContent.Visibility = Visibility.Collapsed;
        UsbMicroscopeContent.Visibility = Visibility.Collapsed;
        ConnectionConfigContent.Visibility = Visibility.Collapsed;
        ParameterSettingsContent.Visibility = Visibility.Visible;
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
