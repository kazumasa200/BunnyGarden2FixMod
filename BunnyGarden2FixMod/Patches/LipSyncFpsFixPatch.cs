using BunnyGarden2FixMod.Utils;
using GB.Scene;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// 口パクの速度をフレームレートが変わっても同じ速度にするパッチ。
///
///   LipSyncCalculator.Calculate 内で口パクの速度が固定値になっていたため
/// 　フレームレートが高いと高速になっていた。フレームレートに合わせた数値に書き換える
///   設定値 = 基準値 0.35f x 60 / FPS
///    60FPS   → 基準 0.35f (×1.0)
///    120FPS  → x0.5
///    240FPS  → x0.25
/// </summary>
[HarmonyPatch(typeof(LipSyncCalculator), nameof(LipSyncCalculator.Calculate))]
public static class LipSyncFpsFixPatch
{
    private static bool Prepare()
    {
        PatchLogger.LogInfo("[LipSyncFpsFixPatch] LipSyncCalculator.Calculate にトランスパイラを登録");
        return true;
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);

        var originalMethod = AccessTools.Method(typeof(Mathf), nameof(Mathf.Lerp), new[] { typeof(float), typeof(float), typeof(float) });
        var replacedMethod = AccessTools.Method(typeof(LipSyncFpsFixPatch), nameof(CustomLerp));

        var GetFrequency = typeof(AudioClip).GetProperty("frequency").GetGetMethod();
        var GetFPSMethod = AccessTools.Method(typeof(LipSyncFpsFixPatch), nameof(GetFPS));

        int patched = 0;
        for (int i = 0; i < codes.Count; i++)
        {
            // this.m_clip.frequency / 60 の書き換え
            if (codes[i].Calls(GetFrequency) &&
                i+1 < codes.Count && codes[i+1].LoadsConstant(60) &&
                i+2 < codes.Count && codes[i+2].opcode == OpCodes.Div)
            {
                codes[i+1] = new CodeInstruction(OpCodes.Call, GetFPSMethod);
                patched++;
                continue;
            }

            // lerpの書き換え
            if (codes[i].opcode == OpCodes.Call && codes[i].operand is MethodInfo method && method == originalMethod)
            {
                codes[i].operand = replacedMethod;
                patched++;
                continue;
            }
        }

        if(patched != 2)
        {
            PatchLogger.LogError("[LipSyncFpsFixPatch] 修正対象のパターンが見つかりませんでした。ゲームのアップデートでパッチが機能していない可能性があります。");
        }
        return codes;
    }

    private static float CustomLerp(float a, float b, float t)
    {
        float custom_t = 1.0f - Mathf.Pow(1.0f - t, 60f / (float)GetFPS());
        return Mathf.Lerp(a, b, custom_t);
    }

    private static int GetFPS()
    {
        int AppliedFrameRate = Application.targetFrameRate;
        int FPS = AppliedFrameRate <= 0 ? 60 : AppliedFrameRate;
        return FPS;
    }

}
