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

public class GraphicsCaptureV3(bool captureHdr = false) : IGameCapture
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
    private readonly bool[] _stagingValid = new bool[2];
    private int _stagingIndex;

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

    // 帧代次 + 同帧去重缓存（SharedFrameCache）
    // 默认关闭：BitBlt 式语义——每次 Capture 都真实读回最新帧。
    // 去重可省重复读回的 CPU，但会给控制回路（如视角旋转）引入"旧帧复用"时序风险；
    // 需要时可通过 settings["WgcSharedReadback"]=true 显式开启
    private long _copyGen;
    private bool _sharedReadbackEnabled = false;

    // 诊断：最近一次 WGC 帧到达时刻（_frameTimer 时基），用于测量帧年龄
    private long _lastCopyTickMs = -1;

    // 投递链路诊断：SystemRelativeTime 为 DWM 合成时刻（QPC 时基，与 GetTimestamp 同源），
    // 用于拆解 合成→回调→消费 各段归属（30fps 下 WGC 与 BitBlt 差距定位用）
    private double _composeBootMs = -1;   // 最新帧合成时刻（开机毫秒）
    private double _cbLagLastMs = -1;     // 最近一帧 合成→回调 滞后
    private double _cbLagSum5s;
    private double _cbLagMax5s;
    private int _cbCount5s;
    private double _ageSum5s;
    private double _ageMax5s;

    /// <summary>最近一帧 合成→回调 的投递滞后（毫秒）；未运行返回 -1</summary>
    public double CallbackLagMs => IsCapturing ? _cbLagLastMs : -1;
    /// <summary>当前帧年龄：距最近一次 WGC 帧送达的毫秒数；未运行返回 -1</summary>
    public long FrameAgeMs => IsCapturing ? _frameTimer.ElapsedMilliseconds - _lastCopyTickMs : -1;
    /// <summary>当前帧代次</summary>
    public long FrameGen => _copyGen;
    private SharedFrameCache? _sharedCache;
    private long _sharedDedupHit5s;
    private long _sharedReadback5s;
    private long _captureCall5s;

    private sealed class SharedFrameCache
    {
        public Mat Owner = null!;
        public long Gen;
        public int Refs;
    }

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

            if (settings != null)
            {
                if (settings.TryGetValue("WgcSharedReadback", out var shared) && shared is bool sb)
                    _sharedReadbackEnabled = sb;
            }

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
            tex?.Dispose();
            _stagingTextures[index] = Direct3D11Helper.CreateStagingTexture(device, width, height, null);
            _stagingValid[index] = false;
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
                            _stagingValid[i] = false;
                        }
                        _stagingIndex = 0;
                        _frameReady = false;
                        InvalidateSharedCacheLocked();
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
                _copyGen++;
                _lastCopyTickMs = _frameTimer.ElapsedMilliseconds;
                var qpcNowMs = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
                var composeMs = frame.SystemRelativeTime.TotalMilliseconds;
                _composeBootMs = composeMs;
                _cbLagLastMs = Math.Max(0, qpcNowMs - composeMs);
                _cbLagSum5s += _cbLagLastMs;
                if (_cbLagLastMs > _cbLagMax5s) _cbLagMax5s = _cbLagLastMs;
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
            Debug.WriteLine($"[WGC V3] FrameArrived 异常: {e.Message}");
        }
    }

    public GameCaptureFrame? Capture()
    {
        if (!_frameReady) return null;

        lock (_lock)
        {
            if (_gpuTexture == null) return null;

            // Map 读本次 cur staging，内容与 gen 一致；同 gen 内重复 Capture 命中缓存返回同一份内容
            var gen = _copyGen;
            _captureCall5s++;
            var nowMap = _frameTimer.ElapsedMilliseconds;

            try
            {
                var cache = _sharedCache;
                if (_sharedReadbackEnabled && cache != null && cache.Gen == gen && !cache.Owner.IsDisposed)
                {
                    cache.Refs++;
                    _sharedDedupHit5s++;
                    TickDiagLocked(nowMap);
                    return new GameCaptureFrame(WgcBgrMat.CreateFrom(cache.Owner, m => ReleaseShared(m, cache)), _captureRect);
                }

                var d3dDevice = _gpuTexture.Device;
                var desc = _gpuTexture.Description;
                var rect = _captureRect;

                var stagingWidth = desc.Width;
                var stagingHeight = desc.Height;

                var curIdx = _stagingIndex;

                EnsureStagingTextureLocked(d3dDevice, curIdx, stagingWidth, stagingHeight);

                var stagingCur = _stagingTextures[curIdx]!;
                var context = d3dDevice.ImmediateContext;
                // Stage 写 cur（GPU -> staging cur）
                context.CopyResource(_gpuTexture, stagingCur);
                _stagingValid[curIdx] = true;

                // 直接 Map cur：等待本次 Copy 完成（阻塞极短）。
                // 不再读 prev 流水缓冲——那会让识别内容固定滞后 1 个 Tick(~50ms)，体感延迟明显
                Texture2D? stagingToMap = stagingCur;

                SharedFrameCache? newCache = null;
                Mat? sharedOwner = null;
                var mat = _sharedReadbackEnabled
                    ? stagingToMap!.CreateMat(d3dDevice, out sharedOwner, AcquireBgrMat,
                        m => { if (newCache != null) ReleaseShared(m, newCache); else ReleaseBgrMat(m); })
                    : stagingToMap!.CreateMat(d3dDevice, out _, AcquireBgrMat, ReleaseBgrMat);

                if (_sharedReadbackEnabled && mat != null && sharedOwner != null)
                {
                    RetireSharedCacheLocked();
                    newCache = new SharedFrameCache { Owner = sharedOwner, Gen = gen, Refs = 1 };
                    _sharedCache = newCache;
                    _sharedReadback5s++;
                }

                // 翻转 cur/prev 供下一帧流水
                _stagingIndex ^= 1;

                var mapGapMs = nowMap - _lastMapTime;
                _lastMapTime = nowMap;
                _copySum5s += _copyCountSinceLastMap;
                _copyCountSinceLastMap = 0;
                _mapCount5s++;
                _mapGapSum5s += mapGapMs;

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

    private void ReleaseShared(Mat owner, SharedFrameCache cache)
    {
        lock (_lock)
        {
            cache.Refs--;
            if (cache.Refs < 0)
            {
                // 防御：正常路径不应出现负值，出现说明归还计数异常
                Debug.WriteLine($"[WGC Shared] refs 计数异常(负值) gen={cache.Gen}");
            }
            if (cache.Refs <= 0 && _sharedCache != cache)
            {
                if (owner.IsDisposed)
                {
                    Debug.WriteLine("[WGC Shared] 归还时 owner 已被释放，跳过（防二次归还）");
                    return;
                }
                ReleaseBgrMat(owner);
            }
        }
    }

    private void RetireSharedCacheLocked()
    {
        var old = _sharedCache;
        _sharedCache = null;
        if (old == null) return;
        if (old.Refs <= 0)
        {
            ReleaseBgrMat(old.Owner);
        }
    }

    private void InvalidateSharedCacheLocked()
    {
        var c = _sharedCache;
        _sharedCache = null;
        if (c == null) return;
        if (c.Refs <= 0)
        {
            _bgrBorrowed.Remove(c.Owner);
            c.Owner.Dispose();
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
        Debug.WriteLine($"[WGC Pipe] 5s: 回调滞后 avg={(_cbCount5s > 0 ? _cbLagSum5s / _cbCount5s : -1):F1}ms max={_cbLagMax5s:F1} | 内容年龄 avg={(_mapCount5s > 0 ? _ageSum5s / Math.Max(1, _mapCount5s) : -1):F1}ms max={_ageMax5s:F1}");
        Debug.WriteLine($"[WGC Shared] 5s: Capture调用={_captureCall5s} 命中={_sharedDedupHit5s} 读回={_sharedReadback5s} 缓存refs={_sharedCache?.Refs ?? -1}");
        Debug.WriteLine($"[WGC Pool] 5s: Hit={_poolAcquireHit} Miss(空={_poolAcquireMissEmpty} 废={_poolAcquireMissDisposed} 尺寸={_poolAcquireMissSize}) Release(Pushed={_poolReleasePushed} 废={_poolReleaseDropDisposed} 关={_poolReleaseDropClosed} 满={_poolReleaseDropFull}) 池存={_bgrQueue.Count} 在途={_poolAcquireTotal - _poolReleaseTotal}(借{_poolAcquireTotal}还{_poolReleaseTotal})");
        _mapCount5s = 0;
        _mapGapSum5s = 0;
        _copySum5s = 0;
        _captureCall5s = 0;
        _sharedDedupHit5s = 0;
        _sharedReadback5s = 0;
        _cbLagSum5s = 0;
        _cbLagMax5s = 0;
        _cbCount5s = 0;
        _ageSum5s = 0;
        _ageMax5s = 0;
        _lastDiagTime = now;
        _windowAcquireCount = 0;
    }

    /// <summary>投递链路统计快照（供探针等外部打印；读取近一个 5s 窗口的累计值）</summary>
    public string GetPipeStatsSnapshot()
    {
        lock (_lock)
        {
            var cbAvg = _cbCount5s > 0 ? _cbLagSum5s / _cbCount5s : -1;
            var ageAvg = _mapCount5s > 0 ? _ageSum5s / Math.Max(1, _mapCount5s) : -1;
            return $"回调滞后 avg={cbAvg:F1}ms max={_cbLagMax5s:F1}({_cbCount5s}帧) | 内容年龄 avg={ageAvg:F1}ms max={_ageMax5s:F1}({_mapCount5s}次)";
        }
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
                _stagingValid[i] = false;
            }
            _stagingIndex = 0;
            while (_bgrQueue.TryDequeue(out var pooled)) pooled.Dispose();
            _bgrPoolClosed = true;
            InvalidateSharedCacheLocked();
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
