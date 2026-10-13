using System.IO;
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
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        foreach (var value in new ushort[] { 0, 1, 32768, 65535 })
        {
            Require(DriveObject5018.ParseValue(value.ToString()) == value, "Unsigned input boundary");
            ushort stored = 0;
            var written = false;
            Require(DriveObject5018.WriteAndVerify(value, v => { stored = v; written = true; },
                () => { Require(written, "Read must follow write"); return stored; }) == value, "Verified unsigned round trip");
        }
        foreach (var text in new[] { "", "-1", "65536", "1.5", "NaN", "+1", "4294967295" })
        {
            try { DriveObject5018.ParseValue(text); throw new Exception("Invalid input accepted"); }
            catch (ArgumentException) { }
        }
        var readCalled = false;
        try { DriveObject5018.WriteAndVerify(5, _ => throw new IOException("write failed"), () => { readCalled = true; return 5; }); }
        catch (IOException) { }
        Require(!readCalled, "Failed write must not read or report success");
        try { DriveObject5018.WriteAndVerify(5, _ => { }, () => throw new IOException("read failed")); throw new Exception("Read failure accepted"); }
        catch (MotionCardException e) { Require(e.Message.Contains("回读失败"), "Read failure is distinct"); }
        try { DriveObject5018.WriteAndVerify(5, _ => { }, () => 4); throw new Exception("Mismatch accepted"); }
        catch (MotionCardException e) { Require(e.Message.Contains("写入5，回读4"), "Mismatch reports both values"); }
        foreach (var axis in new[] { 5, 7, 9, 11 }) DriveObject5018.ValidateAxis(axis);
        try { DriveObject5018.ValidateAxis(0); throw new Exception("DD axis accepted"); }
        catch (ArgumentOutOfRangeException) { }

        var calls = new List<(int Axis, ushort Value)>();
        var all = DriveObject5018.WriteAll(65535, (axis, value) => { calls.Add((axis, value)); return value; });
        Require(calls.SequenceEqual(new[] { (5, (ushort)65535), (7, (ushort)65535), (9, (ushort)65535), (11, (ushort)65535) }),
            "One click writes the same value once to each Z axis in order");
        Require(all.All(result => result.Error is null) && DriveObject5018.DescribeResults(all).StartsWith("四轴写入成功"), "All four verified");
        calls.Clear();
        var partial = DriveObject5018.WriteAll(500, (axis, value) =>
        {
            calls.Add((axis, value));
            if (axis == 7) throw new IOException("injected read failure");
            return axis == 9 ? (ushort)499 : value;
        });
        Require(calls.Count == 4 && partial[0].Error is null && partial[1].Error is not null && partial[2].Error is not null && partial[3].Error is null,
            "Partial failure retains every axis outcome and continues remaining axes");
        Require(DriveObject5018.DescribeResults(partial).StartsWith("四轴写入未全部成功"), "Partial success cannot claim all-axis success");

        _ = new Application();
        var page = new HomePage(); // Not loaded/shown: no device startup or native write.
        _ = page.DetachParameterSettingsPanel(); // MainWindow hosts this panel on the parameter page.
        page.Measure(new Size(1680, 854));
        page.Arrange(new Rect(0, 0, 1680, 854));
        page.UpdateLayout();
        var button = (Button)page.FindName("WriteDrive5018Button");
        var input = (TextBox)page.FindName("Drive5018ValueTextBox");
        var status = (TextBlock)page.FindName("Drive5018StatusText");
        Require(button.Content.ToString() == "四轴写入5018", "Button explicitly identifies all four axes");
        input.Text = "65536";
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(status.Text.Contains("0～65535") && button.IsEnabled, "Invalid input feedback and button recovery");
        foreach (var element in new FrameworkElement[] { button, input, status })
        {
            var position = element.TransformToAncestor(page).Transform(new Point());
            Require(position.Y >= 0 && position.Y + element.ActualHeight <= 854 && element.ActualWidth > 30,
                "Homepage write controls are visible");
        }
        if (args.Length > 0)
        {
            input.Text = "65535";
            status.Text = DriveObject5018.DescribeResults(all);
            page.UpdateLayout();
            var image = new RenderTargetBitmap(1680, 854, 96, 96, PixelFormats.Pbgra32);
            image.Render(page);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(args[0]); encoder.Save(file);
        }
        Console.WriteLine("PASS: UINT16 bounds, four-axis same-value writes, partial failures, readback mismatch, homepage visibility and validation feedback. No hardware writes.");
    }
}
