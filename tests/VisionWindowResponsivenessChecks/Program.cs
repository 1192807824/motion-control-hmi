using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using ControlHub.Services.Vision;
using ControlHub.Services.Motion;

internal static class Program
{
    private const int BlockMessage = 0x8000 + 0x342;
    private const int CountMessage = 0x8000 + 0x343;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "receiver")
        {
            RunReceiver(args[1]);
            return 0;
        }

        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var exitCode = 0;
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await CheckAsync();
                await CheckDiagnosticsAsync();
                Console.WriteLine("PASS: blocked vision process does not block dispatcher continuations; duplicate layouts suppressed; latest parent size applied; failed posts retried. No camera or motion card opened.");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                exitCode = 1;
            }
            finally
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        }));
        Dispatcher.Run();
        return exitCode;
    }

    private static void RunReceiver(string eventPrefix)
    {
        using var entered = EventWaitHandle.OpenExisting(eventPrefix + "entered");
        using var release = EventWaitHandle.OpenExisting(eventPrefix + "release");
        using var source = NewWindow("vision-layout-receiver", 30, 30);
        var count = 0;
        source.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (message == BlockMessage)
            {
                handled = true;
                entered.Set();
                // Bound failures so a regression cannot leave the harness hung.
                release.WaitOne(TimeSpan.FromSeconds(8));
                return IntPtr.Zero;
            }
            if (message == CountMessage)
            {
                handled = true;
                return new IntPtr(count);
            }
            if (message == EmbeddedVisionWindowLayout.ResizeMessage)
            {
                count++;
            }
            return EmbeddedVisionWindowLayout.HandleMessage(hwnd, message, wParam, lParam, ref handled);
        });
        Console.WriteLine(source.Handle.ToInt64().ToString(CultureInfo.InvariantCulture));
        Console.Out.Flush();
        Dispatcher.Run();
    }

    private static async Task CheckAsync()
    {
        var eventPrefix = "Local\\vision-layout-check-" + Guid.NewGuid().ToString("N");
        using var entered = new EventWaitHandle(false, EventResetMode.ManualReset, eventPrefix + "entered");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, eventPrefix + "release");
        using var parent = NewWindow("production-layout-parent", 320, 240);
        using var alternateParent = NewWindow("alternate-layout-parent", 480, 360);
        var startInfo = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("receiver");
        startInfo.ArgumentList.Add(eventPrefix);
        using var receiver = Process.Start(startInfo) ?? throw new InvalidOperationException("Receiver did not start.");
        try
        {
            var handleLine = await receiver.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var window = new IntPtr(long.Parse(handleLine!, CultureInfo.InvariantCulture));
            _ = SetParent(window, parent.Handle);
            _ = SetWindowLong(window, -16, 0x40000000); // WS_CHILD, same embedding boundary as production.
            Require(GetParent(window) == parent.Handle, "Receiver embedded in production parent");

            var layout = new EmbeddedVisionWindowLayout();
            Require(PostMessage(window, BlockMessage, IntPtr.Zero, IntPtr.Zero), "Start simulated vision wait");
            await UntilAsync(() => entered.WaitOne(0));
            var elapsed = Stopwatch.StartNew();
            for (var i = 0; i < 100; i++)
            {
                Require(layout.RequestResize(window, parent.Handle), "Layout request accepted while vision blocked");
            }
            Require(elapsed.ElapsedMilliseconds < 500, "Layout must return without waiting for vision");

            var progress = new int[3];
            await Task.WhenAll(Enumerable.Range(0, 3).Select(async index =>
            {
                for (var step = 0; step < 4; step++)
                {
                    await Task.Delay(20);
                    Require(Dispatcher.CurrentDispatcher == parent.Dispatcher, "Continuation uses production dispatcher");
                    progress[index]++;
                }
            }));
            Require(progress.All(value => value == 4) && elapsed.ElapsedMilliseconds < 1000,
                "All three simulated production sequences advance while vision remains blocked");
            Console.WriteLine($"POSTED layout: all 3 dispatcher sequences completed in {elapsed.ElapsedMilliseconds} ms while receiver was still blocked.");

            // Resize parent while a request is pending, without posting another request.
            Require(SetWindowPos(parent.Handle, IntPtr.Zero, 0, 0, 640, 420, 0x14), "Resize parent");
            release.Set();
            await UntilAsync(() => Size(window) == Size(parent.Handle));
            Require(SendMessage(window, CountMessage, IntPtr.Zero, IntPtr.Zero).ToInt32() == 1,
                "100 identical refreshes generate one message; receiver uses latest dimensions");

            _ = SetParent(window, alternateParent.Handle);
            layout.Invalidate();
            Require(layout.RequestResize(window, alternateParent.Handle), "Request after reparent");
            await UntilAsync(() => Size(window) == Size(alternateParent.Handle));

            var disposedWindow = NewWindow("disposed-layout-target", 10, 10);
            var invalidHandle = disposedWindow.Handle;
            disposedWindow.Dispose();
            Require(!layout.RequestResize(invalidHandle, alternateParent.Handle), "Invalid target fails");
            Require(layout.RequestResize(window, alternateParent.Handle), "Failed post does not suppress retry");
            await UntilAsync(() => SendMessage(window, CountMessage, IntPtr.Zero, IntPtr.Zero).ToInt32() == 3);

            // Compare the actual call still present in remote commit 8080ad1.
            // This is an observation, not an assertion that every cross-process
            // SetWindowPos call blocks on every Windows/window configuration.
            entered.Reset();
            release.Reset();
            Require(PostMessage(window, BlockMessage, IntPtr.Zero, IntPtr.Zero), "Start legacy comparison");
            await UntilAsync(() => entered.WaitOne(0));
            var legacyProgress = new int[3];
            var legacySequences = Enumerable.Range(0, 3).Select(async index =>
            {
                await Task.Delay(50);
                Interlocked.Increment(ref legacyProgress[index]);
            }).ToArray();
            var progressBeforeRelease = -1;
            var releaseTask = Task.Run(async () =>
            {
                await Task.Delay(250);
                progressBeforeRelease = Enumerable.Range(0, 3).Sum(index => Volatile.Read(ref legacyProgress[index]));
                await Task.Delay(350);
                release.Set();
            });
            var legacyElapsed = Stopwatch.StartNew();
            Require(SetWindowPos(window, IntPtr.Zero, 0, 0, 481, 362, 0x70), "Legacy resize succeeds");
            var legacyBlockedMilliseconds = legacyElapsed.ElapsedMilliseconds;
            await releaseTask;
            await Task.WhenAll(legacySequences);
            Console.WriteLine($"LEGACY measured: call duration={legacyBlockedMilliseconds} ms; progress during receiver wait={progressBeforeRelease}/3.");
            Console.WriteLine("NOTE: these hidden-window checks do not reproduce the SDK renderer or prove the field stall's cause.");
        }
        finally
        {
            release.Set();
            if (!receiver.HasExited)
            {
                receiver.Kill();
                await receiver.WaitForExitAsync();
            }
        }
    }

    private static HwndSource NewWindow(string name, int width, int height) => new(new HwndSourceParameters(name)
    {
        WindowStyle = unchecked((int)0x80000000), // Hidden popup; never opens the machine UI.
        Width = width,
        Height = height
    });

    private static async Task CheckDiagnosticsAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "production-diagnostics-" + Guid.NewGuid().ToString("N"));
        var uiThreadId = Environment.CurrentManagedThreadId;
        var session = new ProductionDiagnosticSession(Dispatcher.CurrentDispatcher, () =>
        {
            Require(Environment.CurrentManagedThreadId == uiThreadId, "Snapshot stays on UI thread");
            return "axis=3,pos=50,target=100,moving=False";
        }, directory);
        try
        {
            using var cameraStep = session.Begin("vision-result", "camera is still pending");
            var unload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Track("second-set-unload", unload.Task);
            await Task.Delay(100);
            unload.SetException(new InvalidOperationException("simulated axis failure before camera result"));
            // Deliberately block THIS TEST dispatcher; logger heartbeat must still run.
            Thread.Sleep(1200);
            await Task.Delay(600);
        }
        finally { session.Dispose(); }
        await session.Completion;
        Require(session.WriteError is null, "Diagnostics file written");
        var lines = await File.ReadAllTextAsync(session.LogPath);
        Require(lines.Contains("TASK Faulted") && lines.Contains("simulated axis failure before camera result"),
            "Records unload failure without waiting for camera or awaiting unload");
        Require(System.Text.RegularExpressions.Regex.Matches(lines, @"ui_gap_ms=(\d+)")
            .Any(match => long.Parse(match.Groups[1].Value) >= 700), "Records blocked dispatcher from independent timer");
        Require(lines.Contains("axis=3,pos=50,target=100") && lines.Contains("build_mvid="),
            "Records cached axis feedback and exact build identity");
        Console.WriteLine("PASS: diagnostic log identifies dispatcher stalls and hidden task failures while camera is pending: " + session.LogPath);
    }

    private static (int, int) Size(IntPtr window)
    {
        Require(GetClientRect(window, out var bounds), "Read client dimensions");
        return (bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            Require(timer.Elapsed < TimeSpan.FromSeconds(5), "Timed out waiting for receiver");
            await Task.Delay(10);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr window, IntPtr parent);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, int flags);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect bounds);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
}
