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

    private void MotionMenu_Click(object sender, RoutedEventArgs e)
    {
        ShowMotionPage();
    }

    private void VisionMenu_Click(object sender, RoutedEventArgs e)
    {
        ShowVisionPage();
    }

    private void ConnectionMenu_Click(object sender, RoutedEventArgs e)
    {
        ShowConnectionConfigPage();
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
        ConnectionConfigContent.Shutdown();
        _clockTimer.Stop();
        base.OnClosed(e);
    }

    private void ShowMotionPage()
    {
        MotionMenuButton.Style = (Style)Resources["ActiveMenuButton"];
        VisionMenuButton.Style = (Style)Resources["MenuButton"];
        ConnectionMenuButton.Style = (Style)Resources["MenuButton"];
        MotionPage.Visibility = Visibility.Visible;
        VisionContent.Visibility = Visibility.Collapsed;
        ConnectionConfigContent.Visibility = Visibility.Collapsed;
    }

    private void ShowVisionPage()
    {
        MotionMenuButton.Style = (Style)Resources["MenuButton"];
        VisionMenuButton.Style = (Style)Resources["ActiveMenuButton"];
        ConnectionMenuButton.Style = (Style)Resources["MenuButton"];
        MotionPage.Visibility = Visibility.Collapsed;
        VisionContent.Visibility = Visibility.Visible;
        ConnectionConfigContent.Visibility = Visibility.Collapsed;
    }

    private void ShowConnectionConfigPage()
    {
        MotionMenuButton.Style = (Style)Resources["MenuButton"];
        VisionMenuButton.Style = (Style)Resources["MenuButton"];
        ConnectionMenuButton.Style = (Style)Resources["ActiveMenuButton"];
        MotionPage.Visibility = Visibility.Collapsed;
        VisionContent.Visibility = Visibility.Collapsed;
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
