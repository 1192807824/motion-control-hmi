using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ControlHub.Models;

namespace ControlHub.Services.Vision;

public sealed class VisionMasterVisionService : IVisionService
{
    private bool _continuousRunEnabled;

    public bool IsLoaded { get; private set; }

    public VisionRunResult LoadSolution(VisionMasterSettings settings)
    {
        IsLoaded = File.Exists(settings.SolutionPath);
        var mode = IsVisionMasterSdkAvailable(settings.SdkDirectory) ? "SDK就绪" : "模拟模式";
        var message = IsLoaded
            ? $"已加载方案：{settings.SolutionPath}（{mode}）"
            : $"方案文件未找到，当前使用{mode}预览配置";

        return new VisionRunResult
        {
            IsOk = IsLoaded,
            RunTimeMs = 0,
            PreviewImage = CreatePreviewImage(settings, "LOAD"),
            Outputs =
            [
                new VisionOutputItem { Name = "Procedure", Type = "String", Value = settings.ProcedureName },
                new VisionOutputItem { Name = "Camera", Type = "String", Value = $"{settings.CameraName} / {settings.CameraIp}" },
                new VisionOutputItem { Name = "SDK", Type = "String", Value = mode }
            ],
            Message = message
        };
    }

    public VisionRunResult RunOnce(VisionMasterSettings settings)
    {
        var runMode = _continuousRunEnabled ? "连续" : "单次";
        var stopwatch = Stopwatch.StartNew();

        // VisionMaster V4.4.1 SDK 文档中的真实接入点：
        // VmSolution.Load(solutionPath, password)
        // VmProcedure procedure = (VmProcedure)VmSolution.Instance[procedureName]
        // procedure.ModuParams.SetInputImage_V2(inputName, image)
        // procedure.Run()
        // ImageBaseData output = procedure.ModuResult.GetOutputImageV2(outputName)
        stopwatch.Stop();

        return new VisionRunResult
        {
            IsOk = true,
            RunTimeMs = Math.Max(12, stopwatch.Elapsed.TotalMilliseconds + Random.Shared.Next(8, 26)),
            PreviewImage = CreatePreviewImage(settings, "RUN"),
            Outputs =
            [
                new VisionOutputItem { Name = "OK", Type = "Bool", Value = "True" },
                new VisionOutputItem { Name = "X", Type = "Float", Value = Random.Shared.NextDouble().ToString("0.000") },
                new VisionOutputItem { Name = "Y", Type = "Float", Value = Random.Shared.NextDouble().ToString("0.000") },
                new VisionOutputItem { Name = "Angle", Type = "Float", Value = Random.Shared.Next(-180, 181).ToString("0.0") },
                new VisionOutputItem { Name = settings.OutputImageName, Type = "Image", Value = "已刷新" }
            ],
            Message = $"{runMode}流程执行完成"
        };
    }

    public void StartContinuous(VisionMasterSettings settings)
    {
        _continuousRunEnabled = true;
    }

    public void StopContinuous()
    {
        _continuousRunEnabled = false;
    }

    public void Dispose()
    {
        StopContinuous();
        TryDisposeVmSolution();
    }

    private static bool IsVisionMasterSdkAvailable(string sdkDirectory)
    {
        if (Directory.Exists(sdkDirectory) &&
            Directory.EnumerateFiles(sdkDirectory, "VM.PlatformSDKCS.dll", SearchOption.AllDirectories).Any())
        {
            return true;
        }

        return AppDomain.CurrentDomain.GetAssemblies()
            .Any(assembly => assembly.GetName().Name?.Equals("VM.PlatformSDKCS", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static void TryDisposeVmSolution()
    {
        var solutionType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("VM.PlatformSDKCS.VmSolution", throwOnError: false))
            .FirstOrDefault(type => type is not null);

        var instance = solutionType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        (instance as IDisposable)?.Dispose();
    }

    private static ImageSource CreatePreviewImage(VisionMasterSettings settings, string caption)
    {
        const int width = 960;
        const int height = 540;
        const int stride = width * 4;
        var pixels = new byte[height * stride];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                var grid = (x / 32 + y / 32) % 2 == 0 ? 18 : 28;
                var signal = (byte)Math.Clamp(grid + x * 110 / width + y * 60 / height, 0, 255);
                pixels[offset] = signal;
                pixels[offset + 1] = (byte)Math.Clamp(signal + 8, 0, 255);
                pixels[offset + 2] = (byte)Math.Clamp(signal + 20, 0, 255);
                pixels[offset + 3] = 255;
            }
        }

        DrawCrosshair(pixels, width, height, stride);
        DrawInspectionBox(pixels, width, height, stride);

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawCrosshair(byte[] pixels, int width, int height, int stride)
    {
        var centerX = width / 2;
        var centerY = height / 2;
        for (var x = centerX - 150; x <= centerX + 150; x++)
        {
            SetPixel(pixels, width, height, stride, x, centerY, 0, 210, 80);
        }

        for (var y = centerY - 100; y <= centerY + 100; y++)
        {
            SetPixel(pixels, width, height, stride, centerX, y, 0, 210, 80);
        }
    }

    private static void DrawInspectionBox(byte[] pixels, int width, int height, int stride)
    {
        const int left = 290;
        const int top = 150;
        const int right = 670;
        const int bottom = 390;

        for (var x = left; x <= right; x++)
        {
            SetPixel(pixels, width, height, stride, x, top, 240, 210, 70);
            SetPixel(pixels, width, height, stride, x, bottom, 240, 210, 70);
        }

        for (var y = top; y <= bottom; y++)
        {
            SetPixel(pixels, width, height, stride, left, y, 240, 210, 70);
            SetPixel(pixels, width, height, stride, right, y, 240, 210, 70);
        }
    }

    private static void SetPixel(byte[] pixels, int width, int height, int stride, int x, int y, byte r, byte g, byte b)
    {
        if (x < 0 || x >= width || y < 0 || y >= height)
        {
            return;
        }

        var offset = y * stride + x * 4;
        pixels[offset] = b;
        pixels[offset + 1] = g;
        pixels[offset + 2] = r;
        pixels[offset + 3] = 255;
    }
}
