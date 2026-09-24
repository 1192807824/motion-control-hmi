using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ControlHub.Services.Vision;

namespace ControlHub.Views.Pages;

public partial class HomePage
{
    private readonly record struct ObservationCompensation(
        bool Enabled, double XPulses, double YPulses, double RPulses);

    private static LowerCameraPlacementTarget ApplyObservationCompensation(
        LowerCameraPlacementTarget target,
        int nozzleNumber,
        bool correctionSucceeded,
        ObservationCompensation compensation)
    {
        if (!compensation.Enabled || nozzleNumber != 1 || !correctionSucceeded)
        {
            return target;
        }

        var result = new LowerCameraPlacementTarget(
            target.X + compensation.XPulses,
            target.Y + compensation.YPulses,
            target.R + compensation.RPulses,
            target.RCorrectionPulses + compensation.RPulses);
        if (!double.IsFinite(result.X) || !double.IsFinite(result.Y) ||
            !double.IsFinite(result.R) || !double.IsFinite(result.RCorrectionPulses))
        {
            throw new InvalidOperationException("统一观测位补偿后的X/Y/R目标超出有效范围。");
        }

        return result;
    }

    private ObservationCompensation ReadObservationCompensationFromInputs()
    {
        if (ObservationCompensationEnabledCheckBox.IsChecked != true)
        {
            return default;
        }

        var x = ParseFiniteCoordinate(ObservationCompensationXTextBox.Text, "统一观测位X补偿（pulse）");
        var y = ParseFiniteCoordinate(ObservationCompensationYTextBox.Text, "统一观测位Y补偿（pulse）");
        var degrees = ParseFiniteCoordinate(ObservationCompensationRTextBox.Text, "统一观测位R补偿（°）");
        // 相对电机角度：保留输入正负和整圈数，不套用视觉角度的方向系数或90°周期。
        var r = NozzleRotationMath.ConvertDegreesToPulses(degrees, NozzleRPulsesPerRevolution);
        if (!double.IsFinite(r))
        {
            throw new InvalidOperationException("统一观测位R补偿换算后超出有效范围。");
        }

        return new ObservationCompensation(true, x, y, r);
    }

    private bool ObservationCompensationInputsValid()
    {
        if (ObservationCompensationEnabledCheckBox.IsChecked != true)
        {
            return true;
        }

        return TryReadObservationCompensationValues(out _, out _, out _);
    }

    private bool TryReadObservationCompensationValues(out double x, out double y, out double degrees)
    {
        x = y = degrees = 0d;
        return TryParseCoordinate(ObservationCompensationXTextBox.Text, out x) &&
               TryParseCoordinate(ObservationCompensationYTextBox.Text, out y) &&
               TryParseCoordinate(ObservationCompensationRTextBox.Text, out degrees) &&
               double.IsFinite(degrees / 360d * NozzleRPulsesPerRevolution);
    }

    private void LoadObservationCompensationInputs()
    {
        ObservationCompensationEnabledCheckBox.IsChecked = _homeSettings.ObservationCompensationEnabled;
        ObservationCompensationXTextBox.Text = _homeSettings.ObservationCompensationXPulses.ToString("R", CultureInfo.CurrentCulture);
        ObservationCompensationYTextBox.Text = _homeSettings.ObservationCompensationYPulses.ToString("R", CultureInfo.CurrentCulture);
        ObservationCompensationRTextBox.Text = _homeSettings.ObservationCompensationRDegrees.ToString("R", CultureInfo.CurrentCulture);
        SetObservationCompensationStatus("仅吸嘴1纠偏成功后叠加；X/Y为脉冲，R为度数，均支持正负。启动生产时生效。", true);
    }

    private void ObservationCompensationInput_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingPresetPositions || ObservationCompensationStatusText is null)
        {
            return;
        }

        SaveObservationCompensationFromInputs();
        UpdateHomeCommandState();
    }

    private void SaveObservationCompensationFromInputs(bool throwOnInvalid = false)
    {
        if (_loadingPresetPositions)
        {
            return;
        }

        var enabled = ObservationCompensationEnabledCheckBox.IsChecked == true;
        var valid = TryReadObservationCompensationValues(out var x, out var y, out var degrees);
        if (!valid && (enabled || throwOnInvalid))
        {
            const string message = "补偿未保存：X/Y/R必须为有效的正数、负数或0，且R换算不能溢出。";
            SetObservationCompensationStatus(message, false);
            if (throwOnInvalid)
            {
                throw new InvalidOperationException(message);
            }

            return;
        }

        _homeSettings.ObservationCompensationEnabled = enabled;
        if (valid)
        {
            _homeSettings.ObservationCompensationXPulses = x;
            _homeSettings.ObservationCompensationYPulses = y;
            _homeSettings.ObservationCompensationRDegrees = degrees;
        }

        try
        {
            _homeSettingsStore.Save(_homeSettings);
            SetObservationCompensationStatus(enabled
                ? $"已保存：吸嘴1附加 X={x:0.#####}、Y={y:0.#####} pulse，R={degrees:0.#####}°（{degrees / 360d * NozzleRPulsesPerRevolution:0.#####} pulse）；启动生产时生效。"
                : "已关闭统一观测位补偿；保留已保存参数，两个吸嘴沿用正常纠偏。", true);
        }
        catch (Exception exception)
        {
            SetObservationCompensationStatus($"统一观测位补偿保存失败：{exception.Message}", false);
            if (throwOnInvalid)
            {
                throw;
            }
        }
    }

    private void SetObservationCompensationStatus(string message, bool success)
    {
        if (!success) ControlHub.Services.Persistence.AlarmHistory.Record("观测补偿", "OBSERVATION", message);
        ObservationCompensationStatusText.Text = message;
        ObservationCompensationStatusText.Foreground = new SolidColorBrush(success
            ? Color.FromRgb(159, 177, 191)
            : Color.FromRgb(242, 122, 128));
    }

    private void UpdateObservationCompensationCommandState(bool idle)
    {
        ObservationCompensationEnabledCheckBox.IsEnabled = idle;
        ObservationCompensationXTextBox.IsEnabled = idle;
        ObservationCompensationYTextBox.IsEnabled = idle;
        ObservationCompensationRTextBox.IsEnabled = idle;
    }
}
