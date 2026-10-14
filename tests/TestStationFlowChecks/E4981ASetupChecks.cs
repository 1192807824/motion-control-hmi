using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows.Threading;
using ControlHub.Services.Devices;
using ControlHub.Services.Persistence;
using ControlHub.ViewModels;
using ControlHub.Views.Pages;

internal static partial class Program
{
    private static void CheckE4981ASetupChanges()
    {
        // Pump only this test's WPF dispatcher; the fake instrument is bound exclusively to loopback.
        var dispatcher = Dispatcher.CurrentDispatcher;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        try
        {
            var test = CheckE4981ASetupChangesAsync();
            var frame = new DispatcherFrame();
            _ = test.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
                TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            test.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static async Task CheckE4981ASetupChangesAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var commands = new ConcurrentQueue<string>();
        var failNextSetup = 0;
        var capacitance = 1.5e-10;
        var measurementStatus = 0;
        var averageEnabled = false;
        var failNextMeasurement = 0;
        var failNextReadback = 0;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                using var socket = await listener.AcceptTcpClientAsync(cancellation.Token);
                using var reader = new StreamReader(socket.GetStream());
                using var writer = new StreamWriter(socket.GetStream()) { AutoFlush = true, NewLine = "\n" };
                while (await reader.ReadLineAsync(cancellation.Token) is { } command)
                {
                    commands.Enqueue(command);
                    if (command == "AVER ON") averageEnabled = true;
                    else if (command == "AVER OFF") averageEnabled = false;
                    if (command == "SYST:ERR?")
                        await writer.WriteLineAsync(Interlocked.Exchange(ref failNextSetup, 0) == 1
                            ? "-222,Data out of range" : "+0,No error");
                    else if (command == "TRIG:SOUR?")
                        await writer.WriteLineAsync(Interlocked.Exchange(ref failNextReadback, 0) == 1 ? "INT" : "BUS");
                    else if (command == "INIT:CONT?") await writer.WriteLineAsync("1");
                    else if (command == "AVER?") await writer.WriteLineAsync(averageEnabled ? "1" : "0");
                    else if (command == "*TRG")
                    {
                        if (Interlocked.Exchange(ref failNextMeasurement, 0) == 1)
                            continue;
                        var status = Volatile.Read(ref measurementStatus);
                        await writer.WriteLineAsync(FormattableString.Invariant(
                            $"{status},{Volatile.Read(ref capacitance):G17},0.2,{(status == 0 ? 2 : 11)}"));
                    }
                }
            }
        });

        // No Loaded event, main window, motion controller or real instrument is attached.
        var viewModel = new MainWindowViewModel(new Dictionary<int, AxisSettings>());
        var settings = viewModel.TcpConnectionSettings;
        settings.Host = IPAddress.Loopback.ToString();
        settings.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        settings.CommandTimeoutMilliseconds = 1000;
        settings.AveragingEnabled = false;
        settings.ComparatorEnabled = true;
        settings.Bin1Enabled = settings.Bin3Enabled = false;
        settings.Bin2Enabled = true;
        settings.Bin2LowerNf = 0.1;
        settings.Bin2UpperNf = 0.2;
        settings.LossLimitEnabled = true;
        settings.LossLower = 0.05;
        settings.LossUpper = 0.75;
        var page = new ConnectionConfigPage { DataContext = viewModel };
        var client = (E4981ATcpClient)typeof(ConnectionConfigPage)
            .GetField("_generalTcpClient", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
        try
        {
            await client.ConnectAsync(settings, cancellation.Token);
            Require(!(await page.MeasureE4981AAsync(cancellation.Token)).LossRejected, "Initial range should accept D=0.2.");
            await page.MeasureE4981AAsync(cancellation.Token);
            Require(commands.Count(command => command == "*CLS") == 1, "Unchanged settings should reuse applied setup.");

            // This follows the same property-update path as recipe application, without a Save button click.
            settings.Bin2UpperNf = 0.3;
            settings.LossLower = 0.25;
            Require((await page.MeasureE4981AAsync(cancellation.Token)).LossRejected,
                "Updated loss limit must be applied to the next measurement.");
            Require(commands.Count(command => command == "*CLS") == 2 &&
                    commands.Contains("CALC1:COMP:PRIM:BIN2 1E-10,3E-10") &&
                    commands.Contains("CALC1:COMP:SEC:LIM 0.25,0.75"),
                "Edited BIN and loss limits were not sent to the instrument.");
            await page.MeasureE4981AAsync(cancellation.Token);
            Require(commands.Count(command => command == "*CLS") == 2, "Updated setup was not cached.");

            settings.Bin2UpperNf = 0.4;
            Interlocked.Exchange(ref failNextSetup, 1);
            try
            {
                await page.MeasureE4981AAsync(cancellation.Token);
                throw new Exception("Rejected setup must not trigger a measurement.");
            }
            catch (InvalidOperationException) { }
            Require(commands.Count(command => command == "*TRG") == 4, "Instrument was triggered after failed setup.");
            await page.MeasureE4981AAsync(cancellation.Token);
            Require(commands.Count(command => command == "*CLS") == 4, "A failed setup must be resent on the next attempt.");

            settings.LossLower = 0.05;
            // Production machine has averaging OFF: every logical test must trigger exactly once,
            // regardless of the disabled count retained in settings.
            foreach (var count in new[] { 3, 8, 1, 256 })
            {
                settings.AveragingCount = count;
                Volatile.Write(ref capacitance, 400e-9);
                Volatile.Write(ref measurementStatus, 0);
                var triggersBefore = commands.Count(command => command == "*TRG");
                var contacted = await page.MeasureE4981AAsync(cancellation.Token);
                Require(Math.Abs(contacted.CapacitanceNf - 400) < 1e-6 && contacted.Bin == 2 && !contacted.LossRejected,
                    "First test after contact retained old samples or lost final BIN/D.");
                Require(commands.Count(command => command == "*TRG") - triggersBefore == 1,
                    "A single test must trigger exactly once when averaging is disabled.");

                Volatile.Write(ref capacitance, 0.003e-9);
                Volatile.Write(ref measurementStatus, 2);
                var released = await page.MeasureE4981AAsync(cancellation.Token);
                Require(Math.Abs(released.CapacitanceNf - 0.003) < 1e-6 && !released.IsSuccessful && released.Bin == 11,
                    "First test after release retained previous product or lost NC/BIN11.");
            }

            settings.AveragingEnabled = false;
            var beforeSingle = commands.Count(command => command == "*TRG");
            Volatile.Write(ref capacitance, 400e-9);
            Volatile.Write(ref measurementStatus, 0);
            Require(Math.Abs((await page.MeasureE4981AAsync(cancellation.Token)).CapacitanceNf - 400) < 1e-6 &&
                    commands.Count(command => command == "*TRG") - beforeSingle == 1,
                "Disabled averaging must use one fresh trigger, regardless of saved average count.");

            // Exercise the actual async-void button handler as well as the production entry point.
            settings.AveragingCount = 3;
            Volatile.Write(ref capacitance, 0.003e-9);
            var beforeManual = commands.Count(command => command == "*TRG");
            typeof(ConnectionConfigPage).GetMethod("TriggerMeterTest_Click", Private)!
                .Invoke(page, new object[] { page, new System.Windows.RoutedEventArgs() });
            while ((bool)typeof(ConnectionConfigPage).GetField("_meterOperationRunning", Private)!.GetValue(page)!)
                await Task.Delay(5, cancellation.Token);
            var valueText = (System.Windows.Controls.TextBlock)typeof(ConnectionConfigPage)
                .GetField("MeterCapacitanceText", Private)!.GetValue(page)!;
            Require(valueText.Text == "0.003 nF" && commands.Count(command => command == "*TRG") - beforeManual == 1,
                "Manual single-test button must trigger exactly once and show the current product.");
            Require(viewModel.TcpConnectionLogs.Any(log => log.Contains("TRIG=BUS，INIT:CONT=1，AVER=0")),
                "Actual instrument trigger/averaging state must be read back and logged.");

            settings.Bin2UpperNf = 0.5;
            Interlocked.Exchange(ref failNextReadback, 1);
            var beforeMismatch = commands.Count(command => command == "*TRG");
            try
            {
                await page.MeasureE4981AAsync(cancellation.Token);
                throw new Exception("Mismatched instrument readback must not trigger.");
            }
            catch (InvalidOperationException) { }
            Require(!client.IsConnected && valueText.Text == "-- nF" &&
                    commands.Count(command => command == "*TRG") == beforeMismatch,
                "Readback mismatch must invalidate the connection and the displayed result without triggering.");
            await client.ConnectAsync(settings, cancellation.Token);
            await page.MeasureE4981AAsync(cancellation.Token);

            // A failed refresh must never leave the last valid reading visible as a current result.
            settings.CommandTimeoutMilliseconds = 500;
            Interlocked.Exchange(ref failNextMeasurement, 1);
            try
            {
                await page.MeasureE4981AAsync(cancellation.Token);
                throw new Exception("Missing measurement response must time out.");
            }
            catch (TimeoutException) { }
            Require(!client.IsConnected && valueText.Text == "-- nF",
                "Timeout must invalidate both the TCP stream and the previously displayed capacitance.");
        }
        finally
        {
            client.Close();
            cancellation.Cancel();
            listener.Stop();
            try { await server; } catch (OperationCanceledException) { }
        }
        Console.WriteLine("PASS: E4981A AVG OFF contact/release, exactly one trigger in production/manual tests, final BIN/D, setup cache/rejection, actual-state readback/mismatch and timeout clears old display.");
    }
}
