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

        bool patched = false;
        for (int i = 0; i < codes.Count; i++)
        {
            if (codes[i].opcode == OpCodes.Call && codes[i].operand is MethodInfo method && method == originalMethod)
            {
                codes[i].operand = replacedMethod;
                patched = true;
            }
        }

        if(!patched)
        {
            PatchLogger.LogError("[LipSyncFpsFixPatch] 修正対象のパターンが見つかりませんでした。ゲームのアップデートでパッチが機能していない可能性があります。");
        }
        return codes;
    }

    private static float CustomLerp(float weight, float num, float t)
    {
        int AppliedFrameRate = Application.targetFrameRate;
        float FPS = AppliedFrameRate <= 0 ? 60f : (float)AppliedFrameRate;
        float customSpeed = 0.35f * 60f / FPS;
        return Mathf.Lerp(weight, num, customSpeed);
    }
}
