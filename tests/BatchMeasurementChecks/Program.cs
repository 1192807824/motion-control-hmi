using System.IO;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ControlHub.Services.Devices;
using ControlHub.Services.Persistence;
using ControlHub.Views.Pages;
using ControlHub.Views.Dialogs;
using Microsoft.Data.Sqlite;
using Microsoft.VisualBasic.FileIO;

internal static partial class Program
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static object? Invoke(object? target, string name, params object[] args) =>
        typeof(HomePage).GetMethod(name, Flags)!.Invoke(target, args);
    private static void Set(object target, string name, object value) => typeof(HomePage).GetField(name, Flags)!.SetValue(target, value);
    private static T Field<T>(object target, string name) => (T)typeof(HomePage).GetField(name, Flags)!.GetValue(target)!;
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }

    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--create-demo"])
        {
            CreateDemo();
            return;
        }
        if (args is ["--preview-query"])
        {
            PreviewQueryDesign();
            return;
        }
        if (args is ["--verify-default-sqlite"])
        {
            VerifyDefaultSqlite();
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), "batch-checks-" + Guid.NewGuid().ToString("N"));
        var batch = "QA/批次:一";
        var legacyDirectory = Path.Combine(root, "legacy-json");
        Directory.CreateDirectory(legacyDirectory);
        File.WriteAllText(Path.Combine(legacyDirectory, "batch.json"), JsonSerializer.Serialize(batch));
        for (var i = 0; i < 40; i++)
        {
            var legacy = new BatchMeasurement { BatchNumber = batch, Value = i, StationNumber = 5,
                Id = Guid.NewGuid(), ProductId = Guid.NewGuid(), MeasuredAt = DateTimeOffset.Now.AddSeconds(i) };
            File.WriteAllText(Path.Combine(legacyDirectory, $"{i:D3}.json"), JsonSerializer.Serialize(legacy));
        }
        File.WriteAllText(Path.Combine(legacyDirectory, "interrupted.tmp"), "{");
        var store = new BatchMeasurementStore(root);
        store.Append(new BatchMeasurement { BatchNumber = "QA-2", Value = 99, StationNumber = 6 });
        Require(new BatchMeasurementStore(root).ReadBatch(batch).Count == 40, "Restart persistence failed");
        Require(store.ReadBatch("QA-2").Count == 1 && store.ListBatches().Count == 2, "Batch isolation failed");
        var signature = new byte[16];
        using (var database = new FileStream(store.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            Require(database.Read(signature) == signature.Length && signature.SequenceEqual(Encoding.ASCII.GetBytes("SQLite format 3\0")),
                "Storage is not a SQLite database");
        Require(store.ReadBatch(batch).Count == 40, "Legacy JSON migration or interrupted-file isolation failed");
        Parallel.For(0, 40, i => store.Append(new BatchMeasurement { BatchNumber = "QA-CONCURRENT", Value = i,
            StationNumber = i % 2 == 0 ? 5 : 6, Id = Guid.NewGuid(), ProductId = Guid.NewGuid() }));
        Require(store.ReadBatch("QA-CONCURRENT").Count == 40, "Concurrent station commits lost rows");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath,
                   Mode = SqliteOpenMode.ReadOnly }.ToString()))
        {
            connection.Open();
            using var integrity = connection.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            Require(string.Equals(integrity.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase),
                "SQLite integrity check failed");
        }
        foreach (var invalid in new[] { " ", "x\ny", new string('x', 81) })
        {
            try { store.PrepareBatch(invalid); throw new Exception("Invalid batch accepted"); }
            catch (ArgumentException) { }
        }
        var statistics = MeasurementDistribution.Calculate([1, 2, 3, double.NaN, double.PositiveInfinity]);
        Require(statistics.Count == 3 && statistics.Mean == 2 && statistics.StandardDeviation == 1, "Statistics incorrect");
        Require(MeasurementDistribution.Calculate([7, 7]).StandardDeviation == 0, "Constant data failed");
        Require(MeasurementDistribution.Calculate([]).Count == 0, "Empty data failed");
        Console.WriteLine("PASS: SQLite signature, JSON migration, durable restart, batch isolation, concurrent commits, validation and sample statistics.");

        _ = new Application();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var page = new HomePage();
        Set(page, "_batchStore", store);
        Set(page, "_loadingPresetPositions", true); // UI fixture must not save local machine settings.
        Field<TextBox>(page, "BatchNumberTextBox").Text = "QA-CAPTURE";
        for (var i = 1; i <= 3; i++) Field<ToggleButton>(page, $"Station{i}EnabledToggle").IsChecked = i <= 2;
        Field<ComboBox>(page, "Station1InstrumentComboBox").SelectedIndex = (int)TestStationInstrument.E4981A;
        Field<ComboBox>(page, "Station2InstrumentComboBox").SelectedIndex = (int)TestStationInstrument.SM7110;
        Field<ComboBox>(page, "SM7110LimitModeComboBox").SelectedValue = "R";
        Set(page, "_loadingPresetPositions", false);
        Invoke(page, "PrepareProductionBatch");
        var charts = Field<UniformGrid>(page, "DistributionChartsPanel");
        Require(charts.Children.Count == 2, "Two stations must show two charts");
        Field<ToggleButton>(page, "Station2EnabledToggle").IsChecked = false;
        Invoke(page, "RefreshDistributionCharts");
        Require(charts.Children.Count == 1, "One station must show one chart");
        Field<ToggleButton>(page, "Station2EnabledToggle").IsChecked = true;

        var states = (Array)Invoke(null, "CreateCarouselStationStates")!;
        var product = states.GetValue(1)!;
        product.GetType().GetMethod("SetLoaded")!.Invoke(product, null);
        var e = new E4981AMeasurementResult(0, 1e-9, 0.02, 2, "0,1e-9,0.02,2");
        var result = Invoke(null, "ClassifyE4981AMeasurement", e)!;
        Wait((Task)Invoke(page, "SaveStationMeasurementAsync", 5, product, result, 2)!);
        Require(ChartTotal(charts.Children[0]) == 1 && ChartTotal(charts.Children[1]) == 0,
            "First station chart did not refresh immediately after its own result");
        Require((bool)product.GetType().GetProperty("Tested")!.GetValue(product)!,
            "First station result was not published immediately");
        var rangeType = typeof(HomePage).GetNestedType("SM7110AcceptanceRange", BindingFlags.NonPublic)!;
        var range = Activator.CreateInstance(rangeType, 5e10, double.PositiveInfinity, "R")!;
        Set(page, "_sm7110AcceptanceRange", range);
        var sm = new SM7110MeasurementResult(5.2e10, 0, "R", "0,5.2e10") { TestElapsedSeconds = 0.4 };
        result = Invoke(null, "ClassifySM7110Measurement", sm, "BIN2", range)!;
        Wait((Task)Invoke(page, "SaveStationMeasurementAsync", 6, product, result, 1)!);
        Require(ChartTotal(charts.Children[0]) == 1 && ChartTotal(charts.Children[1]) == 1,
            "Second station chart did not refresh independently");
        sm = sm with { Value = 2e10, TimedOut = true, TestElapsedSeconds = 1 };
        result = Invoke(null, "ClassifySM7110Measurement", sm, "BIN2", range)!;
        Wait((Task)Invoke(page, "SaveStationMeasurementAsync", 6, product, result, 3)!);
        sm = sm with { Value = double.NaN, Status = 1 };
        result = Invoke(null, "ClassifySM7110Measurement", sm, "BIN2", range)!;
        Wait((Task)Invoke(page, "SaveStationMeasurementAsync", 6, product, result, 1)!);
        var captured = store.ReadBatch("QA-CAPTURE");
        Require(captured.Count == 4 && captured[0].Attempts == 2, "Retries duplicated records");
        Require(captured[0].Value == 1 && captured[0].DissipationFactor == 0.02, "E4981A nF/D capture failed");
        Require(captured[1].Value == 52 && captured[1].LowerLimit == 50 && captured[1].Unit == "GΩ" && captured[1].StationPassed,
            "SM7110 final threshold sample/units incorrect");
        Require(captured[2].Value == 20 && captured[2].TimedOut && !captured[2].StationPassed && captured[2].Attempts == 3,
            "Timeout last sample not retained");
        Require(captured[3].Value is null && !captured[3].ValidReading, "Invalid sample must be retained without corrupting plots");
        Require(captured.Select(row => row.ProductId).Distinct().Count() == 1 && captured[0].ProductId != Guid.Empty,
            "Product identity must span stations");
        Console.WriteLine("PASS: one/two station charts, final retry result, capacitance/loss, SM7110 threshold/timeout, invalid sample and product identity.");

        // A failing disk write must fail the station task and never update displayed counts.
        var blocked = Path.Combine(root, "blocked-db");
        var blockedStore = new BatchMeasurementStore(blocked);
        var blockedParent = Path.Combine(root, "not-a-directory");
        File.WriteAllText(blockedParent, "blocked");
        typeof(BatchMeasurementStore).GetField("_connectionString", Flags)!.SetValue(blockedStore,
            new SqliteConnectionStringBuilder { DataSource = Path.Combine(blockedParent, "database.db") }.ToString());
        Set(page, "_batchStore", blockedStore);
        try { Wait((Task)Invoke(page, "SaveStationMeasurementAsync", 6, product, result, 1)!); throw new Exception("Write failure hidden"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("数据保存失败")) { }
        Require(Field<List<BatchMeasurement>>(page, "_batchMeasurements").Count == 4, "Failed write counted as saved");
        Set(page, "_batchStore", store);
        Console.WriteLine("PASS: write failure propagates and unsaved records are not counted.");

        // Render the actual home view with synthetic records only; never connect hardware.
        var random = new Random(42);
        var preview = Field<List<BatchMeasurement>>(page, "_batchMeasurements");
        for (var i = 0; i < 150; i++)
        {
            var z = Math.Sqrt(-2 * Math.Log(random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
            preview.Add(new BatchMeasurement { StationNumber = 5, Instrument = "E4981A", MeasurementMode = "C", Unit = "nF", Value = 1 + z * 0.06, ValidReading = true, StationPassed = Math.Abs(z) < 2 });
            preview.Add(new BatchMeasurement { StationNumber = 6, Instrument = "SM7110", MeasurementMode = "R", Unit = "GΩ", Value = 53 + z * 0.8, ValidReading = true, StationPassed = true });
        }
        Field<TextBlock>(page, "BatchCollectionStatusText").Text = "QA-CAPTURE · 界面验证用模拟数据";
        Invoke(page, "UpdateTestStationConfigurationDisplay");
        var output = Path.Combine(Directory.GetCurrentDirectory(), "outputs", "batch-measurement-qa");
        Directory.CreateDirectory(output);
        Render(page, Path.Combine(output, "home-two-stations.png"), 1664, 854);
        Field<ToggleButton>(page, "Station2EnabledToggle").IsChecked = false;
        Invoke(page, "UpdateTestStationConfigurationDisplay");
        Render(page, Path.Combine(output, "home-one-station.png"), 1664, 854);
        var dialog = new BatchQueryWindow(store, "QA-CAPTURE");
        var queryPage = (BatchQueryPage)dialog.Content;
        Wait(queryPage.ActivateAsync("QA-CAPTURE"));
        Render((FrameworkElement)dialog.Content, Path.Combine(output, "batch-query.png"), 1110, 690);
        CheckQueryAndExport(queryPage, store, captured, root);
        Console.WriteLine($"PASS: rendered home single/dual charts and populated batch query to {output}. No hardware opened.");
    }

    private static void CheckQueryAndExport(BatchQueryPage page, BatchMeasurementStore store,
        IReadOnlyList<BatchMeasurement> captured, string root)
    {
        T QueryField<T>(string name) => (T)typeof(BatchQueryPage).GetField(name, Flags)!.GetValue(page)!;
        Task Query() => (Task)typeof(BatchQueryPage).GetMethod("QueryAsync", Flags)!.Invoke(page, null)!;
        void Update() => typeof(BatchQueryPage).GetMethod("UpdateCommands", Flags)!.Invoke(page, null);
        var input = QueryField<ComboBox>("_batch");
        var export = QueryField<Button>("_export");
        Require(export.IsEnabled && QueryField<IReadOnlyList<BatchMeasurement>>("_records").Count == 4,
            "Query must enable exporting the full captured batch");
        store.Append(captured[0] with { Id = Guid.NewGuid() });
        Require(QueryField<IReadOnlyList<BatchMeasurement>>("_records").Count == 4, "Query snapshot changed under export");
        Wait(Query());
        Require(QueryField<IReadOnlyList<BatchMeasurement>>("_records").Count == 5, "Refresh must include newly captured records");
        input.Text = "QA-2"; Update();
        Require(!export.IsEnabled, "Editing batch must prevent exporting a different batch's previous results");
        Wait(Query());
        Require(export.IsEnabled && QueryField<IReadOnlyList<BatchMeasurement>>("_records").Single().BatchNumber == "QA-2", "Batch switch failed");
        input.Text = "does-not-exist"; Wait(Query());
        Require(!export.IsEnabled && QueryField<IReadOnlyList<BatchMeasurement>>("_records").Count == 0, "Empty query retained stale export");
        input.Text = " "; Wait(Query());
        Require(!export.IsEnabled, "Invalid query enabled stale export");
        Wait(page.ActivateAsync("QA-CAPTURE"));

        var csv = Path.Combine(root, "export.csv");
        var rows = captured.Concat(new[] { captured[0] with {
            BatchNumber = "=SUM(1,2)", Description = "中文,逗号\"引号\"\r\n第二行", RawResponse = " +1+2",
            Value = -0.123456789012345, DissipationFactor = null } }).ToArray();
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            BatchMeasurementCsvExporter.Export(csv, rows);
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
        Require(File.ReadAllBytes(csv).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "Excel Chinese encoding must include UTF-8 BOM");
        using (var parser = new TextFieldParser(csv, System.Text.Encoding.UTF8))
        {
            parser.SetDelimiters(","); parser.HasFieldsEnclosedInQuotes = true; parser.TrimWhiteSpace = false;
            Require(parser.ReadFields()!.Length == 24, "Missing export columns");
            var parsed = new List<string[]>();
            while (!parser.EndOfData) parsed.Add(parser.ReadFields()!);
            Require(parsed.Count == rows.Length && parsed.All(row => row.Length == 24), "Comma/quote/newline escaped incorrectly");
            Require(parsed[0][1] == "QA-CAPTURE" && parsed[1][6] == "52" && parsed[1][7] == "GΩ", "Batch/measurement export incorrect");
            Require(parsed[2][9] == "NG" && parsed[2][13] == "是" && parsed[3][6] == "", "Timeout/invalid data export incorrect");
            Require(parsed[^1][6] == "-0.123456789012345" && parsed[^1][8] == "", "Numeric precision/culture/null changed");
            Require(parsed[^1][1] == "'=SUM(1,2)" && parsed[^1][22] == "' +1+2", "Formula-like text not escaped");
            Require(parsed[^1][21].Contains("中文,逗号\"引号\"") && parsed[^1][21].Contains("第二行"), "Description corrupted");
        }
        var original = File.ReadAllBytes(csv);
        using (var locked = new FileStream(csv, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { BatchMeasurementCsvExporter.Export(csv, captured); throw new Exception("Locked output silently overwritten"); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        Require(File.ReadAllBytes(csv).SequenceEqual(original), "Failed export damaged previous file");
        Require(!Directory.EnumerateFiles(root, "export.csv.*.tmp").Any(), "Failed export left partial output");
        try { BatchMeasurementCsvExporter.Export(csv, []); throw new Exception("Empty export accepted"); }
        catch (InvalidOperationException) { }
        Require(!BatchMeasurementCsvExporter.SuggestedFileName("lot/a:b").Any(c => Path.GetInvalidFileNameChars().Contains(c)), "Unsafe export filename");
        Console.WriteLine("PASS: refreshed query snapshots, stale/empty export prevention, CSV columns/round-trip/BOM/precision, formula text and locked-file preservation.");
    }

    private static void Wait(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static int ChartTotal(object chart) =>
        (int)chart.GetType().GetField("_total", Flags)!.GetValue(chart)!;

    private static void Render(FrameworkElement view, string path, int width, int height)
    {
        view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
