using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.Common.Map;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoPathing;

/// <summary>
/// 相机输入延迟探针：直接复用 BGI 现有的 TaskControl 截图管线，
/// 注入单条相对鼠标位移后以 ~8ms 轮询罗盘读数，测量端到端可见延迟。
/// 通过 开发调试 热键触发；捕获模式跟随全局设置（BitBlt/WGC V3）。
/// </summary>
public static class CameraLatencyProbe
{
    private const double ChangeThresholdDeg = 0.8;
    private const int PollIntervalMs = 8;
    private const int SampleTimeoutMs = 1200;

    public static async Task Run(CancellationToken ct = default)
    {
        var hWnd = SystemControl.FindGenshinImpactHandle();
        var mode = TaskTriggerDispatcher.GlobalGameCapture?.GetType().Name ?? "null";
        Logger.LogInformation("[延迟探针] 开始：当前全局捕获 {Mode}，句柄 0x{HWnd:X}，共 6 次注入", mode, hWnd.ToInt64());
        if (hWnd == 0)
        {
            Logger.LogError("[延迟探针] 未找到游戏窗口");
            return;
        }

        var dx = 260;
        for (var n = 1; n <= 6 && !ct.IsCancellationRequested; n++)
        {
            await ProbeOnce(dx, n, ct);
            dx = -dx;
            await Delay(700, ct);
        }
        Logger.LogInformation("[延迟探针] 结束");

        // 投递链路统计（仅 WGC V3 有数据）：区分 DWM 投递滞后 vs 消费侧读回新鲜度
        if (TaskTriggerDispatcher.GlobalGameCapture is Fischless.GameCapture.Graphics.GraphicsCaptureV3 v3)
        {
            Logger.LogInformation("[延迟探针] 投递统计: {Stats}", v3.GetPipeStatsSnapshot());
        }
    }

    private static async Task ProbeOnce(int dx, int n, CancellationToken ct)
    {
        double baseline;
        if (!TryStableBaseline(n, out baseline))
        {
            Logger.LogWarning("[延迟探针] #{N} 基线不稳定，跳过", n);
            return;
        }

        var sw = Stopwatch.StartNew();
        Simulation.SendInput.Mouse.MoveMouseBy(dx, 0);

        long lat = -1;
        var cursorMoved = false;
        long cursorLat = -1;
        User32.GetCursorPos(out var lastCur);

        while (sw.ElapsedMilliseconds < SampleTimeoutMs && !ct.IsCancellationRequested)
        {
            var t = sw.ElapsedMilliseconds;
            if (cursorLat < 0 && User32.GetCursorPos(out var cur))
            {
                if (cur.X != lastCur.X || cur.Y != lastCur.Y) { cursorMoved = true; cursorLat = t; }
                lastCur = cur;
            }
            if (lat < 0)
            {
                var c = ReadCao();
                if (c.HasValue && Math.Abs(c.Value - baseline) > ChangeThresholdDeg) lat = t;
            }
            if (lat >= 0 && cursorLat >= 0) break;
            await Delay(PollIntervalMs, ct);
        }

        string Fmt(long ms) => ms >= 0 ? $"{ms}ms" : "未变化";
        Logger.LogInformation("[延迟探针] #{N} dx={Dx}: 光标={Cur}(+{CurT}) 可见={Vis}",
            n, dx, cursorMoved ? "动" : "未动", cursorLat, Fmt(lat));
    }

    private static bool TryStableBaseline(int sampleN, out double baseline)
    {
        baseline = double.NaN;
        double sum = 0;
        var count = 0;
        var nullCount = 0;
        double min = double.MaxValue, max = double.MinValue;

        for (var i = 0; i < 6; i++)
        {
            var c = ReadCao();
            if (c.HasValue) { sum += c.Value; count++; min = Math.Min(min, c.Value); max = Math.Max(max, c.Value); }
            else nullCount++;
            Thread.Sleep(30);
        }

        if (count < 4)
        {
            Logger.LogWarning("[延迟探针] #{N} 基线失败：{Null}/{Total} 次读不到帧", sampleN, nullCount, 6);
            return false;
        }
        if (max - min > ChangeThresholdDeg)
        {
            Logger.LogWarning("[延迟探针] #{N} 基线抖动 {Min:F1}~{Max:F1}°（Δ={Delta:F2}°）", sampleN, min, max, max - min);
            return false;
        }
        baseline = sum / count;
        return true;
    }

    private static float? ReadCao()
    {
        try
        {
            using var region = CaptureToRectArea();
            if (region == null || region.SrcMat == null || region.SrcMat.Empty()) return null;
            return CameraOrientation.Compute(region.SrcMat);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[延迟探针] ReadCao 异常: {e.Message}");
            return null;
        }
    }
}
