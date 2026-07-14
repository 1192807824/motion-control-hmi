using System.IO;
using System.Xml.Linq;

namespace VisionMasterHost;

internal sealed class VisionCalibrationSettings
{
    private static readonly string SettingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ControlHub",
        "vision-calibration.xml");

    public string SolutionPath { get; set; } = "";

    public string ProcedureName { get; set; } = "";

    public string ImageModuleKey { get; set; } = "";

    public static VisionCalibrationSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                return new VisionCalibrationSettings();
            }

            var root = XDocument.Load(SettingsFilePath).Root;
            if (root is null)
            {
                return new VisionCalibrationSettings();
            }

            return new VisionCalibrationSettings
            {
                SolutionPath = (string?)root.Element("solutionPath") ?? "",
                ProcedureName = (string?)root.Element("procedureName") ?? "",
                ImageModuleKey = (string?)root.Element("imageModuleKey") ?? ""
            };
        }
        catch
        {
            return new VisionCalibrationSettings();
        }
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(SettingsFilePath)
            ?? throw new InvalidOperationException("无法确定视觉标定配置目录。 ");
        Directory.CreateDirectory(directory);

        new XDocument(
            new XElement(
                "visionCalibration",
                new XElement("solutionPath", SolutionPath),
                new XElement("procedureName", ProcedureName),
                new XElement("imageModuleKey", ImageModuleKey)))
            .Save(SettingsFilePath);
    }
}
