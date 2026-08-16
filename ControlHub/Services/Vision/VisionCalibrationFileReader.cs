using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace ControlHub.Services.Vision;

public static class VisionCalibrationFileReader
{
    /// <summary>
    /// 从成功完成的九点标定 XML 中读取采集顺序第 5 点，并换算为控制卡脉冲坐标。
    /// 本程序固定把第 5 点作为零偏移中心，因此该点就是标定时的拍照中心。
    /// </summary>
    public static (double X, double Y) ReadNinePointCenterPulses(string calibrationFilePath)
    {
        if (string.IsNullOrWhiteSpace(calibrationFilePath))
        {
            throw new InvalidDataException("九点标定文件路径不能为空。");
        }

        XDocument document;
        try
        {
            document = XDocument.Load(Path.GetFullPath(calibrationFilePath));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            throw new InvalidDataException("九点标定文件无法读取。", exception);
        }

        var worldPointList = document
            .Descendants()
            .FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, "CalibPointFListParam", StringComparison.Ordinal) &&
                string.Equals(
                    element.Attributes().FirstOrDefault(attribute =>
                        string.Equals(attribute.Name.LocalName, "ParamName", StringComparison.Ordinal))?.Value,
                    "WorldPointLst",
                    StringComparison.Ordinal));
        if (worldPointList is null)
        {
            throw new InvalidDataException("九点标定文件中未找到 WorldPointLst。");
        }

        var calibrationType = ReadCalibrationParameter(document, "CalibType");
        var calibrationPointCountText = ReadCalibrationParameter(document, "TransNum");
        var calibrationErrorStatusText = ReadCalibrationParameter(document, "CalibErrStatus");
        if (!string.Equals(calibrationType, "NPointCalib", StringComparison.Ordinal) ||
            !int.TryParse(
                calibrationPointCountText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var calibrationPointCount) ||
            calibrationPointCount != 9 ||
            !int.TryParse(
                calibrationErrorStatusText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var calibrationErrorStatus) ||
            calibrationErrorStatus != 0)
        {
            throw new InvalidDataException("所选文件不是成功完成的九点标定文件。");
        }

        var worldPoints = worldPointList
            .Elements()
            .Where(element => string.Equals(element.Name.LocalName, "PointF", StringComparison.Ordinal))
            .Select(ReadWorldPoint)
            .ToArray();
        if (worldPoints.Length != 9)
        {
            throw new InvalidDataException("九点标定文件的机械标定点数量不是 9。");
        }

        var recordedCenter = worldPoints[4];
        var centerX = recordedCenter.X * VisionCalibrationService.PulsesPerVisionUnit;
        var centerY = recordedCenter.Y * VisionCalibrationService.PulsesPerVisionUnit;
        if (!double.IsFinite(centerX) || !double.IsFinite(centerY))
        {
            throw new InvalidDataException("九点标定中心坐标无效。");
        }

        return (centerX, centerY);
    }

    private static string? ReadCalibrationParameter(XDocument document, string parameterName)
    {
        return document
            .Descendants()
            .FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, "CalibParam", StringComparison.Ordinal) &&
                string.Equals(
                    element.Attributes().FirstOrDefault(attribute =>
                        string.Equals(attribute.Name.LocalName, "ParamName", StringComparison.Ordinal))?.Value,
                    parameterName,
                    StringComparison.Ordinal))?
            .Descendants()
            .FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, "ParamValue", StringComparison.Ordinal))?
            .Value;
    }

    private static (double X, double Y) ReadWorldPoint(XElement pointElement)
    {
        var xText = pointElement.Elements().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, "X", StringComparison.Ordinal))?.Value;
        var yText = pointElement.Elements().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, "Y", StringComparison.Ordinal))?.Value;
        if (!double.TryParse(xText, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(yText, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !double.IsFinite(x) ||
            !double.IsFinite(y))
        {
            throw new InvalidDataException("九点标定文件包含无效的 WorldPointLst 坐标。");
        }

        return (x, y);
    }
}
