using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ControlHub.Services.Persistence;
using ControlHub.Views.Pages;

internal static partial class Program
{
    private static void RunMemorySoak(int count)
    {
        _ = new Application();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var page = new HomePage();
        Set(page, "_loadingPresetPositions", true);
        Field<ToggleButton>(page, "Station1EnabledToggle").IsChecked = true;
        Field<System.Windows.Controls.ComboBox>(page, "Station1InstrumentComboBox").SelectedIndex = 1;
        Set(page, "_loadingPresetPositions", false);
        var rows = Field<BatchMeasurementStatistics>(page, "_batchStatistics");
        page.Measure(new Size(1664, 854));
        page.Arrange(new Rect(0, 0, 1664, 854));
        var surface = new RenderTargetBitmap(1664, 854, 96, 96, PixelFormats.Pbgra32);
        var watch = Stopwatch.StartNew();
        void Sample(int n)
        {
            using var process = Process.GetCurrentProcess();
            Console.WriteLine($"SOAK n={n} elapsed={watch.Elapsed.TotalSeconds:F1}s privateMB={process.PrivateMemorySize64 / 1048576d:F1} managedMB={GC.GetTotalMemory(false) / 1048576d:F1} allocatedMB={GC.GetTotalAllocatedBytes() / 1048576d:F1} gen2={GC.CollectionCount(2)}");
        }
        Sample(0);
        for (var i = 0; i < count; i++)
        {
            rows.Add(new BatchMeasurement { BatchNumber = "SOAK", StationNumber = 5,
                Instrument = "E4981A", MeasurementMode = "C", Unit = "nF", Value = 1 + (i % 101) * 0.001,
                ValidReading = true, StationPassed = i % 10 != 0,
                Description = $"测试结果 {i}", RawResponse = $"0,{1 + i % 101 * 0.001},0.001,1" });
            Invoke(page, "RefreshDistributionCharts");
            page.UpdateLayout();
            if (i % 10 == 0)
            {
                surface.Clear();
                surface.Render(page);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }
            if ((i + 1) % 5000 == 0) Sample(i + 1);
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Sample(count);
        Console.WriteLine("PASS: production chart soak completed without hardware or database writes.");
    }
}
