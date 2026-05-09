using System.Linq;
using BunnyGarden2FixMod.Utils;
using Cysharp.Threading.Tasks;
using GB.Scene;
using HarmonyLib;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// CharacterHandle.ApplyStocking の Prefix で mesh_skin_lower / mesh_foot_barefoot の sharedMesh
/// が null なら早期 return して NullReferenceException を回避する。
///
/// 本体 ApplyStocking は SMR != null しか check せず sharedMesh の null を見逃すため、
/// タイトル戻り後の panties only Preload (setupPantiesOnly → ApplyStocking.Forget) で
/// sharedMesh が null 化していると NRE する。同症状の既知記述: KneeSocksLoader.cs:244 のコメント。
///
/// skip 前に m_lastLoadArg.Stocking = type を更新するのは本体 CharacterHandle.cs:605 の挙動
/// 再現で必須。本体は L605 で type を保存してから L606 以降の SMR 処理に進むため、保存だけは
/// 完了している状態を維持する必要がある。CharacterHandle.cs:469 / L830 / L865 の各経路で
/// `ApplyStocking(m_lastLoadArg.Stocking).Forget()` 形で「保存値による再 apply」が走るため、
/// この更新を削除すると skip 後の再 apply で古い値が再注入され、ユーザの選択 type が消失する
/// (HideShoes (L671) の `m_lastLoadArg.Stocking != 0` 判定も同根拠で要更新)。
///
/// IsDisableStocking==true キャラでは本体は L599 で更新せず return するが、Patch Prefix は
/// L599 より前で発火するため sharedMesh==null かつ IsDisableStocking==true の組み合わせでは
/// 「本体は更新しない / Patch は更新する」差分が残る。NRE 回避を優先して許容。
/// </summary>
[HarmonyPatch(typeof(CharacterHandle), nameof(CharacterHandle.ApplyStocking))]
public static class ApplyStockingNullGuardPatch
{
    private static bool Prepare()
    {
        PatchLogger.LogInfo("[ApplyStockingNullGuardPatch] CharacterHandle.ApplyStocking ガード適用");
        return true;
    }

    private static bool Prefix(CharacterHandle __instance, int __0, ref UniTask __result)
    {
        if (__instance?.Chara == null) return true;
        if (__instance.m_lastLoadArg == null) return true;

        var renderers = __instance.Chara.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var lower = renderers.FirstOrDefault(r => r != null && r.name == "mesh_skin_lower");
        var foot = renderers.FirstOrDefault(r => r != null && r.name == "mesh_foot_barefoot");

        bool lowerNull = lower != null && lower.sharedMesh == null;
        bool footNull = foot != null && foot.sharedMesh == null;
        if (!lowerNull && !footNull) return true;

        __instance.m_lastLoadArg.Stocking = __0;

        PatchLogger.LogWarning(
            $"[ApplyStockingNullGuardPatch] sharedMesh null のためスキップ: char={__instance.GetCharID()} lowerNull={lowerNull} footNull={footNull}");
        __result = UniTask.CompletedTask;
        return false;
    }
}
