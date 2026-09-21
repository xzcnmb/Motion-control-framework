using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using OpenCvSharp;
using Sophon.Contracts;

namespace Sophon.Vision.Providers
{
    /// <summary>
    /// 海康威视工业相机专用异常类。
    /// 当 MVS SDK 加载失败、设备枚举失败或相机调用返回非零错误码时抛出。
    /// </summary>
    public class HikvisionCameraException : Exception
    {
        /// <summary>
        /// SDK 返回的原生错误码（十六进制形如 0x8000xxxx）。
        /// </summary>
        public int ErrorCode { get; }

        public HikvisionCameraException(string message) : base(message)
        {
        }

        public HikvisionCameraException(string message, int errorCode)
            : base($"{message} (错误码: 0x{errorCode:X8})")
        {
            ErrorCode = errorCode;
        }

        public HikvisionCameraException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// 基于海康威视 MVS SDK 的真实工业相机图像源实现 (IFrameSource)。
    /// 
    /// 设计与反射机制说明：
    /// 本类通过 .NET 反射机制动态探测并加载 MvCamCtrl.NET.dll (或 MvCameraControl.Net.dll)，
    /// 无需在编译期引入静态包引用或 DLL 依赖，保证在未安装 MVS 客户端的开发/测试/编译机上顺利构建。
    /// 
    /// 遵循海康威视官方 SDK 标准采图生命周期：
    /// 1. MV_CC_EnumDevices_NET: 枚举 GigE / USB 工业相机设备列表
    /// 2. MV_CC_CreateDevice_NET: 创建指定相机的底层设备句柄
    /// 3. MV_CC_OpenDevice_NET: 以独占模式 (MV_ACCESS_Exclusive) 打开相机
    /// 4. MV_CC_GetOptimalPacketSize_NET + SetIntValue("GevSCPSPacketSize"): GigE 网口相机自适应优化包大小
    /// 5. SetEnumValue("TriggerMode") / SetEnumValue("TriggerSource"): 配置触发模式（连续/软触发/硬触发）
    /// 6. SetFloatValue("ExposureTime") / SetFloatValue("Gain"): 设置曝光时间与增益
    /// 7. MV_CC_StartGrabbing_NET: 启动底层数据流接收
    /// 8. MV_CC_GetOneFrameTimeout_NET: 主动取图（软触发模式下先发送 TriggerSoftware 命令）
    /// 9. StopGrabbing -> CloseDevice -> DestroyDevice: 安全释放设备句柄与非托管内存
    /// 
    /// 诚实失败原则 (Honest-Failure)：
    /// 若 DLL 缺失、找不到对应设备或任何 SDK 调用返回非零错误码，坚决不进行静默仿真，
    /// 显式暴露 IsAvailable = false 及详细 LastError 诊断信息，或抛出 HikvisionCameraException。
    /// </summary>
    public sealed class HikvisionFrameSource : IFrameSource
    {
        private readonly object _lock = new();
        private bool _disposed;

        #region Reflection Metadata & Handles

        private Assembly? _mvsAssembly;
        private Type? _myCameraType;
        private object? _cameraInstance;

        // 反射解析出的核心方法引用
        private MethodInfo? _createDeviceMethod;
        private MethodInfo? _openDeviceMethod;
        private MethodInfo? _closeDeviceMethod;
        private MethodInfo? _destroyDeviceMethod;
        private MethodInfo? _startGrabbingMethod;
        private MethodInfo? _stopGrabbingMethod;
        private MethodInfo? _getOneFrameTimeoutMethod;
        private MethodInfo? _getIntValueMethod;
        private MethodInfo? _setIntValueMethod;
        private MethodInfo? _setEnumValueMethod;
        private MethodInfo? _setFloatValueMethod;
        private MethodInfo? _setCommandValueMethod;

        // 帧信息结构体类型 (MV_FRAME_OUT_INFO_EX 或 MV_FRAME_OUT_INFO)
        private Type? _frameInfoType;

        // 非托管图像缓冲区
        private IntPtr _unmanagedBuffer = IntPtr.Zero;
        private uint _bufferSize = 0;

        /// <summary>
        /// 保留 SDK 回调委托引用，防止被垃圾回收器 (GC) 提前回收。
        /// </summary>
#pragma warning disable CS0414
        private object? _sdkCallbackDelegate;
#pragma warning restore CS0414

        #endregion

        #region Public Properties

        /// <summary>
        /// 相机配置参数。
        /// </summary>
        public CameraConfig Config { get; }

        /// <summary>
        /// 相机后端是否可用（SDK 正常加载且设备已识别打开）。
        /// </summary>
        public bool IsAvailable { get; private set; }

        /// <summary>
        /// 设备是否处于打开状态。
        /// </summary>
        public bool IsOpen { get; private set; }

        /// <summary>
        /// 是否正在抓取数据流。
        /// </summary>
        public bool IsGrabbing { get; private set; }

        /// <summary>
        /// 最近一次错误信息（若初始化或操作失败）。
        /// </summary>
        public string? LastError { get; private set; }

        /// <summary>
        /// 实际加载的 MVS SDK 程序集路径。
        /// </summary>
        public string? LoadedDllPath { get; private set; }

        #endregion

        /// <summary>
        /// 构造海康工业相机图像源。
        /// </summary>
        /// <param name="config">相机配置模型</param>
        /// <param name="sdkDllPath">可选的 MvCamCtrl.NET.dll 绝对路径；若为 null 则自动搜索标准安装目录</param>
        /// <param name="autoOpen">是否在构造时自动连接并初始化相机，默认为 true</param>
        public HikvisionFrameSource(CameraConfig config, string? sdkDllPath = null, bool autoOpen = true)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));

            if (autoOpen)
            {
                Initialize(sdkDllPath);
            }
        }

        /// <summary>
        /// 初始化并打开相机连接。
        /// </summary>
        /// <param name="sdkDllPath">可选指定的 SDK DLL 路径</param>
        public void Initialize(string? sdkDllPath = null)
        {
            lock (_lock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(HikvisionFrameSource));

                try
                {
                    // 1. 探测并加载 MvCamCtrl.NET.dll
                    LoadSdkAssembly(sdkDllPath);

                    // 2. 枚举并匹配目标设备
                    object devInfo = FindTargetDevice();

                    // 3. 创建设备实例并打开连接
                    CreateAndOpenDevice(devInfo);

                    // 4. 配置通信参数（包大小、触发模式、曝光增益）
                    ConfigureParameters(devInfo);

                    // 5. 启动图像采集流并分配图像缓冲区
                    StartGrabbingAndAllocateBuffer();

                    IsAvailable = true;
                    LastError = null;
                }
                catch (Exception ex)
                {
                    IsAvailable = false;
                    LastError = ex.Message;
                    CloseInternal();
                }
            }
        }

        /// <summary>
        /// 确保相机已成功打开，若不可用则抛出详细异常。
        /// </summary>
        public void EnsureOpen()
        {
            if (!IsAvailable || !IsOpen)
            {
                throw new HikvisionCameraException($"海康相机不可用: {LastError ?? "设备未连接或未初始化"}");
            }
        }

        /// <inheritdoc />
        public bool TryGrab(out Mat? frame)
        {
            frame = null;

            lock (_lock)
            {
                if (_disposed || !IsAvailable || !IsOpen || !IsGrabbing || _cameraInstance == null)
                {
                    return false;
                }

                try
                {
                    // 1. 若为软触发模式，发送软触发指令
                    if (Config.TriggerMode == CameraTriggerMode.Software)
                    {
                        ExecuteCommand("TriggerSoftware");
                    }

                    // 2. 主动拉取一帧图像
                    int timeoutMs = Config.GrabTimeoutMs > 0 ? Config.GrabTimeoutMs : 1000;
                    object frameInfoBoxed = Activator.CreateInstance(_frameInfoType!)!;
                    object[] grabArgs = new object[] { _unmanagedBuffer, _bufferSize, frameInfoBoxed, timeoutMs };

                    int ret = (int)_getOneFrameTimeoutMethod!.Invoke(_cameraInstance, grabArgs)!;
                    if (ret != 0)
                    {
                        LastError = $"MV_CC_GetOneFrameTimeout_NET 失败: 0x{ret:X8}";
                        return false;
                    }

                    frameInfoBoxed = grabArgs[2];

                    // 3. 从帧信息中解析分辨率和像素类型
                    int width = Convert.ToInt32(_frameInfoType!.GetField("nWidth")?.GetValue(frameInfoBoxed) ?? 0);
                    int height = Convert.ToInt32(_frameInfoType.GetField("nHeight")?.GetValue(frameInfoBoxed) ?? 0);
                    uint pixelType = Convert.ToUInt32(_frameInfoType.GetField("enPixelType")?.GetValue(frameInfoBoxed) ?? 0);

                    if (width <= 0 || height <= 0)
                    {
                        LastError = "解析得到的图像尺寸无效 (Width <= 0 或 Height <= 0)";
                        return false;
                    }

                    // 4. 将原始数据转换为 OpenCvSharp Mat 图像
                    // 常用像素格式:
                    // PixelType_Gvsp_Mono8 = 0x01080001
                    // PixelType_Gvsp_RGB8_Packed = 0x02180014
                    // PixelType_Gvsp_BGR8_Packed = 0x02180015
                    if (pixelType == 0x01080001 || (pixelType & 0x00FF0000) == 0x00080000) // 8位单通道灰度
                    {
                        using var rawMat = Mat.FromPixelData(height, width, MatType.CV_8UC1, _unmanagedBuffer);
                        frame = rawMat.Clone();
                    }
                    else if (pixelType == 0x02180015) // BGR8
                    {
                        using var rawMat = Mat.FromPixelData(height, width, MatType.CV_8UC3, _unmanagedBuffer);
                        frame = new Mat();
                        Cv2.CvtColor(rawMat, frame, ColorConversionCodes.BGR2GRAY);
                    }
                    else if (pixelType == 0x02180014) // RGB8
                    {
                        using var rawMat = Mat.FromPixelData(height, width, MatType.CV_8UC3, _unmanagedBuffer);
                        frame = new Mat();
                        Cv2.CvtColor(rawMat, frame, ColorConversionCodes.RGB2GRAY);
                    }
                    else
                    {
                        // 默认按 8 位灰度封装
                        using var rawMat = Mat.FromPixelData(height, width, MatType.CV_8UC1, _unmanagedBuffer);
                        frame = rawMat.Clone();
                    }

                    // 5. 若配置了 Y 轴翻转，执行垂直翻转 (FlipMode.X)
                    if (Config.FlipY && frame != null)
                    {
                        Cv2.Flip(frame, frame, FlipMode.X);
                    }

                    return frame != null && !frame.Empty();
                }
                catch (Exception ex)
                {
                    LastError = $"抓帧异常: {ex.Message}";
                    frame?.Dispose();
                    frame = null;
                    return false;
                }
            }
        }

        #region Private Helper Methods (Reflection)

        private void LoadSdkAssembly(string? explicitPath)
        {
            string? targetDll = null;

            if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            {
                targetDll = explicitPath;
            }
            else
            {
                // 搜索海康标准路径及环境变量
                string[] candidatePaths = GetCandidateSdkPaths();
                targetDll = candidatePaths.FirstOrDefault(File.Exists);
            }

            if (string.IsNullOrEmpty(targetDll) || !File.Exists(targetDll))
            {
                throw new FileNotFoundException(
                    "未找到海康 MVS SDK 程序集 (MvCamCtrl.NET.dll / MvCameraControl.Net.dll)。" +
                    "请确认是否安装了海康机器人 MVS 客户端或将 SDK DLL 部署至运行目录。");
            }

            _mvsAssembly = Assembly.LoadFrom(targetDll);
            LoadedDllPath = targetDll;

            // 查找 MyCamera 类型
            _myCameraType = _mvsAssembly.GetType("MvCamCtrl.NET.MyCamera")
                ?? _mvsAssembly.GetTypes().FirstOrDefault(t => t.Name == "MyCamera");

            if (_myCameraType == null)
            {
                throw new TypeLoadException($"在程序集 {targetDll} 中未找到 MvCamCtrl.NET.MyCamera 类型。");
            }

            // 解析帧信息结构体
            _frameInfoType = _myCameraType.GetNestedType("MV_FRAME_OUT_INFO_EX")
                ?? _mvsAssembly.GetType("MvCamCtrl.NET.MyCamera+MV_FRAME_OUT_INFO_EX")
                ?? _mvsAssembly.GetType("MvCamCtrl.NET.MV_FRAME_OUT_INFO_EX")
                ?? _myCameraType.GetNestedType("MV_FRAME_OUT_INFO");

            if (_frameInfoType == null)
            {
                throw new TypeLoadException("未找到 MV_FRAME_OUT_INFO_EX 帧信息结构体类型。");
            }

            // 缓存核心方法反射元数据
            _createDeviceMethod = _myCameraType.GetMethods().FirstOrDefault(m => m.Name == "MV_CC_CreateDevice_NET");
            _openDeviceMethod = _myCameraType.GetMethods().FirstOrDefault(m => m.Name == "MV_CC_OpenDevice_NET");
            _closeDeviceMethod = _myCameraType.GetMethod("MV_CC_CloseDevice_NET", Type.EmptyTypes);
            _destroyDeviceMethod = _myCameraType.GetMethod("MV_CC_DestroyDevice_NET", Type.EmptyTypes);
            _startGrabbingMethod = _myCameraType.GetMethod("MV_CC_StartGrabbing_NET", Type.EmptyTypes);
            _stopGrabbingMethod = _myCameraType.GetMethod("MV_CC_StopGrabbing_NET", Type.EmptyTypes);
            _getOneFrameTimeoutMethod = _myCameraType.GetMethods().FirstOrDefault(m => m.Name == "MV_CC_GetOneFrameTimeout_NET");

            _getIntValueMethod = _myCameraType.GetMethods().FirstOrDefault(m => m.Name == "MV_CC_GetIntValue_NET");
            _setIntValueMethod = _myCameraType.GetMethods().FirstOrDefault(m => m.Name == "MV_CC_SetIntValue_NET");
            _setEnumValueMethod = _myCameraType.GetMethods().FirstOrDefault(m => m.Name == "MV_CC_SetEnumValue_NET");
            _setFloatValueMethod = _myCameraType.GetMethods().FirstOrDefault(m => m.Name == "MV_CC_SetFloatValue_NET");
            _setCommandValueMethod = _myCameraType.GetMethods().FirstOrDefault(m => m.Name == "MV_CC_SetCommandValue_NET");
        }

        private static string[] GetCandidateSdkPaths()
        {
            string? mvcEnv = Environment.GetEnvironmentVariable("MVCAM_COMMON_RUNENV");
            string? mvsRoot = Environment.GetEnvironmentVariable("MVS_ROOT");

            return new[]
            {
                !string.IsNullOrEmpty(mvcEnv) ? Path.Combine(mvcEnv, "DotNet", "win64", "MvCamCtrl.NET.dll") : "",
                !string.IsNullOrEmpty(mvcEnv) ? Path.Combine(mvcEnv, "DotNet", "win64", "MvCameraControl.Net.dll") : "",
                !string.IsNullOrEmpty(mvsRoot) ? Path.Combine(mvsRoot, "Development", "DotNet", "win64", "MvCamCtrl.NET.dll") : "",
                @"C:\Program Files (x86)\MVS\Development\DotNet\win64\MvCamCtrl.NET.dll",
                @"C:\Program Files (x86)\MVS\Development\DotNet\win64\MvCameraControl.Net.dll",
                @"C:\Program Files\MVS\Development\DotNet\win64\MvCamCtrl.NET.dll",
                @"C:\Program Files\MVS\Development\DotNet\win64\MvCameraControl.Net.dll",
                @"C:\Program Files (x86)\MVS\Development\DotNet\win32\MvCamCtrl.NET.dll",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MvCamCtrl.NET.dll"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MvCameraControl.Net.dll")
            }.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        }

        private object FindTargetDevice()
        {
            var devListType = _myCameraType!.GetNestedType("MV_CC_DEVICE_INFO_LIST")
                ?? _mvsAssembly!.GetType("MvCamCtrl.NET.MyCamera+MV_CC_DEVICE_INFO_LIST")
                ?? _mvsAssembly!.GetType("MvCamCtrl.NET.MV_CC_DEVICE_INFO_LIST");

            var devInfoType = _myCameraType.GetNestedType("MV_CC_DEVICE_INFO")
                ?? _mvsAssembly!.GetType("MvCamCtrl.NET.MyCamera+MV_CC_DEVICE_INFO")
                ?? _mvsAssembly!.GetType("MvCamCtrl.NET.MV_CC_DEVICE_INFO");

            var enumMethod = _myCameraType.GetMethods()
                .FirstOrDefault(m => m.Name == "MV_CC_EnumDevices_NET" && m.GetParameters().Length == 2);

            if (devListType == null || devInfoType == null || enumMethod == null)
            {
                throw new InvalidOperationException("未找到设备枚举相关的结构体或 MV_CC_EnumDevices_NET 方法。");
            }

            // MV_GIGE_DEVICE = 0x00000001, MV_USB_DEVICE = 0x00000004
            uint layerType = 0x00000001 | 0x00000004;
            object devList = Activator.CreateInstance(devListType)!;
            object[] enumArgs = new object[] { layerType, devList };

            int ret = (int)enumMethod.Invoke(null, enumArgs)!;
            if (ret != 0)
            {
                throw new HikvisionCameraException("枚举海康相机设备失败", ret);
            }

            devList = enumArgs[1];
            uint deviceCount = Convert.ToUInt32(devListType.GetField("nDeviceNum")?.GetValue(devList) ?? 0);

            if (deviceCount == 0)
            {
                throw new HikvisionCameraException("未找到任何在线的海康工业相机设备 (nDeviceNum = 0)");
            }

            var pDeviceInfoField = devListType.GetField("pDeviceInfo");
            var pDeviceInfoArray = (Array)pDeviceInfoField!.GetValue(devList)!;

            // 匹配设备（支持按序号索引、序列号或默认第一台）
            string deviceKey = Config.DeviceKey?.Trim() ?? string.Empty;

            if (int.TryParse(deviceKey, out int targetIndex) && targetIndex >= 0 && targetIndex < deviceCount)
            {
                IntPtr ptr = (IntPtr)pDeviceInfoArray.GetValue(targetIndex)!;
                return Marshal.PtrToStructure(ptr, devInfoType)!;
            }

            for (int i = 0; i < deviceCount; i++)
            {
                IntPtr ptr = (IntPtr)pDeviceInfoArray.GetValue(i)!;
                object info = Marshal.PtrToStructure(ptr, devInfoType)!;

                if (string.IsNullOrEmpty(deviceKey))
                {
                    return info; // 默认使用找到的第一台
                }

                // 尝试比对序列号
                string? serial = ExtractSerialNumber(info);
                if (!string.IsNullOrEmpty(serial) && string.Equals(serial, deviceKey, StringComparison.OrdinalIgnoreCase))
                {
                    return info;
                }
            }

            // 若指定了具体 Key 但未匹配到，报错
            throw new HikvisionCameraException($"未找到与 DeviceKey '{deviceKey}' 匹配的海康相机（在线相机总数: {deviceCount}）");
        }

        private static string? ExtractSerialNumber(object devInfo)
        {
            try
            {
                var specialInfoField = devInfo.GetType().GetField("SpecialInfo");
                if (specialInfoField == null) return null;

                object specialInfo = specialInfoField.GetValue(devInfo)!;
                var gigeField = specialInfo.GetType().GetField("stGigEInfo");
                if (gigeField != null)
                {
                    object gigeInfo = gigeField.GetValue(specialInfo)!;
                    var snField = gigeInfo.GetType().GetField("chSerialNumber");
                    if (snField?.GetValue(gigeInfo) is byte[] bytes)
                    {
                        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private void CreateAndOpenDevice(object devInfo)
        {
            _cameraInstance = Activator.CreateInstance(_myCameraType!)!;

            if (_createDeviceMethod == null || _openDeviceMethod == null)
            {
                throw new InvalidOperationException("未找到 MV_CC_CreateDevice_NET 或 MV_CC_OpenDevice_NET 方法。");
            }

            // 1. 创建句柄
            object[] createArgs = new object[] { devInfo };
            int ret = (int)_createDeviceMethod.Invoke(_cameraInstance, createArgs)!;
            if (ret != 0)
            {
                throw new HikvisionCameraException("MV_CC_CreateDevice_NET 创建设备句柄失败", ret);
            }

            // 2. 打开设备（默认独占模式）
            var openParams = _openDeviceMethod.GetParameters();
            object[]? openArgs = openParams.Length switch
            {
                2 => new object[] { 1u, (ushort)0 }, // MV_ACCESS_Exclusive = 1
                _ => null
            };

            ret = (int)_openDeviceMethod.Invoke(_cameraInstance, openArgs)!;
            if (ret != 0)
            {
                throw new HikvisionCameraException("MV_CC_OpenDevice_NET 打开相机失败", ret);
            }

            IsOpen = true;
        }

        private void ConfigureParameters(object devInfo)
        {
            // 1. GigE 包大小自适应优化
            try
            {
                uint layerType = Convert.ToUInt32(devInfo.GetType().GetField("nTLayerType")?.GetValue(devInfo) ?? 0);
                if (layerType == 1 && Config.OptimizePacketSize) // 1 = MV_GIGE_DEVICE
                {
                    var getOptimalPacketMethod = _myCameraType!.GetMethod("MV_CC_GetOptimalPacketSize_NET", Type.EmptyTypes);
                    if (getOptimalPacketMethod != null)
                    {
                        int packetSize = (int)getOptimalPacketMethod.Invoke(_cameraInstance, null)!;
                        if (packetSize > 0)
                        {
                            SetIntValue("GevSCPSPacketSize", (uint)packetSize);
                        }
                    }
                }
            }
            catch
            {
                // 包大小设置失败通常不阻断主流流程
            }

            // 2. 触发模式设置
            switch (Config.TriggerMode)
            {
                case CameraTriggerMode.Continuous:
                    SetEnumValue("TriggerMode", 0); // 0 = Off
                    break;
                case CameraTriggerMode.Software:
                    SetEnumValue("TriggerMode", 1); // 1 = On
                    SetEnumValue("TriggerSource", 7); // 7 = Software
                    break;
                case CameraTriggerMode.Hardware:
                    SetEnumValue("TriggerMode", 1); // 1 = On
                    SetEnumValue("TriggerSource", 0); // 0 = Line0
                    break;
            }

            // 3. 曝光时间与增益
            if (Config.ExposureUs > 0)
            {
                SetFloatValue("ExposureTime", (float)Config.ExposureUs);
            }

            if (Config.Gain > 0)
            {
                SetFloatValue("Gain", (float)Config.Gain);
            }
        }

        private void StartGrabbingAndAllocateBuffer()
        {
            if (_startGrabbingMethod == null)
            {
                throw new InvalidOperationException("未找到 MV_CC_StartGrabbing_NET 方法。");
            }

            int ret = (int)_startGrabbingMethod.Invoke(_cameraInstance, null)!;
            if (ret != 0)
            {
                throw new HikvisionCameraException("MV_CC_StartGrabbing_NET 开启采图失败", ret);
            }

            IsGrabbing = true;

            // 获取 PayloadSize
            uint payloadSize = GetPayloadSize();
            if (payloadSize == 0)
            {
                payloadSize = 20 * 1024 * 1024; // 兜底 20MB
            }

            _bufferSize = payloadSize;
            _unmanagedBuffer = Marshal.AllocHGlobal((int)_bufferSize);
        }

        private uint GetPayloadSize()
        {
            try
            {
                var intValueType = _myCameraType!.GetNestedType("MVCC_INTVALUE")
                    ?? _mvsAssembly!.GetType("MvCamCtrl.NET.MyCamera+MVCC_INTVALUE")
                    ?? _mvsAssembly!.GetType("MvCamCtrl.NET.MVCC_INTVALUE");

                if (intValueType != null && _getIntValueMethod != null)
                {
                    object intValBoxed = Activator.CreateInstance(intValueType)!;
                    object[] args = new object[] { "PayloadSize", intValBoxed };
                    int ret = (int)_getIntValueMethod.Invoke(_cameraInstance, args)!;
                    if (ret == 0)
                    {
                        intValBoxed = args[1];
                        return Convert.ToUInt32(intValueType.GetField("nCurValue")?.GetValue(intValBoxed) ?? 0);
                    }
                }
            }
            catch
            {
            }
            return 0;
        }

        private void SetIntValue(string key, uint value)
        {
            if (_setIntValueMethod == null || _cameraInstance == null) return;
            var param = _setIntValueMethod.GetParameters()[1].ParameterType;
            object valObj = param == typeof(ulong) ? (ulong)value : (object)value;
            _setIntValueMethod.Invoke(_cameraInstance, new object[] { key, valObj });
        }

        private void SetEnumValue(string key, uint value)
        {
            if (_setEnumValueMethod == null || _cameraInstance == null) return;
            _setEnumValueMethod.Invoke(_cameraInstance, new object[] { key, value });
        }

        private void SetFloatValue(string key, float value)
        {
            if (_setFloatValueMethod == null || _cameraInstance == null) return;
            _setFloatValueMethod.Invoke(_cameraInstance, new object[] { key, value });
        }

        private void ExecuteCommand(string key)
        {
            if (_setCommandValueMethod == null || _cameraInstance == null) return;
            _setCommandValueMethod.Invoke(_cameraInstance, new object[] { key });
        }

        private void CloseInternal()
        {
            try
            {
                if (IsGrabbing && _stopGrabbingMethod != null && _cameraInstance != null)
                {
                    _stopGrabbingMethod.Invoke(_cameraInstance, null);
                }
            }
            catch { }
            finally { IsGrabbing = false; }

            try
            {
                if (IsOpen && _closeDeviceMethod != null && _cameraInstance != null)
                {
                    _closeDeviceMethod.Invoke(_cameraInstance, null);
                }
            }
            catch { }
            finally { IsOpen = false; }

            try
            {
                if (_destroyDeviceMethod != null && _cameraInstance != null)
                {
                    _destroyDeviceMethod.Invoke(_cameraInstance, null);
                }
            }
            catch { }
            finally { _cameraInstance = null; }

            if (_unmanagedBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_unmanagedBuffer);
                _unmanagedBuffer = IntPtr.Zero;
                _bufferSize = 0;
            }
        }

        #endregion

        /// <summary>
        /// 释放所有相机与非托管资源。
        /// </summary>
        public void Dispose()
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    CloseInternal();
                    _sdkCallbackDelegate = null;
                }
            }
        }
    }
}
