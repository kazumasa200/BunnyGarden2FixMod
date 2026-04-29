using BunnyGarden2FixMod.Utils;
using GB.Bar.MiniGame;
using HarmonyLib;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// 鉄骨渡りミニゲームで落下しなくなるチートパッチ。
///
/// SteelFrame.Update() の落下判定:
///   if (m_pushOutTimer > Param.DropOutTime) { dropOut(); }
///
/// m_pushOutTimer を毎フレーム 0 にリセットすることで、
/// カーソルがどれだけ外側にはみ出しても落下タイマーが溜まらなくなる。
/// </summary>
[HarmonyPatch(typeof(SteelFrame), nameof(SteelFrame.Update))]
public static class SteelFrameUltimateSurvivorPatch
{
    private static bool Prepare()
    {
        PatchLogger.LogInfo("[UltimateSurvivor] SteelFrame.Update に落下無効パッチを登録");
        return true;
    }

    private static void Postfix(SteelFrame __instance)
    {
        if (!Configs.UltimateSurvivorEnabled.Value) return;
        if (__instance.m_state != SteelFrame.State.InGame) return;

        // 落下タイマーを毎フレームリセット → DropOutTime を超えなくなる
        __instance.m_pushOutTimer = 0f;
    }
}
