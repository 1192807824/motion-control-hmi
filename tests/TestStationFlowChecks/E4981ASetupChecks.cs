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
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(cancellation.Token);
            using var reader = new StreamReader(socket.GetStream());
            using var writer = new StreamWriter(socket.GetStream()) { AutoFlush = true, NewLine = "\n" };
            while (await reader.ReadLineAsync(cancellation.Token) is { } command)
            {
                commands.Enqueue(command);
                if (command == "SYST:ERR?")
                    await writer.WriteLineAsync(Interlocked.Exchange(ref failNextSetup, 0) == 1
                        ? "-222,Data out of range" : "+0,No error");
                else if (command == "*TRG")
                    await writer.WriteLineAsync("0,1.5E-10,0.2,2");
            }
        });

        // No Loaded event, main window, motion controller or real instrument is attached.
        var viewModel = new MainWindowViewModel(new Dictionary<int, AxisSettings>());
        var settings = viewModel.TcpConnectionSettings;
        settings.Host = IPAddress.Loopback.ToString();
        settings.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        settings.CommandTimeoutMilliseconds = 1000;
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
            Require(commands.Count(command => command == "*TRG") == 12, "Instrument was triggered after failed setup.");
            await page.MeasureE4981AAsync(cancellation.Token);
            Require(commands.Count(command => command == "*CLS") == 4, "A failed setup must be resent on the next attempt.");
            settings.StabilitySampleCount = 2;
            var triggerCount = commands.Count(command => command == "*TRG");
            var resampled = await page.MeasureE4981AAsync(cancellation.Token);
            Require(resampled.SampleCount == 2 && commands.Count(command => command == "*TRG") == triggerCount + 2 &&
                    commands.Count(command => command == "*CLS") == 4,
                "Changing software stability settings must apply immediately without resending SCPI setup.");
        }
        finally
        {
            client.Close();
            listener.Stop();
            cancellation.Cancel();
            try { await server; } catch (OperationCanceledException) { }
        }
        Console.WriteLine("PASS: real E4981A connection path against loopback simulator; changed BIN/loss limits resend, unchanged setup caches, setup failure blocks trigger and retries safely.");
    }
}
