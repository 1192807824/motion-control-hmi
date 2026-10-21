using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using ControlHub.Models;
using ControlHub.Services.Persistence;

namespace ControlHub.Views.Pages;

public sealed class AlarmHistoryPage : UserControl
{
    private readonly DatePicker _from = new() { SelectedDate = DateTime.Today, Width = 145 };
    private readonly DatePicker _through = new() { SelectedDate = DateTime.Today, Width = 145 };
    private readonly Button _query = new() { Content = "查询 / 刷新" };
    private readonly Button _all = new() { Content = "全部日期" };
    private readonly Button _clear = new() { Content = "清空全部报警", Foreground = Brushes.LightPink };
    private readonly Button _previous = new() { Content = "上一页" }, _next = new() { Content = "下一页" };
    private readonly TextBlock _summary = new(), _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly DataGrid _table = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
        EnableRowVirtualization = true, EnableColumnVirtualization = true, HeadersVisibility = DataGridHeadersVisibility.Column,
        SelectionMode = DataGridSelectionMode.Single, RowHeight = 38, ColumnHeaderHeight = 36 };
    private bool _busy;
    private int _page;
    private long _total;
    private DateTime? _appliedFrom = DateTime.Today, _appliedThrough = DateTime.Today;

    public AlarmHistoryPage()
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ControlHub;component/Views/Pages/BatchQueryTheme.xaml", UriKind.Relative) });
        Background = Brush("#0D1D2C"); Foreground = Brush("#DAE7F2"); FontFamily = new FontFamily("Microsoft YaHei UI");
        var dateTextStyle = new Style(typeof(DatePickerTextBox));
        dateTextStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#EDF5FC")));
        dateTextStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#12283B")));
        dateTextStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        dateTextStyle.Setters.Add(new Setter(Control.HeightProperty, 32d));
        Resources.Add(typeof(DatePickerTextBox), dateTextStyle);
        _from.Background = _through.Background = Brush("#12283B");
        _from.Foreground = _through.Foreground = Brush("#EDF5FC");
        _from.Height = _through.Height = 38;
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "报警记录", FontSize = 24, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "记录真实设备与运行故障，重启后保留。日期按本机时间筛选；清空历史不会复位设备报警。",
            Foreground = Brush("#91ACC2"), Margin = new Thickness(0, 8, 0, 16) });
        root.Children.Add(heading);
        var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
        filters.Children.Add(new TextBlock { Text = "开始日期", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        filters.Children.Add(_from);
        filters.Children.Add(new TextBlock { Text = "至", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) });
        filters.Children.Add(_through);
        foreach (var button in new[] { _query, _all, _clear }) { StyleButton(button); filters.Children.Add(button); }
        Grid.SetRow(filters, 1); root.Children.Add(filters);
        _table.Background = Brush("#112638"); _table.Foreground = Brush("#D7E5F0");
        _table.RowBackground = Brush("#112638"); _table.AlternatingRowBackground = Brush("#142B3E");
        _table.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        _table.HorizontalGridLinesBrush = Brush("#20394E");
        _table.ColumnHeaderStyle = (Style)FindResource(typeof(DataGridColumnHeader));
        _table.CellStyle = (Style)FindResource(typeof(DataGridCell)); _table.RowStyle = (Style)FindResource(typeof(DataGridRow));
        foreach (var (caption, property, width) in new[] { ("发生时间", "LocalTime", 205d), ("级别", "Level", 70d),
                     ("来源", "Source", 130d), ("报警代码", "Code", 195d), ("报警内容", "Message", 400d) })
        {
            var textStyle = new Style(typeof(TextBlock));
            textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            textStyle.Setters.Add(new Setter(TextBlock.ToolTipProperty, new Binding(property)));
            _table.Columns.Add(new DataGridTextColumn { Header = caption, Binding = new Binding(property), Width = width, MinWidth = width, ElementStyle = textStyle });
        }
        _table.Columns[^1].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        _table.Columns[^1].MinWidth = 250;
        Grid.SetRow(_table, 2); root.Children.Add(_table);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var pages = new StackPanel { Orientation = Orientation.Horizontal };
        _summary.VerticalAlignment = VerticalAlignment.Center; pages.Children.Add(_summary);
        foreach (var button in new[] { _previous, _next }) { StyleButton(button); pages.Children.Add(button); }
        footer.Children.Add(pages); _status.Margin = new Thickness(0, 8, 0, 0); footer.Children.Add(_status);
        Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
        _query.Click += async (_, _) => await ApplyFilterAsync();
        _all.Click += async (_, _) => { _from.SelectedDate = null; _through.SelectedDate = null; await ApplyFilterAsync(); };
        _previous.Click += async (_, _) => { if (!_busy && _page > 0) { _page--; await QueryAsync(); } };
        _next.Click += async (_, _) => { if (!_busy) { _page++; await QueryAsync(); } };
        _clear.Click += async (_, _) =>
        {
            if (MessageBox.Show(Window.GetWindow(this), "确定删除所有日期的全部报警历史？此操作不可撤销，也不会清除设备当前故障。",
                    "清空全部报警", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
                await ClearAsync();
        };
        _table.MouseDoubleClick += (_, _) =>
        {
            if (_table.SelectedItem is AlarmInfo alarm)
                MessageBox.Show(Window.GetWindow(this), $"{alarm.LocalTime}\n{alarm.Source} · {alarm.Code}\n\n{alarm.Message}", "报警详情");
        };
        UpdateCommands();
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private void StyleButton(Button button)
    {
        button.Style = (Style)FindResource("QueryButton"); button.Margin = new Thickness(10, 0, 0, 0);
    }
    public Task ActivateAsync() => ApplyFilterAsync();
    private async Task ApplyFilterAsync()
    {
        if (_busy) return;
        if (!string.IsNullOrWhiteSpace(_from.Text) && !DateTime.TryParse(_from.Text, out _) ||
            !string.IsNullOrWhiteSpace(_through.Text) && !DateTime.TryParse(_through.Text, out _))
        { _status.Text = "请输入有效的年月日。"; return; }
        if (_from.SelectedDate > _through.SelectedDate) { _status.Text = "开始日期不能晚于结束日期。"; return; }
        _appliedFrom = string.IsNullOrWhiteSpace(_from.Text) ? null : DateTime.Parse(_from.Text).Date;
        _appliedThrough = string.IsNullOrWhiteSpace(_through.Text) ? null : DateTime.Parse(_through.Text).Date;
        if (_appliedFrom > _appliedThrough) { _status.Text = "开始日期不能晚于结束日期。"; return; }
        _page = 0;
        await QueryAsync();
    }
    private async Task QueryAsync()
    {
        if (_busy) return;
        _busy = true; UpdateCommands();
        try
        {
            var result = await AlarmHistory.QueryAsync(_appliedFrom, _appliedThrough, _page);
            _total = result.Total; _table.ItemsSource = result.Records;
            _summary.Text = $"共 {_total} 条 · 第 {_page + 1}/{Math.Max(1, (_total + 199) / 200)} 页（每页200条）";
            _status.Text = AlarmHistory.LastError ?? (_total == 0 ? "所选日期没有报警记录。" : "双击一条记录查看完整报警内容。历史记录不代表当前设备状态。");
        }
        catch (Exception exception) { _table.ItemsSource = null; _total = 0; _summary.Text = "查询失败"; _status.Text = exception.Message; }
        finally { _busy = false; UpdateCommands(); }
    }
    private async Task ClearAsync()
    {
        if (_busy) return;
        _busy = true; UpdateCommands();
        try
        {
            var count = await AlarmHistory.ClearAllAsync();
            _page = 0; _total = 0; _table.ItemsSource = null; _summary.Text = "共 0 条";
            _status.Text = $"已清空全部报警历史，共{count}条。之后新发生的报警仍会记录。";
        }
        catch (Exception exception) { _status.Text = $"清空失败：{exception.Message}"; }
        finally { _busy = false; UpdateCommands(); }
    }
    private void UpdateCommands()
    {
        _query.IsEnabled = _all.IsEnabled = _clear.IsEnabled = _from.IsEnabled = _through.IsEnabled = !_busy;
        _previous.IsEnabled = !_busy && _page > 0;
        _next.IsEnabled = !_busy && (_page + 1L) * 200 < _total;
    }
}
