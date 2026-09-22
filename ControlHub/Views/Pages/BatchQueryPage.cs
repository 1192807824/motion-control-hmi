using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ControlHub.Services.Persistence;
using ControlHub.Views.Controls;
using Microsoft.Win32;

namespace ControlHub.Views.Pages;

public sealed partial class BatchQueryPage : UserControl
{
    private readonly BatchMeasurementStore _store;
    private readonly ComboBox _batch = new() { IsEditable = true, Width = 270, Margin = new Thickness(10, 0, 10, 0) };
    private readonly Button _query = new() { Content = "查询 / 刷新", Padding = new Thickness(18, 6, 18, 6) };
    private readonly Button _refresh = new() { Content = "刷新批次列表", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _export = new() { Content = "导出当前批次 CSV", Padding = new Thickness(18, 6, 18, 6),
        Margin = new Thickness(8, 0, 0, 0), IsEnabled = false, ToolTip = "导出最近一次查询的全部明细，可使用 Excel 打开" };
    private readonly TextBlock _status = new() { Foreground = Brushes.LightGray, Margin = new Thickness(0, 8, 0, 8), TextWrapping = TextWrapping.Wrap };
    private readonly UniformGrid _charts = new() { Rows = 1 };
    private readonly DataGrid _table = new() { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false,
        EnableRowVirtualization = true, EnableColumnVirtualization = true, FrozenColumnCount = 2,
        RowHeight = 28, ColumnHeaderHeight = 32, AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(235, 242, 248)) };
    private IReadOnlyList<BatchMeasurement> _records = [];
    private string _queriedBatch = "";
    private bool _busy;

    public BatchQueryPage() : this(new BatchMeasurementStore()) { }
    public BatchQueryPage(BatchMeasurementStore store)
    {
        _store = store;


        Content = BuildView();
        _query.Click += async (_, _) => await QueryAsync();
        _refresh.Click += async (_, _) => await RefreshBatchesAsync();
        _export.Click += async (_, _) => await ExportAsync();
        _batch.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await QueryAsync(); } };
        _batch.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdateCommands()));
        _batch.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(UpdateCommands);
        _status.Text = "输入或选择批次号后查询，再导出该批次的全部明细。";
    }

    public async Task ActivateAsync(string? initialBatch = null)
    {
        if (_busy) return;
        if (string.IsNullOrWhiteSpace(_batch.Text) && !string.IsNullOrWhiteSpace(initialBatch)) _batch.Text = initialBatch;
        await RefreshBatchesAsync();
        if (!string.IsNullOrWhiteSpace(_batch.Text)) await QueryAsync();
    }

    private async Task RefreshBatchesAsync()
    {
        if (_busy) return;
        _busy = true; UpdateCommands();
        try
        {
            var selected = _batch.Text;
            _batch.ItemsSource = await Task.Run(_store.ListBatches);
            _batch.Text = selected;
        }
        catch (Exception exception) { _status.Text = $"读取批次列表失败：{exception.Message}"; }
        finally { _busy = false; UpdateCommands(); }
    }

    private async Task QueryAsync()
    {
        if (_busy) return;
        _busy = true; _records = []; _queriedBatch = "";
        UpdateSummary([]);
        UpdateCommands();
        _table.ItemsSource = null; _charts.Children.Clear();
        try
        {
            var batch = BatchMeasurementStore.NormalizeBatchNumber(_batch.Text);
            _status.Text = $"正在读取批次 {batch}…";
            var rows = await Task.Run(() => _store.ReadBatch(batch));
            _records = rows; _queriedBatch = batch;
            UpdateSummary(rows);
            _table.ItemsSource = rows;
            foreach (var group in rows.GroupBy(row => (row.StationNumber, row.Instrument, row.MeasurementMode, row.Unit))
                         .OrderBy(group => group.Key.StationNumber))
            {
                var chart = new MeasurementDistributionChart { Margin = new Thickness(4, 0, 4, 0), MinWidth = 360, UseQueryStyle = true,
                    QueryAccent = group.Key.Instrument == "SM7110" ? Color.FromRgb(91, 214, 188) : Color.FromRgb(103, 185, 255) };
                var quantity = group.Key.MeasurementMode == "C" ? "电容分布" : group.Key.MeasurementMode == "R" ? "电阻分布" : group.Key.MeasurementMode + " 分布";
                chart.SetData($"{quantity}   /   工位 {group.Key.StationNumber} · {group.Key.Instrument}", group.Key.Unit, group);
                _charts.Children.Add(chart);
            }
            _status.Text = rows.Count == 0 ? $"批次 {batch} 暂无采集记录" : $"已更新 {DateTime.Now:HH:mm:ss}  ·  {batch}  ·  导出包含本次查询的全部记录";
            if (rows.Count == 0) ShowEmptyCharts();
        }
        catch (Exception exception) { _records = []; _queriedBatch = ""; UpdateSummary([]); ShowEmptyCharts(); _status.Text = $"查询失败：{exception.Message}"; }
        finally { _busy = false; UpdateCommands(); }
    }

    private void UpdateCommands()
    {
        _query.IsEnabled = !_busy; _refresh.IsEnabled = !_busy; _batch.IsEnabled = !_busy;
        _export.IsEnabled = !_busy && _records.Count > 0 && _queriedBatch == _batch.Text.Trim();
    }

    private async Task ExportAsync()
    {
        if (_busy || _records.Count == 0 || _queriedBatch != _batch.Text.Trim()) return;
        var dialog = new SaveFileDialog { Title = $"导出批次 {_queriedBatch}", Filter = "CSV 文件（Excel 可打开）|*.csv",
            DefaultExt = ".csv", AddExtension = true, FileName = BatchMeasurementCsvExporter.SuggestedFileName(_queriedBatch), OverwritePrompt = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        _busy = true; UpdateCommands();
        var rows = _records.ToArray();
        try
        {
            await Task.Run(() => BatchMeasurementCsvExporter.Export(dialog.FileName, rows));
            _status.Text = $"批次 {_queriedBatch} · 已导出 {rows.Length} 条记录\n{dialog.FileName}";
        }
        catch (Exception exception) { _status.Text = $"导出失败：{exception.Message}"; }
        finally { _busy = false; UpdateCommands(); }
    }
}
