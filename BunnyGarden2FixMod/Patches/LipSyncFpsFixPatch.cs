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

        var GetFrequencyMethod = AccessTools.PropertyGetter(typeof(AudioClip), nameof(AudioClip.frequency));
        var GetDeltaTimeMethod = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));

        int patched = 0;
        for (int i = 0; i < codes.Count; i++)
        {
            // this.m_clip.frequency / 60 を (int)((float)this.m_clip.frequency * deltaTime) に書き換え
            if (i+2 < codes.Count &&
                codes[i].Calls(GetFrequencyMethod) &&
                codes[i+1].LoadsConstant(60) &&
                codes[i+2].opcode == OpCodes.Div)
            {
                codes[i+1].opcode = OpCodes.Conv_R4;
                codes[i+1].operand = null;

                codes[i+2].opcode = OpCodes.Call;
                codes[i+2].operand = GetDeltaTimeMethod;

                codes.Insert(i+3, new CodeInstruction(OpCodes.Mul));
                codes.Insert(i+4, new CodeInstruction(OpCodes.Conv_I4));

                i += 4;
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
        float fpsRatio = Time.deltaTime * 60f;
        float custom_t = 1.0f - Mathf.Pow(1.0f - t, fpsRatio);
        return Mathf.Lerp(a, b, custom_t);
    }

}
