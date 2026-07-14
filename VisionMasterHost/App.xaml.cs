using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using VM.PlatformSDKCS;

namespace VisionMasterHost;

public partial class App : Application
{
    private CancellationTokenSource? _parentProcessMonitorCancellation;
    private Task? _parentProcessMonitorTask;
    private bool _visionMasterInitializationPending;

    public App()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            base.OnStartup(e);
            var embedded = e.Args.Any(
                argument => string.Equals(argument, "--embedded", StringComparison.OrdinalIgnoreCase));
            var parentProcessId = ParseParentProcessId(e.Args);
            var window = new MainWindow(embedded);
            MainWindow = window;
            _visionMasterInitializationPending = true;
            window.ContentRendered += MainWindow_ContentRendered;
            window.Show();

            if (parentProcessId.HasValue)
            {
                StartParentProcessMonitor(parentProcessId.Value);
            }
        }
        catch (Exception exception)
        {
            WriteCrashLog(exception);
            throw;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StopParentProcessMonitor();
        base.OnExit(e);
    }

    private void MainWindow_ContentRendered(object? sender, EventArgs e)
    {
        _visionMasterInitializationPending = false;
        if (sender is Window window)
        {
            window.ContentRendered -= MainWindow_ContentRendered;
        }
    }

    private void App_DispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        if (_visionMasterInitializationPending
            && ContainsVmException(e.Exception)
            && MainWindow is MainWindow mainWindow)
        {
            mainWindow.ReportSdkInitializationFailure(e.Exception);
            e.Handled = true;
        }
    }

    private static bool ContainsVmException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is VmException)
            {
                return true;
            }
        }

        return false;
    }

    private static int? ParseParentProcessId(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], "--parent-pid", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= arguments.Count
                || !int.TryParse(arguments[index + 1], out var parentProcessId)
                || parentProcessId <= 0)
            {
                throw new ArgumentException("--parent-pid requires a positive process ID.");
            }

            return parentProcessId;
        }

        return null;
    }

    private void StartParentProcessMonitor(int parentProcessId)
    {
        StopParentProcessMonitor();
        var cancellation = new CancellationTokenSource();
        _parentProcessMonitorCancellation = cancellation;
        _parentProcessMonitorTask = MonitorParentProcessAsync(parentProcessId, cancellation.Token);
    }

    private void StopParentProcessMonitor()
    {
        var cancellation = Interlocked.Exchange(ref _parentProcessMonitorCancellation, null);
        cancellation?.Cancel();
        _parentProcessMonitorTask = null;
    }

    private async Task MonitorParentProcessAsync(int parentProcessId, CancellationToken cancellationToken)
    {
        try
        {
            using var parentProcess = Process.GetProcessById(parentProcessId);
            while (!cancellationToken.IsCancellationRequested)
            {
                var parentExited = false;
                try
                {
                    parentExited = parentProcess.HasExited;
                }
                catch (InvalidOperationException)
                {
                    parentExited = true;
                }

                if (parentExited)
                {
                    RequestShutdown(cancellationToken);
                    return;
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ArgumentException)
        {
            RequestShutdown(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void RequestShutdown(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Send,
            new Action(() =>
            {
                if (!cancellationToken.IsCancellationRequested && !Dispatcher.HasShutdownStarted)
                {
                    Shutdown();
                }
            }));
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
    }

    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VisionMasterHost.crash.log"),
                $"{DateTime.Now:O}{Environment.NewLine}{FormatException(exception)}");
        }
        catch
        {
        }
    }

    private static string FormatException(Exception exception)
    {
        var builder = new StringBuilder();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            builder.AppendLine(current.ToString());
            if (current is VmException vmException)
            {
                builder.AppendLine($"VisionMaster 错误码：0x{vmException.errorCode:X8}");
            }
        }

        return builder.ToString();
    }
}
