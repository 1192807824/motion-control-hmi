using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using VM.Core;
using VM.PlatformSDKCS;
using VMControls.Interface;

namespace VisionMasterHost;

public partial class MainWindow : Window
{
    private readonly VisionCalibrationSettings _settings = VisionCalibrationSettings.Load();
    private VmProcedure? _activeProcedure;
    private bool _solutionLoaded;
    private bool _closed;
    private bool _sdkAvailable = true;
    private bool _busy;

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
        VisionRenderControl.IsEnabled = false;
        ImagePlaceholder.Visibility = Visibility.Collapsed;
        UpdateCommandState();
        SetStatus(message, StatusKind.Error);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_sdkAvailable)
        {
            return;
        }

        try
        {
            VisionRenderControl.SetRenderToolbarVisible(true);
        }
        catch (Exception exception)
        {
            ReportSdkInitializationFailure(exception);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.SolutionPath))
        {
            return;
        }

        SolutionPathTextBox.Text = _settings.SolutionPath;
        if (!File.Exists(_settings.SolutionPath))
        {
            SetStatus("上次使用的方案文件不存在，请重新选择。", StatusKind.Error);
            return;
        }

        await LoadSolutionAsync(_settings.SolutionPath, autoRestore: true);
    }

    private async void ChooseSolution_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 VisionMaster 标定方案",
            Filter = "VisionMaster 方案 (*.sol)|*.sol|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = GetInitialSolutionDirectory()
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        SolutionPathTextBox.Text = dialog.FileName;
        await LoadSolutionAsync(dialog.FileName, autoRestore: false);
    }

    private async Task LoadSolutionAsync(string solutionPath, bool autoRestore)
    {
        if (!File.Exists(solutionPath))
        {
            SetStatus("请选择有效的 VisionMaster .sol 方案文件。", StatusKind.Error);
            return;
        }

        SetBusy(true);
        SetStatus(autoRestore ? "正在恢复上次标定方案…" : "正在加载标定方案…", StatusKind.Busy);
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.Background);

        var previousSolutionClosed = false;
        try
        {
            CloseCurrentSolution();
            previousSolutionClosed = true;
            VmSolution.Load(Path.GetFullPath(solutionPath), "");
            _solutionLoaded = true;

            var procedureNames = GetProcedureNames();
            if (procedureNames.Count == 0)
            {
                throw new InvalidOperationException("方案中没有可用流程。 ");
            }

            _settings.SolutionPath = Path.GetFullPath(solutionPath);
            SolutionPathTextBox.Text = _settings.SolutionPath;
            ProcedureComboBox.ItemsSource = procedureNames;
            ProcedureComboBox.SelectedItem = SelectPreferredProcedure(procedureNames);
            SaveSettingsNoThrow();
            UpdateCommandState();
            SetStatus(
                autoRestore
                    ? $"已自动加载：{Path.GetFileName(solutionPath)}"
                    : $"方案已加载：{Path.GetFileName(solutionPath)}",
                StatusKind.Success);
        }
        catch (Exception exception)
        {
            if (previousSolutionClosed)
            {
                CloseCurrentSolutionNoThrow();
            }

            SetStatus(
                $"{(autoRestore ? "自动加载" : "加载")}失败：{FormatException(exception)}",
                StatusKind.Error);
        }
        finally
        {
            SetBusy(false);
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
            _settings.ProcedureName = procedureName;
            PopulateImageSteps(procedureName, _activeProcedure);
            SaveSettingsNoThrow();
            UpdateCommandState();
        }
        catch (Exception exception)
        {
            _activeProcedure = null;
            ImageStepComboBox.ItemsSource = null;
            ClearRenderer();
            UpdateCommandState();
            SetStatus($"流程加载失败：{FormatException(exception)}", StatusKind.Error);
        }
    }

    private void PopulateImageSteps(string procedureName, VmProcedure procedure)
    {
        var options = GetImageStepOptions(procedureName, procedure);
        ImageStepComboBox.ItemsSource = options;
        ImageStepComboBox.SelectedItem =
            options.FirstOrDefault(option =>
                string.Equals(option.ModuleKey, _settings.ImageModuleKey, StringComparison.Ordinal))
            ?? options.FirstOrDefault(option => option.DisplayName.Contains("N点标定"))
            ?? options.FirstOrDefault(option => option.DisplayName.Contains("标定"))
            ?? options.LastOrDefault();
    }

    private static IReadOnlyList<VisionModuleOption> GetImageStepOptions(
        string procedureName,
        VmProcedure procedure)
    {
        var options = new List<VisionModuleOption>();
        var moduleList = procedure.GetProcedureModuleList();
        for (var index = 0; index < moduleList.nNum; index++)
        {
            var info = moduleList.astModuleInfo[index];
            var displayName = info.strDisplayName?.Trim() ?? "";
            var moduleName = info.strModuleName?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            var option = ResolveModuleOption(procedureName, displayName, moduleName);
            if (option is not null
                && options.All(existing =>
                    !string.Equals(existing.ModuleKey, option.ModuleKey, StringComparison.Ordinal)))
            {
                options.Add(option);
            }
        }

        if (options.Count == 0)
        {
            options.Add(new VisionModuleOption(
                $"{procedureName}（流程图像）",
                procedureName,
                procedure));
        }

        return options;
    }

    private static VisionModuleOption? ResolveModuleOption(
        string procedureName,
        string displayName,
        string moduleName)
    {
        var candidates = new[]
        {
            $"{procedureName}.{displayName}",
            $"{procedureName}.{moduleName}",
            moduleName,
            displayName
        };

        foreach (var candidate in candidates
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.Ordinal))
        {
            try
            {
                if (VmSolution.Instance[candidate] is IVmModule module)
                {
                    return new VisionModuleOption(displayName, candidate, module);
                }
            }
            catch
            {
                // Some SDK module names are type names rather than lookup paths.
            }
        }

        return null;
    }

    private void ImageStepComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_solutionLoaded || ImageStepComboBox.SelectedItem is not VisionModuleOption option)
        {
            return;
        }

        try
        {
            VisionRenderControl.ModuleSource = option.Module;
            try
            {
                VisionRenderControl.UpdateVMResultShow();
            }
            catch
            {
                // A newly loaded module may not have a render result until its first run.
            }

            ImagePlaceholder.Visibility = Visibility.Collapsed;
            _settings.ImageModuleKey = option.ModuleKey;
            SaveSettingsNoThrow();
            SetStatus($"当前图像：{ProcedureComboBox.SelectedItem} / {option.DisplayName}", StatusKind.Ready);
        }
        catch (Exception exception)
        {
            ClearRenderer();
            SetStatus($"图像步骤绑定失败：{FormatException(exception)}", StatusKind.Error);
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
            _activeProcedure!.Run(true);
            stopwatch.Stop();
            VisionRenderControl.UpdateVMResultShow();
            SetStatus(
                $"运行完成，用时 {stopwatch.Elapsed.TotalMilliseconds:0.0} ms",
                StatusKind.Success);
        }
        catch (Exception exception)
        {
            SetStatus($"运行失败：{FormatException(exception)}", StatusKind.Error);
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
            SetStatus("流程正在连续运行", StatusKind.Success);
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
            SetStatus("流程已停止", StatusKind.Ready);
        }
        catch (Exception exception)
        {
            SetStatus($"停止流程失败：{FormatException(exception)}", StatusKind.Error);
        }
    }

    private string SelectPreferredProcedure(IReadOnlyList<string> procedureNames)
    {
        return procedureNames.FirstOrDefault(name =>
                   string.Equals(name, _settings.ProcedureName, StringComparison.Ordinal))
               ?? procedureNames.FirstOrDefault(name => name.Contains("标定"))
               ?? procedureNames[0];
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

        SetStatus("请先选择方案、流程和图像步骤。", StatusKind.Error);
        return false;
    }

    private void CloseCurrentSolution()
    {
        StopActiveProcedureNoThrow();
        ClearRenderer();

        if (_solutionLoaded)
        {
            VmSolution.Instance?.CloseSolution();
        }

        _activeProcedure = null;
        _solutionLoaded = false;
        ProcedureComboBox.ItemsSource = null;
        ImageStepComboBox.ItemsSource = null;
        UpdateCommandState();
    }

    private void CloseCurrentSolutionNoThrow()
    {
        StopActiveProcedureNoThrow();
        ClearRenderer();
        try
        {
            VmSolution.Instance?.CloseSolution();
        }
        catch
        {
        }

        _activeProcedure = null;
        _solutionLoaded = false;
        ProcedureComboBox.ItemsSource = null;
        ImageStepComboBox.ItemsSource = null;
        UpdateCommandState();
    }

    private void ClearRenderer()
    {
        try
        {
            VisionRenderControl.ModuleSource = null;
            VisionRenderControl.ClearDisplayView();
        }
        catch
        {
        }

        if (_sdkAvailable)
        {
            ImagePlaceholder.Visibility = Visibility.Visible;
        }
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
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateCommandState();
    }

    private void UpdateCommandState()
    {
        if (!_sdkAvailable)
        {
            CommandBar.IsEnabled = false;
            return;
        }

        CommandBar.IsEnabled = true;
        var procedureReady = _solutionLoaded && _activeProcedure is not null;
        var continuousRunning = procedureReady && _activeProcedure!.ContinuousRunEnable;
        ChooseSolutionButton.IsEnabled = !_busy && !continuousRunning;
        ProcedureComboBox.IsEnabled = !_busy && _solutionLoaded && !continuousRunning;
        ImageStepComboBox.IsEnabled = !_busy && procedureReady && !continuousRunning;
        RunOnceButton.IsEnabled = !_busy && procedureReady && !continuousRunning;
        ContinuousRunButton.IsEnabled = !_busy && procedureReady && !continuousRunning;
        StopRunButton.IsEnabled = !_busy && continuousRunning;
    }

    private string? GetInitialSolutionDirectory()
    {
        if (string.IsNullOrWhiteSpace(_settings.SolutionPath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(_settings.SolutionPath);
        return Directory.Exists(directory) ? directory : null;
    }

    private void SaveSettingsNoThrow()
    {
        try
        {
            _settings.Save();
        }
        catch
        {
            // The vision workflow remains usable even if local preferences cannot be persisted.
        }
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
            VisionRenderControl.Dispose();
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

    private sealed class VisionModuleOption
    {
        public VisionModuleOption(string displayName, string moduleKey, IVmModule module)
        {
            DisplayName = displayName;
            ModuleKey = moduleKey;
            Module = module;
        }

        public string DisplayName { get; }

        public string ModuleKey { get; }

        public IVmModule Module { get; }
    }

    private enum StatusKind
    {
        Ready,
        Busy,
        Success,
        Error
    }
}
