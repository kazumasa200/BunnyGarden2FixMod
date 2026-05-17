using GB;
using GB.Game;
using HarmonyLib;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// キャストの衣装ををランダムにするパッチ
/// </summary>
[HarmonyPatch(typeof(GameData), nameof(GameData.UpdateTodaysCastOrder))]
internal static class RandomizeCostume
{
    // 私服にする確率
    private const float CasualProbability = 0.2f;
    private static void Postfix()
    {
        if (GBSystem.Instance == null)
            return;

        var setCostume = Configs.RandomizeCostume.Value switch
        {
          RandomCostumeMode.Random => (GBSystem.CostumeOverride)Random.RandomRangeInt(0, (int)GBSystem.CostumeOverride.ForceUniform),
          // ForceUniform は None扱いにしたいので Rangeを 0:None ~ 7:ForceUniform にした。（7は選択されない）
          RandomCostumeMode.Casual => Random.Range(0f,1f) < CasualProbability ? GBSystem.CostumeOverride.ForceCasual : GBSystem.CostumeOverride.None,
          _ => GBSystem.CostumeOverride.None, // default
        };

        if (GBSystem.Instance.m_costumeOverride != setCostume)
        {
            GBSystem.Instance.m_costumeOverride = setCostume;
            Plugin.Logger.LogInfo($"[RandomizeCostume] 衣装が設定されました： {setCostume}");
        }
        return;
    }
}
