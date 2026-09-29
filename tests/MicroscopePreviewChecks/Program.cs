using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ControlHub.Views.Pages;

internal static class Program
{
    private static UsbMicroscopePage _microscope = null!;
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    [STAThread]
    private static void Main(string[] args)
    {
        // Only construct the views: no main window, camera connection, or motion controller.
        _ = new Application();
        _microscope = new UsbMicroscopePage();
        var home = new HomePage();
        home.DetachParameterSettingsPanel();
        home.AttachUsbMicroscopeController(_microscope);
        _microscope.Visibility = Visibility.Collapsed;
        var homeHost = (ContentControl)home.FindName("HomeMicroscopePreviewHost");
        var defaultHost = Named<ContentControl>("DefaultPreviewHost");
        var workspace = Named<Grid>("MicroscopeWorkspace");
        Require(ReferenceEquals(homeHost.Content, workspace), "Initial preview is on home");
        Require(defaultHost.Content is null, "Only one host owns the workspace");

        var frame = MakeFrame();
        Set("_latestFrame", frame);
        Named<Image>("PreviewImage").Source = frame;
        Named<StackPanel>("PreviewPlaceholder").Visibility = Visibility.Collapsed;
        Layout(home, 1440, 820);
        Require(Named<Canvas>("AnnotationCanvas").ActualHeight > 100, "Home image has usable height");

        Click("DrawRectangleButton");
        AddPoint("AddRectangleFitPoint", new Point(160, 120));
        AddPoint("AddRectangleFitPoint", new Point(320, 120));
        Click("UndoPointButton");
        Require(Get<List<Point>>("_rectangleFitPointsPixels").Count == 1, "Undo removes the last corner");

        // Move an unfinished annotation to the full page, then finish it on home.
        _microscope.UseDefaultPreview();
        _microscope.Visibility = Visibility.Visible;
        home.Visibility = Visibility.Collapsed;
        Layout(_microscope, 1440, 820);
        Require(homeHost.Content is null && ReferenceEquals(defaultHost.Content, workspace), "Full page owns the same workspace");
        Require(Get<List<Point>>("_rectangleFitPointsPixels").Count == 1, "Unfinished corners survive navigation");
        _microscope.UseHomePreview();
        home.Visibility = Visibility.Visible;
        _microscope.Visibility = Visibility.Collapsed;
        Layout(home, 1440, 820);
        AddPoint("AddRectangleFitPoint", new Point(320, 280));
        AddPoint("AddRectangleFitPoint", new Point(320, 120));
        AddPoint("AddRectangleFitPoint", new Point(160, 280));
        Require(Get<bool>("_hasRectangle") && Get<bool>("_rectangleIsSquare"), "Four unordered corners create a square on home");
        Require(Named<Polygon>("RectangleAnnotationPolygon").Visibility == Visibility.Visible, "Square is visible");
        Require(Named<TextBlock>("AnnotationStatusText").Text.Contains("正方形"), "Home shows annotation instructions/result");
        var corners = Get<Point[]>("_rectangleCornersPixels").ToArray();
        var annotated = (BitmapSource)Invoke("CreateAnnotatedFrame", frame)!;
        Require(!ReferenceEquals(annotated, frame) && annotated.PixelWidth == 640, "Photo includes annotations at source resolution");

        Set("_zoomScale", 2d);
        Invoke("ApplyZoomTransform");
        for (var i = 0; i < 3; i++)
        {
            _microscope.UseDefaultPreview();
            _microscope.Visibility = Visibility.Visible;
            home.Visibility = Visibility.Collapsed;
            Layout(_microscope, 1440, 820);
            _microscope.UseHomePreview();
            home.Visibility = Visibility.Visible;
            _microscope.Visibility = Visibility.Collapsed;
            Layout(home, 1440, 820);
        }
        Require(ReferenceEquals(_microscope.LatestPreviewFrame, frame), "Frame survives repeated navigation");
        Require(Get<Point[]>("_rectangleCornersPixels").SequenceEqual(corners), "Rectangle pixel coordinates survive resizing/navigation");
        Require(Get<double>("_zoomScale") == 2, "Zoom survives navigation");
        Click("ResetZoomButton");
        Require(Get<double>("_zoomScale") == 1, "Reset zoom works on home");
        Layout(home, 1440, 820);

        if (args.Length > 0)
        {
            Directory.CreateDirectory(args[0]);
            Save(home, System.IO.Path.Combine(args[0], "home-square.png"));
        }

        Click("ClearAnnotationButton");
        Require(!Get<bool>("_hasRectangle"), "Clear works on home");
        Click("DrawCircleButton");
        AddPoint("AddCircleFitPoint", new Point(240, 160));
        AddPoint("AddCircleFitPoint", new Point(320, 240));
        AddPoint("AddCircleFitPoint", new Point(240, 320));
        Click("DrawCircleButton");
        Require(Get<bool>("_hasCircle") && !Get<bool>("_circlePointSelectionEnabled"), "Circle can be fitted and finished on home");
        _microscope.UseDefaultPreview();
        _microscope.Visibility = Visibility.Visible;
        home.Visibility = Visibility.Collapsed;
        Layout(_microscope, 1440, 820);
        Require(Named<System.Windows.Shapes.Ellipse>("CircleAnnotationEllipse").Visibility == Visibility.Visible, "Circle remains visible on full page");

        _microscope.UseHomePreview();
        home.Visibility = Visibility.Visible;
        _microscope.Visibility = Visibility.Collapsed;
        Layout(home, 1160, 650);
        Require(Named<Canvas>("AnnotationCanvas").ActualHeight > 80, "Compact home retains an image viewport");
        foreach (var name in new[] { "DrawCircleButton", "DrawRectangleButton", "UndoPointButton", "ClearAnnotationButton", "RotateDdButton", "CaptureButton" })
        {
            var button = Named<Button>(name);
            var position = button.TranslatePoint(new Point(), workspace);
            Require(position.X >= 0 && position.X + button.ActualWidth <= workspace.ActualWidth + 1, $"{name} fits home width");
        }
        _microscope.Shutdown();
        Console.WriteLine("PASS: shared preview ownership, square/circle tools, undo, clear, annotated photo, zoom, repeated navigation, compact layout. No hardware commands sent.");
    }

    private static void AddPoint(string method, Point pixel)
    {
        var rect = (Rect)Invoke("GetDisplayedImageRect")!;
        Invoke(method, new Point(rect.X + pixel.X * rect.Width / 640, rect.Y + pixel.Y * rect.Height / 480), rect);
    }

    private static void Click(string name) => Named<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Named<T>(string name) => (T)_microscope.FindName(name);
    private static T Get<T>(string name) => (T)typeof(UsbMicroscopePage).GetField(name, PrivateInstance)!.GetValue(_microscope)!;
    private static void Set(string name, object value) => typeof(UsbMicroscopePage).GetField(name, PrivateInstance)!.SetValue(_microscope, value);
    private static object? Invoke(string name, params object[] args) => typeof(UsbMicroscopePage).GetMethod(name, PrivateInstance)!.Invoke(_microscope, args);
    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    private static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static BitmapSource MakeFrame()
    {
        var pixels = new byte[640 * 480 * 4];
        for (var y = 0; y < 480; y++)
        for (var x = 0; x < 640; x++)
        {
            var offset = (y * 640 + x) * 4;
            var grid = x % 80 == 0 || y % 80 == 0;
            pixels[offset] = grid ? (byte)100 : (byte)45;
            pixels[offset + 1] = grid ? (byte)90 : (byte)35;
            pixels[offset + 2] = grid ? (byte)80 : (byte)25;
            pixels[offset + 3] = 255;
        }
        var frame = BitmapSource.Create(640, 480, 96, 96, PixelFormats.Bgra32, null, pixels, 640 * 4);
        frame.Freeze();
        return frame;
    }

    private static void Save(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
