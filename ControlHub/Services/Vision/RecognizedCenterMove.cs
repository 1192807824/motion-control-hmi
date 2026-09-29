using System.Globalization;
using System.IO;
using ControlHub.Services.Motion;

namespace ControlHub.Services.Vision;

public sealed record RecognizedCenterResult(double PixelX, double PixelY, double TransformedX, double TransformedY,
    double CenterPixelX, double CenterPixelY, double CenterTransformedX, double CenterTransformedY)
{
    public static RecognizedCenterResult Parse(string response)
    {
        var parts = response.Split('\t');
        var values = new double[8];
        if (parts.Length != values.Length) throw new InvalidDataException("识别中心点返回格式无效。");
        for (var i = 0; i < values.Length; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !double.IsFinite(values[i]))
                throw new InvalidDataException("识别中心点或标定坐标无效。");
        var result = new RecognizedCenterResult(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7]);
        result.Validate();
        return result;
    }

    public void Validate()
    {
        if (new[] { PixelX, PixelY, TransformedX, TransformedY, CenterPixelX, CenterPixelY, CenterTransformedX, CenterTransformedY }
                .Any(value => !double.IsFinite(value)) || CenterPixelX <= 0 || CenterPixelY <= 0 ||
            PixelX < 0 || PixelY < 0 || PixelX > CenterPixelX * 2 || PixelY > CenterPixelY * 2)
            throw new InvalidDataException("识别中心点超出本次图像范围或标定坐标无效。");
    }
}

public static class RecognizedCenterMove
{
    public const string ProcedureName = "找芯片流程_011_测试";

    public static async Task<VisionMotionTarget> ExecuteAsync(VisionCalibrationSnapshot calibration, VisionTargetTool nozzle,
        Func<CalibrationCenterPosition> capture, Func<CancellationToken, Task<RecognizedCenterResult>> recognize,
        Func<VisionMotionTarget, CancellationToken, Task> move, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = capture();
        var result = await recognize(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var after = capture();
        if (after.XHardwareAxisNo != before.XHardwareAxisNo || after.YHardwareAxisNo != before.YHardwareAxisNo ||
            !double.IsFinite(after.ActualX) || !double.IsFinite(after.ActualY) ||
            Math.Abs(after.ActualX - before.ActualX) > 10 || Math.Abs(after.ActualY - before.ActualY) > 10)
            throw new InvalidOperationException("识别期间XY位置已变化，本次结果不能用于移动，请重新识别。");
        var target = Plan(result, before, calibration, nozzle);
        cancellationToken.ThrowIfCancellationRequested();
        await move(target, cancellationToken);
        return target;
    }

    public static VisionMotionTarget Plan(RecognizedCenterResult result, CalibrationCenterPosition capturePosition,
        VisionCalibrationSnapshot calibration, VisionTargetTool nozzle)
    {
        result.Validate();
        if (calibration.AxisSet != VisionCalibrationAxisSet.First || calibration.XHardwareAxisNo != 1 || calibration.YHardwareAxisNo != 2 ||
            capturePosition.XHardwareAxisNo != 1 || capturePosition.YHardwareAxisNo != 2)
            throw new InvalidOperationException("识别中心点移动仅用于第一套XY（轴1/2）。");
        var (offsetX, offsetY) = nozzle switch
        {
            VisionTargetTool.Nozzle1 when calibration.Nozzle1Calibrated => (calibration.Nozzle1OffsetX, calibration.Nozzle1OffsetY),
            VisionTargetTool.Nozzle2 when calibration.Nozzle2Calibrated => (calibration.Nozzle2OffsetX, calibration.Nozzle2OffsetY),
            _ => throw new InvalidOperationException("请选择已完成偏移标定的吸嘴1或吸嘴2。")
        };
        // 与第四步点选移动一致：当前反馈 + 图像中心到识别点的反向差值 + 所选吸嘴偏移。
        var x = capturePosition.ActualX + (result.CenterTransformedX - result.TransformedX) * VisionCalibrationService.PulsesPerVisionUnit + offsetX;
        var y = capturePosition.ActualY + (result.CenterTransformedY - result.TransformedY) * VisionCalibrationService.PulsesPerVisionUnit + offsetY;
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new InvalidOperationException("识别中心点的XY目标无效。");
        return new(x, y, nozzle);
    }
}
