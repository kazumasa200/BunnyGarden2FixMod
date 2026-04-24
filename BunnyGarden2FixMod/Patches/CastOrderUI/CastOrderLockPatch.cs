using BunnyGarden2FixMod.Utils;
using GB.Game;
using HarmonyLib;

namespace BunnyGarden2FixMod.Patches.CastOrderUI;

/// <summary>
/// GameData.UpdateTodaysCastOrder をパッチし、順番固定中は元の順序を保持する。
/// </summary>
[HarmonyPatch]
public static class CastOrderLockPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameData), nameof(GameData.UpdateTodaysCastOrder))]
    public static bool Prefix(GameData __instance)
    {
        // 順番固定中でなければ通常処理を続行
        if (CastOrderController.Instance == null || !CastOrderController.Instance.AllLocked)
            return true;

        var system = GB.GBSystem.Instance;
        if (system == null) return true;

        var gameData = system.RefGameData();
        if (gameData != __instance) return true;

        // LAST_BAR_DATE 以降の場合は通常処理（全キャラクター順）
        if (__instance.m_gameDate > GameData.LAST_BAR_DATE)
            return true;

        PatchLogger.LogInfo("[CastOrder] 順番固定中のため UpdateTodaysCastOrder をスキップしました");
        return false; // 元のメソッドを実行しない → m_todaysCastOrder は変更されない
    }
}
