using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using ControlHub.Services.Vision;
using ControlHub.ViewModels;

namespace ControlHub.Views.Pages;

public partial class MachineVisionPage : UserControl
{
    private readonly IVisionService _visionService = new VisionMasterVisionService();
    private readonly DispatcherTimer _continuousRunTimer;
    private bool _disposed;

    public MachineVisionPage()
    {
        InitializeComponent();

        _continuousRunTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _continuousRunTimer.Tick += (_, _) =>
        {
            if (ViewModel is { } viewModel)
            {
                ApplyVisionResult(_visionService.RunOnce(viewModel.VisionSettings));
            }
        };
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void BrowseVisionSolution_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择VisionMaster方案文件",
            Filter = "VisionMaster方案 (*.sol)|*.sol|VisionMaster流程 (*.prc)|*.prc|所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        viewModel.VisionSettings.SolutionPath = dialog.FileName;
        VisionSolutionPathTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        AddVisionLog($"选择方案：{dialog.FileName}");
    }

    private void LoadVisionSolution_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            ApplyVisionResult(_visionService.LoadSolution(viewModel.VisionSettings));
        }
    }

    private void RunVisionOnce_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            ApplyVisionResult(_visionService.RunOnce(viewModel.VisionSettings));
        }
    }

    private void StartVisionContinuous_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var interval = Math.Max(50, viewModel.VisionSettings.ContinuousRunIntervalMs);
        _continuousRunTimer.Interval = TimeSpan.FromMilliseconds(interval);
        _visionService.StartContinuous(viewModel.VisionSettings);
        _continuousRunTimer.Start();
        viewModel.VisionStatusText = "连续运行中";
        AddVisionLog($"连续运行启动，间隔 {interval} ms");
    }

    private void StopVisionContinuous_Click(object sender, RoutedEventArgs e)
    {
        StopContinuousRun();

        if (ViewModel is { } viewModel)
        {
            viewModel.VisionStatusText = "已停止";
            AddVisionLog("连续运行停止");
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
        viewModel.VisionPreviewImage = result.PreviewImage;
        viewModel.VisionOutputs.Clear();

        foreach (var output in result.Outputs)
        {
            viewModel.VisionOutputs.Add(output);
        }

        AddVisionLog(result.Message);
    }

    private void AddVisionLog(string message)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.VisionLogs.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (viewModel.VisionLogs.Count > 80)
        {
            viewModel.VisionLogs.RemoveAt(viewModel.VisionLogs.Count - 1);
        }
    }

    private void MachineVisionPage_Unloaded(object sender, RoutedEventArgs e)
    {
        DisposeVisionService();
    }

    private void StopContinuousRun()
    {
        _continuousRunTimer.Stop();
        _visionService.StopContinuous();
    }

    private void DisposeVisionService()
    {
        if (_disposed)
        {
            return;
        }

        StopContinuousRun();
        _visionService.Dispose();
        _disposed = true;
    }
}
