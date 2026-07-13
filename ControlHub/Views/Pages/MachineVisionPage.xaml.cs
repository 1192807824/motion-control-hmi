using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ControlHub.Services.Motion;
using ControlHub.Services.Vision;
using ControlHub.ViewModels;
using Microsoft.Win32;

namespace ControlHub.Views.Pages;

public partial class MachineVisionPage : UserControl
{
    private readonly IVisionService _visionService = new VisionMasterVisionService();
    private readonly ObservableCollection<NinePointCalibrationSample> _calibrationSamples = [];
    private CancellationTokenSource? _calibrationCancellation;
    private bool _disposed;

    public MachineVisionPage()
    {
        InitializeComponent();
        CalibrationSamplesGrid.ItemsSource = _calibrationSamples;
        VisionRenderControl.SetRenderToolbarVisible(true);
    }

    public MotionControlPage? MotionController { get; set; }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public void PrepareForClosing()
    {
        _calibrationCancellation?.Cancel();
    }

    public void Shutdown()
    {
        PrepareForClosing();
        DisposeVisionService();
    }

    private void MachineVisionPage_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshAxisOptions();
    }

    private void BrowseVisionSolution_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择 VisionMaster 方案文件",
            Filter = "VisionMaster 方案 (*.sol)|*.sol|所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        viewModel.VisionSettings.SolutionPath = dialog.FileName;
        VisionSolutionPathTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        if (string.IsNullOrWhiteSpace(CalibrationFilePathTextBox.Text))
        {
            CalibrationFilePathTextBox.Text = Path.Combine(
                Path.GetDirectoryName(dialog.FileName) ?? Environment.CurrentDirectory,
                $"NinePointCalibration_{DateTime.Now:yyyyMMdd_HHmmss}.xml");
        }

        AddVisionLog($"选择方案：{dialog.FileName}");
    }

    private void BrowseCalibrationFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存 VisionMaster 标定文件",
            Filter = "VisionMaster 标定文件 (*.xml)|*.xml|所有文件 (*.*)|*.*",
            DefaultExt = ".xml",
            AddExtension = true,
            FileName = $"NinePointCalibration_{DateTime.Now:yyyyMMdd_HHmmss}.xml"
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            CalibrationFilePathTextBox.Text = dialog.FileName;
            AddVisionLog($"标定文件：{dialog.FileName}");
        }
    }

    private void LoadVisionSolution_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        try
        {
            ApplyVisionResult(_visionService.LoadSolution(viewModel.VisionSettings));
            BindVisionRenderer();
        }
        catch (Exception exception)
        {
            ReportVisionError("加载 VisionMaster 方案失败", exception);
        }
    }

    private void RunVisionOnce_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        try
        {
            ApplyVisionResult(_visionService.RunOnce(viewModel.VisionSettings));
            RefreshVisionRenderer();
        }
        catch (Exception exception)
        {
            ReportVisionError("VisionMaster 测试取点失败", exception);
        }
    }

    private async void StartCalibration_Click(object sender, RoutedEventArgs e)
    {
        if (_calibrationCancellation is not null || ViewModel is not { } viewModel)
        {
            return;
        }

        try
        {
            if (MotionController is null)
            {
                throw new InvalidOperationException("视觉页尚未连接运动控制页。 ");
            }

            RefreshAxisOptions();
            if (XAxisComboBox.SelectedItem is not CalibrationAxisOption xAxis ||
                YAxisComboBox.SelectedItem is not CalibrationAxisOption yAxis)
            {
                throw new InvalidOperationException("请先连接运动控制卡并选择 X、Y 轴。 ");
            }

            var stepX = ParseRequiredDouble(StepXTextBox.Text, "X 每步距离");
            var stepY = ParseRequiredDouble(StepYTextBox.Text, "Y 每步距离");
            var velocity = ParseRequiredDouble(VelocityTextBox.Text, "移动速度");
            var settleMilliseconds = ParseRequiredInt(SettleTextBox.Text, "稳定等待时间");
            var filePath = EnsureCalibrationFilePath(viewModel.VisionSettings);

            if (!_visionService.IsLoaded)
            {
                ApplyVisionResult(_visionService.LoadSolution(viewModel.VisionSettings));
                BindVisionRenderer();
            }

            var confirmation = MessageBox.Show(
                Window.GetWindow(this),
                $"即将执行九点标定：\n\n" +
                $"X：轴 {xAxis.HardwareAxisNo + 1}，每步 {stepX:0.###}\n" +
                $"Y：轴 {yAxis.HardwareAxisNo + 1}，每步 {stepY:0.###}\n" +
                $"速度：{velocity:0.###}\n\n" +
                "请确认 3×3 行程范围内无人员、治具或限位干涉。",
                "确认开始九点标定",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.OK)
            {
                return;
            }

            _calibrationSamples.Clear();
            _calibrationCancellation = new CancellationTokenSource();
            SetCalibrationRunning(true);
            viewModel.VisionStatusText = "九点标定运行中";
            AddVisionLog($"开始九点标定：X=轴 {xAxis.HardwareAxisNo + 1}，Y=轴 {yAxis.HardwareAxisNo + 1}");

            var request = new NinePointMotionRequest(
                xAxis.HardwareAxisNo,
                yAxis.HardwareAxisNo,
                stepX,
                stepY,
                velocity,
                PositionTolerance: 0.01,
                MoveTimeoutMilliseconds: 30_000,
                SettleMilliseconds: settleMilliseconds);
            var progress = new Progress<NinePointMotionProgress>(UpdateCalibrationProgress);

            await MotionController.RunNinePointCalibrationAsync(
                request,
                (position, cancellationToken) => CaptureCalibrationPointAsync(
                    viewModel.VisionSettings,
                    position,
                    cancellationToken),
                progress,
                _calibrationCancellation.Token);

            var result = _visionService.GenerateNinePointCalibrationFile(
                viewModel.VisionSettings,
                _calibrationSamples,
                filePath);
            CalibrationProgressBar.Value = 9;
            CalibrationProgressText.Text = "9/9 完成，标定文件已生成";
            viewModel.VisionStatusText = $"标定完成：{result.FilePath}";
            AddVisionLog(
                $"标定文件生成成功；像素精度 {result.PixelPrecision:0.######}，" +
                $"世界误差 {result.TranslationWorldError:0.######}");
        }
        catch (OperationCanceledException)
        {
            if (ViewModel is { } cancelledViewModel)
            {
                cancelledViewModel.VisionStatusText = "九点标定已取消，所选轴已停止";
            }

            CalibrationProgressText.Text = "标定已取消";
            AddVisionLog("九点标定已由操作员取消");
        }
        catch (Exception exception)
        {
            ReportVisionError("九点标定失败", exception, showDialog: true);
            CalibrationProgressText.Text = "标定失败，已停止所选轴";
        }
        finally
        {
            _calibrationCancellation?.Dispose();
            _calibrationCancellation = null;
            SetCalibrationRunning(false);
        }
    }

    private void CancelCalibration_Click(object sender, RoutedEventArgs e)
    {
        if (_calibrationCancellation is null)
        {
            return;
        }

        CalibrationProgressText.Text = "正在停止标定运动…";
        _calibrationCancellation.Cancel();
    }

    private Task CaptureCalibrationPointAsync(
        VisionMasterSettings settings,
        NinePointMotionPosition position,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var measurement = _visionService.CapturePoint(settings);
        cancellationToken.ThrowIfCancellationRequested();

        _calibrationSamples.Add(new NinePointCalibrationSample
        {
            Index = position.Index,
            TargetMachineX = position.TargetX,
            TargetMachineY = position.TargetY,
            ActualMachineX = position.ActualX,
            ActualMachineY = position.ActualY,
            ImageX = measurement.ImageX,
            ImageY = measurement.ImageY
        });

        if (ViewModel is { } viewModel)
        {
            viewModel.VisionRunTimeText = $"{measurement.RunTimeMs:0.0} ms";
        }

        RefreshVisionRenderer();
        AddVisionLog(
            $"点 {position.Index}/9：机械 ({position.ActualX:0.###}, {position.ActualY:0.###})，" +
            $"图像 ({measurement.ImageX:0.###}, {measurement.ImageY:0.###})");
        return Task.CompletedTask;
    }

    private void RefreshAxisOptions()
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var previousX = XAxisComboBox.SelectedValue as int?;
        var previousY = YAxisComboBox.SelectedValue as int?;
        var options = viewModel.Axes
            .Where(axis => axis.IsAvailable)
            .OrderBy(axis => axis.HardwareAxisNo)
            .Select(axis => new CalibrationAxisOption(
                axis.HardwareAxisNo,
                $"轴 {axis.HardwareAxisNo + 1}（{axis.Name}）"))
            .ToList();

        XAxisComboBox.ItemsSource = options;
        YAxisComboBox.ItemsSource = options;
        XAxisComboBox.SelectedValue = previousX ?? options.ElementAtOrDefault(0)?.HardwareAxisNo;
        YAxisComboBox.SelectedValue = previousY ?? options.ElementAtOrDefault(1)?.HardwareAxisNo;
    }

    private void BindVisionRenderer()
    {
        VisionRenderControl.ModuleSource = _visionService.RenderModuleSource;
        RefreshVisionRenderer();
    }

    private void RefreshVisionRenderer()
    {
        if (_visionService.RenderModuleSource is not null)
        {
            VisionRenderControl.UpdateVMResultShow();
        }
    }

    private void ApplyVisionResult(VisionRunResult result)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.VisionStatusText = result.IsOk ? result.Message : $"警告：{result.Message}";
        viewModel.VisionRunTimeText = result.RunTimeMs <= 0 ? "--" : $"{result.RunTimeMs:0.0} ms";
        AddVisionLog(result.Message);
    }

    private void UpdateCalibrationProgress(NinePointMotionProgress progress)
    {
        CalibrationProgressBar.Value = progress.CompletedPoints;
        CalibrationProgressText.Text = progress.Message;
    }

    private void SetCalibrationRunning(bool running)
    {
        StartCalibrationButton.IsEnabled = !running;
        CancelCalibrationButton.IsEnabled = running;
        XAxisComboBox.IsEnabled = !running;
        YAxisComboBox.IsEnabled = !running;
        StepXTextBox.IsEnabled = !running;
        StepYTextBox.IsEnabled = !running;
        VelocityTextBox.IsEnabled = !running;
        SettleTextBox.IsEnabled = !running;
    }

    private string EnsureCalibrationFilePath(VisionMasterSettings settings)
    {
        var filePath = CalibrationFilePathTextBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(filePath))
        {
            var directory = Path.GetDirectoryName(settings.SolutionPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("请选择标定文件保存位置。 ");
            }

            filePath = Path.Combine(directory, $"NinePointCalibration_{DateTime.Now:yyyyMMdd_HHmmss}.xml");
            CalibrationFilePathTextBox.Text = filePath;
        }

        return filePath;
    }

    private static double ParseRequiredDouble(string text, string fieldName)
    {
        if ((!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) &&
             !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) ||
            !double.IsFinite(value))
        {
            throw new InvalidOperationException($"{fieldName}不是有效数值。 ");
        }

        return value;
    }

    private static int ParseRequiredInt(string text, string fieldName)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) || value < 0)
        {
            throw new InvalidOperationException($"{fieldName}必须是大于或等于 0 的整数。 ");
        }

        return value;
    }

    private void ReportVisionError(string title, Exception exception, bool showDialog = false)
    {
        var message = $"{title}：{exception.Message}";
        if (ViewModel is { } viewModel)
        {
            viewModel.VisionStatusText = message;
        }

        AddVisionLog(message);
        if (showDialog)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                message,
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void AddVisionLog(string message)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.VisionLogs.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (viewModel.VisionLogs.Count > 60)
        {
            viewModel.VisionLogs.RemoveAt(viewModel.VisionLogs.Count - 1);
        }
    }

    private void MachineVisionPage_Unloaded(object sender, RoutedEventArgs e)
    {
        Shutdown();
    }

    private void DisposeVisionService()
    {
        if (_disposed)
        {
            return;
        }

        _visionService.Dispose();
        _disposed = true;
    }

    private sealed record CalibrationAxisOption(int HardwareAxisNo, string DisplayName);
}
