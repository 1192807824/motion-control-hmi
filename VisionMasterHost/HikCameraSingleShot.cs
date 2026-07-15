using System.IO;
using System.Runtime.InteropServices;
using MvCamCtrl.NET;

namespace VisionMasterHost;

internal sealed class HikCameraFrame
{
    public HikCameraFrame(string filePath, int width, int height)
    {
        FilePath = filePath;
        Width = width;
        Height = height;
    }

    public string FilePath { get; }

    public int Width { get; }

    public int Height { get; }
}

/// <summary>
/// 主页专用海康相机单拍。它直接使用 MVS 相机 SDK，不加载 VisionMaster .sol，
/// 每次只在本方法内打开相机、取得一帧并关闭，确保标定方案不会在后台占用设备。
/// </summary>
internal static class HikCameraSingleShot
{
    private static readonly object CaptureSync = new();

    public static HikCameraFrame CaptureBmp()
    {
        lock (CaptureSync)
        {
            return CaptureBmpCore();
        }
    }

    private static HikCameraFrame CaptureBmpCore()
    {
        var deviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
        EnsureSuccess(
            MyCamera.MV_CC_EnumDevices_NET(
                MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE,
                ref deviceList),
            "枚举海康相机");
        if (deviceList.nDeviceNum == 0)
        {
            throw new InvalidOperationException("未发现可用的海康 GigE/USB 相机。");
        }

        var candidates = new List<CameraCandidate>();
        for (var index = 0; index < deviceList.nDeviceNum; index++)
        {
            var pointer = deviceList.pDeviceInfo[index];
            if (pointer == IntPtr.Zero)
            {
                continue;
            }

            var candidate = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(
                pointer,
                typeof(MyCamera.MV_CC_DEVICE_INFO));
            candidates.Add(new CameraCandidate(candidate, ReadSerialNumber(candidate)));
        }

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("海康 SDK 返回的相机列表中没有有效设备信息。");
        }

        // 多相机时不允许“第一台被占就悄悄换第二台”，否则 Blob 与标定矩阵会落到错误相机。
        // 可通过环境变量固定生产相机序列号；只有一台相机时无需额外配置。
        var configuredSerial = Environment
            .GetEnvironmentVariable("CONTROLHUB_HIK_CAMERA_SERIAL")?
            .Trim();
        CameraCandidate selected;
        if (!string.IsNullOrWhiteSpace(configuredSerial))
        {
            selected = candidates.FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.SerialNumber,
                        configuredSerial,
                        StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"未找到序列号为 {configuredSerial} 的海康相机；当前设备：" +
                    string.Join("、", candidates.Select(candidate => candidate.DisplayName)));
        }
        else if (candidates.Count == 1)
        {
            selected = candidates[0];
        }
        else
        {
            throw new InvalidOperationException(
                "检测到多台海康相机，无法安全判断哪一台对应当前标定。" +
                "请设置环境变量 CONTROLHUB_HIK_CAMERA_SERIAL；当前设备：" +
                string.Join("、", candidates.Select(candidate => candidate.DisplayName)));
        }

        var deviceInfo = selected.DeviceInfo;
        if (!MyCamera.MV_CC_IsDeviceAccessible_NET(
                ref deviceInfo,
                MyCamera.MV_ACCESS_Exclusive))
        {
            throw new InvalidOperationException(
                $"标定相机 {selected.DisplayName} 无法独占打开；" +
                "请确认已离开标定页，并关闭 VisionMaster 或其他相机软件。");
        }

        var camera = new MyCamera();
        var created = false;
        var opened = false;
        var grabbing = false;
        var frameAcquired = false;
        uint? originalAcquisitionMode = null;
        uint? originalTriggerMode = null;
        var frame = new MyCamera.MV_FRAME_OUT();
        string? imagePath = null;
        try
        {
            EnsureSuccess(camera.MV_CC_CreateDevice_NET(ref deviceInfo), "创建海康相机句柄");
            created = true;
            // 无参重载默认独占打开，同时兼容 GigE 与 USB 相机。
            EnsureSuccess(camera.MV_CC_OpenDevice_NET(), "独占打开海康相机");
            opened = true;

            var enumValue = new MyCamera.MVCC_ENUMVALUE();
            EnsureSuccess(
                camera.MV_CC_GetEnumValue_NET("AcquisitionMode", ref enumValue),
                "读取相机原采集模式");
            originalAcquisitionMode = enumValue.nCurValue;

            enumValue = new MyCamera.MVCC_ENUMVALUE();
            EnsureSuccess(
                camera.MV_CC_GetEnumValue_NET("TriggerMode", ref enumValue),
                "读取相机原触发模式");
            originalTriggerMode = enumValue.nCurValue;

            // 主页使用一次自由运行帧；无需依赖外部 IO 或相机原先的触发配置。
            EnsureSuccess(camera.MV_CC_SetEnumValue_NET("AcquisitionMode", 2), "设置连续采集模式");
            EnsureSuccess(camera.MV_CC_SetEnumValue_NET("TriggerMode", 0), "设置相机单拍模式");
            EnsureSuccess(camera.MV_CC_StartGrabbing_NET(), "启动相机取图");
            grabbing = true;
            EnsureSuccess(camera.MV_CC_GetImageBuffer_NET(ref frame, 8_000), "获取相机图像");
            frameAcquired = true;

            if (frame.pBufAddr == IntPtr.Zero ||
                frame.stFrameInfo.nWidth == 0 ||
                frame.stFrameInfo.nHeight == 0 ||
                frame.stFrameInfo.nFrameLen == 0)
            {
                throw new InvalidDataException("海康相机返回了空图像。");
            }

            var directory = Path.Combine(Path.GetTempPath(), "ControlHubVision");
            Directory.CreateDirectory(directory);
            imagePath = Path.Combine(directory, $"direct-{Guid.NewGuid():N}.bmp");
            var saveParameters = new MyCamera.MV_SAVE_IMG_TO_FILE_PARAM
            {
                enImageType = MyCamera.MV_SAVE_IAMGE_TYPE.MV_Image_Bmp,
                enPixelType = frame.stFrameInfo.enPixelType,
                pData = frame.pBufAddr,
                nDataLen = frame.stFrameInfo.nFrameLen,
                nWidth = frame.stFrameInfo.nWidth,
                nHeight = frame.stFrameInfo.nHeight,
                iMethodValue = 2,
                pImagePath = imagePath
            };
            EnsureSuccess(camera.MV_CC_SaveImageToFile_NET(ref saveParameters), "保存相机 BMP 原图");
            if (!File.Exists(imagePath))
            {
                throw new IOException("海康相机保存接口未生成 BMP 文件。");
            }

            return new HikCameraFrame(
                imagePath,
                frame.stFrameInfo.nWidth,
                frame.stFrameInfo.nHeight);
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(imagePath))
            {
                TryDelete(imagePath!);
            }

            throw;
        }
        finally
        {
            if (frameAcquired)
            {
                try
                {
                    _ = camera.MV_CC_FreeImageBuffer_NET(ref frame);
                }
                catch
                {
                }
            }

            if (grabbing)
            {
                try
                {
                    _ = camera.MV_CC_StopGrabbing_NET();
                }
                catch
                {
                }
            }

            if (opened)
            {
                try
                {
                    if (originalTriggerMode.HasValue)
                    {
                        _ = camera.MV_CC_SetEnumValue_NET(
                            "TriggerMode",
                            originalTriggerMode.Value);
                    }

                    if (originalAcquisitionMode.HasValue)
                    {
                        _ = camera.MV_CC_SetEnumValue_NET(
                            "AcquisitionMode",
                            originalAcquisitionMode.Value);
                    }
                }
                catch
                {
                }
            }

            if (opened)
            {
                try
                {
                    _ = camera.MV_CC_CloseDevice_NET();
                }
                catch
                {
                }
            }

            if (created)
            {
                try
                {
                    _ = camera.MV_CC_DestroyDevice_NET();
                }
                catch
                {
                }
            }
        }
    }

    private static void EnsureSuccess(int result, string operation)
    {
        if (result != MyCamera.MV_OK)
        {
            throw new InvalidOperationException(
                $"{operation}失败（MVS错误码 0x{unchecked((uint)result):X8}）。");
        }
    }

    private static void TryDelete(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch
        {
        }
    }

    private static string ReadSerialNumber(MyCamera.MV_CC_DEVICE_INFO deviceInfo)
    {
        try
        {
            if (deviceInfo.nTLayerType == MyCamera.MV_GIGE_DEVICE)
            {
                return BytesToStructure<MyCamera.MV_GIGE_DEVICE_INFO>(
                        deviceInfo.SpecialInfo.stGigEInfo)
                    .chSerialNumber?
                    .Trim() ?? "";
            }

            if (deviceInfo.nTLayerType == MyCamera.MV_USB_DEVICE)
            {
                return BytesToStructure<MyCamera.MV_USB3_DEVICE_INFO>(
                        deviceInfo.SpecialInfo.stUsb3VInfo)
                    .chSerialNumber?
                    .Trim() ?? "";
            }
        }
        catch
        {
            // 序列号解析失败时仍保留设备；单相机场景可继续使用，多相机则会要求显式配置。
        }

        return "";
    }

    private static T BytesToStructure<T>(byte[] bytes)
        where T : struct
    {
        if (bytes is null || bytes.Length < Marshal.SizeOf(typeof(T)))
        {
            throw new InvalidDataException("海康相机设备信息长度无效。");
        }

        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            return (T)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(T));
        }
        finally
        {
            handle.Free();
        }
    }

    private sealed class CameraCandidate
    {
        public CameraCandidate(
            MyCamera.MV_CC_DEVICE_INFO deviceInfo,
            string serialNumber)
        {
            DeviceInfo = deviceInfo;
            SerialNumber = serialNumber;
        }

        public MyCamera.MV_CC_DEVICE_INFO DeviceInfo { get; }

        public string SerialNumber { get; }

        public string DisplayName => string.IsNullOrWhiteSpace(SerialNumber)
            ? "序列号未知设备"
            : SerialNumber;
    }
}
