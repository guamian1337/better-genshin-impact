namespace Fischless.GameCapture.Graphics.Helpers;

/// <summary>
///     Packs a BGRA8 texture into a tight BGR24 byte stream (strips alpha, no channel swizzle).
///     Reads via UNORM float4 view; x*255+0.5 rounding restores original bytes losslessly.
///     Each thread handles 4 linear pixels and emits 3 packed uints.
/// </summary>
public static class PackBgraToBgrShader
{
    public const int ThreadsPerGroup = 64;
    public const int PixelsPerThread = 4;

    public static string Content =>
"""
// Pack BGRA8 -> tight BGR24 bytes (alpha stripped).
cbuffer Params : register(b0)
{
    uint ImgW;
    uint ImgH;
    uint TotalPx;
};

Texture2D<float4> SrcTex : register(t0);
RWByteAddressBuffer DstBuf : register(u0);

uint PackPixel(uint px)
{
    int sx = (int)(px % ImgW);
    int sy = (int)(px / ImgW);
    float4 c = SrcTex.Load(int3(sx, sy, 0));
    uint r = (uint)(c.x * 255.0f + 0.5f);
    uint g = (uint)(c.y * 255.0f + 0.5f);
    uint b = (uint)(c.z * 255.0f + 0.5f);
    uint v = (b & 0xFFu) | ((g & 0xFFu) << 8) | ((r & 0xFFu) << 16);
    return v;
}

[numthreads(64, 1, 1)]
void CS_PackBgraToBgr(uint3 tid : SV_DispatchThreadID)
{
    uint basePix = tid.x * 4u;
    if (basePix >= TotalPx)
    {
        return;
    }

    uint q0 = PackPixel(basePix);

    uint q1 = 0;
    uint q2 = 0;
    uint q3 = 0;
    if (basePix + 1u < TotalPx)
    {
        q1 = PackPixel(basePix + 1u);
    }
    if (basePix + 2u < TotalPx)
    {
        q2 = PackPixel(basePix + 2u);
    }
    if (basePix + 3u < TotalPx)
    {
        q3 = PackPixel(basePix + 3u);
    }

    uint addr = basePix * 3u;
    uint o0 = (q0 & 0x00FFFFFFu) | ((q1 & 0x000000FFu) << 24);
    uint o1 = ((q1 >> 8) & 0x0000FFFFu) | ((q2 & 0x0000FFFFu) << 16);
    uint o2 = ((q2 >> 16) & 0x000000FFu) | ((q3 & 0x00FFFFFFu) << 8);
    DstBuf.Store(addr, o0);
    DstBuf.Store(addr + 4u, o1);
    DstBuf.Store(addr + 8u, o2);
}
""";
}
