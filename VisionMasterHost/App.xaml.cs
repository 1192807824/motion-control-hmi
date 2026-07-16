using System.Diagnostics;
using System.Globalization;
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
            var liveOnly = e.Args.Any(
                argument => string.Equals(argument, "--live-only", StringComparison.OrdinalIgnoreCase));
            var parentProcessId = ParseParentProcessId(e.Args);
            var pipeName = ParseArgumentValue(e.Args, "--pipe-name");
            var eventPipeName = ParseArgumentValue(e.Args, "--event-pipe-name");
            if (liveOnly)
            {
                var liveEventPipeName = ParseArgumentValue(e.Args, "--live-event-pipe-name");
                var liveCommandPipeName = ParseArgumentValue(e.Args, "--live-command-pipe-name");
                var liveParentWindow = ParseWindowHandle(e.Args, "--live-parent-hwnd");
                var liveWindow = new RealtimeWindow(
                    liveEventPipeName,
                    liveCommandPipeName,
                    liveParentWindow);
                MainWindow = liveWindow;
                if (!liveWindow.PrepareForDisplay())
                {
                    Shutdown(-1);
                    return;
                }

                liveWindow.Show();
            }
            else
            {
                var window = new MainWindow(embedded, pipeName, eventPipeName);
                MainWindow = window;
                window.Show();
            }

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

    private void App_DispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        // VM 控件的 Loaded/Rendered 回调可能发生在 ContentRendered 之后。
        // 对 SDK 初始化或授权异常统一转为宿主界面的可见错误，不能让嵌入进程崩溃。
        if (ContainsVmException(e.Exception)
            && MainWindow is MainWindow mainWindow)
        {
            mainWindow.ReportSdkInitializationFailure(e.Exception);
            e.Handled = true;
        }
        else if (ContainsVmException(e.Exception)
                 && MainWindow is RealtimeWindow realtimeWindow)
        {
            realtimeWindow.ReportSdkInitializationFailure(e.Exception);
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

    private static string? ParseArgumentValue(IReadOnlyList<string> arguments, string optionName)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], optionName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index + 1]))
            {
                throw new ArgumentException($"{optionName} requires a value.");
            }

            return arguments[index + 1].Trim();
        }

        return null;
    }

    private static IntPtr ParseWindowHandle(IReadOnlyList<string> arguments, string optionName)
    {
        var value = ParseArgumentValue(arguments, optionName);
        if (value is null)
        {
            return IntPtr.Zero;
        }

        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var handle)
            || handle <= 0)
        {
            throw new ArgumentException($"{optionName} requires a positive window handle.");
        }

        return new IntPtr(handle);
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
