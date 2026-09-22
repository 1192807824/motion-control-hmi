using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ControlHub.Services.Persistence;

namespace ControlHub.Views.Pages;

public sealed partial class BatchQueryPage
{
    private readonly TextBlock _metricRecords = new(), _metricProducts = new(), _metricYield = new(), _metricNg = new();
    private readonly TextBlock _resultCaption = new(), _batchBadge = new();
    private readonly TextBlock _emptyMessage = new() { Text = "输入批次号，查看测量记录", TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _details = new() { Content = "记录详情", IsEnabled = false };
    private static SolidColorBrush Ink(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private static TextBlock Label(string text, double size = 12, string color = "#91ACC2") => new()
    {
        Text = text, FontSize = size, Foreground = Ink(color), VerticalAlignment = VerticalAlignment.Center
    };

    private UIElement BuildView()
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ControlHub;component/Views/Pages/BatchQueryTheme.xaml", UriKind.Relative) });
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        Background = Ink("#0D1D2C"); Foreground = Ink("#DAE7F2");
        var root = new Grid { Margin = new Thickness(22, 14, 22, 12) };
        foreach (var height in new[] { new GridLength(62), new GridLength(62), new GridLength(86), new GridLength(270), new GridLength(1, GridUnitType.Star), new GridLength(27) })
            root.RowDefinitions.Add(new RowDefinition { Height = height });
        root.SizeChanged += (_, _) => root.RowDefinitions[3].Height = new GridLength(root.ActualHeight < 750 ? 210 : 270);

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 12) };
        _export.Style = (Style)FindResource("QueryPrimaryButton"); _export.Margin = new Thickness(14, 0, 0, 0);
        _export.Content = ButtonContent("\uE896", "导出批次数据");
        _export.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_export, Dock.Right); header.Children.Add(_export);
        var heading = new StackPanel();
        heading.Children.Add(Label("生产数据  /  批次追溯", 11, "#78A0BE"));
        var title = Label("数据查询", 24, "#F0F6FC"); title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 3, 0, 0);
        heading.Children.Add(title); header.Children.Add(heading);
        root.Children.Add(header);

        var search = new DockPanel { LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _query.Style = (Style)FindResource("QueryPrimaryButton"); _query.Content = ButtonContent("\uE721", "查询批次");
        actions.Children.Add(_query);
        DockPanel.SetDock(actions, Dock.Right); search.Children.Add(actions);
        var input = new DockPanel { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        var inputLabel = Label("批次号", 13, "#BCCFDF"); inputLabel.Margin = new Thickness(0, 0, 14, 0);
        DockPanel.SetDock(inputLabel, Dock.Left); input.Children.Add(inputLabel);
        _batch.Style = (Style)FindResource("QueryBatchInput"); _batch.Width = double.NaN; _batch.Margin = new Thickness(0);
        input.Children.Add(_batch); search.Children.Add(input);
        var searchPanel = Panel(search, new Thickness(16, 10, 16, 10));
        searchPanel.Margin = new Thickness(0, 0, 2, 0); Grid.SetRow(searchPanel, 1); root.Children.Add(searchPanel);

        var metrics = new UniformGrid { Columns = 4, Margin = new Thickness(-4, 10, -2, 8) };
        metrics.Children.Add(Metric("测试记录", _metricRecords, "条", "#75BEFF"));
        metrics.Children.Add(Metric("已采集产品", _metricProducts, "件", "#EDF5FC"));
        metrics.Children.Add(Metric("本站合格率", _metricYield, "", "#64D7B1", "按全部工位记录的本站判定统计，不代表产品最终良率"));
        metrics.Children.Add(Metric("NG 记录", _metricNg, "条", "#FF9AA5"));
        Grid.SetRow(metrics, 2); root.Children.Add(metrics);

        _charts.Margin = new Thickness(-4, 0, -4, 10);
        var chartScroll = new ScrollViewer { Content = _charts, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(chartScroll, 3); root.Children.Add(chartScroll);

        var tableArea = new Grid();
        tableArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });
        tableArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var tableHeading = new DockPanel { Margin = new Thickness(16, 0, 14, 0) };
        _details.Style = (Style)FindResource("QueryButton"); _details.Height = 30; _details.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_details, Dock.Right); tableHeading.Children.Add(_details);
        var caption = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        caption.Children.Add(Label("采集明细", 15, "#ECF4FB"));
        _resultCaption.Foreground = Ink("#7899B3"); _resultCaption.FontSize = 11; _resultCaption.Margin = new Thickness(12, 0, 0, 0);
        _resultCaption.VerticalAlignment = VerticalAlignment.Center; caption.Children.Add(_resultCaption);
        tableHeading.Children.Add(caption); tableArea.Children.Add(tableHeading);
        _table.Background = Ink("#112638"); _table.Foreground = Ink("#D7E5F0"); _table.BorderThickness = new Thickness(0);
        _table.RowBackground = Ink("#112638"); _table.AlternatingRowBackground = Ink("#142B3E"); _table.AlternationCount = 2;
        _table.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal; _table.HorizontalGridLinesBrush = Ink("#20394E");
        _table.HeadersVisibility = DataGridHeadersVisibility.Column; _table.SelectionMode = DataGridSelectionMode.Single;
        _table.SelectionUnit = DataGridSelectionUnit.FullRow; _table.RowHeight = 36; _table.ColumnHeaderHeight = 38;
        _table.FontSize = 12; _table.FrozenColumnCount = 2; _table.CanUserReorderColumns = false;
        _table.ColumnHeaderStyle = (Style)FindResource(typeof(DataGridColumnHeader));
        _table.CellStyle = (Style)FindResource(typeof(DataGridCell)); _table.RowStyle = (Style)FindResource(typeof(DataGridRow));
        _table.Columns.Clear();
        AddColumn("采集时间", "LocalTime", 186);
        AddColumn("工位", "StationNumber", 64);
        AddColumn("仪表", "Instrument", 102);
        AddColumn("测量值", "Value", 111, "G8", true);
        AddColumn("单位", "Unit", 68);
        AddColumn("损耗 D", "DissipationFactor", 100, "G5", true);
        _table.Columns.Add(new DataGridTemplateColumn { Header = "本站判定", Width = 95, MinWidth = 95, SortMemberPath = "StationPassed",
            CellTemplate = (DataTemplate)FindResource("QueryResultBadge") });
        AddColumn("分档", "Bin", 75);
        _table.Columns.Add(new DataGridTemplateColumn { Header = "产品判定", Width = 120, MinWidth = 120, SortMemberPath = "ProductPassedSoFar",
            CellTemplate = (DataTemplate)FindResource("QueryProductBadge") });
        AddColumn("耗时 / s", "ElapsedSeconds", 95, "0.###", true);
        AddColumn("轮数", "Attempts", 64);
        AddColumn("结果说明", "Description", 280);
        _table.Columns[^1].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        _table.Columns[^1].MinWidth = 210;
        Grid.SetRow(_table, 1); tableArea.Children.Add(_table);
        _emptyMessage.Foreground = Ink("#86A4BC"); _emptyMessage.FontSize = 14;
        _emptyMessage.IsHitTestVisible = false; Grid.SetRow(_emptyMessage, 1); tableArea.Children.Add(_emptyMessage);
        var tablePanel = Panel(tableArea, new Thickness(0)); tablePanel.ClipToBounds = true;
        Grid.SetRow(tablePanel, 4); root.Children.Add(tablePanel);

        var footer = new DockPanel();
        _batchBadge.FontSize = 10; _batchBadge.Foreground = Ink("#728FA6"); _batchBadge.VerticalAlignment = VerticalAlignment.Center;
        _batchBadge.Text = "CSV 导出保留全部原始字段"; DockPanel.SetDock(_batchBadge, Dock.Right); footer.Children.Add(_batchBadge);
        _status.Foreground = Ink("#88A5BC"); _status.FontSize = 11; _status.Margin = new Thickness(0, 0, 14, 0);
        _status.VerticalAlignment = VerticalAlignment.Center; _status.TextWrapping = TextWrapping.NoWrap; _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.SetBinding(ToolTipProperty, new Binding("Text") { Source = _status }); footer.Children.Add(_status);
        Grid.SetRow(footer, 5); root.Children.Add(footer);
        _table.SelectionChanged += (_, _) => _details.IsEnabled = _table.SelectedItem is BatchMeasurement;
        _table.MouseDoubleClick += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(_table, e.OriginalSource as DependencyObject) is DataGridRow) ShowRecordDetails();
        };
        _details.Click += (_, _) => ShowRecordDetails();
        UpdateSummary([]);
        ShowEmptyCharts();
        return root;
    }

    private static UIElement ButtonContent(string glyph, string text)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14,
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return content;
    }

    private static Border Panel(UIElement child, Thickness padding) => new()
    {
        Background = Ink("#12283B"), BorderBrush = Ink("#294257"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8), Padding = padding, Child = child
    };

    private static Border Metric(string caption, TextBlock value, string unit, string color, string? tooltip = null)
    {
        var panel = new StackPanel();
        panel.Children.Add(Label(caption, 11));
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        value.Foreground = Ink(color); value.FontFamily = new FontFamily("Segoe UI"); value.FontSize = 26; value.FontWeight = FontWeights.SemiBold;
        line.Children.Add(value); var suffix = Label(unit, 11); suffix.Margin = new Thickness(8, 10, 0, 0); line.Children.Add(suffix);
        panel.Children.Add(line);
        var border = Panel(panel, new Thickness(16, 8, 16, 7)); border.Margin = new Thickness(4, 0, 4, 0); border.ToolTip = tooltip;
        return border;
    }

    private void AddColumn(string header, string property, double width, string? format = null, bool numeric = false)
    {
        var textStyle = new Style(typeof(TextBlock));
        textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        textStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        textStyle.Setters.Add(new Setter(TextBlock.ToolTipProperty, new Binding(property)));
        if (numeric)
        {
            textStyle.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new FontFamily("Consolas")));
            textStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
            textStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, Ink("#B5DDFF")));
        }
        _table.Columns.Add(new DataGridTextColumn { Header = header, Width = width, MinWidth = width, Binding = new Binding(property)
            { StringFormat = format, TargetNullValue = "—" }, ElementStyle = textStyle });
    }

    private void UpdateSummary(IReadOnlyList<BatchMeasurement> rows)
    {
        _metricRecords.Text = rows.Count.ToString("N0");
        _metricProducts.Text = rows.Select(row => row.ProductId).Distinct().Count().ToString("N0");
        _metricYield.Text = rows.Count == 0 ? "—" : (100d * rows.Count(row => row.StationPassed) / rows.Count).ToString("0.0") + "%";
        _metricNg.Text = rows.Count(row => !row.StationPassed).ToString("N0");
        _resultCaption.Text = rows.Count == 0 ? "暂无数据" : $"{rows.Count:N0} 条记录  ·  双击查看完整信息";
        _emptyMessage.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowEmptyCharts()
    {
        _charts.Children.Clear();
        var message = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        message.Children.Add(Label("测量分布", 17, "#D0E1EF"));
        var hint = Label("查询批次后，按工位显示频数与正态拟合曲线", 12); hint.Margin = new Thickness(0, 10, 0, 0);
        message.Children.Add(hint); _charts.Children.Add(Panel(message, new Thickness(24)));
    }

    private void ShowRecordDetails()
    {
        if (_table.SelectedItem is not BatchMeasurement row) return;
        var stack = new StackPanel { Margin = new Thickness(24) };
        var title = Label($"工位 {row.StationNumber}  ·  {row.Instrument}  ·  {row.StationResult}", 20, "#EDF5FC");
        title.Margin = new Thickness(0, 0, 0, 20); stack.Children.Add(title);
        static string Number(double? n) => n?.ToString("G9", CultureInfo.CurrentCulture) ?? "—";
        foreach (var (label, value) in new[] {
            ("批次号", row.BatchNumber), ("采集时间", row.LocalTime), ("产品编号", row.ProductId.ToString()),
            ("测量模式 / 单位", row.MeasurementMode + " / " + row.Unit), ("最终测量值", Number(row.Value)),
            ("损耗 D", Number(row.DissipationFactor)), ("截至本站产品判定", row.ProductResult), ("分档", row.Bin),
            ("耗时 / s", Number(row.ElapsedSeconds)), ("超时 / 有效读数", $"{(row.TimedOut ? "是" : "否")} / {(row.ValidReading ? "是" : "否")}"),
            ("测量轮数", row.Attempts.ToString()), ("下限 / 上限", Number(row.LowerLimit) + " / " + Number(row.UpperLimit)),
            ("最长测试时间 / s", Number(row.MaximumTestSeconds)), ("仪表状态", row.InstrumentStatus.ToString()),
            ("结果说明", row.Description), ("原始响应", row.RawResponse), ("记录编号", row.Id.ToString()) })
        {
            var item = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(155) });
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            item.Children.Add(Label(label));
            var text = new TextBox { Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent,
                Foreground = Ink("#DAE7F2"), BorderThickness = new Thickness(0), FontSize = 13 };
            Grid.SetColumn(text, 1); item.Children.Add(text); stack.Children.Add(item);
        }
        var details = new Window { Title = "采集记录详情", Owner = Window.GetWindow(this), Width = 720, Height = 720,
            MinWidth = 560, MinHeight = 420, Background = Background, FontFamily = FontFamily, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        details.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ControlHub;component/Views/Pages/BatchQueryTheme.xaml", UriKind.Relative) });
        details.Show();
    }
}
