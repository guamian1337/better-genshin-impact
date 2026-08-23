namespace Fischless.GameCapture.Graphics.Helpers;

/// <summary>
///     将 BGRA 纹理打包为紧凑 BGR24 字节流的计算着色器（丢 Alpha，无通道重排）。
///     每线程处理 4 个线性像素：读 4 个 R32_UINT（内存序 B,G,R,A），剥 Alpha 打包成 3 个 uint 写出。
/// </summary>
public static class PackBgraToBgrShader
{
    public const int ThreadsPerGroup = 64;
    public const int PixelsPerThread = 4;

    public static string Content =>
"""
cbuffer Params : register(b0)
{
    uint Width;
    uint Height;
    uint TotalPixels;
};

Texture2D<uint4> bgraTex : register(t0);
RWByteAddressBuffer PackedBgr : register(u0);

[numthreads(64, 1, 1)]
void CS_PackBgraToBgr(uint3 dt : SV_DispatchThreadID)
{
    uint p0 = dt.x * 4u;
    if (p0 >= TotalPixels) return;

    uint i0 = 0, i1 = 0, i2 = 0, i3 = 0;

    i0 = bgraTex.Load(int3((int)(p0 % Width), (int)(p0 / Width), 0)).x;
    if (p0 + 1u < TotalPixels) i1 = bgraTex.Load(int3((int)((p0 + 1u) % Width), (int)((p0 + 1u) / Width), 0)).x;
    if (p0 + 2u < TotalPixels) i2 = bgraTex.Load(int3((int)((p0 + 2u) % Width), (int)((p0 + 2u) / Width), 0)).x;
    if (p0 + 3u < TotalPixels) i3 = bgraTex.Load(int3((int)((p0 + 3u) % Width), (int)((p0 + 3u) / Width), 0)).x;

    uint addr = p0 * 3u;
    PackedBgr.Store(addr,
        (i0 & 0x00FFFFFFu) | ((i1 & 0x000000FFu) << 24));
    PackedBgr.Store(addr + 4u,
        ((i1 >> 8) & 0x0000FFFFu) | ((i2 & 0x0000FFFFu) << 16));
    PackedBgr.Store(addr + 8u,
        ((i2 >> 16) & 0x000000FFu) | ((i3 & 0x00FFFFFFu) << 8));
}
""";
}
