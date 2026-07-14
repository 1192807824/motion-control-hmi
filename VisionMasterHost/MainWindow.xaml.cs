using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IMVSNPointCalibModuCs;
using Microsoft.Win32;
using VM.Core;
using VM.PlatformSDKCS;
using VMControls.Interface;

namespace VisionMasterHost;

public partial class MainWindow : Window
{
    private readonly VisionCalibrationSettings _settings = VisionCalibrationSettings.Load();
    private readonly string? _commandPipeName;
    private readonly CancellationTokenSource _commandPipeCancellation = new();
    private readonly object _commandPipeSync = new();
    private VmProcedure? _activeProcedure;
    private NamedPipeServerStream? _activeCommandPipe;
    private Task? _commandPipeTask;
    private VisionCalibrationSession? _calibrationSession;
    private bool _solutionLoaded;
    private bool _closed;
    private bool _sdkAvailable = true;
    private bool _busy;

    public MainWindow(bool embedded, string? commandPipeName = null)
    {
        InitializeComponent();
        _commandPipeName = string.IsNullOrWhiteSpace(commandPipeName) ? null : commandPipeName;
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
        StartCommandPipeServer();
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

    private void StartCommandPipeServer()
    {
        if (_commandPipeTask is not null || string.IsNullOrWhiteSpace(_commandPipeName))
        {
            return;
        }

        _commandPipeTask = RunCommandPipeServerAsync(_commandPipeCancellation.Token);
    }

    private async Task RunCommandPipeServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _commandPipeName!,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                lock (_commandPipeSync)
                {
                    _activeCommandPipe = pipe;
                }

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using (var reader = new StreamReader(
                           pipe,
                           new UTF8Encoding(false),
                           false,
                           1024,
                           leaveOpen: true))
                using (var writer = new StreamWriter(
                           pipe,
                           new UTF8Encoding(false),
                           1024,
                           leaveOpen: true)
                       {
                           AutoFlush = true
                       })
                {
                    var command = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (command is null)
                    {
                        continue;
                    }

                    var response = await Dispatcher.InvokeAsync(
                        () => ExecuteCalibrationCommandSafely(command));
                    await writer.WriteLineAsync(response).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // The client may disconnect between a command and its reply; accept the next command.
            }
            finally
            {
                lock (_commandPipeSync)
                {
                    if (ReferenceEquals(_activeCommandPipe, pipe))
                    {
                        _activeCommandPipe = null;
                    }
                }

                pipe?.Dispose();
            }
        }
    }

    private string ExecuteCalibrationCommandSafely(string command)
    {
        try
        {
            return EncodePipeResponse(success: true, ExecuteCalibrationCommand(command));
        }
        catch (Exception exception)
        {
            return EncodePipeResponse(success: false, FormatException(exception));
        }
    }

    private string ExecuteCalibrationCommand(string command)
    {
        var parts = command.Split('\t');
        return parts[0] switch
        {
            "PREPARE" => PrepareNinePointCalibration(parts),
            "CAPTURE" => CaptureNinePointCalibration(parts),
            "COMPLETE" => CompleteNinePointCalibration(),
            "ABORT" => AbortNinePointCalibration(),
            _ => throw new InvalidOperationException($"不支持的视觉标定命令：{parts[0]}")
        };
    }

    private string PrepareNinePointCalibration(IReadOnlyList<string> parts)
    {
        if (parts.Count != 6)
        {
            throw new InvalidDataException("准备九点标定的参数数量不正确。");
        }

        if (!_solutionLoaded || _activeProcedure is null || ProcedureComboBox.SelectedItem is not string procedureName)
        {
            throw new InvalidOperationException("请先在视觉组件中加载标定方案并选择标定流程。");
        }

        if (_activeProcedure.ContinuousRunEnable)
        {
            throw new InvalidOperationException("请先停止 VisionMaster 连续运行。");
        }

        var centerX = ParseFiniteDouble(parts[1], "基准点X");
        var centerY = ParseFiniteDouble(parts[2], "基准点Y");
        var offsetX = ParseFiniteNonZeroDouble(parts[3], "偏移X");
        var offsetY = ParseFiniteNonZeroDouble(parts[4], "偏移Y");
        var xFirst = parts[5] switch
        {
            "X" => true,
            "Y" => false,
            _ => throw new InvalidDataException("移动优先参数只能是 X 或 Y。")
        };

        var nPointModule = ResolveNPointCalibrationModule(procedureName);
        var parameters = nPointModule.ModuParams;
        parameters.CalibPointGet = NPointCalibParam.CalibPointGetEnum.TriggerAcquisition;
        parameters.CalibPointTotalNum = 9;
        parameters.RotPointTotalNum = 0;
        parameters.TeachEnable = false;
        parameters.BasePointX = centerX;
        parameters.BasePointY = centerY;
        parameters.MoveAlignX = offsetX;
        parameters.MoveAlignY = offsetY;
        parameters.MoveFirstType = xFirst
            ? NPointCalibParam.MoveFirstTypeEnum.XFirst
            : NPointCalibParam.MoveFirstTypeEnum.YFirst;
        parameters.ChangeDirectionMoveTime = 3;
        parameters.UseRelativeCoordinates = false;

        var calibrationDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ControlHub",
            "Calibration");
        Directory.CreateDirectory(calibrationDirectory);
        var calibrationPath = Path.Combine(calibrationDirectory, "first-xy-calibration.xml");
        parameters.CalibPathName = calibrationPath;
        parameters.RefreshFileEnable = true;
        parameters.DoClearPoint();

        _calibrationSession = new VisionCalibrationSession(nPointModule, calibrationPath);
        VisionRenderControl.ModuleSource = nPointModule;
        SetBusy(true);
        SetStatus(
            $"九点标定已准备：基准({centerX:0.####}, {centerY:0.####})，" +
            $"偏移({offsetX:0.####}, {offsetY:0.####})，{(xFirst ? "X" : "Y")}优先",
            StatusKind.Busy);
        return "VisionMaster 九点参数已写入，旧标定点已清空。";
    }

    private string CaptureNinePointCalibration(IReadOnlyList<string> parts)
    {
        if (parts.Count != 2 ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pointNumber) ||
            pointNumber is < 1 or > 9)
        {
            throw new InvalidDataException("采集点号必须在 1 到 9 之间。");
        }

        var session = _calibrationSession
            ?? throw new InvalidOperationException("尚未准备九点标定参数。");
        if (pointNumber != session.NextPointNumber)
        {
            throw new InvalidOperationException(
                $"采集顺序错误：当前应采集第 {session.NextPointNumber} 点，不是第 {pointNumber} 点。");
        }

        if (_activeProcedure is null)
        {
            throw new InvalidOperationException("当前 VisionMaster 流程已失效。");
        }

        var stopwatch = Stopwatch.StartNew();
        _activeProcedure.Run(true);
        stopwatch.Stop();
        VisionRenderControl.UpdateVMResultShow();

        var result = session.Module.ModuResult;
        if (result.ModuStatus != 1)
        {
            throw new InvalidOperationException(
                $"第 {pointNumber} 点 N点标定模块返回 NG，请检查相机取像和圆查找结果。");
        }

        session.NextPointNumber++;
        SetStatus(
            $"第 {pointNumber}/9 点已采集，流程用时 {stopwatch.Elapsed.TotalMilliseconds:0.0} ms",
            StatusKind.Busy);
        return $"第 {pointNumber}/9 点 VisionMaster 流程执行完成。";
    }

    private string CompleteNinePointCalibration()
    {
        var session = _calibrationSession
            ?? throw new InvalidOperationException("尚未准备九点标定参数。");
        if (session.NextPointNumber != 10)
        {
            throw new InvalidOperationException(
                $"九点数据尚未采集完成，当前已完成 {session.NextPointNumber - 1}/9 点。");
        }

        var result = session.Module.ModuResult;
        if (result.ModuStatus != 1 || result.CalibStatus != 1)
        {
            throw new InvalidOperationException("九点已采集，但 VisionMaster 未生成有效标定结果。");
        }

        if (result.CalibErrStatus != 0)
        {
            throw new InvalidOperationException("标定矩阵已生成，但标定误差评估未通过。");
        }

        session.Module.ModuParams.DoSaveFile(session.CalibrationPath);
        var message =
            $"九点标定成功，像素精度 {result.PixelPrecision:0.######}，" +
            $"标定文件：{session.CalibrationPath}";
        _calibrationSession = null;
        SetBusy(false);
        SetStatus(message, StatusKind.Success);
        return message;
    }

    private string AbortNinePointCalibration()
    {
        if (_calibrationSession is { } session)
        {
            session.Module.ModuParams.DoClearPoint();
            _calibrationSession = null;
        }

        SetBusy(false);
        SetStatus("九点标定已取消，本次未完成的标定点已清空。", StatusKind.Ready);
        return "VisionMaster 九点标定已取消。";
    }

    private static double ParseFiniteDouble(string value, string name)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ||
            double.IsNaN(result) ||
            double.IsInfinity(result))
        {
            throw new InvalidDataException($"{name}必须是有效数值。");
        }

        return result;
    }

    private static double ParseFiniteNonZeroDouble(string value, string name)
    {
        var result = ParseFiniteDouble(value, name);
        if (Math.Abs(result) <= double.Epsilon)
        {
            throw new InvalidDataException($"{name}不能为 0。");
        }

        return result;
    }

    private static string EncodePipeResponse(bool success, string message)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(message ?? ""));
        return $"{(success ? "OK" : "ERR")}\t{encoded}";
    }

    private static IMVSNPointCalibModuTool ResolveNPointCalibrationModule(string procedureName)
    {
        var procedure = VmSolution.Instance[procedureName] as VmProcedure
            ?? throw new InvalidOperationException($"方案中未找到流程“{procedureName}”。");
        var moduleList = procedure.GetProcedureModuleList();
        for (var index = 0; index < moduleList.nNum; index++)
        {
            var info = moduleList.astModuleInfo[index];
            var displayName = info.strDisplayName?.Trim() ?? "";
            var moduleName = info.strModuleName?.Trim() ?? "";
            if (!string.Equals(moduleName, "IMVSNPointCalibModu", StringComparison.Ordinal) &&
                !displayName.Contains("N点标定"))
            {
                continue;
            }

            foreach (var candidate in new[]
                     {
                         $"{procedureName}.{displayName}",
                         $"{procedureName}.{moduleName}",
                         displayName,
                         moduleName
                     }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
            {
                try
                {
                    if (VmSolution.Instance[candidate] is IMVSNPointCalibModuTool module)
                    {
                        return module;
                    }
                }
                catch
                {
                }
            }
        }

        throw new InvalidOperationException("当前流程中未找到“N点标定”模块。");
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
        _commandPipeCancellation.Cancel();
        lock (_commandPipeSync)
        {
            try
            {
                _activeCommandPipe?.Dispose();
            }
            catch
            {
            }

            _activeCommandPipe = null;
        }

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

    private sealed class VisionCalibrationSession
    {
        public VisionCalibrationSession(
            IMVSNPointCalibModuTool module,
            string calibrationPath)
        {
            Module = module;
            CalibrationPath = calibrationPath;
        }

        public IMVSNPointCalibModuTool Module { get; }

        public string CalibrationPath { get; }

        public int NextPointNumber { get; set; } = 1;
    }

    private enum StatusKind
    {
        Ready,
        Busy,
        Success,
        Error
    }
}
