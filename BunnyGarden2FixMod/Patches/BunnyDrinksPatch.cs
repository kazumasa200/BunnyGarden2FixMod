using BunnyGarden2FixMod.Utils;
using GB;
using HarmonyLib;
using GB.Game.Params;
using System.Collections.Generic;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// <c>DrinkSetParams.ToDrinkParamList()</c> の Postfix。
/// 選択されたドリンクセット（メニュー構成）に、バニードリンクを強制的に追加する。
///
/// <para>
/// ゲームの元の仕様では、イベントなどの限定的な条件下でしか
/// バニードリンクがリストに含まれない。
/// 本パッチでは、UIに渡される最終的なドリンクリストに対して
/// 直接バニー系ドリンクを追加することで、進行状況に関わらず常設化する。
/// </para>
/// </summary>

[HarmonyPatch(typeof(DrinkSetParams), nameof(DrinkSetParams.ToDrinkParamList))]
public class AddBunnyDrinksToDrinkSetParamsPatch
{
    // 追加したいバニードリンクの ID (DrinkMenus)
    private static readonly DrinkMenus[] targetMenus = {
        DrinkMenus.BUNNY_TRAP,
        DrinkMenus.BUNNY_MAX,
        DrinkMenus.BUNNY_PUNCH
    };
    private static void Postfix(ref List<DrinkParam> __result)
    {
        if (!Plugin.ConfigBunnyDrinksEnabled.Value) return;
        if (__result == null) return;

        // 全ドリンクリストを取得
        List<DrinkParam> allDrinks = GBSystem.Instance.RefDrinkParams();

        foreach (var menuId in targetMenus)
        {
            // インデックスの境界チェック
            if((int)menuId >= allDrinks.Count) continue;

            // すでにリストに入っていないか確認
            // allDrinks[(int)menuId] で取得したインスタンスそのものが含まれているかチェック
            DrinkParam bunnyDrink = allDrinks[(int)menuId];
            if (!__result.Contains(bunnyDrink))
            {
                __result.Add(bunnyDrink);
            }
        }
        PatchLogger.LogInfo($"バニードリンクをメニューに追加しました: {string.Join(" / ", targetMenus)}");
    }
}
