using GB;
using GB.Game;
using GB.Game.Params;
using HarmonyLib;
using System.Linq;
using System.Collections.Generic;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// キャストの並び順をランダムにするパッチ
/// </summary>

[HarmonyPatch(typeof(GameData), nameof(GameData.UpdateTodaysCastOrder))]
internal static class RandomizeCastOrder
{
    private static bool Prefix(GameData __instance)
    {
        if (!Configs.RandomizeCastOrder.Value)
            return true;

        if (CastOrderUI.CastOrderController.Instance != null && CastOrderUI.CastOrderController.Instance.AllLocked)
        {
            Plugin.Logger.LogInfo("[RandomizeCastOrder] 順番固定中のためキャスト順シャッフルをスキップします");
            return false;
        }

        ScheduleParam param = GBSystem.Instance.RefScheduleParamToday();
        List<CharID> list = CharIDUtil.AllCharID().Where((CharID x) => !param.IsAbsent(x)).ToList();
        bool hasEvent = list.Any(x =>
            param.IsBirthDay(x) ||
            __instance.CharData(x)?.ProposeState == GameData.ProposeState.Declined ||
            __instance.IsEscortedEntryGone(x));

        if (hasEvent)
        {
            Plugin.Logger.LogInfo("[RandomizeCastOrder] イベントありのため通常処理に戻ります");
            return true;
        }

        var randomizedList = list.OrderBy(_ => UnityEngine.Random.value).ToList();
        for (int j = randomizedList.Count; j < CharIDUtil.Num; j++)
            randomizedList.Add(CharID.NUM);
        __instance.m_todaysCastOrder = randomizedList.ToArray();
        Plugin.Logger.LogInfo("[RandomizeCastOrder] キャスト順をランダムに並べ替えました");
        return false;
    }
}