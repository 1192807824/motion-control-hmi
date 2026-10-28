using System.Globalization;
using System.Windows.Controls;
using System.Windows.Media;
using ControlHub.Services.Devices;

namespace ControlHub.Views.Pages;

public partial class HomePage
{
    private E4981ARetryRange? _e4981ARetryRange;

    private sealed record E4981ARetryRange(double? LowerNf, double? UpperNf)
    {
        public bool Accepts(E4981AMeasurementResult reading) =>
            reading.IsSuccessful && (LowerNf is null && UpperNf is null ||
                reading.CapacitanceFarads >= LowerNf / 1e9 &&
                reading.CapacitanceFarads <= UpperNf / 1e9);
    }

    private static bool IsTestStationMeasurementComplete(TestStationMeasurementResult result, E4981ARetryRange? range) =>
        result.Passed && (result.E4981AReading is null || range is null || range.Accepts(result.E4981AReading));

    private E4981ARetryRange ReadE4981ARetryRange()
    {
        _ = ReadTestRetryCount();
        if (!TryParseOptionalCoordinate(E4981ARetryLowerTextBox.Text, out var lower) ||
            !TryParseOptionalCoordinate(E4981ARetryUpperTextBox.Text, out var upper) || lower > upper)
            throw new ArgumentException("E4981A免重试上下限必须为有限数值，且下限不能大于上限（nF）。");
        if (lower.HasValue != upper.HasValue)
            throw new ArgumentException("请同时填写E4981A免重试下限和上限（nF），或同时留空。");
        return new E4981ARetryRange(lower, upper);
    }

    private void LoadE4981ARetryRangeEditors()
    {
        E4981ARetryLowerTextBox.Text = _homeSettings.E4981ARetryLowerNf?.ToString("R", CultureInfo.CurrentCulture) ?? "";
        E4981ARetryUpperTextBox.Text = _homeSettings.E4981ARetryUpperNf?.ToString("R", CultureInfo.CurrentCulture) ?? "";
        try { ShowE4981ARetryRangeStatus(ReadE4981ARetryRange()); }
        catch (ArgumentException exception) { ShowE4981ARetryRangeError(exception.Message); }
    }

    private void SaveE4981ARetryRangeFromInputs(bool throwOnInvalid = false)
    {
        if (_loadingPresetPositions)
            return;
        try
        {
            var range = ReadE4981ARetryRange();
            _homeSettings.E4981ARetryLowerNf = range.LowerNf;
            _homeSettings.E4981ARetryUpperNf = range.UpperNf;
            _homeSettings.TestRetryCount = ReadTestRetryCount();
            _homeSettingsStore.Save(_homeSettings);
            ShowE4981ARetryRangeStatus(range);
        }
        catch (Exception exception) when (!throwOnInvalid)
        {
            ShowE4981ARetryRangeError($"未保存：{exception.Message}");
        }
    }

    private void E4981ARetryRangeTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingPresetPositions || E4981ARetryRangeStatusText is null)
            return;
        SaveE4981ARetryRangeFromInputs();
        UpdateHomeCommandState();
    }

    private void ShowE4981ARetryRangeStatus(E4981ARetryRange range)
    {
        var count = ReadTestRetryCount();
        E4981ARetryRangeStatusText.Text = count == 0
            ? "所有测试重试已关闭；按首次测量结果处理。"
            : range.LowerNf is null
                ? $"未设置电容区间，仅按仪表失败条件重试；每站最多追加{count}次。"
                : $"电容不在 [{range.LowerNf:G9}, {range.UpperNf:G9}] nF（含边界）内才触发区间复测；与仪表失败共用{count}次额度，按末次结果分料。";
        E4981ARetryRangeStatusText.Foreground = new SolidColorBrush(Color.FromRgb(159, 177, 191));
    }

    private void ShowE4981ARetryRangeError(string message)
    {
        E4981ARetryRangeStatusText.Text = message;
        E4981ARetryRangeStatusText.Foreground = new SolidColorBrush(Color.FromRgb(242, 122, 128));
    }
}
