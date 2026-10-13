using ControlHub.Services.Motion;

namespace ControlHub.Services.Vision;

/// <summary>A single successful recognized-center move, followed by manual XY correction.</summary>
public sealed class NozzleAlignmentCorrection
{
    private readonly VisionCalibrationSnapshot _calibration;
    private readonly VisionMotionTarget _target;
    private CalibrationCenterPosition _lastPosition;

    public NozzleAlignmentCorrection(VisionCalibrationSnapshot calibration, VisionMotionTarget target,
        CalibrationCenterPosition actual)
    {
        _calibration = calibration;
        _target = target;
        _lastPosition = actual;
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

    public void EnsurePositionUnchanged(CalibrationCenterPosition actual, double tolerance)
    {
        ValidatePosition(actual);
        if (!double.IsFinite(tolerance) || tolerance < 0 ||
            Math.Abs(actual.ActualX - _lastPosition.ActualX) > tolerance ||
            Math.Abs(actual.ActualY - _lastPosition.ActualY) > tolerance)
            throw new InvalidOperationException("XY位置已在本次微调之外发生变化，请重新识别中心点。");
    }

    public void RecordMove(CalibrationCenterPosition actual)
    {
        ValidatePosition(actual);
        _lastPosition = actual;
    }

    public (double X, double Y) CalculateOffset(CalibrationCenterPosition actual, double tolerance)
    {
        EnsurePositionUnchanged(actual, tolerance);
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
