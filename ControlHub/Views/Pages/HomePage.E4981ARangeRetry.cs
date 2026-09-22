using System.Globalization;
using System.Windows.Controls;
using System.Windows.Media;
using ControlHub.Services.Devices;

namespace ControlHub.Views.Pages;

public partial class HomePage
{
    private E4981ARetryRange? _e4981ARetryRange;

    private sealed record E4981ARetryRange(double? LowerNf, double? UpperNf, int RetryCount)
    {
        public bool Contains(E4981AMeasurementResult? reading) =>
            RetryCount > 0 && reading is { IsSuccessful: true } &&
            reading.CapacitanceFarads >= LowerNf / 1e9 &&
            reading.CapacitanceFarads <= UpperNf / 1e9;
    }

    private E4981ARetryRange ReadE4981ARetryRange()
    {
        if (!int.TryParse(E4981ARangeRetryCountTextBox.Text, NumberStyles.Integer,
                CultureInfo.CurrentCulture, out var count) ||
            count is < 0 or > TestMeasurementRetry.MaximumRetryCount)
            throw new ArgumentException($"E4981A区间重试次数必须是0～{TestMeasurementRetry.MaximumRetryCount}之间的整数。");
        if (!TryParseOptionalCoordinate(E4981ARetryLowerTextBox.Text, out var lower) ||
            !TryParseOptionalCoordinate(E4981ARetryUpperTextBox.Text, out var upper) || lower > upper)
            throw new ArgumentException("E4981A重试上下限必须为有限数值，且下限不能大于上限（nF）。");
        if (count > 0 && (lower is null || upper is null))
            throw new ArgumentException("启用E4981A区间重试时，请填写重试下限和上限（nF）。");
        return new E4981ARetryRange(lower, upper, count);
    }

    private void LoadE4981ARetryRangeEditors()
    {
        E4981ARetryLowerTextBox.Text = _homeSettings.E4981ARetryLowerNf?.ToString("R", CultureInfo.CurrentCulture) ?? "";
        E4981ARetryUpperTextBox.Text = _homeSettings.E4981ARetryUpperNf?.ToString("R", CultureInfo.CurrentCulture) ?? "";
        E4981ARangeRetryCountTextBox.Text = _homeSettings.E4981ARangeRetryCount.ToString(CultureInfo.CurrentCulture);
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
            _homeSettings.E4981ARangeRetryCount = range.RetryCount;
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
        E4981ARetryRangeStatusText.Text = range.RetryCount == 0
            ? "区间重试已关闭；仪表异常仍按测试重试次数处理。"
            : $"电容落入 [{range.LowerNf:G9}, {range.UpperNf:G9}] nF（含边界）时，最多追加{range.RetryCount}次机械复测；按末次结果分料。";
        E4981ARetryRangeStatusText.Foreground = new SolidColorBrush(Color.FromRgb(159, 177, 191));
    }

    private void ShowE4981ARetryRangeError(string message)
    {
        E4981ARetryRangeStatusText.Text = message;
        E4981ARetryRangeStatusText.Foreground = new SolidColorBrush(Color.FromRgb(242, 122, 128));
    }
}
