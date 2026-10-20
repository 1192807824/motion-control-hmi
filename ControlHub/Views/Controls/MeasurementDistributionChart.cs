using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ControlHub.Services.Devices;
using ControlHub.Services.Persistence;

namespace ControlHub.Views.Controls;

public sealed class MeasurementDistributionChart : FrameworkElement
{
    public bool UseQueryStyle { get; set; }
    public Color QueryAccent { get; set; } = Color.FromRgb(103, 185, 255);
    private string _title = "";
    private string _unit = "";
    private int _total, _passed, _ng, _unmeasured;
    private MeasurementDistribution _distribution = MeasurementDistribution.Calculate([]);
    // Drawing resources are immutable: share them across frames and chart instances.
    private static readonly Brush TextBrush = CreateBrush("#EAF2F8"), Muted = CreateBrush("#9FB1BF"), Blue = CreateBrush("#62B5FF"), Green = CreateBrush("#36CFA0");
    private static readonly Brush BackgroundBrush = CreateBrush("#10283A"), QueryBackgroundBrush = CreateBrush("#112638"), HistogramBrush = CreateBrush("#286197");
    private static readonly Pen BorderPen = CreatePen(CreateBrush("#345269"), 1), QueryBorderPen = CreatePen(CreateBrush("#294257"), 1);
    private static readonly Pen GridPen = CreatePen(CreateBrush("#29465C"), 1), CurvePen = CreatePen(Green, 2);
    private static readonly Typeface LabelTypeface = new("Microsoft YaHei UI");
    private SolidColorBrush? _queryAccentBrush;
    private Brush? _queryHistogramBrush;
    private Pen? _queryCurvePen;

    private static SolidColorBrush CreateBrush(string color) => CreateBrush((Color)ColorConverter.ConvertFromString(color));

    private static SolidColorBrush CreateBrush(Color color, double opacity = 1)
    {
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    public void SetData(string title, string unit, IEnumerable<BatchMeasurement> source)
    {
        var rows = source.ToArray();
        _title = title; _unit = unit; _total = rows.Length; _passed = rows.Count(row => row.CountsAsOk);
        _ng = rows.Count(row => row.CountsAsNg);
        _unmeasured = rows.Count(row => row.IsUnmeasured);
        _distribution = MeasurementDistribution.Calculate(rows.Where(row => row.HasDistributionSample).Select(row => row.Value!.Value));
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var w = ActualWidth; var h = ActualHeight;
        if (w < 150 || h < 120) return;
        if (UseQueryStyle && (_queryAccentBrush is null || _queryAccentBrush.Color != QueryAccent))
        {
            _queryAccentBrush = CreateBrush(QueryAccent);
            _queryHistogramBrush = CreateBrush(QueryAccent, 0.38);
            _queryCurvePen = CreatePen(_queryAccentBrush, 2.5);
        }
        var accent = UseQueryStyle ? _queryAccentBrush! : Green;
        var histogramBrush = UseQueryStyle ? _queryHistogramBrush! : HistogramBrush;
        dc.DrawRoundedRectangle(UseQueryStyle ? QueryBackgroundBrush : BackgroundBrush, UseQueryStyle ? QueryBorderPen : BorderPen, new Rect(1, 1, w - 2, h - 2), 8, 8);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        void Label(string value, double x, double y, Brush brush, double size = 11, double? width = null)
        {
            var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                LabelTypeface, size, brush, pixelsPerDip)
                { MaxTextWidth = Math.Max(1, width ?? w - x - 12), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(text, new Point(x, y));
        }
        Label(_title, UseQueryStyle ? 18 : 14, 12, TextBrush, UseQueryStyle ? 15 : 13);
        Label($"采集 {_total:N0} · OK {_passed} / NG {_ng} · 未测到 {_unmeasured} · 样本 {_distribution.Count}", UseQueryStyle ? 18 : 14, 36, Muted, UseQueryStyle ? 12 : 11);
        var d = _distribution;
        if (d.Count == 0)
        {
            Label(_total == 0 ? "等待本站采集数据" : "暂无有效读数，异常记录可在批次查询中查看", 24, h / 2, Muted, 12);
            return;
        }
        Label($"均值 {d.Mean:G6} {_unit}   ·   标准差 {d.StandardDeviation:G5} {_unit}", UseQueryStyle ? 18 : 14, 55, UseQueryStyle ? accent : Muted, UseQueryStyle ? 12 : 11);
        if (!double.IsFinite(d.StandardDeviation)) { Label("数值跨度过大，无法拟合", 24, h / 2, Muted); return; }
        var low = Math.Min(d.Minimum, d.Mean - 3.5 * d.StandardDeviation);
        var high = Math.Max(d.Maximum, d.Mean + 3.5 * d.StandardDeviation);
        if (low == high) { var padding = Math.Max(Math.Abs(low) * 0.05, 0.001); low -= padding; high += padding; }
        var span = high - low;
        if (!double.IsFinite(span) || span <= 0) return;
        var plot = new Rect(UseQueryStyle ? 56 : 45, UseQueryStyle ? 91 : 83, Math.Max(1, w - (UseQueryStyle ? 82 : 65)), Math.Max(1, h - (UseQueryStyle ? 140 : 123)));
        var bins = Math.Clamp((int)Math.Ceiling(Math.Sqrt(d.Count)), 5, 20);
        var counts = new int[bins];
        foreach (var value in d.Values) counts[Math.Clamp((int)((value - low) / span * bins), 0, bins - 1)]++;
        var binWidth = span / bins;
        var canFit = d.Count >= 2 && d.StandardDeviation > 0;
        var peak = canFit ? d.Count * binWidth / (d.StandardDeviation * Math.Sqrt(2 * Math.PI)) : 0;
        var maxY = Math.Max(counts.Max(), peak) * 1.15;
        Label("频数", plot.Left, 70, Muted, 10);
        for (var i = 0; i <= 3; i++)
        {
            var y = plot.Bottom - plot.Height * i / 3;
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            Label((maxY * i / 3).ToString("0.#"), 5, y - 8, Muted, 9, 37);
        }
        for (var i = 0; i < bins; i++)
        {
            var height = counts[i] / maxY * plot.Height;
            dc.DrawRectangle(histogramBrush, null, new Rect(plot.Left + i * plot.Width / bins + 1,
                plot.Bottom - height, Math.Max(1, plot.Width / bins - 2), height));
        }
        if (canFit)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                for (var i = 0; i <= 160; i++)
                {
                    var value = low + span * i / 160;
                    var z = (value - d.Mean) / d.StandardDeviation;
                    var y = peak * Math.Exp(-0.5 * z * z);
                    var point = new Point(plot.Left + plot.Width * i / 160, plot.Bottom - y / maxY * plot.Height);
                    if (i == 0) context.BeginFigure(point, false, false); else context.LineTo(point, true, false);
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, UseQueryStyle ? _queryCurvePen! : CurvePen, geometry);
        }
        Label(low.ToString("G5"), plot.Left, plot.Bottom + 4, Muted, 10, plot.Width / 3);
        Label(high.ToString("G5") + " " + _unit, Math.Max(plot.Left, plot.Right - 115), plot.Bottom + 4, Muted, 10);
        Label(canFit ? UseQueryStyle ? "柱状：实测频数   ·   曲线：正态拟合（仅供分布参考）" : "蓝柱：实际频数  ·  绿线：正态拟合（不代表已验证正态性）"
            : "样本不足或标准差为0，仅显示实际频数", UseQueryStyle ? 18 : 14, h - 20, UseQueryStyle ? Muted : Blue, UseQueryStyle ? 10 : 9);
    }
}
