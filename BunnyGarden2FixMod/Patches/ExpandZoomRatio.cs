using BunnyGarden2FixMod.Utils;
using GB.Bar;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// ドリンク購入時のズーム比率を上げる
/// </summary>

[HarmonyPatch]
public static class ExpandZoomRatio
{
    // ズームする画角（デフォルト 15f 度： 60度基準だと1.33倍）
    // 22.9はズーム比率1.618倍（黄金比）に相当
    private const float maxZoom = 22.9f;

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
        // var targetField = AccessTools.Field(typeof(DrinkPurchase), "m_camFOV");
        var targetField = AccessTools.Field(typeof(DrinkPurchase), nameof(DrinkPurchase.m_camFOV));
        int patched = 0;

        for (int i = 0; i < codes.Count; i++)
        {
            // this.m_camera.fieldOfView = Mathf.Lerp(this.m_camFOV, this.m_camFOV - 15f, this.m_zoom); を書き換え，
            // this.m_camera.fieldOfView = Mathf.Lerp(this.m_camFOV, this.m_camFOV - maxZoom, this.m_zoom); にする
            if (i+1 < codes.Count &&
                codes[i].opcode == OpCodes.Ldfld && codes[i].operand is FieldInfo f && f == targetField &&
                codes[i+1].opcode == OpCodes.Ldc_R4 && (float)codes[i+1].operand == 15f)
            {
                codes[i + 1].operand = maxZoom;
                patched++;
                break; 
            }
            
        }
        if(patched != 1)
        {
            PatchLogger.LogError($"[{nameof(ExpandZoomRatio)}] 修正対象のパターンが見つかりませんでした。ゲームのアップデートでパッチが機能していない可能性があります。");
        }
        return codes;
    }

}
