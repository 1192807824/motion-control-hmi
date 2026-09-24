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
    private long _alarmRevision = -1;
    private bool _loadingAlarms;

    public MainWindow()
    {
        AlarmHistory.Initialize();
        InitializeComponent();

        VisualCalibrationContent.AttachMotionController(MotionPage);
        VisualCalibrationContent.AttachConnectionConfigController(ConnectionConfigContent);
        HomeContent.AttachMotionController(MotionPage);
        VisualCalibrationContent.AttachHomeSettingsProvider(
            HomeContent.GetCurrentParameterSettings,
            HomeContent.SetLowerCameraRotationCenter,
            HomeContent.ClearLowerCameraRotationCenter);
        UsbMicroscopeContent.AttachHomeController(HomeContent);
        HomeContent.AttachUsbMicroscopeController(UsbMicroscopeContent);
        HomeContent.AttachVisionCalibrationController(VisualCalibrationContent);
        HomeContent.AttachConnectionConfigController(ConnectionConfigContent);
        ParameterSettingsContent.AttachSettingsContent(HomeContent.DetachParameterSettingsPanel());

        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
        ParameterSettingsContent.ActiveRecipeChanged += ParameterSettingsContent_ActiveRecipeChanged;
        ParameterSettingsContent.AttachRecipeContext(
            HomeContent,
            MotionPage,
            VisualCalibrationContent,
            _viewModel);
        LoadRememberedLogin();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += async (_, _) =>
        {
            _viewModel.NowText = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");
            await RefreshRecentAlarmsAsync();
        };
        BatchMeasurementStore.DataCleared += BatchDataCleared;
        _clockTimer.Start();
        Loaded += MainWindow_Loaded;
    }

    private void ParameterSettingsContent_ActiveRecipeChanged(object? sender, string? recipeName)
    {
        CurrentRecipeNameText.Text = string.IsNullOrWhiteSpace(recipeName)
            ? "未应用配方"
            : recipeName;
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
        if (!await VisualCalibrationContent.DeactivateCalibrationViewAsync())
        {
            return;
        }
        ShowMotionPage();
    }

    private async void HomeMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!await VisualCalibrationContent.DeactivateCalibrationViewAsync())
        {
            return;
        }
        ShowHomePage();
        await UsbMicroscopeContent.ActivateAsync();
    }

    private async void VisualCalibrationMenu_Click(object sender, RoutedEventArgs e)
    {
        await HomeContent.DeactivateProductionAsync();
        VisualCalibrationContent.RefreshTeachingPositions();
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
            AlarmHistory.Record("视觉标定", "VISION-OPEN", exception.Message);
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
        if (!await VisualCalibrationContent.DeactivateCalibrationViewAsync())
        {
            return;
        }
        ShowConnectionConfigPage();
    }

    private async void ParameterSettingsMenu_Click(object sender, RoutedEventArgs e)
    {
        await HomeContent.DeactivateProductionAsync();
        if (!await VisualCalibrationContent.DeactivateCalibrationViewAsync())
        {
            return;
        }

        ShowParameterSettingsPage();
    }

    private async void BatchQueryMenu_Click(object sender, RoutedEventArgs e)
    {
        await HomeContent.DeactivateProductionAsync();
        if (!await VisualCalibrationContent.DeactivateCalibrationViewAsync()) return;
        ShowBatchQueryPage();
        await BatchQueryContent.ActivateAsync(HomeContent.CurrentBatchNumber);
    }

    private void ShowBatchQueryPage()
    {
        AlarmHistoryContent.Visibility = Visibility.Collapsed;
        AlarmHistoryMenuButton.Style = (Style)Resources["MenuButton"];
        foreach (var button in new[] { HomeMenuButton, MotionMenuButton, VisualCalibrationMenuButton,
                     UsbMicroscopeMenuButton, ConnectionMenuButton, ParameterSettingsMenuButton })
            button.Style = (Style)Resources["MenuButton"];
        BatchQueryMenuButton.Style = (Style)Resources["ActiveMenuButton"];
        HomeContent.Visibility = Visibility.Collapsed;
        MotionPage.Visibility = Visibility.Collapsed;
        VisualCalibrationContent.Visibility = Visibility.Collapsed;
        UsbMicroscopeContent.Visibility = Visibility.Collapsed;
        ConnectionConfigContent.Visibility = Visibility.Collapsed;
        ParameterSettingsContent.Visibility = Visibility.Collapsed;
        BatchQueryContent.Visibility = Visibility.Visible;
    }

    private void HideBatchQueryPage()
    {
        AlarmHistoryContent.Visibility = Visibility.Collapsed;
        AlarmHistoryMenuButton.Style = (Style)Resources["MenuButton"];
        BatchQueryContent.Visibility = Visibility.Collapsed;
        BatchQueryMenuButton.Style = (Style)Resources["MenuButton"];
    }

    private async void AlarmHistoryMenu_Click(object sender, RoutedEventArgs e)
    {
        await HomeContent.DeactivateProductionAsync();
        if (!await VisualCalibrationContent.DeactivateCalibrationViewAsync()) return;
        ShowBatchQueryPage();
        BatchQueryContent.Visibility = Visibility.Collapsed;
        BatchQueryMenuButton.Style = (Style)Resources["MenuButton"];
        AlarmHistoryMenuButton.Style = (Style)Resources["ActiveMenuButton"];
        AlarmHistoryContent.Visibility = Visibility.Visible;
        await AlarmHistoryContent.ActivateAsync();
    }

    private void BatchDataCleared(string path) => Dispatcher.InvokeAsync(() => HomeContent.RefreshAfterBatchDataCleared(path));

    private async Task RefreshRecentAlarmsAsync()
    {
        if (_loadingAlarms || _alarmRevision == AlarmHistory.Revision && AlarmHistory.LastError is null) return;
        _loadingAlarms = true;
        try
        {
            var revision = AlarmHistory.Revision;
            var result = await AlarmHistory.QueryAsync(null, null);
            _viewModel.AlarmRecords.Clear();
            foreach (var alarm in result.Records) _viewModel.AlarmRecords.Add(alarm);
            _alarmRevision = revision;
            AlarmHistoryMenuButton.ToolTip = AlarmHistory.LastError ?? "查询全部历史报警，可按日期筛选。切换前停止当前生产。";
            AlarmHistoryMenuLabel.Text = AlarmHistory.LastError is null ? "报警记录" : "报警记录（保存异常）";
        }
        catch (Exception exception)
        {
            AlarmHistoryMenuLabel.Text = "报警记录（读取异常）";
            AlarmHistoryMenuButton.ToolTip = exception.Message;
        }
        finally { _loadingAlarms = false; }
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
        BatchMeasurementStore.DataCleared -= BatchDataCleared;
        VisualCalibrationContent.Shutdown();
        UsbMicroscopeContent.Shutdown();
        ConnectionConfigContent.Shutdown();
        _clockTimer.Stop();
        base.OnClosed(e);
    }

    private void ShowMotionPage()
    {
        HideBatchQueryPage();
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
        HideBatchQueryPage();
        UsbMicroscopeContent.UseHomePreview();
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
        HideBatchQueryPage();
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
        HideBatchQueryPage();
        UsbMicroscopeContent.UseDefaultPreview();
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
        HideBatchQueryPage();
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
        HideBatchQueryPage();
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
