using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.Common.Map;
using BetterGenshinImpact.GameTask.Model.Area;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoPathing;

public class CameraRotateTask(CancellationToken ct)
{
    private readonly double _dpi = TaskContext.Instance().DpiScale;

    /// <summary>
    /// 向目标角度旋转
    /// </summary>
    /// <param name="targetOrientation"></param>
    /// <param name="imageRegion"></param>
    /// <param name="gainScale">增益缩放（过冲阻尼用，默认 1）</param>
    /// <returns></returns>
    public float RotateToApproach(float targetOrientation, ImageRegion imageRegion, double gainScale = 1.0)
    {
        var cao = CameraOrientation.Compute(imageRegion.SrcMat);
        var diff = (cao - targetOrientation + 180) % 360 - 180;
        diff += diff < -180 ? 360 : 0;
        if (diff == 0)
        {
            return diff;
        }

        // 平滑的旋转视角
        // todo dpi 和分辨率都会影响转动速度
        double controlRatio = 1;
        if (Math.Abs(diff) > 90)
        {
            controlRatio = 4;
        }
        else if (Math.Abs(diff) > 30)
        {
            controlRatio = 3;
        }
        else if (Math.Abs(diff) > 5)
        {
            controlRatio = 2;
        }

        controlRatio *= gainScale;
        var dx = (int)Math.Round(-controlRatio * diff * _dpi);
        // 单次指令上限，防止积压输入一次性兑现时甩过头
        if (dx > 900) dx = 900;
        else if (dx < -900) dx = -900;
        Simulation.SendInput.Mouse.MoveMouseBy(dx, 0);
        return diff;
    }

    /// <summary>
    /// 转动视角到目标角度
    /// </summary>
    /// <param name="targetOrientation">目标角度</param>
    /// <param name="maxDiff">最大误差</param>
    /// <param name="maxTryTimes">最大尝试次数（超时时间）</param>
    /// <returns></returns>
    public async Task<bool> WaitUntilRotatedTo(int targetOrientation, int maxDiff, int maxTryTimes = 50)
    {
        bool isSuccessful = false;
        int count = 0;
        var v3 = TaskTriggerDispatcher.GlobalGameCapture as Fischless.GameCapture.Graphics.GraphicsCaptureV3;

        float prevCao = float.NaN;   // 上一轮罗盘读数（冻结检测用）
        bool hasSentDelta = false;   // 是否已发出未兑现的旋转指令
        float prevDiff = float.NaN;  // 过冲检测用
        double gainScale = 1.0;
        int frozenSkips = 0;

        while (!ct.IsCancellationRequested)
        {
            using var screen = CaptureToRectArea();
            var cao = CameraOrientation.Compute(screen.SrcMat);

            // 冻结防护：已发旋转指令但罗盘读数与上一轮完全一致，
            // 说明该指令还在 输入IPC→游戏处理→渲染→送帧 的管线里没兑现（分身可达 200ms+）。
            // 此时继续叠加全量 delta，管线追上时会一次性兑现 → 冲过目标来回打转。
            // 正确做法：不再发指令，等反馈更新。
            if (hasSentDelta && cao == prevCao)
            {
                frozenSkips++;
                await Delay(30, ct);
                count++;
                if (count > maxTryTimes)
                {
                    Logger.LogWarning("视角转动到目标角度超时（等待输入兑现），停止转动");
                    break;
                }
                continue;
            }

            var diff = RotateToApproach(targetOrientation, screen, gainScale);
            prevCao = cao;
            hasSentDelta = Math.Abs(diff) >= maxDiff;

            if (frozenSkips > 0 && (count < 8 || count % 10 == 0))
            {
                Logger.LogInformation("[转向调试] 冻结等待 x{Skips} 后恢复", frozenSkips);
                frozenSkips = 0;
            }

            // 转向调试：帧龄=截图内容距今多久；gen 是否前进；gain=过冲阻尼系数
            if (count < 8 || count % 10 == 0)
            {
                Logger.LogInformation(
                    "[转向调试] n={Count} diff={Diff:F1} 帧龄={Age}ms gen={Gen} gain={Gain:F2} 目标={Target}",
                    count, diff, v3?.FrameAgeMs ?? -1, v3?.FrameGen ?? -1, gainScale, targetOrientation);
            }

            if (Math.Abs(diff) < maxDiff)
            {
                isSuccessful = true;
                break;
            }

            // 过冲阻尼：符号翻转（冲过目标）说明积压输入仍在兑现，
            // 降低后续增益并多等一拍，避免反向全量修正再次穿越
            if (!float.IsNaN(prevDiff) && prevDiff * diff < 0 && Math.Abs(prevDiff) > maxDiff)
            {
                gainScale = Math.Max(0.35, gainScale * 0.6);
                await Delay(60, ct);
                count += 2;
            }
            else if (Math.Abs(diff) < 30)
            {
                // 回到小偏差区间后恢复增益
                gainScale = 1.0;
            }

            prevDiff = diff;

            if (count > maxTryTimes)
            {
                Logger.LogWarning("视角转动到目标角度超时，停止转动");
                break;
            }

            await Delay(50, ct);
            count++;
        }
        return isSuccessful;
    }
}
