namespace Fischless.GameCapture.Graphics.Helpers;

/// <summary>
///     将 BGRA 纹理打包为紧凑 BGR24 字节流的计算着色器（丢 Alpha，无通道重排）。
///     通过 B8G8R8A8_UNORM 浮点视图读取（该家族无 UINT 格式），x*255+0.5 取整可无损还原字节；
///     每线程处理 4 个线性像素，剥 Alpha 打包成 3 个 uint 写出。
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

Texture2D<float4> bgraTex : register(t0);
RWByteAddressBuffer PackedBgr : register(u0);

uint ToBgr24(uint2 id)
{
    // UNORM SRV 逻辑序为 RGBA；目标内存序为 B,G,R；*255+0.5 取整无损还原字节
    float4 c = bgraTex.Load(int3(id, 0));
    uint r = (uint)(c.x * 255.0f + 0.5f);
    uint g = (uint)(c.y * 255.0f + 0.5f);
    uint b = (uint)(c.z * 255.0f + 0.5f);
    return b | (g << 8) | (r << 16);
}

[numthreads(64, 1, 1)]
void CS_PackBgraToBgr(uint3 dt : SV_DispatchThreadID)
{
    uint p0 = dt.x * 4u;
    if (p0 >= TotalPixels) return;

    uint x = p0 % Width;
    uint y = p0 / Width;
    uint i0 = ToBgr24(uint2(x, y));

    uint i1 = 0, i2 = 0, i3 = 0;
    if (p0 + 1u < TotalPixels) { uint xx = (p0 + 1u) % Width; uint yy = (p0 + 1u) / Width; i1 = ToBgr24(uint2(xx, yy)); }
    if (p0 + 2u < TotalPixels) { uint xx = (p0 + 2u) % Width; uint yy = (p0 + 2u) / Width; i2 = ToBgr24(uint2(xx, yy)); }
    if (p0 + 3u < TotalPixels) { uint xx = (p0 + 3u) % Width; uint yy = (p0 + 3u) / Width; i3 = ToBgr24(uint2(xx, yy)); }

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
