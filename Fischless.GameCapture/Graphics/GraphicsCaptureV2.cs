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

    private long _poolAcquireHit;
    private long _poolAcquireMissEmpty;
    private long _poolAcquireMissDisposed;
    private long _poolAcquireMissSize;
    private long _poolReleasePushed;
    private long _poolReleaseDropDisposed;
    private long _poolReleaseDropClosed;
    private long _poolReleaseDropFull;
    private long _poolAcquireTotal;
    private long _poolReleaseTotal;
    private int _windowAcquireCount;

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

    // 单 GPU 广播源（参考 bgi-wgc-single-slot / _gpuFrameTexture 模式）
    // 回调只做 GPU->GPU Copy 到此纹理，消费侧 Capture() 再从它 Copy 到 staging 读回
    private Texture2D? _gpuTexture;
    private volatile bool _frameReady;

    // staging 双缓冲：交替使用两块 staging，避免 GPU 写与 CPU Map 连续命中同一块表面；
    // Stage 与 Map 均针对本次的 cur（不读旧帧）
    private const int StagingCount = 2;
    private readonly Texture2D?[] _stagingTextures = new Texture2D?[2];
    private int _stagingIndex;

    // 零拷贝直通（CaptureRaw）：当前处于 Mapped 状态的 staging 槽登记。
    // 视图契约：返回的 Mat 包装映射指针，存活到「下下一次捕获」开始；
    // 任何路径写入/重建/销毁该槽前必须先 Unmap（BeginSlotWriteLocked / UnmapTrackedLocked）
    private int _rawMappedSlot = -1;
    private Texture2D? _rawMappedTexture;

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

    // 诊断：GPU 提交 vs Map 关系
    private int _copyCountSinceLastMap;
    private long _lastMapTime;
    private long _lastDiagTime;
    private int _mapCount5s;
    private long _mapGapSum5s;
    private int _copySum5s;

    // 投递链路诊断：SystemRelativeTime 为 DWM 合成时刻（QPC 时基，与 GetTimestamp 同源），
    // 用于拆解 合成→回调→消费 各段归属（30fps 下 WGC 与 BitBlt 差距定位用）
    private double _composeBootMs = -1;   // 最新帧合成时刻（开机毫秒）
    private double _cbLagSum5s;
    private double _cbLagMax5s;
    private int _cbCount5s;
    private double _ageSum5s;
    private double _ageMax5s;

    // 消费分段耗时：提交(GPU->staging 命令) 与 读回+转换(阻塞 Map + CvtColor)
    private double _submitSum5s;
    private double _submitMax5s;
    private double _readbackSum5s;
    private double _readbackMax5s;

    private long _captureCall5s;

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

            try
            {
                if (!_isHdrEnabled)
                {
                    throw new Exception();
                }

                _pixelFormat = DirectXPixelFormat.R16G16B16A16Float;
                _captureFramePool = Direct3D11CaptureFramePool.Create(
                    _d3dDevice,
                    _pixelFormat,
                    2,
                    _captureItem.Size);
            }
            catch (Exception)
            {
                _pixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
                _captureFramePool = Direct3D11CaptureFramePool.Create(
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

    private void EnsureStagingTextureLocked(SharpDX.Direct3D11.Device device, int index, int width, int height)
    {
        var tex = _stagingTextures[index];
        if (tex == null || tex.Description.Width != width || tex.Description.Height != height)
        {
            // 该槽若处于 Mapped 状态（旧视图仍被外部持有），重建前强制释放映射
            if (_rawMappedSlot == index) UnmapTrackedLocked();
            tex?.Dispose();
            _stagingTextures[index] = Direct3D11Helper.CreateStagingTexture(device, width, height, null);
        }
    }

    /// <summary>目标槽即将被写入/销毁：若处于 Mapped 状态则立即 Unmap</summary>
    private void BeginSlotWriteLocked(int index)
    {
        if (_rawMappedSlot == index) UnmapTrackedLocked();
    }

    private void UnmapTrackedLocked()
    {
        if (_rawMappedSlot < 0 || _rawMappedTexture == null)
        {
            _rawMappedSlot = -1;
            _rawMappedTexture = null;
            return;
        }
        try
        {
            _rawMappedTexture.Device.ImmediateContext.UnmapSubresource(_rawMappedTexture, 0);
        }
        catch
        {
            // 迟到释放容错：纹理可能已被尺寸重建/Stop 销毁，此时无需也无法 Unmap
        }
        _rawMappedSlot = -1;
        _rawMappedTexture = null;
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
            if (_hWnd == 0) return;
            using var frame = sender.TryGetNextFrame();
            if (frame == null) return;

            var now = _frameTimer.ElapsedMilliseconds;

            if (_sizeDirty || now - _lastSizeFallbackCheckMs >= SizeFallbackCheckMs)
            {
                var regionDirty = _sizeDirty;
                _lastSizeFallbackCheckMs = now;
                _sizeDirty = false;
                lock (_lock)
                {
                    // Stop() 迟到回调防护：会话已结束则放弃本帧（防 NRE / 幽灵纹理复活）
                    if (!IsCapturing || _captureItem == null || _captureFramePool == null || _d3dDevice == null) return;

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
                        _gpuTexture?.Dispose();
                        _gpuTexture = null;
                        for (var i = 0; i < StagingCount; i++)
                        {
                            _stagingTextures[i]?.Dispose();
                            _stagingTextures[i] = null;
                        }
                        _stagingIndex = 0;
                        _frameReady = false;
                        UnmapTrackedLocked();
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
            }

            using var surfaceTexture = Direct3D11Helper.CreateSharpDXTexture2D(frame.Surface);
            lock (_lock)
            {
                // Stop 迟到回调防护：防 EnsureGpuTexture 复活幽灵纹理
                if (!IsCapturing) return;
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
                _copyCountSinceLastMap++;
                var qpcNowMs = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
                var composeMs = frame.SystemRelativeTime.TotalMilliseconds;
                _composeBootMs = composeMs;
                var cbLag = Math.Max(0, qpcNowMs - composeMs);
                _cbLagSum5s += cbLag;
                if (cbLag > _cbLagMax5s) _cbLagMax5s = cbLag;
                _cbCount5s++;
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

        lock (_lock)
        {
            if (_gpuTexture == null) return null;

            // Map 读本次 cur staging，内容与 gen 一致
            _captureCall5s++;
            var nowMap = _frameTimer.ElapsedMilliseconds;

            try
            {
                var d3dDevice = _gpuTexture.Device;
                var desc = _gpuTexture.Description;
                var rect = _captureRect;

                var stagingWidth = desc.Width;
                var stagingHeight = desc.Height;

                var curIdx = _stagingIndex;

                EnsureStagingTextureLocked(d3dDevice, curIdx, stagingWidth, stagingHeight);

                var stagingCur = _stagingTextures[curIdx]!;
                var context = d3dDevice.ImmediateContext;
                // 该槽若被上一次 CaptureRaw 的视图占用，先 Unmap 再写入
                BeginSlotWriteLocked(curIdx);
                // Stage 写 cur（GPU -> staging cur）
                var qpcSubmit0 = Stopwatch.GetTimestamp();
                context.CopyResource(_gpuTexture, stagingCur);
                var qpcSubmit1 = Stopwatch.GetTimestamp();

                // 直接 Map cur：等待本次 Copy 完成（阻塞极短）。
                // 不再读 prev 流水缓冲——那会让识别内容固定滞后 1 个 Tick(~50ms)，体感延迟明显
                var mat = stagingCur.CreateMat(d3dDevice, AcquireBgrMat, ReleaseBgrMat);
                var qpcReadback1 = Stopwatch.GetTimestamp();
                // CreateMat 内部已完成 Map+Unmap：若登记簿指向本槽则同步清除
                if (_rawMappedSlot == curIdx)
                {
                    _rawMappedSlot = -1;
                    _rawMappedTexture = null;
                }

                // 翻转 cur/prev 供下一帧流水
                _stagingIndex ^= 1;

                var mapGapMs = nowMap - _lastMapTime;
                _lastMapTime = nowMap;
                _copySum5s += _copyCountSinceLastMap;
                _copyCountSinceLastMap = 0;
                _mapCount5s++;
                _mapGapSum5s += mapGapMs;

                // 消费分段耗时入账
                var submitMs = (qpcSubmit1 - qpcSubmit0) * 1000.0 / Stopwatch.Frequency;
                var readbackMs = (qpcReadback1 - qpcSubmit1) * 1000.0 / Stopwatch.Frequency;
                _submitSum5s += submitMs;
                if (submitMs > _submitMax5s) _submitMax5s = submitMs;
                _readbackSum5s += readbackMs;
                if (readbackMs > _readbackMax5s) _readbackMax5s = readbackMs;

                // 内容年龄：消费时刻 - 该帧 DWM 合成时刻（含投递+轮询相位的端到端新鲜度）
                if (_composeBootMs >= 0)
                {
                    var qpcNowMs = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
                    var age = Math.Max(0, qpcNowMs - _composeBootMs);
                    _ageSum5s += age;
                    if (age > _ageMax5s) _ageMax5s = age;
                }
                TickDiagLocked(nowMap);

                return mat == null ? null : new GameCaptureFrame(mat, rect);
            }
            catch (SharpDXException e)
            {
                HandleSharpDxError(e);
                return null;
            }
        }
    }

    /// <summary>
    ///     零拷贝 BGRA 直通视图：跳过池化/转换/memcpy，返回映射指针包装的 CV_8UC4 Mat。
    ///     <para><b>生命周期契约（与 BitBltMat 同款）：</b>
    ///     数据存活到「下下一次 Capture/CaptureRaw」开始，届时该 staging 被覆写。
    ///     跨两帧持有将读到脏数据或直接崩溃；需要长期持有的消费方请自行 .Clone()。</para>
    ///     <para>通道为 4 通道带 Alpha（B,G,R,A 内存序，前三通道值与 BGR 完全一致）；
    ///     仅限「读完即弃」的延迟敏感路径使用（如罗盘角度读取），勿接入存量 3ch 识别链。</para>
    /// </summary>
    public GameCaptureFrame? CaptureRaw()
    {
        if (!_frameReady) return null;

        lock (_lock)
        {
            if (!IsCapturing || _gpuTexture == null) return null;

            var d3dDevice = _gpuTexture.Device;
            var desc = _gpuTexture.Description;
            var rect = _captureRect;
            var curIdx = _stagingIndex;

            EnsureStagingTextureLocked(d3dDevice, curIdx, desc.Width, desc.Height);
            BeginSlotWriteLocked(curIdx);

            var context = d3dDevice.ImmediateContext;
            var qpcSubmit0 = Stopwatch.GetTimestamp();
            context.CopyResource(_gpuTexture, _stagingTextures[curIdx]!);
            var box = context.MapSubresource(_stagingTextures[curIdx]!, 0,
                SharpDX.Direct3D11.MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
            var qpcSubmit1 = Stopwatch.GetTimestamp();

            // 登记映射：本视图存活至下下次捕获（另一槽先被写入一轮）
            _rawMappedSlot = curIdx;
            _rawMappedTexture = _stagingTextures[curIdx];
            _stagingIndex ^= 1;

            var nowMap = _frameTimer.ElapsedMilliseconds;
            _mapCount5s++;

            // 内容年龄与 Capture() 同口径
            if (_composeBootMs >= 0)
            {
                var qpcNowMs = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
                var age = Math.Max(0, qpcNowMs - _composeBootMs);
                _ageSum5s += age;
                if (age > _ageMax5s) _ageMax5s = age;
            }
            TickDiagLocked(nowMap);

            // 零转换：BGRA 视图直接交付
            var view = Mat.FromPixelData(desc.Height, desc.Width, MatType.CV_8UC4, box.DataPointer, box.RowPitch);
            return new GameCaptureFrame(view, rect);
        }
    }

    private Mat AcquireBgrMat(int height, int width)
    {
        lock (_lock)
        {
            _poolAcquireTotal++;
            _windowAcquireCount++;
            while (_bgrQueue.TryDequeue(out var mat))
            {
                if (_bgrBorrowed.Add(mat))
                {
                    if (mat.IsDisposed)
                    {
                        _bgrBorrowed.Remove(mat);
                        _poolAcquireMissDisposed++;
                        continue;
                    }
                    if (mat.Rows == height && mat.Cols == width && mat.Type() == MatType.CV_8UC3)
                    {
                        _poolAcquireHit++;
                        return mat;
                    }
                    _bgrBorrowed.Remove(mat);
                    _poolAcquireMissSize++;
                    mat.Dispose();
                }
                else
                {
                    // 防御分支：队内 Mat 不应同时存在于借出集合（账本错乱才可达），
                    // 计入专用计数以免污染 MissEmpty 语义
                    _poolAcquireMissEmpty++;
                    var fresh = new Mat(height, width, MatType.CV_8UC3);
                    _bgrBorrowed.Add(fresh);
                    return fresh;
                }
            }
            _poolAcquireMissEmpty++;
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
            _poolReleaseTotal++;
            if (mat == null || !_bgrBorrowed.Remove(mat))
            {
                _poolReleaseDropDisposed++;
                mat?.Dispose();
                return;
            }
            if (mat.IsDisposed)
            {
                _poolReleaseDropDisposed++;
                mat.Dispose();
                return;
            }
            if (_bgrPoolClosed)
            {
                _poolReleaseDropClosed++;
                mat.Dispose();
                return;
            }
            if (_bgrQueue.Count < MaxPoolSize)
            {
                _poolReleasePushed++;
                _bgrQueue.Enqueue(mat);
            }
            else
            {
                if (_bgrQueue.TryDequeue(out var stale))
                {
                    stale.Dispose();
                }
                _poolReleaseDropFull++;
                _bgrQueue.Enqueue(mat);
            }
        }
    }

    private void TickDiagLocked(long now)
    {
        if (_lastDiagTime == 0)
        {
            _lastDiagTime = now;
            _lastMapTime = now;
            return;
        }
        if (now - _lastDiagTime < 5000) return;
        Debug.WriteLine($"[WGC Diag] 5s: Map次数={_mapCount5s} 平均间隔={_mapGapSum5s / Math.Max(1, _mapCount5s):F0}ms 平均攒获Copy={_copySum5s / Math.Max(1, _mapCount5s):F1} 总提交={_copySum5s + _mapCount5s}");
        Debug.WriteLine($"[WGC Pipe] 5s: 回调滞后 avg={(_cbCount5s > 0 ? _cbLagSum5s / _cbCount5s : -1):F1}ms max={_cbLagMax5s:F1} | 内容年龄 avg={(_mapCount5s > 0 ? _ageSum5s / Math.Max(1, _mapCount5s) : -1):F1}ms max={_ageMax5s:F1} | 消费分段 提交 avg={(_mapCount5s > 0 ? _submitSum5s / Math.Max(1, _mapCount5s) : -1):F2}ms max={_submitMax5s:F2} 读回+转换 avg={(_mapCount5s > 0 ? _readbackSum5s / Math.Max(1, _mapCount5s) : -1):F2}ms max={_readbackMax5s:F2}");
        Debug.WriteLine($"[WGC Pool] 5s: Hit={_poolAcquireHit} Miss(空={_poolAcquireMissEmpty} 废={_poolAcquireMissDisposed} 尺寸={_poolAcquireMissSize}) Release(Pushed={_poolReleasePushed} 废={_poolReleaseDropDisposed} 关={_poolReleaseDropClosed} 满={_poolReleaseDropFull}) 池存={_bgrQueue.Count} 在途={_poolAcquireTotal - _poolReleaseTotal}(借{_poolAcquireTotal}还{_poolReleaseTotal})");
        _mapCount5s = 0;
        _mapGapSum5s = 0;
        _copySum5s = 0;
        _captureCall5s = 0;
        _cbLagSum5s = 0;
        _cbLagMax5s = 0;
        _cbCount5s = 0;
        _ageSum5s = 0;
        _ageMax5s = 0;
        _submitSum5s = 0;
        _submitMax5s = 0;
        _readbackSum5s = 0;
        _readbackMax5s = 0;
        _lastDiagTime = now;
        _windowAcquireCount = 0;
    }

    private void TrimBgrPoolForSizeLocked(int height, int width)
    {
        var total = _bgrQueue.Count;
        if (total == 0) return;
        var recycled = 0;
        var retained = 0;
        for (var i = 0; i < total; i++)
        {
            if (!_bgrQueue.TryDequeue(out var mat)) break;
            if (mat != null && !mat.IsDisposed && mat.Rows == height && mat.Cols == width && mat.Type() == MatType.CV_8UC3)
            {
                _bgrQueue.Enqueue(mat);
                retained++;
            }
            else
            {
                mat?.Dispose();
                recycled++;
            }
        }
        Debug.WriteLine($"[WGC Pool] 尺寸变化 {width}x{height}: 清理 {total} 个 Mat，丢弃 {recycled}（旧尺寸/废），保留 {retained} 个");
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
            _gpuTexture?.Dispose();
            _gpuTexture = null;
            for (var i = 0; i < StagingCount; i++)
            {
                _stagingTextures[i]?.Dispose();
                _stagingTextures[i] = null;
            }
            _stagingIndex = 0;
            UnmapTrackedLocked();
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
