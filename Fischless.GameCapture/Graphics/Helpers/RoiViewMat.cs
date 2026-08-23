using OpenCvSharp;
using OpenCvSharp.Internal;

namespace Fischless.GameCapture.Graphics.Helpers;

/// <summary>
///     零拷贝 ROI 视图：包装 staging 映射指针的外部内存 Mat。
///     Dispose 时触发归还回调（Unmap + staging 纹理回池），在此之前数据始终有效。
/// </summary>
public sealed class RoiViewMat : Mat
{
    private readonly Action _onRelease;
    private int _released;

    private RoiViewMat(IntPtr ptr, Action onRelease)
    {
        if (ptr == IntPtr.Zero)
            throw new OpenCvSharpException("Native object address is NULL");
        this.ptr = ptr;
        _onRelease = onRelease;
    }

    public static Mat FromPixelData(int rows, int cols, IntPtr data, long step, Action onRelease)
    {
        NativeMethods.HandleException(
            NativeMethods.core_Mat_new8(rows, cols, MatType.CV_8UC4, data, new IntPtr(step), out var ptr));
        return new RoiViewMat(ptr, onRelease);
    }

    protected override void DisposeUnmanaged()
    {
        base.DisposeUnmanaged();
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _onRelease();
        }
    }
}
