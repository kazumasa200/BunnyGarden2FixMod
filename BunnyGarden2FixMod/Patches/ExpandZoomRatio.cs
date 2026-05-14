using BunnyGarden2FixMod.Utils;
using GB.Bar;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// ドリンク購入時のズーム比率を上げる
/// DrinkPurchase.GBUpdate がコルーチンで呼び出されているため
/// TargetMethodを指定する形でTrispilerを当てている
/// </summary>

[HarmonyPatch]
public static class ExpandZoomRatio
{
    // ズームする画角（デフォルト 15f 度： 60度基準だと1.33倍）
    // 22.9はズーム比率1.618倍（黄金比）に相当
    private const float maxZoom = 22.9f;

    // ズーム量（デフォルト0.05f/フレーム）
    // 画角増に合わせてスピードを変更。0.0372は1/1.618倍（黄金比）に相当
    private const float zoomSpeed = 0.05f; //0.0327f; フルズームまでの時間は変えない

    [HarmonyTargetMethod]
    private static MethodBase TargetMethod()
    {
        // DrinkPurchase.GBUpdate の実体（MoveNext）を自動的に取得
        return AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(DrinkPurchase), nameof(DrinkPurchase.GBUpdate)));
    }

    private static bool Prepare()
    {
        PatchLogger.LogInfo(
            $"[{nameof(ExpandZoomRatio)}] " +
            $"{nameof(DrinkPurchase)}.{nameof(DrinkPurchase.GBUpdate)} " +
            $"をパッチしました。");
        return true;
    }
    
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        var camFOVField = AccessTools.Field(typeof(DrinkPurchase), nameof(DrinkPurchase.m_camFOV));

        var ClampMethod = AccessTools.Method(typeof(Mathf), nameof(Mathf.Clamp01));
        var zoomField = AccessTools.Field(typeof(DrinkPurchase), nameof(DrinkPurchase.m_zoom));
        var deltaTimeMethod = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
        
        int patched = 0;
        for (int i = 0; i < codes.Count; i++)
        {
            // this.m_camera.fieldOfView = Mathf.Lerp(this.m_camFOV, this.m_camFOV - 15f, this.m_zoom); を書き換え，
            // this.m_camera.fieldOfView = Mathf.Lerp(this.m_camFOV, this.m_camFOV - maxZoom, this.m_zoom); にする
            if (i+1 < codes.Count &&
                codes[i].LoadsField(camFOVField) &&
                codes[i+1].LoadsConstant(15f))
            {
                codes[i + 1].operand = maxZoom;
                patched++;
                continue; 
            }

            // this.m_zoom = Mathf.Clamp01(this.m_zoom +/- 0.05f); を書き換え
            // this.m_zoom = Mathf.Clamp01(this.m_zoom +/- zoomSpeed * Time.deltaTime * 60f); にする
            if (i+3 < codes.Count &&
                codes[i].LoadsField(zoomField) &&
                codes[i+1].LoadsConstant(0.05f) &&
                (codes[i+2].opcode == OpCodes.Sub || codes[i+2].opcode == OpCodes.Add) &&
                codes[i+3].Calls(ClampMethod))
            {
                codes[i+1].operand = zoomSpeed;

                codes.Insert(i+2, new CodeInstruction(OpCodes.Call, deltaTimeMethod));
                codes.Insert(i+3, new CodeInstruction(OpCodes.Mul));

                codes.Insert(i+4, new CodeInstruction(OpCodes.Ldc_R4, 60f));
                codes.Insert(i+5, new CodeInstruction(OpCodes.Mul));
                i += 5;
                patched++;   
                continue;
            }            
        }
        if(patched != 3)
        {
            PatchLogger.LogError($"[{nameof(ExpandZoomRatio)}] 修正対象のパターンが見つかりませんでした。ゲームのアップデートでパッチが機能していない可能性があります。");
        }
        return codes;
    }

}
