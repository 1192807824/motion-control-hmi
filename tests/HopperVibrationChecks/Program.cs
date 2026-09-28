using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ControlHub.Services.Devices;
using ControlHub.Services.Persistence;
using ControlHub.ViewModels;
using ControlHub.Views.Pages;

internal static class Program
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var result = 0;
        app.Startup += async (_, _) =>
        {
            try { await CheckAsync(); }
            catch (Exception exception) { Console.Error.WriteLine(exception); result = 1; }
            finally { app.Shutdown(); }
        };
        app.Run();
        return result;
    }

    private static async Task CheckAsync()
    {
        var vm = new MainWindowViewModel();
        vm.FeederSettings.LastSuccessfulConnectionSignature = null;
        var page = new ConnectionConfigPage { DataContext = vm, Width = 1500, Height = 980 };
        // Do not trigger startup auto-connect or construct any motion hardware.
        typeof(ConnectionConfigPage).GetField("_loaded", Hidden)!.SetValue(page, true);
        page.Measure(new Size(1500, 980));
        page.Arrange(new Rect(0, 0, 1500, 980));
        page.UpdateLayout();
        await page.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var client = (VibrationFeederTcpClient)typeof(ConnectionConfigPage).GetField("_tcpClient", Hidden)!.GetValue(page)!;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await client.ConnectAsync(new VibrationFeederSettings { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port });
        using var peer = await listener.AcceptTcpClientAsync();
        var stream = peer.GetStream();
        var packets = new List<string>();
        var reading = Task.Run(async () =>
        {
            var pending = "";
            var buffer = new byte[1024];
            while (true)
            {
                var count = await stream.ReadAsync(buffer);
                if (count == 0) break;
                pending += Encoding.ASCII.GetString(buffer, 0, count);
                int end;
                while ((end = pending.IndexOf('$')) >= 0)
                {
                    lock (packets) packets.Add(pending[..(end + 1)]);
                    pending = pending[(end + 1)..];
                }
            }
        });
        string[] Snapshot() { lock (packets) return packets.ToArray(); }
        void Clear() { lock (packets) packets.Clear(); }
        Task<bool> Run() => (Task<bool>)typeof(ConnectionConfigPage).GetMethod("RunHopperVibrationAsync", Hidden)!
            .Invoke(page, [CancellationToken.None])!;
        void Click(string method) => typeof(ConnectionConfigPage).GetMethod(method, Hidden)!.Invoke(page, [page, new RoutedEventArgs()]);
        void Inputs(string frequency, string amplitude, string duration)
        {
            ((TextBox)page.FindName("HopperFrequencyTextBox")).Text = frequency;
            ((TextBox)page.FindName("HopperAmplitudeTextBox")).Text = amplitude;
            ((TextBox)page.FindName("HopperDurationTextBox")).Text = duration;
        }
        async Task WaitFor(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(3000);
            while (!condition()) await Task.Delay(10, timeout.Token);
        }

        Inputs("28", "50", "120");
        var pulseClock = System.Diagnostics.Stopwatch.StartNew();
        Require(await Run(), "Timed vibration succeeds.");
        Require(pulseClock.ElapsedMilliseconds >= 120, "Configured pulse duration is respected.");
        await WaitFor(() => Snapshot().Length == 3);
        Require(Snapshot().SequenceEqual(new[] { "&05,00$", "&13,50,028$", "&04$" }), "Exact protocol order and ASCII field widths.");
        var saved = new VibrationFeederSettingsStore().Load();
        Require(saved.HopperVibrationFrequency == 28 && saved.HopperVibrationAmplitude == 50 && saved.HopperVibrationDurationMilliseconds == 120,
            "Hopper parameters persist independently.");
        var legacy = JsonSerializer.Deserialize<VibrationFeederSettings>("{\"DirectionalVibrationFrequency\":45}")!;
        Require(legacy.HopperVibrationFrequency == 28 && legacy.HopperVibrationAmplitude == 50, "Old settings load with hopper defaults.");
        var recipe = ProductRecipeStore.Clone(new ProductRecipe { VibrationFeeder = saved });
        Require(recipe.VibrationFeeder.HopperVibrationDurationMilliseconds == 120, "Recipe clone retains hopper parameters.");
        Console.WriteLine("PASS: timed command, field widths, settings and recipe persistence, legacy settings.");

        Clear();
        foreach (var values in new[] { new[] { "abc", "50", "120" }, new[] { "28", "100", "120" }, new[] { "28", "50", "0" } })
        {
            Inputs(values[0], values[1], values[2]);
            Require(!await Run(), "Invalid parameters rejected.");
        }
        Inputs("28", "50", "120");
        page.AttachHopperManualControlInterlock(() => true);
        Require(!await Run(), "Production interlock blocks manual hopper.");
        page.AttachHopperManualControlInterlock(() => false);
        await Task.Delay(50);
        Require(Snapshot().Length == 0, "Rejected operations send no commands.");
        Console.WriteLine("PASS: invalid input and active-production rejection send no commands.");

        Inputs("7", "9", "30000");
        var running = Run();
        await WaitFor(() => Snapshot().Contains("&13,09,007$"));
        Require(!await Run(), "Repeated starts are blocked.");
        var direction = (Task<bool>)typeof(ConnectionConfigPage).GetMethod("RunDirectionalVibrationAsync", Hidden)!
            .Invoke(page, ["04", "震散", CancellationToken.None])!;
        Require(!await direction, "Directional vibration cannot overlap hopper.");
        Click("StopVibration_Click");
        Require(!await running.WaitAsync(TimeSpan.FromSeconds(3)), "Manual stop cancels long pulse promptly.");
        await WaitFor(() => Snapshot().Contains("&04$"));
        Require(Snapshot().Count(packet => packet.StartsWith("&13,")) == 1, "Only one hopper start sent.");
        Require(((Button)page.FindName("HopperStartButton")).IsEnabled, "Start button restored after stop.");
        Console.WriteLine("PASS: shared vibration interlock, manual all-stop and restart availability.");

        // A stop while a command is queued must prevent a later hopper start.
        Clear();
        var writeLock = (SemaphoreSlim)typeof(ConnectionConfigPage).GetField("_protocolWriteLock", Hidden)!.GetValue(page)!;
        await writeLock.WaitAsync();
        running = Run();
        Click("StopVibration_Click");
        writeLock.Release();
        Require(!await running.WaitAsync(TimeSpan.FromSeconds(3)), "Queued startup can be cancelled.");
        await WaitFor(() => Snapshot().Contains("&04$"));
        Require(!Snapshot().Any(packet => packet.StartsWith("&13,")), "No delayed start after stop.");
        Console.WriteLine("PASS: queued cancellation cannot restart hopper after stop.");

        Clear();
        running = Run();
        await WaitFor(() => Snapshot().Any(packet => packet.StartsWith("&13,")));
        Click("DisconnectFeeder_Click");
        await running.WaitAsync(TimeSpan.FromSeconds(3));
        await reading.WaitAsync(TimeSpan.FromSeconds(3));
        Require(Snapshot().Contains("&04$") && !client.IsConnected, "Disconnect sends stop before closing socket.");
        Require(!await Run(), "Disconnected startup is blocked.");
        Console.WriteLine("PASS: disconnect stops vibration and blocks disconnected startup.");

        Inputs("28", "50", "300");
        page.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1500, 980, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var imagePath = Path.Combine(AppContext.BaseDirectory, "hopper-connection-config.png");
        using (var imageFile = File.Create(imagePath)) encoder.Save(imageFile);
        Console.WriteLine($"SCREENSHOT: {imagePath}");

        await client.ConnectAsync(new VibrationFeederSettings { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port });
        using var shutdownPeer = await listener.AcceptTcpClientAsync();
        using var shutdownReader = new StreamReader(shutdownPeer.GetStream(), Encoding.ASCII);
        var shutdownPackets = shutdownReader.ReadToEndAsync();
        Inputs("28", "50", "30000");
        var previousStarts = vm.FeederConnectionLogs.Count(log => log.Contains("&13,50,028$"));
        running = Run();
        await WaitFor(() => vm.FeederConnectionLogs.Count(log => log.Contains("&13,50,028$")) > previousStarts);
        Require(await page.PrepareFeederShutdownAsync(), "Shutdown stop is written before disposal.");
        Require(!await running.WaitAsync(TimeSpan.FromSeconds(3)), "Shutdown cancels the active hopper pulse.");
        Require(!await Run(), "Hopper cannot restart while shutting down.");
        page.Shutdown();
        var shutdownWire = await shutdownPackets.WaitAsync(TimeSpan.FromSeconds(3));
        Require(shutdownWire.Contains("&13,50,028$") && shutdownWire.EndsWith("&04$"), "Final shutdown wire command stops both motors.");
        Console.WriteLine("PASS: shutdown cancels hopper and writes all-stop before socket disposal.");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
