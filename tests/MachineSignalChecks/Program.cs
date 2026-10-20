using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ControlHub.Services.Motion;
using ControlHub.ViewModels;
using ControlHub.Views.Pages;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static void Main()
    {
        _ = new Application();
        CheckController();
        CheckPageIntegration();
        CheckNormalShutdown();
        CheckFullShutdown();
        Console.WriteLine("PASS: state colors, buzzer disabled for all states and manual commands, reset, emergency ordering, shutdown verification, write retries and process IO isolation. Simulated hardware only.");
    }

    private static void CheckController()
    {
        var card = DispatchProxy.Create<IMotionCard, RecordingCard>();
        var raw = (RecordingCard)(object)card;
        raw.Inner.Open();
        var signals = new MachineSignalController();
        signals.Apply(card);
        CheckOutputs(raw, red: false, green: false, yellow: true, buzzer: false);
        var count = raw.Calls.Count;
        signals.Apply(card);
        Require(raw.Calls.Count == count, "Stable states must not write outputs on every 5ms poll");
        signals.SetOperatingState(MachineOperatingState.Running);
        signals.Apply(card);
        CheckOutputs(raw, red: false, green: true, yellow: false, buzzer: false);
        foreach (var state in new[] { MachineOperatingState.Standby, MachineOperatingState.Paused,
                     MachineOperatingState.Resetting, MachineOperatingState.Stopping })
        {
            signals.SetOperatingState(state);
            signals.Apply(card);
            CheckOutputs(raw, red: false, green: false, yellow: true, buzzer: false);
        }

        signals.UpdateActiveFaults(["axis:1"]);
        signals.ReportAlarm("AXIS-1");
        signals.Apply(card);
        CheckOutputs(raw, red: true, green: false, yellow: false, buzzer: false);
        signals.SilenceBuzzer();
        signals.Apply(card);
        signals.ResetLatchedAlarms();
        signals.UpdateActiveFaults(["axis:1"]);
        signals.Apply(card);
        CheckOutputs(raw, red: true, green: false, yellow: false, buzzer: false);
        signals.UpdateActiveFaults(["axis:1", "axis:2"]);
        signals.Apply(card);
        Require(!raw.High(0), "A different new fault must keep the disabled buzzer off");
        signals.SilenceBuzzer();
        signals.UpdateActiveFaults([]);
        signals.UpdateActiveFaults(["axis:1"]);
        signals.Apply(card);
        Require(!raw.High(0), "A recurring fault must keep the disabled buzzer off");
        signals.UpdateActiveFaults([]);
        signals.SilenceBuzzer();
        signals.Apply(card);
        raw.Inner.WriteDigitalOutput(0, true);
        signals.SilenceBuzzer();
        signals.Apply(card);
        Require(!raw.High(0), "Silence must close a buzzer manually opened through the IO page");

        signals.ReportAlarm("WRITE-FAILURE-TEST");
        raw.FailBit = 29;
        try { signals.Apply(card); throw new Exception("Output failure was hidden"); }
        catch (AggregateException) { }
        Require(!raw.High(0), "A failed lamp output must not open the buzzer");
        raw.FailBit = null;
        signals.Apply(card);
        Require(raw.High(29), "Failed output must retry rather than be cached as successful");
        signals.Shutdown(card);
        CheckOutputs(raw, red: false, green: false, yellow: false, buzzer: false);
        Require(raw.Writes.All(write => write.Bit is 0 or 29 or 30 or 31), "Signal controller must never write process IO");
    }

    private static void CheckPageIntegration()
    {
        var card = DispatchProxy.Create<IMotionCard, RecordingCard>();
        var raw = (RecordingCard)(object)card;
        raw.Inner.Open();
        for (var bit = 1; bit < 29; bit++) raw.Inner.WriteDigitalOutput(bit, bit % 2 == 0);
        var processBefore = raw.Inner.ReadDigitalOutputs(0) & 0x1ffffffe;
        var motion = new MotionControlPage(); // Not loaded/shown: no native hardware connection.
        Set(motion, "_motionCard", card);
        var viewModel = new MainWindowViewModel();
        viewModel.PopulateMotionAxes(1);
        foreach (var axis in viewModel.Axes) { axis.IsAvailable = true; axis.StatusReadHealthy = true; }
        motion.DataContext = viewModel;
        var home = new HomePage();
        home.AttachMotionController(motion);
        Pump();
        Require(motion.SetDigitalOutputHardwareBit(0, true) && !raw.High(0), "Hardware-bit API must not enable disabled buzzer");
        Require(motion.SetDigitalOutputChannel(0, true) && !raw.High(0), "Channel API must not enable disabled buzzer");
        var manualOutput = new System.Windows.Controls.Button
        {
            DataContext = new ControlHub.Models.IoPoint
            { Channel = 0, HardwareBitNo = 0, Kind = ControlHub.Models.IoPointKind.DigitalOutput }
        };
        Call(motion, "DigitalOutput_Click", manualOutput, new RoutedEventArgs());
        Require(!raw.High(0), "Manual IO button must not enable disabled buzzer");
        CheckOutputs(raw, red: false, green: false, yellow: true, buzzer: false);
        Set(home, "_startSequenceRunning", true);
        Call(home, "UpdateHomeCommandState");
        Pump();
        CheckOutputs(raw, red: false, green: true, yellow: false, buzzer: false);
        Set(home, "_productionPauseRequested", true);
        Call(home, "UpdateHomeCommandState");
        Pump();
        CheckOutputs(raw, red: false, green: false, yellow: true, buzzer: false);
        Set(home, "_productionPauseRequested", false);
        Call(home, "UpdateHomeCommandState");
        Pump();
        raw.Inner.WriteDigitalOutput(0, true);
        Set(motion, "_calibrationOperationActive", true);
        Call(motion, "ClearAlarm_Click", motion, new RoutedEventArgs());
        Pump();
        CheckOutputs(raw, red: false, green: true, yellow: false, buzzer: false);
        Set(motion, "_calibrationOperationActive", false);
        Set(home, "_startSequenceRunning", false);
        Call(home, "UpdateHomeCommandState");
        Pump();

        var before = raw.Writes.Count;
        motion.ReportMachineSignalAlarm("TEST-ALARM");
        Require(raw.Writes.Count == before, "Alarm recording must not perform IO before the safety stop");
        Pump();
        CheckOutputs(raw, red: true, green: false, yellow: false, buzzer: false);
        Set(motion, "_motionSafetyLock", true);
        Call(motion, "ClearAlarm_Click", motion, new RoutedEventArgs());
        Pump();
        CheckOutputs(raw, red: true, green: false, yellow: false, buzzer: false);
        Require((bool)Get(motion, "_motionSafetyLock")!, "Silence must not release the motion safety lock");
        motion.ReportMachineSignalAlarm("NEW-FAULT");
        Pump();
        Require(!raw.High(0), "A new alarm must leave the disabled buzzer off");
        Set(motion, "_calibrationOperationActive", true);
        var clears = raw.ClearCount;
        Call(motion, "ClearAlarm_Click", motion, new RoutedEventArgs());
        Pump();
        Require(!raw.High(0) && raw.ClearCount == clears, "Busy reset must mute without clearing active motion alarms");
        Set(motion, "_calibrationOperationActive", false);
        Set(motion, "_motionSafetyLock", false);

        raw.AxisAlarm = true;
        Call(motion, "PollMotionState");
        Call(motion, "ClearAlarm_Click", motion, new RoutedEventArgs());
        Pump();
        CheckOutputs(raw, red: true, green: false, yellow: false, buzzer: false);
        raw.AxisAlarm = false;
        Call(motion, "PollMotionState");
        CheckOutputs(raw, red: false, green: false, yellow: true, buzzer: false);
        raw.AxisAlarm = true;
        Call(motion, "PollMotionState");
        Pump();
        Require(!raw.High(0), "A recurring hardware fault must leave the disabled buzzer off");

        Set(home, "_startSequenceRunning", true);
        Call(home, "UpdateHomeCommandState");
        Pump();
        raw.Calls.Clear();
        Call(motion, "IssueEmergencyStopAndNotify");
        Require(raw.Calls.SequenceEqual(new[] { "EmergencyStop" }), "Hardware emergency stop must precede indicator IO");
        Require((bool)Get(home, "_preserveIoOnEmergencyStop")!, "Emergency notification must still freeze process IO");
        Pump();
        Require(raw.High(29) && !raw.High(0), "Emergency stop must indicate red without opening the disabled buzzer");
        Require(!raw.Writes.Any(write => write.Bit == 0 && write.High), "Program must never write Y00=1");
        Require((raw.Inner.ReadDigitalOutputs(0) & 0x1ffffffe) == processBefore,
            "Startup/state changes, reset/mute and emergency signaling must preserve every process output");
    }

    private static void CheckNormalShutdown()
    {
        var card = DispatchProxy.Create<IMotionCard, RecordingCard>();
        var raw = (RecordingCard)(object)card;
        raw.Inner.Open();
        var motion = new MotionControlPage();
        Set(motion, "_motionCard", card);
        var home = new HomePage();
        home.AttachMotionController(motion);
        Pump();
        raw.Calls.Clear();
        Call(motion, "IssueHardwareEmergencyStopAndNotify", false);
        Pump();
        Require(raw.Calls.First() == "EmergencyStop", "Normal shutdown must still issue hardware safe stop");
        Require(!raw.High(0) && !raw.Writes.Any(write => write.Bit == 0 && write.High),
            "Normal shutdown stop must never queue an emergency buzzer");

        // Even an externally/manual switched buzzer must be closed before disconnect.
        raw.Inner.WriteDigitalOutput(0, true);
        var shutdown = typeof(MotionControlPage).GetMethod("TryShutdownMachineSignals", Private)!;
        object?[] args = [null];
        Require((bool)shutdown.Invoke(motion, args)!, "Signal shutdown must verify outputs off");
        CheckOutputs(raw, red: false, green: false, yellow: false, buzzer: false);
        Set(motion, "_closed", true);
        motion.ReportMachineSignalAlarm("LATE-CALLBACK");
        Pump();
        Require(!raw.High(0), "Delayed callbacks after close must not reopen the buzzer");

        var failed = new MachineSignalController();
        raw.IgnoreBuzzerOff = true;
        raw.Inner.WriteDigitalOutput(0, true);
        try { failed.Shutdown(card); throw new Exception("False off readback accepted"); }
        catch (InvalidOperationException exception)
        {
            Require(exception.Message.Contains("回读不符"), "Shutdown must check real off state");
        }
        raw.IgnoreBuzzerOff = false;
        failed.ReportAlarm("REAL-SHUTDOWN-FAILURE");
        failed.Apply(card);
        Require(raw.High(29) && !raw.High(0), "Failed shutdown must show red and keep the buzzer off");
        failed.Shutdown(card);
        Require(!raw.High(0), "Retry can close buzzer after failed shutdown");
    }

    private static void CheckFullShutdown()
    {
        var directory = Path.Combine(Path.GetTempPath(), "machine-shutdown-" + Guid.NewGuid().ToString("N"));
        var card = DispatchProxy.Create<IMotionCard, RecordingCard>();
        var raw = (RecordingCard)(object)card;
        raw.Inner.Open();
        var motion = new MotionControlPage();
        Set(motion, "_motionCard", card);
        Set(motion, "_motionOptionsStore", new MotionCardOptionsStore(Path.Combine(directory, "motion-settings.json")));
        var home = new HomePage();
        home.AttachMotionController(motion);
        Pump();
        raw.Writes.Clear();
        Require(motion.TryShutdown(out var error), $"Normal complete shutdown failed: {error}");
        Pump();
        Require(raw.SignalsAtClose == 0 && !card.IsOpen, "All signal outputs must be off before disconnect");
        Require(!raw.Writes.Any(write => write.Bit == 0 && write.High), "Start then close must never write buzzer on");
        Require(motion.TryShutdown(out _), "Repeated close must be harmless");

        var failedCard = DispatchProxy.Create<IMotionCard, RecordingCard>();
        var failedRaw = (RecordingCard)(object)failedCard;
        failedRaw.Inner.Open();
        failedRaw.IgnoreBuzzerOff = true;
        failedRaw.Inner.WriteDigitalOutput(0, true);
        var failedMotion = new MotionControlPage();
        Set(failedMotion, "_motionCard", failedCard);
        Set(failedMotion, "_motionOptionsStore", new MotionCardOptionsStore(Path.Combine(directory, "failed", "motion-settings.json")));
        Require(!failedMotion.TryShutdown(out error) && error.Contains("蜂鸣器"), "Failed off verification must block close with a useful error");
        Require(failedCard.IsOpen && !(bool)Get(failedMotion, "_closed")!, "Failed close must keep hardware connection available for retry");
        failedRaw.IgnoreBuzzerOff = false;
        Require(failedMotion.TryShutdown(out error), $"Close retry failed: {error}");
        Pump();
        Require(failedRaw.SignalsAtClose == 0, "Retry must also verify output off before close");
    }

    private static void CheckOutputs(RecordingCard raw, bool red, bool green, bool yellow, bool buzzer) =>
        Require(raw.High(29) == red && raw.High(30) == green && raw.High(31) == yellow && raw.High(0) == buzzer,
            $"Wrong signals: expected red={red}, green={green}, yellow={yellow}, buzzer={buzzer}; actual port={raw.Inner.ReadDigitalOutputs(0):X8}");
    private static object? Get(object target, string field) => target.GetType().GetField(field, Private)!.GetValue(target);
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Private)!.SetValue(target, value);
    private static void Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Private)!.Invoke(target, args);
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}

public class RecordingCard : DispatchProxy
{
    public SimulatedMotionCard Inner { get; } = new(new MotionCardOptions { SimulationMode = true });
    public List<string> Calls { get; } = [];
    public List<(int Bit, bool High)> Writes { get; } = [];
    public int? FailBit { get; set; }
    public bool AxisAlarm { get; set; }
    public int ClearCount { get; private set; }
    public bool IgnoreBuzzerOff { get; set; }
    public uint? SignalsAtClose { get; private set; }
    public bool High(int bit) => (Inner.ReadDigitalOutputs(0) & (1u << bit)) != 0;

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == nameof(IMotionCard.WriteDigitalOutput))
        {
            var bit = (int)args![0]!;
            var high = (bool)args[1]!;
            Calls.Add($"Y{bit:00}={high}");
            Writes.Add((bit, high));
            if (bit == FailBit) throw new MotionCardException("Injected signal output failure");
            if (IgnoreBuzzerOff && bit == 0 && !high) return null;
        }
        if (method.Name == nameof(IMotionCard.EmergencyStop)) Calls.Add("EmergencyStop");
        if (method.Name == nameof(IMotionCard.ClearAlarms)) ClearCount++;
        if (method.Name == nameof(IMotionCard.Close)) SignalsAtClose = Inner.ReadDigitalOutputs(0) & 0xe0000001;
        if (method.Name == nameof(IMotionCard.ReadAxis) && AxisAlarm)
            return Inner.ReadAxis((int)args![0]!) with { Alarm = true, AxisErrorCode = 42 };
        try { return method.Invoke(Inner, args); }
        catch (TargetInvocationException exception) { throw exception.InnerException!; }
    }
}
