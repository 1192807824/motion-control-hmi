using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ControlHub.Models;
using ControlHub.Services.Devices;
using ControlHub.Services.Persistence;
using ControlHub.Views.Pages;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    [STAThread]
    private static void Main()
    {
        _ = new Application();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var dispatcher = Dispatcher.CurrentDispatcher;
        var task = RunAsync();
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "controlhub-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var batches = Path.Combine(root, "batches");
        Directory.CreateDirectory(batches);
        var legacy = new BatchMeasurement { BatchNumber = "旧批次", Value = 12, Instrument = "E4981A" };
        File.WriteAllText(Path.Combine(batches, "legacy.json"), JsonSerializer.Serialize(legacy));
        var data = new BatchMeasurementStore(batches);
        data.Append(new BatchMeasurement { BatchNumber = "新批次", Value = 5, Instrument = "SM7110" });
        using (data.BeginCollection())
        {
            try { new BatchMeasurementStore(batches).ClearAll(); throw new Exception("Clear during production was allowed."); }
            catch (InvalidOperationException) { }
            Require(data.ReadBatch("旧批次").Count == 1 && data.ReadBatch("新批次").Count == 1, "Rejected clear damaged records.");
        }
        Require(data.ClearAll() == 2 && data.ListBatches().Count == 0 && data.ReadBatch("新批次").Count == 0,
            "Clear must remove every batch and measurement.");
        // Simulate a new process's legacy scan without deleting the customer's old JSON backup.
        var scans = (ConcurrentDictionary<string, byte>)typeof(BatchMeasurementStore)
            .GetField("LegacyScans", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        scans.TryRemove(data.DatabasePath, out _);
        var reopened = new BatchMeasurementStore(batches);
        Require(reopened.ListBatches().Count == 0 && File.Exists(Path.Combine(batches, "legacy.json")),
            "Restart reimported deleted history, or removed the old backup.");
        reopened.Append(new BatchMeasurement { BatchNumber = "清空后新批次", Value = 7 });
        Require(reopened.ReadBatch("清空后新批次").Count == 1, "Database cannot collect after clearing.");
        var batchPage = new BatchQueryPage(reopened);
        Field<TextBox>(batchPage, "_batch").Text = "清空后新批次";
        await (Task)Call(batchPage, "QueryAsync")!;
        Require(Field<DataGrid>(batchPage, "_table").Items.Count == 1, "Query did not load the test data.");
        var otherPage = new BatchQueryPage(reopened);
        otherPage.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Field<TextBox>(otherPage, "_batch").Text = "清空后新批次";
        await (Task)Call(otherPage, "QueryAsync")!;
        await (Task)Call(batchPage, "ClearAllDataAsync")!;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Require(Field<DataGrid>(batchPage, "_table").Items.Count == 0 && reopened.ListBatches().Count == 0 &&
                Field<TextBlock>(batchPage, "_metricRecords").Text == "0", "Clear button workflow left stale rows/summary.");
        Require(Field<DataGrid>(otherPage, "_table").Items.Count == 0, "Other open query window retained deleted data.");
        otherPage.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Console.WriteLine("PASS: all-batch transactional clear, production lease across store instances, restart migration suppression, new writes and real query UI reset.");

        var store = new AlarmHistoryStore(Path.Combine(root, "alarms.db"));
        var day = new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Local);
        AlarmInfo Alarm(DateTime time, string code) => new() { OccurredAt = new DateTimeOffset(time), Source = "测试源", Code = code, Message = "中文报警详情", Level = "报警" };
        var first = Alarm(day, "MIDNIGHT");
        store.Append(first); store.Append(first);
        store.Append(Alarm(day.AddDays(1).AddTicks(-1), "LAST-TICK"));
        store.Append(Alarm(day.AddDays(1), "NEXT-DAY"));
        store.Append(Alarm(day.AddTicks(-1), "PREVIOUS-DAY"));
        var filtered = new AlarmHistoryStore(store.DatabasePath).Query(day, day);
        Require(filtered.Total == 2 && filtered.Records.Select(r => r.Code).ToHashSet().SetEquals(["MIDNIGHT", "LAST-TICK"]),
            "Date filtering must include local midnight through the final tick and exclude adjacent dates.");
        for (var i = 0; i < 450; i++) store.Append(Alarm(day.AddMinutes(i + 1), $"PAGE-{i}"));
        var pages = Enumerable.Range(0, 3).Select(i => store.Query(day, day, i)).ToArray();
        Require(pages.All(p => p.Total == 452) && pages.SelectMany(p => p.Records).Select(r => r.Id).Distinct().Count() == 452,
            "Pagination lost or duplicated alarms beyond 200 records.");
        try { store.Query(day.AddDays(1), day); throw new Exception("Reversed dates accepted."); }
        catch (ArgumentException) { }
        Require(store.ClearAll() == 454 && new AlarmHistoryStore(store.DatabasePath).Query(null, null).Total == 0,
            "Clear alarms must clear every date, persist across store reopen and preserve the schema.");
        Console.WriteLine("PASS: durable alarms, idempotent inserts, local calendar boundaries, all-date query, pagination and clear/reopen.");

        AlarmHistory.Initialize(store);
        await Task.Run(() => Parallel.For(0, 50, i => AlarmHistory.Record("并发源", $"ASYNC-{i}", "并发故障")));
        await AlarmHistory.FlushAsync();
        Require(store.Query(null, null).Total == 50, "Async alarm writer lost records.");
        var blocked = Path.Combine(root, "blocked");
        File.WriteAllText(blocked, "test file prevents directory creation");
        var activeStore = typeof(AlarmHistory).GetField("_store", BindingFlags.Static | BindingFlags.NonPublic)!;
        activeStore.SetValue(null, new AlarmHistoryStore(Path.Combine(blocked, "alarms.db")));
        AlarmHistory.Record("写盘故障", "RECOVERABLE", "待恢复写入");
        await AlarmHistory.FlushAsync();
        Require(AlarmHistory.LastError is not null, "Persistence failure was silently ignored.");
        activeStore.SetValue(null, store);
        await AlarmHistory.FlushAsync();
        Require(AlarmHistory.LastError is null && store.Query(null, null).Total == 51, "Pending alarm was lost or duplicated on recovery.");

        var motion = new MotionControlPage();
        Call(motion, "RecordAlarmOnce", "test-key", "MOTION-TEST", "运动测试故障");
        Call(motion, "RecordAlarmOnce", "test-key", "MOTION-TEST", "运动测试故障");
        var connection = new ConnectionConfigPage();
        Call(connection, "AddLog", "振动盘连接失败", true);
        Call(connection, "AddTcpLog", "E4981A通讯失败", true);
        Call(connection, "AddSerialLog", "SM7110通讯失败", true);
        Call(connection, "UpdateSerialMeterResult", new SM7110MeasurementResult(0, 2, "R", "invalid"));
        Call(connection, "UpdateSerialMeterResult", new SM7110MeasurementResult(0, 2, "R", "invalid"));
        var home = new HomePage();
        Call(home, "SetStartProductionStatus", "测试生产故障", Color.FromRgb(242, 122, 128));
        var vision = new VisualCalibrationPage();
        var error = Enum.Parse(typeof(VisualCalibrationPage).GetNestedType("WorkflowStatus", BindingFlags.NonPublic)!, "Error");
        Call(vision, "SetWorkflowStatus", "测试视觉故障", error);
        await AlarmHistory.FlushAsync();
        var integrated = store.Query(null, null).Records;
        foreach (var code in new[] { "MOTION-TEST", "FEEDER-COMM", "E4981A-COMM", "SM7110-COMM", "PRODUCTION", "VISION-WORKFLOW" })
            Require(integrated.Any(a => a.Code == code), $"Missing actual alarm source: {code}");
        Require(integrated.Count(a => a.Code == "MOTION-TEST") == 1 && integrated.Count(a => a.Code == "SM7110-STATUS-2") == 1,
            "Persistent motion/measurement faults flooded history.");
        var alarmPage = new AlarmHistoryPage();
        Field<DatePicker>(alarmPage, "_from").SelectedDate = null;
        Field<DatePicker>(alarmPage, "_through").SelectedDate = null;
        await alarmPage.ActivateAsync();
        Require(Field<DataGrid>(alarmPage, "_table").Items.Count >= 58, "Alarm UI did not display real persisted sources.");
        store.Append(Alarm(day.AddHours(12), "DATE-UI"));
        Field<DatePicker>(alarmPage, "_from").SelectedDate = day;
        Field<DatePicker>(alarmPage, "_through").SelectedDate = day;
        await alarmPage.ActivateAsync();
        Require(Field<DataGrid>(alarmPage, "_table").Items.Count == 1 &&
                ((AlarmInfo)Field<DataGrid>(alarmPage, "_table").Items[0]).Code == "DATE-UI", "Date pickers did not filter the displayed history.");
        Field<DatePicker>(alarmPage, "_from").SelectedDate = DateTime.Today;
        Field<DatePicker>(alarmPage, "_through").SelectedDate = DateTime.Today;
        await alarmPage.ActivateAsync();
        var artifacts = Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(artifacts);
        Render(alarmPage, Path.Combine(artifacts, "alarm-history.png"));
        Require(Field<DataGrid>(alarmPage, "_table").Columns[0].ActualWidth >= 205 &&
                Field<DataGrid>(alarmPage, "_table").Columns[3].ActualWidth >= 195,
            "Changing date filters collapsed the time/code columns.");
        Render(batchPage, Path.Combine(artifacts, "batch-clear.png"));
        await (Task)Call(alarmPage, "ClearAsync")!;
        Require((await AlarmHistory.QueryAsync(null, null)).Total == 0 && Field<DataGrid>(alarmPage, "_table").Items.Count == 0,
            "Alarm clear UI failed to remove persisted history.");
        AlarmHistory.Record("清空后", "NEW", "新报警");
        Require((await AlarmHistory.QueryAsync(null, null)).Total == 1, "New alarms were disabled by clearing history.");
        Console.WriteLine("PASS: asynchronous/concurrent logging, write-failure recovery, real motion/production/device/vision sources, deduplication and query/clear UI.");
        Console.WriteLine("UI artifacts: " + artifacts);
    }

    private static void Render(FrameworkElement element, string path)
    {
        element.Measure(new Size(1440, 850)); element.Arrange(new Rect(0, 0, 1440, 850)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1440, 850, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
