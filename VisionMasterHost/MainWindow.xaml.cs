using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using VM.Core;
using VM.PlatformSDKCS;

namespace VisionMasterHost;

public partial class MainWindow : Window
{
    private VmProcedure? _activeProcedure;
    private bool _solutionLoaded;
    private bool _workAreaLocked;
    private bool _closed;
    private bool _sdkAvailable = true;

    public MainWindow(bool embedded)
    {
        InitializeComponent();
        if (embedded)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
        }
    }

    public void ReportSdkInitializationFailure(Exception exception)
    {
        _sdkAvailable = false;
        var vmException = FindVmException(exception);
        var message = vmException?.errorCode == unchecked((int)0xE0000700)
            ? "未检测到 VisionMaster 加密狗或授权状态异常（0xE0000700）。"
            : FormatException(exception);
        SdkErrorTextBlock.Text = message;
        SdkErrorPanel.Visibility = Visibility.Visible;
        VmMainView.IsEnabled = false;
        UpdateCommandState();
        SetStatus(message, StatusKind.Error);
    }

    private void BrowseSolution_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 VisionMaster 标定方案",
            Filter = "VisionMaster 方案 (*.sol)|*.sol|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            SolutionPathTextBox.Text = dialog.FileName;
            SetStatus($"已选择方案：{Path.GetFileName(dialog.FileName)}", StatusKind.Ready);
        }
    }

    private async void LoadSolution_Click(object sender, RoutedEventArgs e)
    {
        var solutionPath = SolutionPathTextBox.Text?.Trim() ?? "";
        if (!File.Exists(solutionPath))
        {
            SetStatus("请选择有效的 VisionMaster .sol 方案文件。", StatusKind.Error);
            return;
        }

        SetBusy(true);
        SetStatus("正在加载 VisionMaster 方案…", StatusKind.Busy);
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.Background);

        var previousSolutionClosed = false;
        try
        {
            CloseCurrentSolution();
            previousSolutionClosed = true;
            VmSolution.Load(Path.GetFullPath(solutionPath), SolutionPasswordBox.Password ?? "");
            _solutionLoaded = true;

            var procedureNames = GetProcedureNames();
            if (procedureNames.Count == 0)
            {
                throw new InvalidOperationException("方案中没有可用流程。 ");
            }

            ProcedureComboBox.ItemsSource = procedureNames;
            ProcedureComboBox.SelectedItem =
                procedureNames.FirstOrDefault(name => name.Contains("标定"))
                ?? procedureNames[0];
            UpdateCommandState();
            SetStatus(
                $"方案已加载：{Path.GetFileName(solutionPath)}，共 {procedureNames.Count} 个流程。",
                StatusKind.Success);
        }
        catch (Exception exception)
        {
            if (previousSolutionClosed)
            {
                CloseCurrentSolutionNoThrow();
            }

            SetStatus($"加载失败：{FormatException(exception)}", StatusKind.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SaveSolution_Click(object sender, RoutedEventArgs e)
    {
        if (!_solutionLoaded)
        {
            SetStatus("请先加载 VisionMaster 标定方案。", StatusKind.Error);
            return;
        }

        try
        {
            VmSolution.Save();
            SetStatus("VisionMaster 标定方案已保存。", StatusKind.Success);
        }
        catch (Exception exception)
        {
            SetStatus($"保存失败：{FormatException(exception)}", StatusKind.Error);
        }
    }

    private void ProcedureComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_solutionLoaded || ProcedureComboBox.SelectedItem is not string procedureName)
        {
            return;
        }

        try
        {
            StopActiveProcedureNoThrow();
            _activeProcedure = VmSolution.Instance[procedureName] as VmProcedure
                ?? throw new InvalidOperationException($"方案中未找到流程“{procedureName}”。");
            VmMainView.BindSingleProcedure(procedureName);
            ApplyEditState();
            UpdateCommandState();
            SetStatus($"当前标定流程：{procedureName}", StatusKind.Ready);
        }
        catch (Exception exception)
        {
            _activeProcedure = null;
            UpdateCommandState();
            SetStatus($"流程绑定失败：{FormatException(exception)}", StatusKind.Error);
        }
    }

    private void RunOnce_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProcedureReady())
        {
            return;
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            _activeProcedure!.Run();
            stopwatch.Stop();
            SetStatus(
                $"流程“{_activeProcedure.Name}”执行完成，用时 {stopwatch.Elapsed.TotalMilliseconds:0.0} ms。",
                StatusKind.Success);
        }
        catch (Exception exception)
        {
            SetStatus($"单次运行失败：{FormatException(exception)}", StatusKind.Error);
        }
    }

    private void ContinuousRun_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProcedureReady())
        {
            return;
        }

        try
        {
            _activeProcedure!.ContinuousRunEnable = true;
            UpdateCommandState();
            SetStatus($"流程“{_activeProcedure.Name}”正在连续运行。", StatusKind.Success);
        }
        catch (Exception exception)
        {
            SetStatus($"连续运行失败：{FormatException(exception)}", StatusKind.Error);
        }
    }

    private void StopRun_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProcedure is null)
        {
            return;
        }

        try
        {
            _activeProcedure.ContinuousRunEnable = false;
            UpdateCommandState();
            SetStatus($"流程“{_activeProcedure.Name}”已停止。", StatusKind.Ready);
        }
        catch (Exception exception)
        {
            SetStatus($"停止流程失败：{FormatException(exception)}", StatusKind.Error);
        }
    }

    private void ToggleEditLock_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _workAreaLocked = !_workAreaLocked;
            ApplyEditState();
            SetStatus(
                _workAreaLocked ? "标定流程编辑已锁定。" : "标定流程编辑已解锁。",
                StatusKind.Ready);
        }
        catch (Exception exception)
        {
            SetStatus($"切换编辑状态失败：{FormatException(exception)}", StatusKind.Error);
        }
    }

    private void ApplyEditState()
    {
        if (_workAreaLocked)
        {
            VmMainView.LockWorkArea();
            VmMainView.SetParamTabEditable(false);
            EditLockButton.Content = "解锁编辑";
        }
        else
        {
            VmMainView.UnlockWorkArea();
            VmMainView.SetParamTabEditable(true);
            EditLockButton.Content = "锁定编辑";
        }
    }

    private static IReadOnlyList<string> GetProcedureNames()
    {
        var procedureList = VmSolution.Instance.GetAllProcedureList();
        var names = new List<string>(checked((int)procedureList.nNum));
        for (var index = 0; index < procedureList.nNum; index++)
        {
            var name = procedureList.astProcessInfo[index].strProcessName?.Trim();
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name!);
            }
        }

        return names;
    }

    private bool EnsureProcedureReady()
    {
        if (_solutionLoaded && _activeProcedure is not null)
        {
            return true;
        }

        SetStatus("请先加载方案并选择标定流程。", StatusKind.Error);
        return false;
    }

    private void CloseCurrentSolution()
    {
        StopActiveProcedureNoThrow();

        if (_solutionLoaded)
        {
            VmSolution.Instance?.CloseSolution();
        }

        _activeProcedure = null;
        ProcedureComboBox.ItemsSource = null;
        _solutionLoaded = false;
        UpdateCommandState();
    }

    private void CloseCurrentSolutionNoThrow()
    {
        StopActiveProcedureNoThrow();
        try
        {
            VmSolution.Instance?.CloseSolution();
        }
        catch
        {
        }

        _activeProcedure = null;
        ProcedureComboBox.ItemsSource = null;
        _solutionLoaded = false;
        UpdateCommandState();
    }

    private void StopActiveProcedureNoThrow()
    {
        if (_activeProcedure is null)
        {
            return;
        }

        try
        {
            _activeProcedure.ContinuousRunEnable = false;
        }
        catch
        {
            // Cleanup continues so the solution can still be released.
        }
    }

    private void SetBusy(bool busy)
    {
        LoadSolutionButton.IsEnabled = !busy;
        BrowseSolutionButton.IsEnabled = !busy;
        SolutionPathTextBox.IsEnabled = !busy;
        SolutionPasswordBox.IsEnabled = !busy;
        if (busy)
        {
            SaveSolutionButton.IsEnabled = false;
            ProcedureComboBox.IsEnabled = false;
            RunOnceButton.IsEnabled = false;
            ContinuousRunButton.IsEnabled = false;
            StopRunButton.IsEnabled = false;
            EditLockButton.IsEnabled = false;
        }
        else
        {
            UpdateCommandState();
        }
    }

    private void UpdateCommandState()
    {
        if (!_sdkAvailable)
        {
            BrowseSolutionButton.IsEnabled = false;
            SolutionPathTextBox.IsEnabled = false;
            SolutionPasswordBox.IsEnabled = false;
            LoadSolutionButton.IsEnabled = false;
            ProcedureComboBox.IsEnabled = false;
            SaveSolutionButton.IsEnabled = false;
            RunOnceButton.IsEnabled = false;
            ContinuousRunButton.IsEnabled = false;
            StopRunButton.IsEnabled = false;
            EditLockButton.IsEnabled = false;
            return;
        }

        var procedureReady = _solutionLoaded && _activeProcedure is not null;
        var continuousRunning = procedureReady && _activeProcedure!.ContinuousRunEnable;
        BrowseSolutionButton.IsEnabled = !continuousRunning;
        SolutionPathTextBox.IsEnabled = !continuousRunning;
        SolutionPasswordBox.IsEnabled = !continuousRunning;
        LoadSolutionButton.IsEnabled = !continuousRunning;
        ProcedureComboBox.IsEnabled = _solutionLoaded && !continuousRunning;
        SaveSolutionButton.IsEnabled = _solutionLoaded && !continuousRunning;
        RunOnceButton.IsEnabled = procedureReady && !continuousRunning;
        ContinuousRunButton.IsEnabled = procedureReady && !continuousRunning;
        StopRunButton.IsEnabled = continuousRunning;
        EditLockButton.IsEnabled = _solutionLoaded && !continuousRunning;
    }

    private void SetStatus(string message, StatusKind kind)
    {
        StatusTextBlock.Text = message;
        StatusIndicator.Fill = new SolidColorBrush(kind switch
        {
            StatusKind.Success => Color.FromRgb(57, 197, 107),
            StatusKind.Error => Color.FromRgb(217, 13, 22),
            StatusKind.Busy => Color.FromRgb(13, 110, 232),
            _ => Color.FromRgb(224, 162, 26)
        });
    }

    private static string FormatException(Exception exception)
    {
        var vmException = FindVmException(exception) ?? VmSolution.GetVmException(exception);
        return vmException is null
            ? exception.Message
            : $"{vmException.Message}（错误码 0x{vmException.errorCode:X8}）";
    }

    private static VmException? FindVmException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is VmException vmException)
            {
                return vmException;
            }
        }

        return null;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        StopActiveProcedureNoThrow();
        try
        {
            VmMainView.Dispose();
        }
        catch
        {
        }

        try
        {
            VmSolution.Instance?.CloseSolution();
        }
        catch
        {
        }

        try
        {
            VmSolution.Instance?.Dispose();
        }
        catch
        {
        }
    }

    private enum StatusKind
    {
        Ready,
        Busy,
        Success,
        Error
    }
}
