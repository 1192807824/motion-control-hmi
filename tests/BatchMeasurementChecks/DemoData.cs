using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ControlHub.Services.Persistence;
using ControlHub.Views.Pages;

internal static partial class Program
{
    private static void PreviewQueryDesign()
    {
        _ = new Application();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var store = new BatchMeasurementStore();
        var batch = store.ListBatches().First(value => value.StartsWith("演示-双站-", StringComparison.Ordinal));
        var page = new BatchQueryPage(store);
        Wait(page.ActivateAsync(batch));
        var output = Path.Combine(Directory.GetCurrentDirectory(), "outputs", "batch-query-design");
        Directory.CreateDirectory(output);
        Render(page, Path.Combine(output, "query-wide.png"), 1664, 854);
        var table = (DataGrid)typeof(BatchQueryPage).GetField("_table", Flags)!.GetValue(page)!;
        Require(table.Columns[0].ActualWidth >= 180, "Timestamp column collapsed");
        Render(page, Path.Combine(output, "query-compact.png"), 1110, 690);
        table.SelectedIndex = 2;
        Render(page, Path.Combine(output, "query-selected.png"), 1664, 854);
        var button = (Button)typeof(BatchQueryPage).GetField("_details", Flags)!.GetValue(page)!;
        Require(button.IsEnabled, "Selecting a record must enable full details");
        table.ScrollIntoView(table.Items[^1]);
        Render(page, Path.Combine(output, "query-last-records.png"), 1110, 690);
        var empty = new BatchQueryPage(store);
        Render(empty, Path.Combine(output, "query-empty.png"), 1110, 690);
        Console.WriteLine(output);
    }

    private static void VerifyDefaultSqlite()
    {
        var store = new BatchMeasurementStore();
        var batches = store.ListBatches();
        var records = batches.Sum(batch => store.ReadBatch(batch).Count);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = store.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "PRAGMA integrity_check;";
        Console.WriteLine(JsonSerializer.Serialize(new { store.DatabasePath, Batches = batches.Count, Records = records,
            Integrity = command.ExecuteScalar()?.ToString() }, new JsonSerializerOptions { WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    // Explicit demo command only: never runs during tests or production startup.
    private static void CreateDemo()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var doubleBatch = $"演示-双站-{stamp}";
        var singleBatch = $"演示-单站-{stamp}";
        var store = new BatchMeasurementStore();
        var dual = GenerateDemo(doubleBatch, 500, true, 7110);
        var single = GenerateDemo(singleBatch, 300, false, 4981);
        foreach (var group in new[] { dual, single })
        {
            store.PrepareBatch(group[0].BatchNumber);
            foreach (var row in group) store.Append(row);
            Require(store.ReadBatch(group[0].BatchNumber).Count == group.Count, "Demo records incomplete");
        }

        _ = new Application();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var home = new HomePage();
        Set(home, "_loadingPresetPositions", true);
        Set(home, "_updatingTestStationConfiguration", true);
        for (var i = 1; i <= 3; i++) Field<ToggleButton>(home, $"Station{i}EnabledToggle").IsChecked = i <= 2;
        Field<ComboBox>(home, "Station1InstrumentComboBox").SelectedIndex = (int)TestStationInstrument.E4981A;
        Field<ComboBox>(home, "Station2InstrumentComboBox").SelectedIndex = (int)TestStationInstrument.SM7110;
        Field<ComboBox>(home, "Station3InstrumentComboBox").SelectedIndex = (int)TestStationInstrument.None;
        Field<ComboBox>(home, "SM7110LimitModeComboBox").SelectedValue = "R";
        Set(home, "_updatingTestStationConfiguration", false);
        Set(home, "_loadingPresetPositions", false);
        var output = Path.Combine(Directory.GetCurrentDirectory(), "outputs", "batch-demo", stamp);
        Directory.CreateDirectory(output);

        void RenderHome(string batch, bool twoStations, string file)
        {
            Field<ToggleButton>(home, "Station2EnabledToggle").IsChecked = twoStations;
            Field<TextBox>(home, "BatchNumberTextBox").Text = batch;
            Wait((Task)Invoke(home, "LoadBatchPreviewAsync")!);
            Invoke(home, "UpdateTestStationConfigurationDisplay");
            Field<TextBlock>(home, "BatchCollectionStatusText").Text = $"模拟演示 · {batch} · {store.ReadBatch(batch).Count} 条记录";
            Render(home, Path.Combine(output, file), 1664, 854);
        }
        RenderHome(doubleBatch, true, "home-two-stations.png");
        RenderHome(singleBatch, false, "home-one-station.png");
        var query = new BatchQueryPage(store);
        Wait(query.ActivateAsync(doubleBatch));
        Render(query, Path.Combine(output, "batch-query.png"), 1664, 854);
        var summary = new
        {
            DoubleBatch = doubleBatch, SingleBatch = singleBatch, OutputDirectory = output,
            DataDirectory = store.RootDirectory,
            Batches = new[] { dual, single }.Select(rows => new
            {
                Batch = rows[0].BatchNumber, Products = rows.Select(row => row.ProductId).Distinct().Count(),
                Records = rows.Count,
                Stations = rows.GroupBy(row => row.StationNumber).Select(group => new
                {
                    Station = group.Key, OK = group.Count(row => row.StationPassed), NG = group.Count(row => !row.StationPassed),
                    Timeout = group.Count(row => row.TimedOut), Invalid = group.Count(row => !row.ValidReading)
                })
            })
        };
        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(Path.Combine(output, "demo-summary.json"), json);
        Console.WriteLine(json);
        Console.WriteLine("Demo data saved and real WPF views rendered. No hardware connected; machine configuration unchanged.");
    }

    private static List<BatchMeasurement> GenerateDemo(string batch, int count, bool twoStations, int seed)
    {
        var random = new Random(seed);
        double Normal() => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
        var rows = new List<BatchMeasurement>();
        var start = DateTimeOffset.Now.AddMinutes(-20);
        for (var i = 0; i < count; i++)
        {
            var product = Guid.NewGuid();
            var capacitance = Math.Round(1 + Normal() * 0.024, 6);
            var loss = Math.Round(Math.Max(0.002, 0.012 + Normal() * 0.002), 6);
            if (i % 47 == 0) loss = 0.026;
            var bin = capacitance < 0.94 || capacitance > 1.06 ? "BIN0" : capacitance < 0.98 ? "BIN1" : capacitance <= 1.02 ? "BIN2" : "BIN3";
            var ePassed = bin != "BIN0" && loss <= 0.02;
            rows.Add(new BatchMeasurement
            {
                BatchNumber = batch, ProductId = product, MeasuredAt = start.AddSeconds(i * 1.5), StationNumber = 5,
                Instrument = "E4981A", MeasurementMode = "C", Value = capacitance, Unit = "nF", DissipationFactor = loss,
                ValidReading = true, StationPassed = ePassed, ProductPassedSoFar = ePassed, Bin = bin, InstrumentStatus = 0,
                Description = $"【模拟数据】C={capacitance:G6}nF · D={loss:G6} · {bin} · {(ePassed ? "OK" : loss > 0.02 ? "NG 损耗超限" : "NG 超出BIN范围")}",
                RawResponse = FormattableString.Invariant($"0,{capacitance / 1e9:E6},{loss:G6},{bin[3..]}"), Attempts = i % 23 == 0 ? 2 : 1
            });
            if (!twoStations) continue;
            var invalid = i % 173 == 0;
            var timeout = !invalid && i % 61 == 0;
            double? resistance = invalid ? null : timeout ? Math.Round(46 + random.NextDouble() * 3, 5) : Math.Round(Math.Max(50.02, 52 + Normal() * 0.65), 5);
            var passed = !invalid && !timeout;
            var elapsed = timeout ? 3 : Math.Round(0.35 + random.NextDouble() * 0.9, 3);
            rows.Add(new BatchMeasurement
            {
                BatchNumber = batch, ProductId = product, MeasuredAt = start.AddSeconds(i * 1.5 + 0.8), StationNumber = 6,
                Instrument = "SM7110", MeasurementMode = "R", Value = resistance, Unit = "GΩ", ValidReading = !invalid,
                StationPassed = passed, ProductPassedSoFar = ePassed && passed, Bin = bin, InstrumentStatus = invalid ? 5 : 0,
                Description = $"【模拟数据】{(invalid ? "NG 接触检查失败" : $"R={resistance:G6}GΩ · ≥50GΩ · {(timeout ? "NG 超时未达标" : "OK 首次达标值")}")}" + (!ePassed ? " · 前站NG保留" : ""),
                RawResponse = invalid ? "5,9.9E+37" : "0," + (resistance!.Value * 1e9).ToString("E6", CultureInfo.InvariantCulture),
                ElapsedSeconds = elapsed, TimedOut = timeout, Attempts = invalid || timeout ? 3 : i % 29 == 0 ? 2 : 1,
                LowerLimit = 50, MaximumTestSeconds = 3
            });
        }
        return rows;
    }
}
