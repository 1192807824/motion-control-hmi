using System.IO;
using System.Collections.Specialized;
using System.Diagnostics;
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
        var simulatedHopperAmplitude = 0;
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
                    var packet = pending[..(end + 1)];
                    lock (packets)
                    {
                        // Reproduce the reported controller: &04$ acknowledges but does not stop channel 5.
                        if (packet.StartsWith("&13,"))
                            simulatedHopperAmplitude = int.Parse(packet.Split(',')[1]);
                        packets.Add(packet);
                    }
                    var command = packet[..3] + "$\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(command));
                    pending = pending[(end + 1)..];
                }
            }
        });
        string[] Snapshot() { lock (packets) return packets.ToArray(); }
        bool HopperStopped() { lock (packets) return simulatedHopperAmplitude == 0; }
        static bool IsHopperStart(string packet) => packet.StartsWith("&13,") && !packet.StartsWith("&13,00,");
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
        await WaitFor(() => Snapshot().Length == 4);
        Require(Snapshot().SequenceEqual(new[] { "&05,00$", "&13,50,028$", "&13,00,028$", "&04$" }), "Zero amplitude precedes &04$, with exact field widths.");
        Require(HopperStopped(), "Timed stop clears hopper amplitude even when &04$ does not stop channel 5.");
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

        vm.FeederSettings.DirectionalVibrationFrequency = 47;
        vm.FeederSettings.DirectionalVibrationAmplitude = 62;
        vm.FeederSettings.DirectionalVibrationDurationMilliseconds = 130;
        Clear();
        var sequenceClock = Stopwatch.StartNew();
        Require(await page.RunProductionScatterThenLeftAsync(CancellationToken.None), "Four-stage production vibration completes.");
        await WaitFor(() => Snapshot().Length == 12);
        var productionPackets = Snapshot();
        foreach (var mode in new[] { "04", "03", "06" })
            Require(productionPackets.Contains($"&02,047,062,1,047,062,1,047,062,1,047,062,1,{mode}$"),
                "All production modes use the configured frequency/amplitude.");
        Require(productionPackets.Where(packet => packet.StartsWith("&03,") || packet == "&04$")
            .SequenceEqual(new[] { "&03,04$", "&04$", "&03,03$", "&04$", "&03,06$", "&04$", "&03,04$", "&04$" }),
            "Production runs scatter, left, gather, scatter, stopping after each stage.");
        Require(sequenceClock.ElapsedMilliseconds >= 3 * 130 + 100 + 4 * 50 - 20,
            "Completion waits for all configured pulses and stop-settle intervals.");

        Clear();
        using (var finalScatterCancellation = new CancellationTokenSource())
        {
            var sequence = page.RunProductionScatterThenLeftAsync(finalScatterCancellation.Token);
            await WaitFor(() => Snapshot().Count(packet => packet == "&03,04$") == 2);
            finalScatterCancellation.Cancel();
            Require(!await sequence, "Cancellation in the added final scatter must not report completion.");
            await WaitFor(() => Snapshot().LastOrDefault() == "&04$");
            Require(Snapshot().Count(packet => packet.StartsWith("&03,")) == 4,
                "Cancelling the final scatter stops it without restarting another pulse.");
        }
        Console.WriteLine("PASS: configured scatter/left/gather/scatter sequence, final stop and final-pulse cancellation.");

        // Production has a fixed recipe, independent of the manual controls and their production interlock.
        long productionStartAt = 0, productionZeroAt = 0, productionStopAt = 0;
        void TrackProductionTiming(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (args.NewItems is null) return;
            foreach (string log in args.NewItems)
            {
                if (!log.Contains("TX [ASCII]") || !log.Contains("生产拍照前料仓补料")) continue;
                if (log.Contains("&13,50,100$")) productionStartAt = Stopwatch.GetTimestamp();
                if (log.Contains("&13,00,100$")) productionZeroAt = Stopwatch.GetTimestamp();
                if (log.Contains("&04$")) productionStopAt = Stopwatch.GetTimestamp();
            }
        }
        vm.FeederConnectionLogs.CollectionChanged += TrackProductionTiming;
        page.AttachHopperManualControlInterlock(() => true);
        var cameraTriggers = 0;
        for (var photoIndex = 0; photoIndex < 2; photoIndex++)
        {
            Clear();
            productionStartAt = productionZeroAt = productionStopAt = 0;
            Require(await page.FeedHopperBeforeProductionPhotoAsync(CancellationToken.None), "Each production photo completes hopper preparation.");
            cameraTriggers++;
            await WaitFor(() => Snapshot().Length == 4);
            Require(Snapshot().SequenceEqual(new[] { "&05,00$", "&13,50,100$", "&13,00,100$", "&04$" }),
                "Each photo uses exactly one 100-frequency/50%-amplitude pulse, then zero amplitude and stop.");
            Require(productionStartAt > 0 && productionZeroAt > 0 && productionStopAt > 0, "All production stages observed.");
            Require(Stopwatch.GetElapsedTime(productionStartAt, productionZeroAt).TotalMilliseconds >= 95,
                "Production hopper pulse lasts 100 ms before zero amplitude (5 ms timer tolerance).");
            Require(Stopwatch.GetElapsedTime(productionStopAt).TotalMilliseconds >= 195,
                "Photo gate waits 200 ms after final stop (5 ms timer tolerance).");
            Require(HopperStopped(), "Hopper is zeroed before photo gate opens.");
        }
        Require(vm.FeederSettings.HopperVibrationFrequency == 28 && vm.FeederSettings.HopperVibrationDurationMilliseconds == 120,
            "Production constants do not change manual settings.");

        foreach (var cancelDuringSettle in new[] { false, true })
        {
            Clear();
            using var photoCancellation = new CancellationTokenSource();
            var photoGate = page.FeedHopperBeforeProductionPhotoAsync(photoCancellation.Token);
            await WaitFor(() => Snapshot().Contains(cancelDuringSettle ? "&04$" : "&13,50,100$"));
            Require(!await page.FeedHopperBeforeProductionPhotoAsync(CancellationToken.None), "Overlapping photo preparation is blocked.");
            photoCancellation.Cancel();
            if (await photoGate) cameraTriggers++;
            Require(cameraTriggers == 2, "Cancellation during pulse or settle must not release a photo.");
            await WaitFor(() => Snapshot().Contains("&13,00,100$"));
            Require(HopperStopped(), "Cancellation still zeros hopper amplitude.");
        }
        Clear();
        using (var alreadyCancelled = new CancellationTokenSource())
        {
            alreadyCancelled.Cancel();
            Require(!await page.FeedHopperBeforeProductionPhotoAsync(alreadyCancelled.Token), "Cancelled production never starts feeding.");
        }
        await Task.Delay(50);
        Require(Snapshot().Length == 0, "Already-cancelled photo preparation emits no commands.");
        vm.FeederConnectionLogs.CollectionChanged -= TrackProductionTiming;
        page.AttachHopperManualControlInterlock(() => false);
        Console.WriteLine("PASS: every production photo feeds once at 100/50/100 ms, waits 200 ms after stop, and cancellation prevents photo.");

        Inputs("7", "9", "30000");
        var running = Run();
        await WaitFor(() => Snapshot().Contains("&13,09,007$"));
        await client.WriteAsync(Encoding.ASCII.GetBytes("&04$"));
        await WaitFor(() => Snapshot().Contains("&04$"));
        Require(!HopperStopped(), "Regression setup: &04$ alone acknowledges but leaves channel 5 moving.");
        Require(!await Run(), "Repeated starts are blocked.");
        var direction = (Task<bool>)typeof(ConnectionConfigPage).GetMethod("RunDirectionalVibrationAsync", Hidden)!
            .Invoke(page, ["04", "震散", CancellationToken.None])!;
        Require(!await direction, "Directional vibration cannot overlap hopper.");
        vm.FeederSettings.HopperVibrationFrequency = 88;
        Click("StopVibration_Click");
        Require(!await running.WaitAsync(TimeSpan.FromSeconds(3)), "Manual stop cancels long pulse promptly.");
        await WaitFor(() => Snapshot().Contains("&04$"));
        Require(Snapshot().Count(IsHopperStart) == 1, "Only one nonzero hopper start sent.");
        Require(Snapshot().Contains("&13,00,007$") && !Snapshot().Contains("&13,00,088$"), "Stop keeps the actual start frequency when settings change mid-pulse.");
        Require(HopperStopped(), "Manual stop clears channel 5 amplitude.");
        Require(vm.FeederSettings.HopperVibrationAmplitude == 9 && new VibrationFeederSettingsStore().Load().HopperVibrationAmplitude == 9,
            "Zero-amplitude stop does not overwrite the user's saved run amplitude.");
        Require(((Button)page.FindName("HopperStartButton")).IsEnabled, "Start button restored after stop.");
        Console.WriteLine("PASS: shared vibration interlock, manual all-stop and restart availability.");

        // A stop while a command is queued must prevent a later hopper start.
        await WaitFor(() => WriteLockAvailable(page));
        Clear();
        Inputs("7", "9", "30000");
        var writeLock = (SemaphoreSlim)typeof(ConnectionConfigPage).GetField("_protocolWriteLock", Hidden)!.GetValue(page)!;
        await writeLock.WaitAsync();
        running = Run();
        Click("StopVibration_Click");
        writeLock.Release();
        Require(!await running.WaitAsync(TimeSpan.FromSeconds(3)), "Queued startup can be cancelled.");
        await WaitFor(() => Snapshot().Contains("&04$"));
        Require(!Snapshot().Any(IsHopperStart) && HopperStopped(), "No delayed nonzero start after stop.");
        Console.WriteLine("PASS: queued cancellation cannot restart hopper after stop.");

        Clear();
        running = Run();
        await WaitFor(() => Snapshot().Any(IsHopperStart));
        Click("DisconnectFeeder_Click");
        await running.WaitAsync(TimeSpan.FromSeconds(3));
        await reading.WaitAsync(TimeSpan.FromSeconds(3));
        Require(Snapshot().Contains("&13,00,007$") && Snapshot().Contains("&04$") && HopperStopped() && !client.IsConnected,
            "Disconnect clears hopper amplitude and sends &04$ before closing socket.");
        Require(!await Run(), "Disconnected startup is blocked.");
        Require(!await page.FeedHopperBeforeProductionPhotoAsync(CancellationToken.None), "Disconnected feeder cannot release production photo.");
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
        Require(shutdownWire.Contains("&13,50,028$") && shutdownWire.Contains("&13,00,028$") && shutdownWire.EndsWith("&04$"),
            "Shutdown permits hopper zero-amplitude command before final &04$ and socket disposal.");
        Console.WriteLine("PASS: shutdown cancels hopper and writes all-stop before socket disposal.");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static bool WriteLockAvailable(ConnectionConfigPage page) =>
        ((SemaphoreSlim)typeof(ConnectionConfigPage).GetField("_protocolWriteLock", Hidden)!.GetValue(page)!).CurrentCount == 1;
}
