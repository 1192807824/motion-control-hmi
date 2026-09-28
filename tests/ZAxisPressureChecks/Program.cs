using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ControlHub.Services.Motion;
using ControlHub.Views.Pages;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
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
        var apply = typeof(HomePage).GetMethod("ApplyPressureReadings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        apply.Invoke(page, new object[] { partial });
        page.Measure(new Size(1680, 854));
        page.Arrange(new Rect(0, 0, 1680, 854));
        page.UpdateLayout();
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
}

public class CardProxy : DispatchProxy
{
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
            if (axis == FailedAxis) throw new MotionCardException("SDK error 42", "nmc_get_torque", 42);
            if (axis == 11 && DisconnectOnLastAxis) Open = false;
            return axis switch { 5 => -32768, 7 => 0, 9 => 123, 11 => 32767, _ => throw new Exception("Unexpected axis") };
        }
        throw new Exception($"Unexpected device operation: {method.Name}");
    }
}
