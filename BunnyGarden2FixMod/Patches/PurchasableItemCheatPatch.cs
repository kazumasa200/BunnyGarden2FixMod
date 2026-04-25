using BunnyGarden2FixMod.Utils;
using GB;
using GB.Bar;
using GB.Game;
using GB.Game.Params;
using HarmonyLib;
using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// ドリンク・フードの選択画面で、現在のキャストにとっての
/// 「お気に入り」「旬（ヴォーグ）」「嫌い」のアイテムを背景色で示すパッチ。
///
/// PurchasableItem.SetInfo() の Postfix で、パラメータから現在のキャストとの
/// 相性を判定し、m_base（背景 Image）の色を変化させる。
///
/// ・緑色 : お気に入り（AddFavoriteLikability > 0）― 好感度が大幅に上がる
/// ・黄色 : 旬アイテム（IsVogueDrink / IsVogueFood）― ボーナスがつく
/// ・赤色 : 嫌いなもの（AddFavoriteLikability &lt; 0）― 好感度が下がる
///
/// SetSelectable(false) が後から呼ばれると色が灰色に上書きされるが、
/// それは「選択不可」の項目であり実害はない。
/// </summary>
[HarmonyPatch(typeof(PurchasableItem), "SetInfo")]
public static class PurchasableItemCheatPatch
{
    private static readonly FieldInfo s_baseField =
        AccessTools.Field(typeof(PurchasableItem), "m_base");

    private static bool Prepare()
    {
        PatchLogger.LogInfo("[PurchasableItemCheatPatch] PurchasableItem.SetInfo をパッチしました（ドリンク・フード正解表示チート）");
        return true;
    }

    private static void Postfix(PurchasableItem __instance, PurchasableParam purchasableParam)
    {
        if (!Plugin.ConfigCheatLikability.Value) return;
        try
        {
            if (GBSystem.Instance == null) return;
            var gameData = GBSystem.Instance.RefGameData();
            if (gameData == null) return;

            CharID cast = gameData.GetCurrentCast();
            // CharID.NUM はキャスト未確定（プレイヤー自身）を表す。
            // ドリンクの「自分用注文」フェーズでも cast は現在のキャストが設定されているため
            // そのまま使用して問題ない。
            if ((int)cast < 0 || (int)cast >= 6) return;

            bool isFav = purchasableParam.IsFavoriteItem(cast);
            bool isDislike = purchasableParam.IsDislikeItem(cast);
            bool isVogue = false;

            if (!isFav && !isDislike)
            {
                var schedule = GBSystem.Instance.RefScheduleParamToday();
                if (schedule != null)
                {
                    // IsVogueDrink / IsVogueFood は共に PurchasableParam を受け取る
                    isVogue = purchasableParam is FoodParam
                        ? schedule.IsVogueFood(purchasableParam)
                        : schedule.IsVogueDrink(purchasableParam);
                }
            }

            if (!isFav && !isDislike && !isVogue) return;

            var baseImage = s_baseField?.GetValue(__instance) as Image;
            if (baseImage == null) return;

            if (isFav)
                // 緑：お気に入り（最優先）
                baseImage.color = new Color(0.5f, 1f, 0.5f);
            else if (isVogue)
                // 黄：旬アイテム
                baseImage.color = new Color(1f, 1f, 0.5f);
            else
                // 赤：嫌いなもの
                baseImage.color = new Color(1f, 0.5f, 0.5f);
        }
        catch (Exception e)
        {
            PatchLogger.LogError($"[PurchasableItemCheatPatch] エラー: {e.Message}");
        }
    }
}
