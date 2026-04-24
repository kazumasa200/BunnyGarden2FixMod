using BunnyGarden2FixMod.Patches.CostumeChanger;
using BunnyGarden2FixMod.Utils;
using GB.Scene;
using HarmonyLib;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// CharacterHandle.ApplyStocking() の Prefix でストッキングタイプを強制的に 0（なし）に差し替えるパッチ。
///
/// ApplyStocking の type 引数：
///   0 : なし
///   1 : m_stockings.mat（黒ストッキング）
///   2 : m_stockings_white.mat（白ストッキング）
///   3 : m_fishnetstockings.mat（黒網タイツ）
///   4 : m_fishnetstockings_white.mat（白網タイツ）
///
/// ConfigDisableStockings が true のとき type を 0 に置き換えることで
/// ストッキングが適用されなくなる。
/// </summary>
[HarmonyPatch(typeof(CharacterHandle), nameof(CharacterHandle.ApplyStocking))]
public static class DisableStockingPatch
{
    private static bool Prepare()
    {
        PatchLogger.LogInfo("[DisableStockingPatch] CharacterHandle.ApplyStocking をパッチしました（ストッキング無効化）");
        return true;
    }

    private static void Prefix(ref int __0)
    {
        if (KneeSocksLoader.IsPreloading) return; // マテリアルプリロード中はスキップ
        if (!Plugin.ConfigDisableStockings.Value) return;
        __0 = 0;
    }
}
