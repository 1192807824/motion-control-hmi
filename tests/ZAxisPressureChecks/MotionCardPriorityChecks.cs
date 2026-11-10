using System.Collections.Concurrent;
using System.Reflection;
using ControlHub.Services.Motion;

internal static class MotionCardPriorityChecks
{
    public static void Run()
    {
        CheckPriorityAndReentrancy();
        CheckServoWait();
        CheckPressureBatch();
        Console.WriteLine("PASS: serialized SDK access, emergency/pressure priority, reentrant servo wait, four-axis batch ahead of reset query backlog.");
    }

    private static int Waiting(MotionCardAccessGate gate, int priority)
    {
        var state = typeof(MotionCardAccessGate).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gate)!;
        lock (state)
            return ((int[])typeof(MotionCardAccessGate).GetField("_waiting", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gate)!)[priority];
    }

    private static void Until(Func<bool> predicate) => Require(SpinWait.SpinUntil(predicate, 3000), "Worker did not queue");
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Join(Thread thread) => Require(thread.Join(3000), "Gate deadlocked");

    private static void CheckPriorityAndReentrancy()
    {
        var gate = new MotionCardAccessGate();
        var order = new ConcurrentQueue<string>();
        var inside = 0;
        var overlap = 0;
        Thread Worker(string name, MotionCardAccessPriority priority) => new(() =>
        {
            using (gate.Enter(priority))
            {
                if (Interlocked.Increment(ref inside) != 1) Interlocked.Increment(ref overlap);
                using (gate.Enter()) order.Enqueue(name); // Home->ReadAxis/ReadBusErrorCode nested access.
                Interlocked.Decrement(ref inside);
            }
        }) { IsBackground = true };
        var normals = Enumerable.Range(0, 8).Select(i => Worker("normal", MotionCardAccessPriority.Normal)).ToArray();
        var pressure = Worker("pressure", MotionCardAccessPriority.Pressure);
        var stop = Worker("stop", MotionCardAccessPriority.EmergencyStop);
        using (gate.Enter())
        {
            foreach (var thread in normals) thread.Start();
            Until(() => Waiting(gate, 0) == normals.Length);
            pressure.Start();
            Until(() => Waiting(gate, 1) == 1);
            stop.Start();
            Until(() => Waiting(gate, 2) == 1);
        }
        foreach (var thread in normals.Append(pressure).Append(stop)) Join(thread);
        Require(order.Take(2).SequenceEqual(new[] { "stop", "pressure" }) && overlap == 0 && order.Count == 10,
            "SDK must remain serial while pending emergency/pressure requests precede ordinary queries");
    }

    private static void CheckServoWait()
    {
        var gate = new MotionCardAccessGate();
        var sampled = false;
        var pressure = new Thread(() => { using (gate.Enter(MotionCardAccessPriority.Pressure)) sampled = true; }) { IsBackground = true };
        using (gate.Enter())
        using (gate.Enter())
        {
            pressure.Start();
            Until(() => Waiting(gate, 1) == 1);
            gate.SleepOutside(1);
            Require(sampled, "Servo wait must release all recursion levels and let pressure run before reacquiring");
        }
        Join(pressure);
        using (gate.Enter()) { } // Recursion depth was restored and fully released.
    }

    private static void CheckPressureBatch()
    {
        var card = DispatchProxy.Create<IMotionCard, PriorityCardProxy>();
        var fake = (PriorityCardProxy)(object)card;
        var safety = new ZAxisPressureSafety();
        var decorated = new DdInterlockedMotionCard(card, () => throw new Exception("Monitoring must not invoke DD interlock"));
        var stops = 0;
        var pressure = new Thread(() => safety.CheckOnce(decorated, () => Interlocked.Increment(ref stops), _ => { })) { IsBackground = true };
        var ordinary = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            using (fake.Gate.Enter()) { fake.Operations.Enqueue("query"); Thread.Sleep(20); }
        }) { IsBackground = true }).ToArray();
        using (fake.Gate.Enter())
        {
            foreach (var thread in ordinary) thread.Start();
            Until(() => Waiting(fake.Gate, 0) == ordinary.Length);
            pressure.Start();
            Until(() => Waiting(fake.Gate, 1) == 1);
        }
        Join(pressure);
        foreach (var thread in ordinary) Join(thread);
        Require(stops == 0 && safety.TripReason is null, "Reset query backlog must not cause a pressure timeout");
        Require(fake.Operations.Take(5).SequenceEqual(new[] { "5", "7", "9", "11", "bus" }),
            "DD decorator must retain batch priority; normal calls cannot interleave the four-axis/bus scan");
    }
}

public class PriorityCardProxy : DispatchProxy, IPriorityPressureSampling
{
    public MotionCardAccessGate Gate { get; } = new();
    public ConcurrentQueue<string> Operations { get; } = new();
    public IDisposable EnterPressureSampling() => Gate.Enter(MotionCardAccessPriority.Pressure);
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        using (Gate.Enter())
        {
            switch (method!.Name)
            {
                case "get_IsOpen": return true;
                case "ReadActualTorque": Operations.Enqueue(args![0]!.ToString()!); return 10;
                case "ReadBusErrorCode": Operations.Enqueue("bus"); return (ushort)0;
                default: throw new Exception("Unexpected SDK operation: " + method.Name);
            }
        }
    }
}
