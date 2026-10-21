using ControlHub.Services.Motion;

namespace ControlHub.Services.Vision;

/// <summary>A single successful recognized-center move, followed by manual XY correction.</summary>
public sealed class NozzleAlignmentCorrection
{
    private readonly VisionCalibrationSnapshot _calibration;
    private readonly VisionMotionTarget _target;

    public NozzleAlignmentCorrection(VisionCalibrationSnapshot calibration, VisionMotionTarget target,
        CalibrationCenterPosition actual)
    {
        _calibration = calibration;
        _target = target;
        if (!Matches(calibration, target.Tool) || !double.IsFinite(target.X) || !double.IsFinite(target.Y))
            throw new InvalidOperationException("微调仅支持第一套XY已标定的吸嘴。");
        ValidatePosition(actual);
    }

    public VisionTargetTool Tool => _target.Tool;

    public bool Matches(VisionCalibrationSnapshot current, VisionTargetTool tool) =>
        tool == Tool && current.AxisSet == VisionCalibrationAxisSet.First &&
        _calibration.AxisSet == VisionCalibrationAxisSet.First &&
        current.XHardwareAxisNo == 1 && current.YHardwareAxisNo == 2 &&
        _calibration.XHardwareAxisNo == 1 && _calibration.YHardwareAxisNo == 2 &&
        string.Equals(current.CalibrationFilePath, _calibration.CalibrationFilePath, StringComparison.OrdinalIgnoreCase) &&
        (tool switch
        {
            VisionTargetTool.Nozzle1 => current.Nozzle1Calibrated && _calibration.Nozzle1Calibrated &&
                current.Nozzle1OffsetX == _calibration.Nozzle1OffsetX && current.Nozzle1OffsetY == _calibration.Nozzle1OffsetY,
            VisionTargetTool.Nozzle2 => current.Nozzle2Calibrated && _calibration.Nozzle2Calibrated &&
                current.Nozzle2OffsetX == _calibration.Nozzle2OffsetX && current.Nozzle2OffsetY == _calibration.Nozzle2OffsetY,
            _ => false
        });

    public (double X, double Y) CalculateOffset(CalibrationCenterPosition actual)
    {
        ValidatePosition(actual);
        // Use the original commanded target, not the arrived position or accumulated button distances.
        // This includes the final feedback correction and never adds the same correction twice.
        var x = (Tool == VisionTargetTool.Nozzle1 ? _calibration.Nozzle1OffsetX : _calibration.Nozzle2OffsetX)
            + actual.ActualX - _target.X;
        var y = (Tool == VisionTargetTool.Nozzle1 ? _calibration.Nozzle1OffsetY : _calibration.Nozzle2OffsetY)
            + actual.ActualY - _target.Y;
        if (!double.IsFinite(x) || !double.IsFinite(y))
            throw new InvalidOperationException("微调后的吸嘴偏移无效。");
        return (x, y);
    }

    private static void ValidatePosition(CalibrationCenterPosition position)
    {
        if (position.XHardwareAxisNo != 1 || position.YHardwareAxisNo != 2 ||
            !double.IsFinite(position.ActualX) || !double.IsFinite(position.ActualY))
            throw new InvalidOperationException("微调需要有效的第一套XY反馈位置。");
    }
}
