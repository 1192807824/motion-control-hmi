using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class Program
{
    // Only invoke pure result readers. Never construct a window, load a solution or run a camera/motion command.
    [STAThread]
    private static int Main(string[] args)
    {
        var sdkDirectory = args.Length > 0 ? args[0] : @"D:\VM\VisionMaster4.4.0\Development\V4.x\ComControls\Assembly";
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var path = Path.Combine(sdkDirectory, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        var host = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VisionMasterHost.exe"));
        var window = host.GetType("VisionMasterHost.MainWindow", true);
        object Invoke(string name, params object[] values) => window.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, values);
        object[] Parse(string text) => ((IEnumerable)Invoke("ReadScriptResultShowRows", text)).Cast<object>().ToArray();
        float Field(object row, string name) => (float)row.GetType().GetProperty(name).GetValue(row);
        void Require(bool valid, string message) { if (!valid) throw new Exception(message); }
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var rows = Parse("X:3881.69,Y:906.5294,R:47.89328\r\n模块状态:1");
            Require(rows.Length == 1, "Screenshot contains exactly one script row");
            Require(Math.Abs(Field(rows[0], "PixelX") - 3881.69f) < .001 &&
                    Math.Abs(Field(rows[0], "PixelY") - 906.5294f) < .001 &&
                    Math.Abs(Field(rows[0], "RotationDegrees") - 47.89328f) < .0001, "Screenshot X/Y/R are preserved");
            Require(Parse("模块状态:1").Length == 0, "Status-only output is not a point");
            Require(Parse("X:1,Y:2,R:3\nX:4,Y:5,R:6").Length == 2, "Multiple points stay multiple for movement rejection");
            Require(Parse("X：3.88169e3，Y：906.5294，R：-47.89328").Length == 1, "Localized separators and exponent values");
            try { Parse("X:-1,Y:2,R:3"); throw new Exception("Negative pixels accepted"); }
            catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { }

            // Exercise the same declared float-array result reader used before ResultShow fallback.
            var arrayType = Assembly.LoadFrom(Path.Combine(sdkDirectory, "VM.PlatformSDKCS.dll"))
                .GetType("VM.PlatformSDKCS.FloatDataArray", true);
            object Array(float[] values)
            {
                var value = Activator.CreateInstance(arrayType);
                arrayType.GetField("nValueNum").SetValue(value, values.Length);
                arrayType.GetField("pFloatVal").SetValue(value, values);
                return value;
            }
            var x = Array(new[] { 3881.69f });
            var y = Array(new[] { 906.5294f });
            var r = Array(new[] { 47.89328f });
            Require((int)Invoke("GetMatchingScriptResultCount", x, y, r) == 1, "Declared X/Y/R count");
            var row = Invoke("ReadScriptResultRow", x, y, r, 0);
            Require(Field(row, "PixelX") == Field(rows[0], "PixelX") && Field(row, "PixelY") == Field(rows[0], "PixelY"),
                "Float outputs and displayed text yield identical coordinates");
            try { Invoke("GetMatchingScriptResultCount", x, Array(new[] { 1f, 2f }), r); throw new Exception("Mismatched counts accepted"); }
            catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { }
            Console.WriteLine("PASS: actual host script readers parse screenshot text and float outputs; empty, multiple, negative and mismatched results validated. No hardware accessed.");
            return 0;
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }
}
