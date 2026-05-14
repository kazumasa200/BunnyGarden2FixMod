using GB.Game;
using HarmonyLib;
using UnityEngine;
using static GB.GBSystem;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// キャストの衣装ををランダムにするパッチ
/// </summary>
[HarmonyPatch(typeof(GameData), nameof(GameData.UpdateTodaysCastOrder))]
internal static class RandomizeCostume
{
    private static void Postfix()
    {
        ref var costume = ref Instance.m_costumeOverride;
        if (!Configs.RandomizeCostume.Value)
        {
            if (costume != CostumeOverride.None)
            {
                costume = CostumeOverride.None;
                Plugin.Logger.LogInfo($"[RandomizeCostume] 衣装がデフォルトに設定されました： {costume}");
            }
            return;
        }

        costume = (CostumeOverride)Random.RandomRangeInt((int)CostumeOverride.ForceCasual, (int)CostumeOverride.Num);
        Plugin.Logger.LogInfo($"[RandomizeCostume] 衣装がランダムに設定されました： {costume}");
        return;
    }
}
