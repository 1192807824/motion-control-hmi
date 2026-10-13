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

        _ = new Application();
        var page = new HomePage(); // Not loaded/shown: no device startup or native write.
        _ = page.DetachParameterSettingsPanel(); // MainWindow hosts this panel on the parameter page.
        page.Measure(new Size(1680, 854));
        page.Arrange(new Rect(0, 0, 1680, 854));
        page.UpdateLayout();
        var button = (Button)page.FindName("WriteDrive5018Button");
        var input = (TextBox)page.FindName("Drive5018ValueTextBox");
        var status = (TextBlock)page.FindName("Drive5018StatusText");
        var axes = (ComboBox)page.FindName("Drive5018AxisComboBox");
        Require(axes.Items.Cast<ComboBoxItem>().Select(item => item.Tag.ToString()).SequenceEqual(new[] { "5", "7", "9", "11" }), "Correct Z axis targets");
        input.Text = "65536";
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(status.Text.Contains("0～65535") && button.IsEnabled, "Invalid input feedback and button recovery");
        foreach (var element in new FrameworkElement[] { button, input, status, axes })
        {
            var position = element.TransformToAncestor(page).Transform(new Point());
            Require(position.Y >= 0 && position.Y + element.ActualHeight <= 854 && element.ActualWidth > 30,
                "Homepage write controls are visible");
        }
        if (args.Length > 0)
        {
            input.Text = "500";
            status.Text = "输入0～65535；写入后回读核对";
            page.UpdateLayout();
            var image = new RenderTargetBitmap(1680, 854, 96, 96, PixelFormats.Pbgra32);
            image.Render(page);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(args[0]); encoder.Save(file);
        }
        Console.WriteLine("PASS: UINT16 bounds, write/read order, SDK write/read failures, mismatch, Z axis selection, homepage visibility and validation feedback. No hardware writes.");
    }
}
