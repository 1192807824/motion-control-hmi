using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlHub.Services.Persistence;

public sealed class LowerCameraTeachDataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public void Save(string filePath, LowerCameraTeachData data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(data);
        data.Validate();

        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, JsonSerializer.Serialize(data, JsonOptions));
    }

    public LowerCameraTeachData Load(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var data = JsonSerializer.Deserialize<LowerCameraTeachData>(File.ReadAllText(fullPath))
            ?? throw new InvalidDataException("下相机示教数据文件内容为空。");
        data.Validate();
        return data;
    }
}

public sealed class LowerCameraTeachData
{
    [JsonPropertyName("数据圆心X")]
    public double CircleCenterX { get; set; }

    [JsonPropertyName("数据圆心Y")]
    public double CircleCenterY { get; set; }

    [JsonPropertyName("直线起点X")]
    public double LineStartX { get; set; }

    [JsonPropertyName("直线起点Y")]
    public double LineStartY { get; set; }

    [JsonPropertyName("直线终点X")]
    public double LineEndX { get; set; }

    [JsonPropertyName("直线终点Y")]
    public double LineEndY { get; set; }

    [JsonPropertyName("转换坐标X")]
    public double TransformedX { get; set; }

    [JsonPropertyName("转换坐标Y")]
    public double TransformedY { get; set; }

    public void Validate()
    {
        var values = new[]
        {
            CircleCenterX,
            CircleCenterY,
            LineStartX,
            LineStartY,
            LineEndX,
            LineEndY,
            TransformedX,
            TransformedY
        };
        if (values.Any(value => !double.IsFinite(value)))
        {
            throw new InvalidDataException("下相机示教数据包含无效数值。");
        }
    }
}
