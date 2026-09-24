using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ControlHub.Services.Devices;
using ControlHub.Services.Persistence;
using ControlHub.Views.Controls;
using ControlHub.Views.Dialogs;

namespace ControlHub.Views.Pages;

public partial class HomePage
{
    private readonly BatchMeasurementStore _batchStore = new();
    private readonly List<BatchMeasurement> _batchMeasurements = [];
    private string _activeBatchNumber = "";
    private int _batchLoadVersion;

    public void RefreshAfterBatchDataCleared(string path)
    {
        if (!string.Equals(path, _batchStore.DatabasePath, StringComparison.OrdinalIgnoreCase)) return;
        if (_startSequenceRunning) return; // A new collection started after the clear already loaded fresh data.
        ++_batchLoadVersion;
        _batchMeasurements.Clear();
        BatchCollectionStatusText.Text = "全部测量记录已清空";
        RefreshDistributionCharts();
    }

    private void BatchNumberTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (BatchCollectionStatusText is null || _startSequenceRunning || BatchNumberTextBox.Text.Trim() == _activeBatchNumber) return;
        ++_batchLoadVersion;
        _activeBatchNumber = "";
        _batchMeasurements.Clear();
        BatchCollectionStatusText.Text = "输入批次号后离开输入框，即可加载该批次数据";
        RefreshDistributionCharts();
    }

    private async void BatchNumberTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_startSequenceRunning || string.IsNullOrWhiteSpace(BatchNumberTextBox.Text)) return;
        await LoadBatchPreviewAsync();
    }

    private async Task LoadBatchPreviewAsync()
    {
        var version = ++_batchLoadVersion;
        try
        {
            var batch = BatchMeasurementStore.NormalizeBatchNumber(BatchNumberTextBox.Text);
            var records = await Task.Run(() => _batchStore.ReadBatch(batch));
            if (version != _batchLoadVersion) return;
            _batchMeasurements.Clear();
            _batchMeasurements.AddRange(records);
            _activeBatchNumber = batch;
            BatchCollectionStatusText.Text = $"批次 {batch} · 已保存 {records.Count} 条";
            RefreshDistributionCharts();
        }
        catch (Exception exception)
        {
            AlarmHistory.Record("数据采集", "BATCH-READ", exception.Message);
            if (version == _batchLoadVersion) BatchCollectionStatusText.Text = $"批次读取失败：{exception.Message}";
        }
    }

    private void PrepareProductionBatch()
    {
        var batch = BatchMeasurementStore.NormalizeBatchNumber(BatchNumberTextBox.Text);
        ++_batchLoadVersion; // Discard any preview query still completing.
        _batchStore.PrepareBatch(batch); // Fail before motion if the data directory cannot be written.
        var records = _batchStore.ReadBatch(batch);
        BatchNumberTextBox.Text = batch;
        _activeBatchNumber = batch;
        _batchMeasurements.Clear();
        _batchMeasurements.AddRange(records);
        BatchCollectionStatusText.Text = $"批次 {batch} · 已保存 {records.Count} 条";
        RefreshDistributionCharts();
    }

    private async Task SaveStationMeasurementAsync(int station, CarouselStationState state,
        TestStationMeasurementResult result, int attempts)
    {
        var e = result.E4981AReading;
        var sm = result.SM7110Reading;
        if (e is null && sm is null) throw new InvalidOperationException("测试结果缺少原始采集数据。");
        static double? Finite(double? value) => value.HasValue && double.IsFinite(value.Value) ? value : null;
        var range = sm is null ? null : _sm7110AcceptanceRange;
        var record = new BatchMeasurement
        {
            BatchNumber = _activeBatchNumber, ProductId = state.ProductId, StationNumber = station,
            Instrument = e is not null ? "E4981A" : "SM7110",
            MeasurementMode = e is not null ? "C" : sm!.MeasurementMode,
            Value = Finite(e is not null ? e.CapacitanceNf : sm!.DisplayValue),
            Unit = e is not null ? "nF" : sm!.DisplayUnit,
            DissipationFactor = Finite(e?.DissipationFactor),
            ValidReading = e?.IsSuccessful ?? sm!.IsSuccessful,
            StationPassed = e is not null ? !result.IsNg && result.Passed : result.SM7110Passed == true,
            ProductPassedSoFar = !result.IsNg && result.Passed, Bin = result.Bin,
            InstrumentStatus = e?.Status ?? sm!.Status, Description = result.DisplayText,
            RawResponse = e?.RawResponse ?? sm!.RawResponse,
            ElapsedSeconds = Finite(sm?.TestElapsedSeconds), TimedOut = sm?.TimedOut ?? false,
            Attempts = attempts,
            LowerLimit = range is null ? null : Finite(SM7110Protocol.ToDisplayValue(range.Lower, range.MeasurementMode)),
            UpperLimit = range is null ? null : Finite(SM7110Protocol.ToDisplayValue(range.Upper, range.MeasurementMode)),
            MaximumTestSeconds = range?.MaximumTestSeconds
        };
        try { await Task.Run(() => _batchStore.Append(record)); }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"批次 {record.BatchNumber} / 工位{station} 数据保存失败，停止生产：{exception.Message}", exception);
        }
        _batchMeasurements.Add(record);
        state.SetMeasurement(result);
        BatchCollectionStatusText.Text = $"批次 {_activeBatchNumber} · 已保存 {_batchMeasurements.Count} 条";
        RefreshDistributionCharts();
        SetTestStationRuntimeDisplay(
            station,
            $"测试完成 · {result.StatusDescription}",
            result.ResultLabel,
            result.DisplayText,
            result.Passed && !result.IsNg
                ? Color.FromRgb(73, 209, 125)
                : Color.FromRgb(242, 122, 128));
    }

    private void RefreshDistributionCharts()
    {
        if (DistributionChartsPanel is null || _loadingPresetPositions) return;
        DistributionChartsPanel.Children.Clear();
        foreach (var definition in TestStationDefinitions)
        {
            var station = definition.StationNumber;
            var controls = GetTestStationConfigurationControls(station);
            var instrument = GetSelectedTestStationInstrument(controls.InstrumentComboBox);
            if (controls.EnabledToggle.IsChecked != true || instrument == TestStationInstrument.None) continue;
            var name = FormatTestStationInstrument(instrument);
            var mode = instrument == TestStationInstrument.E4981A ? "C" : SM7110LimitModeComboBox.SelectedValue as string ?? "R";
            var records = _batchMeasurements.Where(row => row.StationNumber == station && row.Instrument == name && row.MeasurementMode == mode).ToArray();
            var unit = instrument == TestStationInstrument.E4981A ? "nF" : new SM7110MeasurementResult(0, 0, mode, "").DisplayUnit;
            var chart = new MeasurementDistributionChart { Margin = new Thickness(0, 3, 0, 3), MinHeight = 170 };
            chart.SetData($"工位 {station} · {name} · {(mode == "C" ? "电容" : mode == "R" ? "最终电阻" : mode)}", unit, records);
            DistributionChartsPanel.Children.Add(chart);
        }
        if (DistributionChartsPanel.Children.Count == 0)
            DistributionChartsPanel.Children.Add(new TextBlock { Text = "请先启用测试工位", Foreground = Brushes.LightGray,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
    }

    public void ShowBatchQuery()
    {
        var dialog = new BatchQueryWindow(_batchStore, BatchNumberTextBox.Text.Trim()) { Owner = Window.GetWindow(this) };
        dialog.Show();
    }

    public string CurrentBatchNumber => BatchNumberTextBox.Text.Trim();

    private void BatchQuery_Click(object sender, RoutedEventArgs e) => ShowBatchQuery();
}
