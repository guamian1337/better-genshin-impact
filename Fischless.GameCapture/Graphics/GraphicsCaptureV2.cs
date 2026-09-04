using System.Collections.Concurrent;
using System.Diagnostics;
using Fischless.GameCapture.Graphics.Helpers;
using SharpDX.Direct3D11;
using Vanara.PInvoke;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using OpenCvSharp;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.DXGI;

namespace Fischless.GameCapture.Graphics;

public class GraphicsCaptureV2(bool captureHdr = false) : IGameCapture
{
    // BGR Mat 池：有界 ConcurrentQueue，FIFO 回收
    private const int MaxPoolSize = 16;
    private readonly ConcurrentQueue<Mat> _bgrQueue = new();
    private bool _bgrPoolClosed = true;
    private readonly HashSet<Mat> _bgrBorrowed = new();

    private nint _hWnd;

    private Direct3D11CaptureFramePool? _captureFramePool;
    private GraphicsCaptureItem? _captureItem;
    private GraphicsCaptureSession? _captureSession;
    private IDirect3DDevice? _d3dDevice;

    public bool IsCapturing { get; private set; }

    private ResourceRegion? _region;
    private RECT? _captureRect;

    // HDR 相关
    private bool _isHdrEnabled = captureHdr;
    private DirectXPixelFormat _pixelFormat;
    private Texture2D? _hdrOutputTexture;
    private ComputeShader? _hdrComputeShader;
    private UnorderedAccessView? _hdrOutputUav;

    private readonly object _lock = new();

    // 单 GPU 广播源：FrameArrived 每帧一次 GPU->GPU 拷贝（latest-wins），消费侧 Capture() 从它 stage 读回
    private Texture2D? _gpuTexture;
    private volatile bool _frameReady;

    // 单一 staging：消费时从 _gpuTexture 提交 GPU 拷贝并同 tick 阻塞 Map（等待通常亚毫秒级，无流水滞后）。
    // 消费被 _captureGate 串行化，单块 staging 即安全
    private Texture2D? _stagingTexture;

    // 单飞闸门：并发消费方逐个进入；FrameArrived 只用 _lock，不受此闸门影响
    private readonly object _captureGate = new();

    // 在途捕获数（正持锁外 Map/CvtColor）。Stop 与尺寸 Recreate 销毁 staging 前必须等其归零；仅锁内访问
    private int _activeCaptures;

    // 无新帧跳过读回：识别节拍与帧到达率解耦——窗口静止时 FrameArrived 不触发，但识别 tick 仍在跑，
    // 若无缓存，每个 tick 都会对同一块旧纹理重复 stage 拷贝 + Map 回读 + CvtColor（三重空转）。
    // 策略：仅在真的出现重复 tick 时才建/用缓存，持续更新的场景（游戏）零额外开销。
    // 三个时间戳/缓存仅在 _captureGate（单飞）内写；读侧 _lastArrivedFrameTs 在 _lock 内写
    private Mat? _cachedBgr;
    private long _lastArrivedFrameTs;    // 最近到达帧的 SystemRelativeTime（_lock 内写）
    private long _lastConsumedFrameTs;   // 最近一次成功消费帧的时间戳
    private long _cachedFrameTs;         // 缓存内容对应的帧时间戳

    // Surface 大小
    private int _surfaceWidth;
    private int _surfaceHeight;

    // WinEventHook：尺寸/位置变化时才查询 get_Size
    private User32.HWINEVENTHOOK _winEventHookMoveSize;
    private User32.HWINEVENTHOOK _winEventHookLocation;
    private User32.WinEventProc _winEventProc = null!;
    private const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
    private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint WINEVENT_SKIPOWNTHREAD = 0x0001;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private volatile bool _sizeDirty = true;

    // 驱动尺寸变化 3s fallback 检查
    private readonly Stopwatch _frameTimer = new();

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    public void Start(nint hWnd, Dictionary<string, object>? settings = null)
    {
        // 全程持锁，防并发双 Start 泄漏 framepool/session/hook（Monitor 可重入，内部 Stop 安全）
        lock (_lock)
        {
            StartCore(hWnd, settings);
        }
    }

    private void StartCore(nint hWnd, Dictionary<string, object>? settings = null)
    {
        Stop();
        try
        {
            _hWnd = hWnd;

            (_region, _captureRect) = GetGameScreenInfo(hWnd);

            IsCapturing = true;

            try
            {
                _captureItem = CaptureHelper.CreateItemForWindow(_hWnd);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    $"创建 WGC 捕获器失败，hWnd=0x{_hWnd.ToInt64():X8}，可能原因：窗口句柄失效、游戏窗口被最小化/未启动、或被其他应用/系统不支持图形捕获", e);
            }

            if (_captureItem == null)
            {
                throw new InvalidOperationException("Failed to create capture item.");
            }

            _surfaceWidth = _captureItem.Size.Width;
            _surfaceHeight = _captureItem.Size.Height;

            _d3dDevice = Direct3D11Helper.CreateDevice();

            // CreateFreeThreaded 契约：FrameArrived/Closed 派发于 WGC 内部队列线程，
            // 不依赖启动线程的 DispatcherQueue/消息泵（热键线程、脚本后台链启动均可正常收帧）。
            // 帧池访问(TryGetNextFrame/Recreate/Dispose)与 GPU 资源操作必须在 _lock 内串行；
            // WinEventHook 仍在启动线程注册，仅对 _sizeDirty 做锁外原子置位。
            try
            {
                if (!_isHdrEnabled)
                {
                    throw new Exception();
                }

                _pixelFormat = DirectXPixelFormat.R16G16B16A16Float;
                _captureFramePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _d3dDevice,
                    _pixelFormat,
                    2,
                    _captureItem.Size);
            }
            catch (Exception)
            {
                _pixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
                _captureFramePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _d3dDevice,
                    _pixelFormat,
                    2,
                    _captureItem.Size);
                _isHdrEnabled = false;
            }

            _captureItem.Closed += CaptureItemOnClosed;
            _captureFramePool.FrameArrived += OnFrameArrived;

            _winEventProc = WinEventProc;
            var winEventFlags = (User32.WINEVENT)(WINEVENT_SKIPOWNPROCESS | WINEVENT_SKIPOWNTHREAD);
            _winEventHookMoveSize = User32.SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND, default, _winEventProc, 0, 0, winEventFlags);
            _winEventHookLocation = User32.SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, default, _winEventProc, 0, 0, winEventFlags);
            _sizeDirty = true;

            _captureSession = _captureFramePool.CreateCaptureSession(_captureItem);
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession",
                    nameof(GraphicsCaptureSession.IsCursorCaptureEnabled)))
            {
                _captureSession.IsCursorCaptureEnabled = false;
            }

            if (ApiInformation.IsWriteablePropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession",
                    nameof(GraphicsCaptureSession.IsBorderRequired)))
            {
                _captureSession.IsBorderRequired = false;
            }

            _frameTimer.Start();
            _captureSession.StartCapture();
            IsCapturing = true;
            lock (_lock)
            {
                _bgrPoolClosed = false;
            }
        }
        catch
        {
            Stop();
            throw;
        }
    }

    private static (ResourceRegion? Region, RECT? CaptureRect) GetGameScreenInfo(nint hWnd)
    {
        var exStyle = User32.GetWindowLong(hWnd, User32.WindowLongFlags.GWL_EXSTYLE);
        if ((exStyle & (int)User32.WindowStylesEx.WS_EX_TOPMOST) != 0)
        {
            return (null, null);
        }

        ResourceRegion region = new();
        DwmApi.DwmGetWindowAttribute<RECT>(hWnd, DwmApi.DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
            out var windowRect);
        User32.GetClientRect(hWnd, out var clientRect);
        POINT point = default;
        User32.ClientToScreen(hWnd, ref point);

        region.Left = point.X > windowRect.Left ? point.X - windowRect.Left : 0;
        region.Top = point.Y > windowRect.Top ? point.Y - windowRect.Top : 0;
        region.Right = region.Left + clientRect.Width;
        region.Bottom = region.Top + clientRect.Height;
        region.Front = 0;
        region.Back = 1;

        var left = windowRect.Left;
        var top = windowRect.Top + windowRect.Height - clientRect.Height;
        var right = left + clientRect.Width;
        var bottom = top + clientRect.Height;

        return (region, new RECT(left, top, right, bottom));
    }

    private Texture2D ProcessHdrTexture(Texture2D hdrTexture)
    {
        var device = hdrTexture.Device;
        var context = device.ImmediateContext;

        var width = hdrTexture.Description.Width;
        var height = hdrTexture.Description.Height;

        // V2 消费侧需要从输出纹理读原始字节打包：UNORM 资源 + 同格式显式视图（浮点往返精确还原字节）
        _hdrOutputTexture ??= Direct3D11Helper.CreateOutputTexture(device, width, height);
        _hdrOutputUav ??= new UnorderedAccessView(device, _hdrOutputTexture);
        _hdrComputeShader ??= new ComputeShader(device, ShaderBytecode.Compile(HdrToSdrShader.Content, "CS_HDRtoSDR", "cs_5_0"));

        using var inputSrv = new ShaderResourceView(device, hdrTexture);

        context.ComputeShader.Set(_hdrComputeShader);
        context.ComputeShader.SetShaderResource(0, inputSrv);
        context.ComputeShader.SetUnorderedAccessView(0, _hdrOutputUav);

        var threadGroupCountX = (int)Math.Ceiling(width / 16.0);
        var threadGroupCountY = (int)Math.Ceiling(height / 16.0);

        context.Dispatch(threadGroupCountX, threadGroupCountY, 1);

        // 解绑，避免后续 CopyResource 触发 read/write hazard
        context.ComputeShader.SetUnorderedAccessView(0, null);
        context.ComputeShader.SetShaderResource(0, null);
        context.ComputeShader.Set(null);

        return _hdrOutputTexture;
    }

    private void EnsureGpuTexture(SharpDX.Direct3D11.Device device, int width, int height, ResourceRegion? region)
    {
        var w = region == null ? width : region.Value.Right - region.Value.Left;
        var h = region == null ? height : region.Value.Bottom - region.Value.Top;

        if (_gpuTexture == null ||
            _gpuTexture.Description.Width != w ||
            _gpuTexture.Description.Height != h)
        {
            _gpuTexture?.Dispose();
            _gpuTexture = new Texture2D(device, new Texture2DDescription
            {
                Width = w,
                Height = h,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                CpuAccessFlags = CpuAccessFlags.None,
                BindFlags = BindFlags.None,
                OptionFlags = ResourceOptionFlags.None,
            });
        }
    }

    private void EnsureStagingTextureLocked(SharpDX.Direct3D11.Device device, int width, int height)
    {
        if (_stagingTexture == null || _stagingTexture.Description.Width != width ||
            _stagingTexture.Description.Height != height)
        {
            _stagingTexture?.Dispose();
            _stagingTexture = Direct3D11Helper.CreateStagingTexture(device, width, height, null);
        }
    }

    private static void HandleSharpDxError(SharpDXException e)
    {
        Debug.WriteLine($"SharpDXException: {e.Descriptor}");
        if (e.ResultCode == SharpDX.DXGI.ResultCode.DeviceRemoved || e.ResultCode == SharpDX.DXGI.ResultCode.DeviceReset)
        {
            Debug.WriteLine($"[WGC] D3D 设备丢失 ({e.ResultCode})，后续捕获可能失效，等待上层重建");
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        // 兜底：任何异常不得穿透 WinRT 回调线程（否则可能直接崩溃进程）
        try
        {
            var now = _frameTimer.ElapsedMilliseconds;

            // FreeThreaded 派发线程与消费/Stop 线程并发，全部串行在单锁内
            lock (_lock)
            {
                // Stop() 迟到回调防护：会话已结束则放弃本帧（防 NRE / 幽灵纹理复活）
                if (!IsCapturing || _captureItem == null || _captureFramePool == null || _d3dDevice == null) return;
                if (_hWnd == 0) return;

                using var frame = sender.TryGetNextFrame();
                if (frame == null) return;

                _lastArrivedFrameTs = frame.SystemRelativeTime.Ticks;

                // 尺寸脏标志清零与 fallback 节流统一在锁内；
                // 置位方 WinEventProc(UI hook 线程) 为锁外原子写 true
                if (_sizeDirty || now - _lastSizeFallbackCheckMs >= SizeFallbackCheckMs)
                {
                    var regionDirty = _sizeDirty;
                    _lastSizeFallbackCheckMs = now;
                    _sizeDirty = false;

                    var captureSize = _captureItem.Size;
                    if (captureSize.Width != _surfaceWidth || captureSize.Height != _surfaceHeight)
                    {
                        if (User32.IsIconic(_hWnd)) return;
                        _captureFramePool.Recreate(_d3dDevice, _pixelFormat, 2, captureSize);
                        _surfaceWidth = captureSize.Width;
                        _surfaceHeight = captureSize.Height;
                        (_region, _captureRect) = GetGameScreenInfo(_hWnd);
                        var newW = _region != null ? _region.Value.Right - _region.Value.Left : captureSize.Width;
                        var newH = _region != null ? _region.Value.Bottom - _region.Value.Top : captureSize.Height;
                        TrimBgrPoolForSizeLocked(newH, newW);

                        // 先挡住新的 Capture()，再等在途捕获退出（其可能正在锁外 Map staging/读缓存），
                        // 之后才允许销毁资源，否则会踩到已释放纹理
                        _frameReady = false;
                        while (_activeCaptures > 0)
                        {
                            Monitor.Wait(_lock);
                        }

                        _gpuTexture?.Dispose();
                        _gpuTexture = null;
                        _stagingTexture?.Dispose();
                        _stagingTexture = null;
                        _cachedBgr?.Dispose();
                        _cachedBgr = null;
                        _cachedFrameTs = 0;
                        _lastConsumedFrameTs = 0;
                        _lastArrivedFrameTs = 0;
                        _hdrOutputTexture?.Dispose();
                        _hdrOutputTexture = null;
                        _hdrOutputUav?.Dispose();
                        _hdrOutputUav = null;
                        return;
                    }

                    // 纯移动窗口不改尺寸时也需刷新裁剪区，否则 region 错位
                    if (regionDirty)
                    {
                        (_region, _captureRect) = GetGameScreenInfo(_hWnd);
                    }
                }

                // 每帧一次 GPU->GPU 拷贝（latest-wins），消费侧 Capture() 从 _gpuTexture stage 读回
                using var surfaceTexture = Direct3D11Helper.CreateSharpDXTexture2D(frame.Surface);
                var sourceTexture = _isHdrEnabled ? ProcessHdrTexture(surfaceTexture) : surfaceTexture;
                var d3dDevice = sourceTexture.Device;
                EnsureGpuTexture(d3dDevice, frame.ContentSize.Width, frame.ContentSize.Height, _region);
                var context = d3dDevice.ImmediateContext;
                if (_region != null)
                {
                    context.CopySubresourceRegion(sourceTexture, 0, _region, _gpuTexture, 0);
                }
                else
                {
                    context.CopyResource(sourceTexture, _gpuTexture);
                }
                _frameReady = true;
            }
        }
        catch (SharpDXException e)
        {
            HandleSharpDxError(e);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[WGC V2] FrameArrived 异常: {e.Message}");
        }
    }

    public GameCaptureFrame? Capture()
    {
        if (!_frameReady) return null;

        // 单飞：等价 OBS 的单视频线程；FrameArrived 只用 _lock，不受此闸门影响
        lock (_captureGate)
        {
            // 阶段1（持 _lock）：状态校验、无新帧判定、从持帧表面提交拷贝到 staging（同 tick）。
            // 临界区仅微秒级命令提交，FrameArrived 不会被本线程后续的 Map/CvtColor 阻塞
            SharpDX.Direct3D11.Device? d3dDevice = null;
            RECT? rect;
            long frameTs;
            var isDuplicate = false;   // 无新帧：最新内容与上一 tick 相同
            var cacheHit = false;      // 且缓存可用：可完全跳过 GPU 拷贝、PCIe 回读与 CvtColor
            lock (_lock)
            {
                if (!IsCapturing || _gpuTexture == null) return null;

                rect = _captureRect;
                frameTs = _lastArrivedFrameTs;

                isDuplicate = frameTs != 0 && frameTs == _lastConsumedFrameTs;
                cacheHit = isDuplicate && frameTs == _cachedFrameTs && _cachedBgr != null;
                if (!cacheHit)
                {
                    d3dDevice = _gpuTexture.Device;

                    // _gpuTexture 在到达侧已按客户区裁剪，直接整块拷入 staging
                    EnsureStagingTextureLocked(d3dDevice, _gpuTexture.Description.Width, _gpuTexture.Description.Height);
                    var context = d3dDevice.ImmediateContext;
                    context.CopyResource(_gpuTexture, _stagingTexture);
                }

                // 两条路径都计为在途：Stop/Recreate 销毁 staging/_cachedBgr 前必须等其归零
                _activeCaptures++;
            }

            Mat? target = null;
            try
            {
                if (cacheHit)
                {
                    // 重复帧且缓存可用：零 GPU 拷贝、零 PCIe 回读、零 CvtColor，克隆缓存给消费者私有副本
                    target = AcquireBgrMat(_cachedBgr!.Rows, _cachedBgr.Cols);
                    _cachedBgr.CopyTo(target);
                }
                else
                {
                    // 阶段2（锁外）：Map 本次提交的 staging（同 tick，等待 GPU 拷贝通常亚毫秒级）
                    var context = d3dDevice!.ImmediateContext;
                    var stagingDesc = _stagingTexture!.Description;
                    var dataBox = context.MapSubresource(_stagingTexture, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                    try
                    {
                        using var bgra = Mat.FromPixelData(stagingDesc.Height, stagingDesc.Width, MatType.CV_8UC4, dataBox.DataPointer, dataBox.RowPitch);
                        if (isDuplicate)
                        {
                            // 重复帧但缓存失效：本次顺带重建缓存，后续连续重复 tick 走零流量路径
                            EnsureCache(stagingDesc.Width, stagingDesc.Height);
                            Cv2.CvtColor(bgra, _cachedBgr!, ColorConversionCodes.BGRA2BGR);
                            _cachedFrameTs = frameTs;
                            target = AcquireBgrMat(stagingDesc.Height, stagingDesc.Width);
                            _cachedBgr!.CopyTo(target);
                        }
                        else
                        {
                            // 正常新帧：直接转换进池化 Mat；
                            // 不主动维护缓存——等真出现重复 tick 再建，持续更新场景（游戏）零额外开销
                            target = AcquireBgrMat(stagingDesc.Height, stagingDesc.Width);
                            Cv2.CvtColor(bgra, target, ColorConversionCodes.BGRA2BGR);
                        }
                    }
                    finally
                    {
                        context.UnmapSubresource(_stagingTexture, 0);
                    }

                    lock (_lock)
                    {
                        _lastConsumedFrameTs = frameTs;
                    }
                }

                return new GameCaptureFrame(WgcBgrMat.CreateFrom(target, ReleaseBgrMat), rect);
            }
            catch (SharpDXException e)
            {
                if (target != null)
                {
                    ReleaseBgrMat(target);
                }

                HandleSharpDxError(e);
                return null;
            }
            catch (Exception e)
            {
                if (target != null)
                {
                    ReleaseBgrMat(target);
                }

                // CvtColor/CopyTo 等 OpenCvSharp 异常：池化获取的 Mat 已归还（无泄漏）；
                // 时间戳作废，避免下一 tick 误判为重复帧而返回残缺缓存
                lock (_lock)
                {
                    _lastConsumedFrameTs = 0;
                    _cachedFrameTs = 0;
                }

                Debug.WriteLine($"[WGC V2] Capture 异常: {e.Message}");
                return null;
            }
            finally
            {
                lock (_lock)
                {
                    _activeCaptures--;
                    Monitor.PulseAll(_lock);
                }
            }
        }
    }

    private void EnsureCache(int width, int height)
    {
        if (_cachedBgr == null || _cachedBgr.Cols != width || _cachedBgr.Rows != height)
        {
            _cachedBgr?.Dispose();
            _cachedBgr = new Mat(height, width, MatType.CV_8UC3);
        }
    }

    private Mat AcquireBgrMat(int height, int width)
    {
        lock (_lock)
        {
            while (_bgrQueue.TryDequeue(out var mat))
            {
                if (_bgrBorrowed.Add(mat))
                {
                    if (mat.IsDisposed)
                    {
                        _bgrBorrowed.Remove(mat);
                        continue;
                    }
                    if (mat.Rows == height && mat.Cols == width && mat.Type() == MatType.CV_8UC3)
                    {
                        return mat;
                    }
                    _bgrBorrowed.Remove(mat);
                    mat.Dispose();
                }
                else
                {
                    // 防御分支：队内 Mat 不应同时存在于借出集合（账本错乱才可达）
                    var fresh = new Mat(height, width, MatType.CV_8UC3);
                    _bgrBorrowed.Add(fresh);
                    return fresh;
                }
            }
            return CreateAndRegisterBgrMat(height, width);
        }
    }

    private Mat CreateAndRegisterBgrMat(int height, int width)
    {
        var fresh = new Mat(height, width, MatType.CV_8UC3);
        _bgrBorrowed.Add(fresh);
        return fresh;
    }

    private void ReleaseBgrMat(Mat mat)
    {
        lock (_lock)
        {
            if (mat == null || !_bgrBorrowed.Remove(mat))
            {
                mat?.Dispose();
                return;
            }
            if (mat.IsDisposed)
            {
                mat.Dispose();
                return;
            }
            if (_bgrPoolClosed)
            {
                mat.Dispose();
                return;
            }
            if (_bgrQueue.Count < MaxPoolSize)
            {
                _bgrQueue.Enqueue(mat);
            }
            else
            {
                if (_bgrQueue.TryDequeue(out var stale))
                {
                    stale.Dispose();
                }
                _bgrQueue.Enqueue(mat);
            }
        }
    }

    private void TrimBgrPoolForSizeLocked(int height, int width)
    {
        var total = _bgrQueue.Count;
        if (total == 0) return;
        for (var i = 0; i < total; i++)
        {
            if (!_bgrQueue.TryDequeue(out var mat)) break;
            if (mat != null && !mat.IsDisposed && mat.Rows == height && mat.Cols == width && mat.Type() == MatType.CV_8UC3)
            {
                _bgrQueue.Enqueue(mat);
            }
            else
            {
                mat?.Dispose();
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            IsCapturing = false;
            _hWnd = 0;
            _frameTimer.Reset();
            if (_captureItem != null)
            {
                _captureItem.Closed -= CaptureItemOnClosed;
            }
            if (_captureFramePool != null)
            {
                _captureFramePool.FrameArrived -= OnFrameArrived;
            }
            if (_winEventHookMoveSize != default)
            {
                User32.UnhookWinEvent(_winEventHookMoveSize);
                _winEventHookMoveSize = default;
            }
            if (_winEventHookLocation != default)
            {
                User32.UnhookWinEvent(_winEventHookLocation);
                _winEventHookLocation = default;
            }
            _sizeDirty = true;
            _captureSession?.Dispose();
            _captureSession = null;
            _captureFramePool?.Dispose();
            _captureFramePool = null;
            _captureItem = null;

            // IsCapturing=false 已挡住新的 Capture()；等待在途捕获（正锁外 Map/CvtColor）退出，
            // 才能销毁其正在使用的 staging/纹理/设备。Monitor.Wait 原子放锁，捕获 finally 归零时唤醒
            while (_activeCaptures > 0)
            {
                Monitor.Wait(_lock);
            }

            _gpuTexture?.Dispose();
            _gpuTexture = null;
            _stagingTexture?.Dispose();
            _stagingTexture = null;
            _cachedBgr?.Dispose();
            _cachedBgr = null;
            _cachedFrameTs = 0;
            _lastConsumedFrameTs = 0;
            _lastArrivedFrameTs = 0;
            while (_bgrQueue.TryDequeue(out var pooled)) pooled.Dispose();
            _bgrPoolClosed = true;
            _hdrOutputTexture?.Dispose();
            _hdrOutputTexture = null;
            _hdrOutputUav?.Dispose();
            _hdrOutputUav = null;
            _hdrComputeShader?.Dispose();
            _hdrComputeShader = null;
            _d3dDevice?.Dispose();
            _d3dDevice = null;
            _frameReady = false;
        }
    }

    private void CaptureItemOnClosed(GraphicsCaptureItem sender, object args)
    {
        Stop();
    }

    private void WinEventProc(User32.HWINEVENTHOOK hWinEventHook, uint @event, HWND hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != 0) return;
        if (hwnd != default && hwnd.DangerousGetHandle() == _hWnd)
        {
            _sizeDirty = true;
        }
    }

    private const int SizeFallbackCheckMs = 3000;
    private long _lastSizeFallbackCheckMs;
}
