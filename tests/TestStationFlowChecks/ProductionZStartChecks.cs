using System.Runtime.CompilerServices;
using System.Windows.Controls;
using ControlHub.Services.Motion;
using ControlHub.Views.Pages;

internal static partial class Program
{
    private static void CheckProductionZStartInterlock()
    {
        var page = (HomePage)RuntimeHelpers.GetUninitializedObject(typeof(HomePage));
        var names = new[] { "FirstSetNozzle1", "FirstSetNozzle2", "SecondSetNozzle1", "SecondSetNozzle2" };
        var axes = new[] { 5, 7, 9, 11 };
        for (var i = 0; i < axes.Length; i++)
            Set(page, names[i] + "SafeZPositionTextBox", new TextBox { Text = (-5000 + i * 1000).ToString() });
        var safe = (IReadOnlyDictionary<int, double>)Invoke(page, "ReadProductionZSafePositions")!;
        for (var i = 0; i < axes.Length; i++)
            Require(safe[axes[i]] == -5000 + i * 1000, "Safety height mapped to wrong Z axis.");

        var states = new Dictionary<int, MotionAxisSnapshot>();
        void Reset(double offset = 0)
        {
            foreach (var axis in axes) states[axis] = DdSnapshot(axis, safe[axis] + offset);
        }
        string Blocked(Action action)
        {
            try { action(); }
            catch (InvalidOperationException exception) { return exception.Message; }
            throw new Exception("Unsafe Z position allowed production start.");
        }
        foreach (var offset in new[] { -100000d, -100.001d, 0d, 99.999d, 100d })
        {
            Reset(offset);
            var reads = new List<int>();
            ProductionZStartInterlock.EnsureSafe(safe, axis => { reads.Add(axis); return states[axis]; });
            Require(reads.SequenceEqual(axes), "All four Z axes must be read on every start.");
        }
        foreach (var axis in axes)
        {
            Reset();
            states[axis] = DdSnapshot(axis, safe[axis] + 100.001);
            var message = Blocked(() => ProductionZStartInterlock.EnsureSafe(safe, id => states[id]));
            Require(message.Contains($"轴{axis}") && message.Contains("实时位置") && message.Contains("允许上限"),
                "Alarm must identify the failing axis, feedback and limit.");
            foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                Reset();
                states[axis] = DdSnapshot(axis, invalid);
                Blocked(() => ProductionZStartInterlock.EnsureSafe(safe, id => states[id]));
            }
            Reset();
            states[axis] = DdSnapshot(axis + 1, safe[axis]);
            Blocked(() => ProductionZStartInterlock.EnsureSafe(safe, id => states[id]));
            states.Remove(axis);
            Require(Blocked(() => ProductionZStartInterlock.EnsureSafe(safe, id => states[id])).Contains("无法读取实时位置"),
                "Feedback read failures must block start.");
        }
        Reset(101);
        var allFailures = Blocked(() => ProductionZStartInterlock.EnsureSafe(safe, id => states[id]));
        Require(axes.All(axis => allFailures.Contains($"轴{axis}")), "Alarm should report all unsafe axes together.");
        Blocked(() => ProductionZStartInterlock.EnsureSafe(new Dictionary<int, double>(), id => states[id]));
        var invalidSafe = safe.ToDictionary(pair => pair.Key, pair => pair.Value);
        invalidSafe[5] = double.NaN;
        Reset();
        Blocked(() => ProductionZStartInterlock.EnsureSafe(invalidSafe, id => states[id]));
        Console.WriteLine("PASS: four Z safety-height mappings, fresh feedback, <= safe+100 including equality/no lower bound, each axis above limit, invalid/missing feedback/settings and combined alarm. No hardware opened.");
    }
}
