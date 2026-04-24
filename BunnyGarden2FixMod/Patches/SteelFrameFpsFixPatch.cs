using BunnyGarden2FixMod.Utils;
using GB.Bar.MiniGame;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection.Emit;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// 鉄骨渡りミニゲームのカーソル移動がフレームレート依存になっているバグを修正するパッチ。
///
/// ■ 原因
///   SteelFrame.Update() 内で速度（m_vel）を位置（m_pos）に加算する際に
///   Time.deltaTime が掛かっていない:
///     this.m_pos += this.m_vel;          // ← バグ
///
///   m_vel 自体は Time.deltaTime を掛けて積算されるため単位は正しいが、
///   m_pos への反映が 1 フレームあたり 1 回固定で行われるため、
///   フレームレートが高いほど 1 秒間の加算回数が増えカーソルが激しくブレる。
///     60FPS  → 基準 (×1.0)
///    120FPS  → 2 倍のブレ
///    240FPS  → 4 倍のブレ
///
/// ■ 修正
///   IL トランスパイラで上記箇所を
///     this.m_pos += this.m_vel * (Time.deltaTime * 60f);
///   に書き換える。60f を掛けることで 60FPS 時の挙動を基準として保つ。
///     60FPS  : deltaTime ≈ 1/60 → 係数 1.0（変化なし）
///    120FPS  : deltaTime ≈ 1/120 → 係数 0.5（1 フレーム分が半分・2 倍頻度で同量）
/// </summary>
[HarmonyPatch(typeof(SteelFrame), nameof(SteelFrame.Update))]
public static class SteelFrameFpsFixPatch
{
    private static bool Prepare()
    {
        PatchLogger.LogInfo("[SteelFrameFpsFix] SteelFrame.Update にトランスパイラを登録");
        return true;
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);

        var velField = AccessTools.Field(typeof(SteelFrame), "m_vel");
        var posField = AccessTools.Field(typeof(SteelFrame), "m_pos");
        var opAdd = AccessTools.Method(typeof(Vector3), "op_Addition");
        var getDt = AccessTools.PropertyGetter(typeof(Time), "deltaTime");
        var opMul = AccessTools.Method(typeof(Vector3), "op_Multiply",
                           new[] { typeof(Vector3), typeof(float) });

        for (int i = 0; i < codes.Count - 2; i++)
        {
            // m_pos += m_vel のパターンを探す:
            //   ldfld m_vel  →  call op_Addition  →  stfld m_pos
            if (codes[i].LoadsField(velField) &&
                codes[i + 1].Calls(opAdd) &&
                codes[i + 2].StoresField(posField))
            {
                // ldfld m_vel の直後に「* (Time.deltaTime * 60f)」を挿入
                // スタック: [..., m_vel] → [..., m_vel * (deltaTime * 60f)]
                codes.InsertRange(i + 1, new[]
                {
                    new CodeInstruction(OpCodes.Call,   getDt), // Time.deltaTime
                    new CodeInstruction(OpCodes.Ldc_R4, 60f),   // 60f
                    new CodeInstruction(OpCodes.Mul),            // deltaTime * 60f
                    new CodeInstruction(OpCodes.Call,   opMul), // m_vel * (deltaTime * 60f)
                });

                PatchLogger.LogInfo("[SteelFrameFpsFix] m_pos += m_vel の FPS 依存バグを修正しました");
                return codes;
            }
        }

        PatchLogger.LogError("[SteelFrameFpsFix] 修正対象のパターンが見つかりませんでした。ゲームのアップデートでパッチが機能していない可能性があります。");
        return codes;
    }
}
