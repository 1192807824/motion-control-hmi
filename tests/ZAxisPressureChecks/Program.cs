using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ControlHub.Services.Motion;
using ControlHub.Views.Pages;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        CheckPressureSafety();
        var card = DispatchProxy.Create<IMotionCard, CardProxy>();
        var fake = (CardProxy)(object)card;
        var interlocked = new DdInterlockedMotionCard(card, () => throw new Exception("Monitoring must not invoke motion interlocks."));
        var values = ZAxisPressureMonitor.Read(interlocked, false);
        Require(values.Select(value => value.AxisNo).SequenceEqual(new[] { 5, 7, 9, 11 }), "Incorrect Z axis mapping.");
        Require(values.Select(value => value.RawValue).SequenceEqual(new int?[] { -32768, 0, 123, 32767 }), "Signed/zero feedback corrupted.");

        fake.FailedAxis = 7;
        var partial = ZAxisPressureMonitor.Read(interlocked, false);
        Require(partial[1].RawValue is null && partial[1].Detail.Contains("SDK error 42") && partial[3].RawValue == 32767,
            "One failed axis must clear its value without hiding other axes.");
        fake.FailedAxis = null;
        fake.BusError = 0x1234;
        Require(ZAxisPressureMonitor.Read(card, false).All(value => value.RawValue is null && value.Status == "总线异常"), "Bus failure retained values.");
        fake.BusError = 0x0228;
        Require(ZAxisPressureMonitor.Read(card, false).All(value => value.RawValue.HasValue), "Allowed ring warning blocked feedback.");
        fake.BusError = 0;
        fake.Open = false;
        Require(ZAxisPressureMonitor.Read(card, false).All(value => value.RawValue is null && value.Status == "未连接"), "Disconnected card retained values.");
        fake.Open = true;
        fake.RejectReads = true;
        Require(ZAxisPressureMonitor.Read(card, true).All(value => value.RawValue is null && value.Status == "模拟模式"), "Simulation must not fabricate pressure.");
        fake.RejectReads = false;
        fake.DisconnectOnLastAxis = true;
        Require(ZAxisPressureMonitor.Read(card, false).All(value => value.RawValue is null), "Mid-batch disconnect retained values.");
        fake.DisconnectOnLastAxis = false;
        fake.Open = true;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { ZAxisPressureMonitor.Read(card, false, cancelled.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS: correct four axes, signed values, independent failures, bus faults, disconnect, simulation, cancellation, read-only decorator.");

        _ = new Application();
        var page = new HomePage();
        CheckThresholdConfiguration(page);
        var apply = typeof(HomePage).GetMethod("ApplyPressureReadings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        apply.Invoke(page, new object[] { partial });
        page.Measure(new Size(1680, 854));
        page.Arrange(new Rect(0, 0, 1680, 854));
        page.UpdateLayout();
        CheckProductionPressureRefresh(page);
        apply.Invoke(page, new object[] { partial });
        var items = (ItemsControl)page.FindName("ZPressureItems");
        var texts = Descendants(items).OfType<TextBlock>().Select(text => text.Text).ToArray();
        Require(texts.Contains("-32768") && texts.Contains("32767") && texts.Contains("读取失败") && texts.Contains("—"),
            "Homepage values/status bindings failed.");
        Require(Descendants(items).OfType<TextBlock>().All(text =>
            text.DesiredSize.Width - text.Margin.Left - text.Margin.Right <= text.ActualWidth + 1), "Pressure labels clipped.");
        if (args.Length > 0)
        {
            var bitmap = new RenderTargetBitmap(1680, 854, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(page);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
            using var output = File.Create(args[0]);
            encoder.Save(output);
        }
        apply.Invoke(page, new object[] { ZAxisPressureMonitor.Unavailable("未连接") });
        page.UpdateLayout();
        texts = Descendants(items).OfType<TextBlock>().Select(text => text.Text).ToArray();
        Require(texts.Count(text => text == "—") == 4 && !texts.Contains("-32768"), "UI retained stale readings after disconnect.");
        Console.WriteLine("PASS: homepage renders four readings, independent error, and clears stale values on disconnect.");
        if (args.Length > 0)
        {
            var parameters = new ParameterSettingsPage();
            parameters.AttachSettingsContent(page.DetachParameterSettingsPanel());
            parameters.Measure(new Size(1680, 854));
            parameters.Arrange(new Rect(0, 0, 1680, 854));
            parameters.UpdateLayout();
            var button = (Button)page.FindName("SaveZPressureThresholdButton");
            var position = button.TransformToAncestor(parameters).Transform(new Point());
            Require(position.X >= 0 && position.Y >= 0 && position.Y + button.ActualHeight < 854,
                "Pressure setting is not visible on parameter page.");
            var preview = new RenderTargetBitmap(1680, 854, 96, 96, PixelFormats.Pbgra32);
            preview.Render(parameters);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(preview));
            using var output = File.Create(Path.ChangeExtension(args[0], ".parameters.png"));
            encoder.Save(output);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void CheckProductionPressureRefresh(HomePage page)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var motion = (MotionControlPage)typeof(HomePage).GetField("_motionController", flags)!.GetValue(page)!;
        var protection = new ZAxisPressureSafety();
        typeof(MotionControlPage).GetField("_pressureSafety", flags)!.SetValue(motion, protection);
        typeof(HomePage).GetField("_startSequenceRunning", flags)!.SetValue(page, true);
        var card = DispatchProxy.Create<IMotionCard, CardProxy>();
        var fake = (CardProxy)(object)card;
        fake.TorqueValues = new() { [5] = 10, [7] = 20, [9] = 30, [11] = 40 };
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var timer = (DispatcherTimer)typeof(HomePage).GetField("_pressureTimer", flags)!.GetValue(page)!;
        var items = (ItemsControl)page.FindName("ZPressureItems");
        var changes = new int[4];
        var subscriptions = new List<(INotifyPropertyChanged Item, PropertyChangedEventHandler Handler)>();
        for (var index = 0; index < 4; index++)
        {
            var slot = index;
            var item = (INotifyPropertyChanged)items.Items[index];
            PropertyChangedEventHandler handler = (_, _) => changes[slot]++;
            item.PropertyChanged += handler;
            subscriptions.Add((item, handler));
        }
        var elapsed = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var cycles = 0;
        Action? productionContinuation = null;
        productionContinuation = () =>
        {
            for (var index = 0; index < 4; index++) fake.TorqueValues[new[] { 5, 7, 9, 11 }[index]] = 10 + (cycles % 350) + index;
            protection.CheckOnce(card, () => throw new Exception("Unexpected protection trip"), _ => { });
            cycles++;
            Thread.Sleep(1);
            if (elapsed.ElapsedMilliseconds < 650)
                dispatcher.BeginInvoke(DispatcherPriority.Normal, productionContinuation!);
            else frame.Continue = false;
        };
        timer.Start();
        dispatcher.BeginInvoke(DispatcherPriority.Normal, productionContinuation);
        try { Dispatcher.PushFrame(frame); }
        finally
        {
            timer.Stop();
            foreach (var (item, handler) in subscriptions) item.PropertyChanged -= handler;
            typeof(HomePage).GetField("_startSequenceRunning", flags)!.SetValue(page, false);
        }
        Require(cycles > 20 && changes.All(count => count >= 3),
            $"Production queue starved pressure refresh: cycles={cycles}, updates=[{string.Join(",", changes)}].");
        Require(protection.TripReason is null, "Display load disrupted pressure safety sampling.");
        Console.WriteLine($"PASS: all four pressure displays update during production queue load: [{string.Join(",", changes)}].");
    }

    private static void CheckPressureSafety()
    {
        static (IMotionCard Card, CardProxy Fake) SafeCard()
        {
            var card = DispatchProxy.Create<IMotionCard, CardProxy>();
            var fake = (CardProxy)(object)card;
            fake.TorqueValues = new() { [5] = 0, [7] = 500, [9] = -501, [11] = 499 };
            return (card, fake);
        }
        static void MustBlock(ZAxisPressureSafety safety)
        {
            try { safety.ThrowIfMotionBlocked(); throw new Exception("Unsafe motion was permitted."); }
            catch (MotionCardException) { }
        }

        var (card, fake) = SafeCard();
        var protection = new ZAxisPressureSafety();
        var stops = 0;
        MustBlock(protection);
        protection.CheckOnce(card, () => stops++, _ => { });
        protection.ThrowIfMotionBlocked();
        Require(stops == 0, "500 or negative values incorrectly triggered signed >500 protection.");
        protection.ConfigureThreshold(800);
        MustBlock(protection);
        fake.TorqueValues![7] = 800;
        protection.CheckOnce(card, () => stops++, _ => { });
        protection.ThrowIfMotionBlocked();
        Require(stops == 0 && protection.Status.Contains("800"), "Custom threshold equality should not trip.");
        fake.TorqueValues[7] = 801;
        protection.CheckOnce(card, () => stops++, _ => { });
        Require(stops == 1 && protection.TripReason!.Contains("> 800"), "Custom threshold did not replace 500.");
        protection.ConfigureThreshold(900);
        MustBlock(protection);
        foreach (var invalid in new[] { -1, 0, 32767, int.MaxValue })
        {
            try { protection.ConfigureThreshold(invalid); throw new Exception("Invalid threshold accepted."); }
            catch (InvalidDataException) { }
            Require(protection.Threshold == 900, "Invalid input changed active protection threshold.");
        }

        // A threshold changed mid-scan must not be treated as verified by an older scan.
        (card, fake) = SafeCard();
        protection = new ZAxisPressureSafety();
        fake.BeforeRead = axis => { if (axis == 11) protection.ConfigureThreshold(200); };
        protection.CheckOnce(card, () => { }, _ => { });
        MustBlock(protection);
        fake.BeforeRead = null;
        stops = 0;
        protection.CheckOnce(card, () => stops++, _ => { });
        Require(stops == 1 && protection.TripReason!.Contains("> 200"), "Lowered threshold did not apply on next scan.");
        foreach (var axis in new[] { 5, 7, 9, 11 })
        {
            (card, fake) = SafeCard();
            protection = new ZAxisPressureSafety();
            fake.TorqueValues![axis] = 501;
            var stopped = false;
            fake.BeforeRead = next =>
            {
                if (next > axis) Require(stopped, "Protection waited for subsequent axis reads before stopping.");
            };
            protection.CheckOnce(card, () => { MustBlock(protection); stopped = true; }, _ => { });
            Require(stopped && protection.TripReason!.Contains($"轴{axis}"), "Overload on a Z axis did not stop all axes.");
            fake.TorqueValues[axis] = 0;
            protection.CheckOnce(card, () => throw new Exception("Successful stop repeated unnecessarily."), _ => { });
            MustBlock(protection);
        }

        (card, fake) = SafeCard();
        fake.FailedAxis = 9;
        protection = new ZAxisPressureSafety();
        stops = 0;
        protection.CheckOnce(card, () => stops++, _ => { });
        Require(stops == 1 && protection.TripReason!.Contains("读取失败"), "Read error did not fail closed.");
        MustBlock(protection);

        (card, fake) = SafeCard();
        fake.BusError = 0x1234;
        protection = new ZAxisPressureSafety();
        stops = 0;
        protection.CheckOnce(card, () => stops++, _ => { });
        Require(stops == 1, "Bus error did not stop all axes.");

        (card, fake) = SafeCard();
        protection = new ZAxisPressureSafety();
        fake.TorqueValues![5] = 501;
        var outcomes = new List<PressureSafetyTrip>();
        protection.CheckOnce(card, () => throw new IOException("Injected emergency stop failure"), outcomes.Add);
        Require(outcomes.Count == 1 && outcomes[0].StopError is not null, "Stop failure was not reported.");
        MustBlock(protection);
        Thread.Sleep(15);
        stops = 0;
        protection.CheckOnce(card, () => stops++, outcomes.Add);
        Require(stops == 1 && outcomes.Count == 2 && outcomes[1].StopError is null, "Failed stop was not retried.");

        (card, fake) = SafeCard();
        protection = new ZAxisPressureSafety();
        protection.CheckOnce(card, () => { }, _ => { });
        Thread.Sleep(120);
        MustBlock(protection);
        Require(protection.Readings.All(reading => reading.RawValue is null), "Stale cached pressure appeared live.");
        stops = 0;
        protection.CheckOnce(card, () => stops++, _ => { });
        Require(stops == 1, "Missed scan deadline did not stop all axes.");

        (card, fake) = SafeCard();
        protection = new ZAxisPressureSafety();
        using var ready = new ManualResetEventSlim();
        using var inject = new ManualResetEventSlim();
        using var stoppedSignal = new ManualResetEventSlim();
        fake.BeforeRead = axis => { if (axis == 5) { ready.Set(); if (inject.IsSet) fake.TorqueValues![5] = 501; } };
        var stopThread = 0;
        protection.Start(card, () => { stopThread = Environment.CurrentManagedThreadId; stoppedSignal.Set(); }, _ => { });
        try
        {
            Require(ready.Wait(3000), "Protection worker did not start.");
            inject.Set();
            // Deliberately block the caller/UI thread; no Dispatcher pumping is allowed here.
            Require(stoppedSignal.Wait(3000), "Protection depended on caller/UI thread progress.");
            Require(stopThread != Environment.CurrentManagedThreadId, "Stop executed on UI thread.");
        }
        finally { protection.Stop(); }
        // Wait until the worker has left CheckOnce before disposing its test signals.
        protection.CheckOnce(card, () => { }, _ => { });
        MustBlock(protection);
        Console.WriteLine("PASS: >500 boundary on all four axes, immediate per-axis stop, fail closed, latch, retry, stale feedback, UI-independent worker.");
    }

    private static void CheckThresholdConfiguration(HomePage page)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ControlHub-pressure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "motion-settings.json");
        File.WriteAllText(path, "{}");
        var store = new MotionCardOptionsStore(path);
        var options = store.Load();
        Require(options.ZPressureEmergencyStopThreshold == 500, "Legacy configuration must default to 500.");
        var safety = new ZAxisPressureSafety();
        var motion = (MotionControlPage)RuntimeHelpers.GetUninitializedObject(typeof(MotionControlPage));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MotionControlPage).GetField("_motionOptions", flags)!.SetValue(motion, options);
        typeof(MotionControlPage).GetField("_motionOptionsStore", flags)!.SetValue(motion, store);
        typeof(MotionControlPage).GetField("_pressureSafety", flags)!.SetValue(motion, safety);
        typeof(HomePage).GetField("_motionController", flags)!.SetValue(page, motion);
        typeof(HomePage).GetMethod("LoadPressureThresholdInput", flags)!.Invoke(page, null);
        var input = (TextBox)page.FindName("ZPressureThresholdTextBox");
        var save = (Button)page.FindName("SaveZPressureThresholdButton");
        var status = (TextBlock)page.FindName("ZPressureThresholdSettingsStatusText");
        Require(input.Text == "500" && save.IsEnabled, "Parameter editor did not load active threshold.");
        input.Text = "850";
        Require(safety.Threshold == 500, "Uncommitted edits affected safety.");
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(safety.Threshold == 850 && store.Load().ZPressureEmergencyStopThreshold == 850 && status.Text.Contains("已保存"),
            "Save did not persist and activate threshold.");
        foreach (var invalid in new[] { "", "0", "-10", "1.5", "abc", "32767" })
        {
            input.Text = invalid;
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(safety.Threshold == 850 && store.Load().ZPressureEmergencyStopThreshold == 850 && status.Text.Contains("未应用"),
                "Invalid editor input replaced saved threshold.");
        }
        // Use a directory as destination to inject an actual filesystem save failure.
        typeof(MotionControlPage).GetField("_motionOptionsStore", flags)!.SetValue(motion, new MotionCardOptionsStore(directory));
        input.Text = "950";
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(safety.Threshold == 850 && options.ZPressureEmergencyStopThreshold == 850 && status.Text.Contains("未应用"),
            "Failed persistence changed active protection.");
        typeof(MotionControlPage).GetField("_motionOptionsStore", flags)!.SetValue(motion, store);
        typeof(HomePage).GetMethod("LoadPressureThresholdInput", flags)!.Invoke(page, null);
        Require(input.Text == "850", "Saved threshold was not restored to editor.");
        Console.WriteLine("PASS: configurable threshold, legacy default, UI save/validation, persistence, failed-save rollback, latch and in-flight update.");
    }
}

public class CardProxy : DispatchProxy
{
    public Dictionary<int, int>? TorqueValues;
    public Action<int>? BeforeRead;
    public bool Open = true;
    public int? FailedAxis;
    public ushort BusError;
    public bool RejectReads;
    public bool DisconnectOnLastAxis;

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "get_IsOpen") return Open;
        if (RejectReads) throw new Exception("Unexpected device read in simulation.");
        if (method.Name == "ReadBusErrorCode") return BusError;
        if (method.Name == "ReadActualTorque")
        {
            var axis = (int)args![0]!;
            BeforeRead?.Invoke(axis);
            if (axis == FailedAxis) throw new MotionCardException("SDK error 42", "nmc_get_torque", 42);
            if (axis == 11 && DisconnectOnLastAxis) Open = false;
            return TorqueValues is not null ? TorqueValues[axis] :
                axis switch { 5 => -32768, 7 => 0, 9 => 123, 11 => 32767, _ => throw new Exception("Unexpected axis") };
        }
        throw new Exception($"Unexpected device operation: {method.Name}");
    }
}
