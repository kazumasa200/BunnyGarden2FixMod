using GB;
using GB.Game;
using GB.Game.Params;
using HarmonyLib;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// キャストの並び順と衣装ををランダムにするパッチ
/// </summary>

[HarmonyPatch(typeof(GameData), nameof(GameData.UpdateTodaysCastOrder))]
public static class RandomizeCastOrderAndCostume
{
    private const float CasualProbability = 0.2f;

    private static bool Prefix(GameData __instance)
    {
        RandomizeCostumeMethod();
 
        bool isRandomized = RandomizeCastOrderMethod(__instance);
        return !isRandomized; // isRandomized == true で通常処理スキップ
    }

    // キャスト順をランダムにする
    // ランダム化したら true，何もしなければ false を返す
    private static bool RandomizeCastOrderMethod(GameData instance)
    {
        if (!Configs.RandomizeCastOrder.Value)
            return false;

        if (CastOrderUI.CastOrderController.Instance != null && CastOrderUI.CastOrderController.Instance.AllLocked)
        {
            Plugin.Logger.LogInfo("[RandomizeCastOrderAndCostume][RandomizeCastOrderMethod] 順番固定中のためキャスト順シャッフルをスキップします");
            return false;
        }

        ScheduleParam param = GBSystem.Instance.RefScheduleParamToday();
        List<CharID> list = CharIDUtil.AllCharID().Where((CharID x) => !param.IsAbsent(x)).ToList();
        bool hasEvent = list.Any(x =>
            param.IsBirthDay(x) ||
            instance.CharData(x)?.ProposeState == GameData.ProposeState.Declined ||
            instance.IsEscortedEntryGone(x));

        if (hasEvent)
        {
            Plugin.Logger.LogInfo("[RandomizeCastOrderAndCostume][RandomizeCastOrderMethod] イベントありのため通常処理に戻ります");
            return false;
        }

        var randomizedList = list.OrderBy(_ => Random.value).ToList();
        for (int j = randomizedList.Count; j < CharIDUtil.Num; j++)
            randomizedList.Add(CharID.NUM);
        instance.m_todaysCastOrder = randomizedList.ToArray();
        Plugin.Logger.LogInfo("[RandomizeCastOrderAndCostume][RandomizeCastOrderMethod] キャスト順をランダムに並べ替えました");
        return true;
    }

    // キャスト衣装をランダムにする
    private static bool RandomizeCostumeMethod()
    {        
        if (GBSystem.Instance == null)
            return false;

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
            Plugin.Logger.LogInfo($"[RandomizeCastOrderAndCostume][RandomizeCostumeMethod] 衣装が設定されました： {setCostume}");
        }
        return true;
    }

}