using BunnyGarden2FixMod.Utils;
using GB;
using GB.Game.Params;
using HarmonyLib;
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
internal static class AddBunnyDrinksToDrinkSetParamsPatch
{
    // 追加したいバニードリンクの ID (DrinkMenus)
    private static readonly DrinkMenus[] targetMenus = {
        DrinkMenus.BUNNY_TRAP,
        DrinkMenus.BUNNY_MAX,
        DrinkMenus.BUNNY_PUNCH
    };

    private static void Postfix(ref List<DrinkParam> __result)
    {
        if (!Configs.BunnyDrinksEnabled.Value) return;
        if (__result == null) return;

        // 全ドリンクリストを取得
        List<DrinkParam> allDrinks = GBSystem.Instance.RefDrinkParams();
        if (allDrinks == null) return;

        // 新しいリスト
        List<DrinkParam> newDrinkList = new List<DrinkParam>();

        // バニードリンクを挿入する位置としてシャンパンを特定
        DrinkParam CHAMPAGNE = allDrinks.Count > (int)DrinkMenus.RUSTI ? allDrinks[(int)DrinkMenus.RUSTI] : null;

        bool injected = false;

        // 既にバニー系が入っているか事前チェック（二重追加防止）
        foreach (var menuId in targetMenus)
        {
            int idx = (int)menuId;
            if (idx >= 0 && idx < allDrinks.Count && __result.Contains(allDrinks[idx]))
            {
                injected = true;
                break;
            }
        }

        // バニードリンクを追加する関数
        void InjectBunnyDrinks()
        {
            if (injected) return;
            foreach (var menuId in targetMenus)
            {
                int idx = (int)menuId;
                if (idx >= 0 && idx < allDrinks.Count)
                {
                    // バニードリンクを追加
                    newDrinkList.Add(allDrinks[idx]);
                }
            }
            injected = true;
            PatchLogger.LogInfo($"[BunnyDrinksPatch] バニー系ドリンクをメニューに追加しました。({string.Join(" / ", targetMenus)})");
        }

        foreach (var drink in __result)
        {
            // シャンパンが見つかったらその直前にバニードリンクを追加する
            if (drink == CHAMPAGNE)
            {
                InjectBunnyDrinks();
            }
            newDrinkList.Add(drink);
        }

        // バニードリンクが追加されなければ，最後尾にバニードリンクを追加する
        if (!injected)
        {
            InjectBunnyDrinks();
        }

        __result = newDrinkList;
    }
}
