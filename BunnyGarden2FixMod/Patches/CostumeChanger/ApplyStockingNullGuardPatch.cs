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
/// skip 前に m_lastLoadArg.Stocking = type を更新する。NRE 経路 (IsDisableStocking==false) との
/// 互換性維持で、IsDisableStocking==true キャラとは挙動差が出るが NRE 回避を優先。
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
