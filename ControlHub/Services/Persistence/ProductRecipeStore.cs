using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ControlHub.Services.Devices;
using ControlHub.Services.Motion;

namespace ControlHub.Services.Persistence;

public sealed class ProductRecipeStore
{
    public const string RecipeFileName = "recipe.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _rootDirectory;
    private readonly string _activeRecipeFilePath;

    public ProductRecipeStore(string? rootDirectory = null)
    {
        _rootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ControlHub",
            "Recipes");
        _activeRecipeFilePath = Path.Combine(_rootDirectory, "active-recipe.txt");
    }

    public IReadOnlyList<ProductRecipe> LoadAll()
    {
        if (!Directory.Exists(_rootDirectory))
        {
            return [];
        }

        var recipes = new List<ProductRecipe>();
        foreach (var directory in Directory.EnumerateDirectories(_rootDirectory))
        {
            var recipePath = Path.Combine(directory, RecipeFileName);
            if (!File.Exists(recipePath))
            {
                continue;
            }

            try
            {
                var recipe = Deserialize(File.ReadAllText(recipePath));
                recipes.Add(recipe);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                // 单个损坏配方不能影响其它产品配方的加载。
            }
        }

        return recipes
            .OrderByDescending(recipe => recipe.ModifiedAtUtc)
            .ThenBy(recipe => recipe.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public ProductRecipe Create(string name)
    {
        return new ProductRecipe
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = ValidateName(name),
            CreatedAtUtc = DateTime.UtcNow,
            ModifiedAtUtc = DateTime.UtcNow
        };
    }

    public void Save(ProductRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        recipe.Id = ValidateId(recipe.Id);
        recipe.Name = ValidateName(recipe.Name);
        Normalize(recipe);
        recipe.ModifiedAtUtc = DateTime.UtcNow;
        if (recipe.CreatedAtUtc == default)
        {
            recipe.CreatedAtUtc = recipe.ModifiedAtUtc;
        }

        var recipeDirectory = GetRecipeDirectory(recipe.Id);
        Directory.CreateDirectory(recipeDirectory);

        ArchiveVisionAssets(recipe, recipeDirectory);

        WriteAtomic(
            Path.Combine(recipeDirectory, RecipeFileName),
            JsonSerializer.Serialize(recipe, JsonOptions));
    }

    public ProductRecipe DeserializeForExistingRecipe(string json, string recipeId)
    {
        var recipe = Deserialize(json);
        recipe.Id = ValidateId(recipeId);
        recipe.Name = ValidateName(recipe.Name);
        return recipe;
    }

    public string Serialize(ProductRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        return JsonSerializer.Serialize(recipe, JsonOptions);
    }

    public void Delete(string recipeId)
    {
        var directory = GetRecipeDirectory(ValidateId(recipeId));
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        if (string.Equals(GetActiveRecipeId(), recipeId, StringComparison.OrdinalIgnoreCase))
        {
            SetActiveRecipeId(null);
        }
    }

    public string? GetActiveRecipeId()
    {
        try
        {
            var value = File.Exists(_activeRecipeFilePath)
                ? File.ReadAllText(_activeRecipeFilePath).Trim() is { Length: > 0 } id
                    ? id
                    : null
                : null;
            return value is null ? null : ValidateId(value);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    public void SetActiveRecipeId(string? recipeId)
    {
        Directory.CreateDirectory(_rootDirectory);
        if (string.IsNullOrWhiteSpace(recipeId))
        {
            if (File.Exists(_activeRecipeFilePath))
            {
                File.Delete(_activeRecipeFilePath);
            }
            return;
        }

        WriteAtomic(_activeRecipeFilePath, ValidateId(recipeId));
    }

    public static string? FindDefaultVisionSolutionPath()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var preferred = Path.Combine(desktop, "新纳方案.sol");
        if (File.Exists(preferred))
        {
            return preferred;
        }

        var fallback = Path.Combine(desktop, "标定方案.sol");
        return File.Exists(fallback) ? fallback : null;
    }

    public static T Clone<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)
               ?? throw new InvalidDataException($"无法复制{typeof(T).Name}配置。");
    }

    private ProductRecipe Deserialize(string json)
    {
        var recipe = JsonSerializer.Deserialize<ProductRecipe>(json, JsonOptions)
                     ?? throw new InvalidDataException("配方内容为空。");
        recipe.Id = ValidateId(recipe.Id);
        recipe.Name = ValidateName(recipe.Name);
        Normalize(recipe);
        return recipe;
    }

    private static void Normalize(ProductRecipe recipe)
    {
        recipe.Production ??= new HomePageSettings();
        recipe.Axes ??= [];
        recipe.Motion ??= new MotionRecipeSettings();
        recipe.Motion.DefaultMoveProfile ??= new MotionMoveProfile();
        recipe.Motion.AxisMoveProfiles ??= [];
        recipe.Motion.DefaultHomeProfile ??= new MotionHomeProfile();
        recipe.Motion.AxisHomeProfiles ??= [];
        recipe.Motion.HomeSequence ??= [];
        recipe.Motion.IoPointNames ??= [];
        recipe.VisionCalibration ??= new VisualCalibrationSettings();
        recipe.VisionAssets ??= [];
        recipe.VisionProcedureNames ??= new VisionProcedureNames();
        if (recipe.LegacyVisionProcedures is { Length: >= 7 } legacy)
        {
            recipe.VisionProcedureNames = new VisionProcedureNames
            {
                Inspection = legacy[0],
                NozzleTeaching = legacy[1],
                Calibration = legacy[2],
                LowerCameraCalibration = legacy[3],
                RotationPoint = legacy[4],
                RotationCenter = legacy[5],
                LowerCameraCorrection = legacy[6]
            };
            recipe.LegacyVisionProcedures = null;
        }
        recipe.VisionProcedureNames.Validate();
        recipe.E4981A ??= new TcpConnectionSettings();
        recipe.SM7110 ??= new SerialConnectionSettings();
        recipe.VibrationFeeder ??= new VibrationFeederSettings();
    }

    private string GetRecipeDirectory(string recipeId)
    {
        var root = Path.GetFullPath(_rootDirectory);
        var directory = Path.GetFullPath(Path.Combine(root, recipeId));
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("配方目录越界。");
        }
        return directory;
    }

    private static void ArchiveVisionAssets(ProductRecipe recipe, string recipeDirectory)
    {
        var assetDirectory = Path.Combine(recipeDirectory, "vision-assets");
        foreach (var property in typeof(VisualCalibrationSettings).GetProperties()
                     .Where(property =>
                         property.CanRead &&
                         property.CanWrite &&
                         property.PropertyType == typeof(string) &&
                         (property.Name.EndsWith("FilePath", StringComparison.Ordinal) ||
                          property.Name.EndsWith("ProfilePath", StringComparison.Ordinal))))
        {
            var sourceValue = property.GetValue(recipe.VisionCalibration) as string;
            if (string.IsNullOrWhiteSpace(sourceValue) || !File.Exists(sourceValue))
            {
                continue;
            }

            Directory.CreateDirectory(assetDirectory);
            var extension = Path.GetExtension(sourceValue);
            var destination = Path.Combine(
                assetDirectory,
                property.Name + (string.IsNullOrWhiteSpace(extension) ? ".dat" : extension));
            var source = Path.GetFullPath(sourceValue);
            if (!string.Equals(source, Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(source, destination, overwrite: true);
            }
            property.SetValue(recipe.VisionCalibration, destination);
            recipe.VisionAssets[property.Name] = Path.GetRelativePath(recipeDirectory, destination);
        }
    }

    private static string ValidateId(string? recipeId)
    {
        return Guid.TryParseExact(recipeId, "N", out var parsed)
            ? parsed.ToString("N")
            : throw new InvalidDataException("配方编号无效。");
    }

    private static string ValidateName(string? name)
    {
        var value = name?.Trim() ?? "";
        if (value.Length is < 1 or > 80)
        {
            throw new InvalidDataException("配方名称必须为1–80个字符。");
        }
        return value;
    }

    private static void WriteAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)
                        ?? throw new InvalidDataException("配置文件目录无效。");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

public sealed class ProductRecipe
{
    public int Version { get; set; } = 1;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "未命名配方";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime ModifiedAtUtc { get; set; } = DateTime.UtcNow;

    public HomePageSettings Production { get; set; } = new();

    public Dictionary<int, AxisSettings> Axes { get; set; } = [];

    public MotionRecipeSettings Motion { get; set; } = new();

    public VisualCalibrationSettings VisionCalibration { get; set; } = new();

    public Dictionary<string, string> VisionAssets { get; set; } = [];

    public VisionProcedureNames VisionProcedureNames { get; set; } = new();

    [JsonPropertyName("VisionProcedures")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? LegacyVisionProcedures { get; set; }

    public TcpConnectionSettings E4981A { get; set; } = new();

    public SerialConnectionSettings SM7110 { get; set; } = new();

    public VibrationFeederSettings VibrationFeeder { get; set; } = new();
}

public sealed class MotionRecipeSettings
{
    public int HomeTimeoutSeconds { get; set; } = 60;

    public MotionMoveProfile DefaultMoveProfile { get; set; } = new();

    public Dictionary<int, MotionMoveProfile> AxisMoveProfiles { get; set; } = [];

    public MotionHomeProfile DefaultHomeProfile { get; set; } = new();

    public Dictionary<int, MotionHomeProfile> AxisHomeProfiles { get; set; } = [];

    public int[] HomeSequence { get; set; } = [];

    public Dictionary<string, string> IoPointNames { get; set; } = [];
}

public sealed class VisionProcedureNames
{
    public string Inspection { get; set; } = "找芯片流程";

    public string NozzleTeaching { get; set; } = "粗定位示教流程";

    public string Calibration { get; set; } = "标定流程";

    public string LowerCameraCalibration { get; set; } = "下相机标定流程";

    public string RotationPoint { get; set; } = "获取三点流程";

    public string RotationCenter { get; set; } = "计算旋转中心";

    public string LowerCameraCorrection { get; set; } = "下相机纠偏";

    public void Validate()
    {
        foreach (var (label, value) in new[]
                 {
                     ("找芯片流程", Inspection),
                     ("粗定位示教流程", NozzleTeaching),
                     ("标定流程", Calibration),
                     ("下相机标定流程", LowerCameraCalibration),
                     ("获取三点流程", RotationPoint),
                     ("计算旋转中心", RotationCenter),
                     ("下相机纠偏", LowerCameraCorrection)
                 })
        {
            if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 100)
            {
                throw new InvalidDataException($"{label}名称必须为1–100个字符。");
            }
        }
    }
}
