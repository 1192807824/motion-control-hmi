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
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static void Main(string[] args)
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        static void Set(object target, string name, object value) =>
            target.GetType().GetField(name, Private)!.SetValue(target, value);
        static void Refresh(HomePage page) =>
            typeof(HomePage).GetMethod("RefreshCarouselUpperVacuumControl", Private)!.Invoke(page, null);

        _ = new Application();
        var page = new HomePage(); // Never load a device page: use only the simulated card below.
        _ = page.DetachParameterSettingsPanel();
        var button = (Button)page.FindName("CarouselUpperVacuumButton");
        var state = (TextBlock)page.FindName("CarouselUpperVacuumStateText");
        Require(!button.IsEnabled, "Disconnected manual control must be disabled");

        var card = new SimulatedMotionCard(new MotionCardOptions { SimulationMode = true });
        card.Open();
        card.WriteDigitalOutput(24, true);
        card.WriteDigitalOutput(25, true);
        var controller = new MotionControlPage();
        Set(controller, "_motionCard", card);
        page.AttachMotionController(controller);
        bool High(int bit) => (card.ReadDigitalOutputs(0) & (1u << bit)) != 0;
        void Click() => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Require(button.IsEnabled && state.Text.Contains("已关闭"), "Read actual active-low output on attach");
        Click();
        Require(!High(24) && High(25) && state.Text.Contains("已开启"), "Enable suction without changing spray");
        Click();
        Require(High(24) && High(25) && state.Text.Contains("已关闭"), "Second click closes suction");
        card.WriteDigitalOutput(24, false);
        Refresh(page);
        Require(state.Text.Contains("已开启"), "Reflect changes made through another IO entry");
        Click();
        Require(High(24), "Toggle actual output rather than stale UI state");

        foreach (var flag in new[] { "_startSequenceRunning", "_oneKeyCollectRunning", "_oneKeyResetRunning",
                     "_presetPositionMoveRunning", "_drive5018Writing", "_preserveIoOnEmergencyStop" })
        {
            Set(page, flag, true);
            Refresh(page);
            Require(!button.IsEnabled, $"Manual control blocked by {flag}");
            Click();
            Require(High(24) && High(25), $"Blocked handler must leave outputs unchanged: {flag}");
            Set(page, flag, false);
        }
        Set(controller, "_motionSafetyLock", true);
        Click();
        Require(High(24) && state.Text.Contains("操作失败"), "Controller safety lock rejects manual write");
        Set(controller, "_motionSafetyLock", false);
        Click();
        Require(!High(24) && state.Text.Contains("已开启"), "Successful retry clears failure feedback");

        page.Measure(new Size(1680, 854));
        page.Arrange(new Rect(0, 0, 1680, 854));
        page.UpdateLayout();
        Require(button.ActualWidth > 250 && button.ActualHeight == 52, "Homepage button has usable size");
        var position = button.TranslatePoint(new Point(), page);
        Require(position.Y >= 0 && position.Y + button.ActualHeight <= 854, "Button fits homepage viewport");
        if (args.Length > 0)
        {
            var image = new RenderTargetBitmap(1680, 854, 96, 96, PixelFormats.Pbgra32);
            image.Render(page);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(args[0]);
            encoder.Save(stream);
        }
        card.Close();
        Refresh(page);
        Require(!button.IsEnabled && state.Text.Contains("未连接"), "Disconnect invalidates displayed state");
        Console.WriteLine("PASS: active-low toggle, spray isolation, output readback, busy/emergency interlocks, safety-lock failure, disconnect and homepage layout. Simulated IO only.");
    }
}
