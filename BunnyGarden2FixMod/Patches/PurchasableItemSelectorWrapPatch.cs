using System;
using System.Reflection;
using BunnyGarden2FixMod.Utils;
using DG.Tweening;
using GB;
using GB.Bar;
using HarmonyLib;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// ドリンク・フード選択メニューの左右ナビゲーションにラップアラウンドを追加するパッチ群。
///
/// <list type="bullet">
///   <item>一番左の項目で左を押す → 一番右の項目へ（カーソル・コンテナ位置も正しく移動）</item>
///   <item>一番右の項目で右を押す → 一番左の項目へ（同上）</item>
///   <item>左右矢印は常に両方表示（アイテムが 2 個以上の場合）</item>
/// </list>
///
/// <para>
/// <b>コンテナ位置の計算</b><br/>
/// ゲームは <c>m_itemContainer</c> を DOTween で 1 ステップ ±250px スライドさせることで
/// 選択アイテムを表示する。インデックス 0 のときのコンテナ X 座標を <c>m_startPos.x</c> として
/// 保持しているため、任意のインデックス <c>N</c> に対応する X は
/// <c>m_startPos.x - N * 250f</c> で算出できる。
/// </para>
///
/// <para>
/// <b>ラップ時の処理</b><br/>
/// 通常移動では元の <c>playAnimation</c> をそのまま呼ぶ。
/// ラップ移動のみ、<c>DOTween.Kill</c> で進行中アニメーションを中断してから
/// <c>localPosition.x</c> をスナップ後、<c>showArrow</c> / <c>setDescription</c> を手動で呼ぶ。
/// </para>
/// </summary>
[HarmonyPatch(typeof(PurchasableItemSelector), "move")]
public static class PurchasableItemSelectorWrapPatch
{
    // 反射情報キャッシュ（Prepare で初期化）
    private static FieldInfo   s_selectField;
    private static PropertyInfo s_selectValueProp;
    private static FieldInfo   s_itemsField;
    private static FieldInfo   s_itemContainerField;
    private static FieldInfo   s_startPosField;
    private static FieldInfo   s_isMoveingField;
    private static MethodInfo  s_playAnimationMethod;
    private static MethodInfo  s_setDescriptionMethod;
    private static MethodInfo  s_showArrowMethod;

    private static bool Prepare()
    {
        var t = typeof(PurchasableItemSelector);

        s_selectField          = AccessTools.Field(t, "m_select");
        s_itemsField           = AccessTools.Field(t, "m_purchasableItems");
        s_itemContainerField   = AccessTools.Field(t, "m_itemContainer");
        s_startPosField        = AccessTools.Field(t, "m_startPos");
        s_isMoveingField       = AccessTools.Field(t, "m_isMoveing");
        s_playAnimationMethod  = AccessTools.Method(t, "playAnimation");
        s_setDescriptionMethod = AccessTools.Method(t, "setDescription");
        s_showArrowMethod      = AccessTools.Method(t, "showArrow");

        if (s_selectField != null)
            s_selectValueProp = s_selectField.FieldType.GetProperty("Value");

        bool ok = s_selectField        != null
               && s_selectValueProp    != null
               && s_itemsField         != null
               && s_itemContainerField != null
               && s_startPosField      != null
               && s_isMoveingField     != null
               && s_playAnimationMethod  != null
               && s_setDescriptionMethod != null
               && s_showArrowMethod      != null;

        if (ok)
            PatchLogger.LogInfo("[WrapNav] PurchasableItemSelector.move をパッチしました（ラップアラウンド有効）");
        else
            PatchLogger.LogWarning("[WrapNav] 必要なフィールド/メソッドが見つからずパッチをスキップします");

        return ok;
    }

    /// <summary>
    /// アクティブなアイテム数を返す共通ヘルパー。
    /// </summary>
    private static int CountActiveItems(PurchasableItemSelector instance)
    {
        int num = 0;
        var itemsObj = s_itemsField.GetValue(instance) as System.Collections.IEnumerable;
        if (itemsObj == null) return 0;
        foreach (var item in itemsObj)
        {
            var comp = item as Component;
            if (comp != null && comp.gameObject.activeSelf)
                num++;
        }
        return num;
    }

    /// <summary>
    /// <c>move(int direction)</c> を丸ごと代替する Prefix。
    /// <list type="bullet">
    ///   <item>通常移動: 元の <c>playAnimation</c> + <c>setDescription</c> を呼ぶ（挙動変わらず）</item>
    ///   <item>ラップ移動: コンテナを正確な位置へスナップしてから <c>showArrow</c> / <c>setDescription</c></item>
    /// </list>
    /// </summary>
    private static bool Prefix(PurchasableItemSelector __instance, int direction)
    {
        try
        {
            // m_select.Value を取得
            var mSelectObj = s_selectField.GetValue(__instance);
            int oldValue   = (int)s_selectValueProp.GetValue(mSelectObj);

            int num = CountActiveItems(__instance);
            if (num <= 0) return true; // フォールバック

            // ラップアラウンドで新インデックスを計算
            // ((n % m) + m) % m で負の剰余を正に揃える
            int newValue   = ((oldValue + direction) % num + num) % num;
            bool isWrapping = newValue != oldValue + direction; // 端をまたいだ

            s_selectValueProp.SetValue(mSelectObj, newValue);

            if (newValue != oldValue)
            {
                GBSystem.Instance.PlaySelectSE();

                if (isWrapping)
                {
                    // ─── ラップ時: コンテナを正しい位置へ即スナップ ───────────────────

                    var containerComp      = s_itemContainerField.GetValue(__instance) as Component;
                    var containerTransform = containerComp?.transform;

                    if (containerTransform != null)
                    {
                        // 進行中の DOTween があれば中断（でないと次フレームで位置を上書きされる）
                        bool isMoveing = (bool)s_isMoveingField.GetValue(__instance);
                        if (isMoveing)
                            DOTween.Kill(containerTransform, false);

                        // インデックス N に対応する X = m_startPos.x - N * 250f
                        var startPos = (Vector3)s_startPosField.GetValue(__instance);
                        float targetX = startPos.x - newValue * 250f;
                        var pos = containerTransform.localPosition;
                        pos.x   = targetX;
                        containerTransform.localPosition = pos;

                        // アニメーション中フラグを解除
                        s_isMoveingField.SetValue(__instance, false);
                    }

                    // 矢印表示・説明文を更新
                    // （showArrow は ShowArrowPatch の Postfix によって常時両矢印になる）
                    s_showArrowMethod.Invoke(__instance, null);
                    s_setDescriptionMethod.Invoke(__instance, null);
                }
                else
                {
                    // ─── 通常移動: 元と同じ playAnimation + setDescription ─────────────
                    s_playAnimationMethod.Invoke(__instance, new object[] { direction * -1 });
                    s_setDescriptionMethod.Invoke(__instance, null);
                }
            }

            return false; // 元のメソッドをスキップ
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[WrapNav] 例外発生、元の move を実行します: {ex.Message}");
            return true; // 安全フォールバック
        }
    }
}

/// <summary>
/// <c>PurchasableItemSelector.showArrow()</c> の Postfix。
/// ラップアラウンドが有効な場合、両端でも矢印を常に表示する。
///
/// <para>
/// 元の <c>showArrow</c> はインデックスが 0 のとき左矢印を、最後のとき右矢印を非表示にする。
/// ラップアラウンド実装後は両方向に常に移動できるため、
/// アイテムが 2 個以上あれば両矢印を強制的に表示する。
/// アイテムが 1 個以下の場合は元の制御に従う（非表示のまま）。
/// </para>
/// </summary>
[HarmonyPatch(typeof(PurchasableItemSelector), "showArrow")]
public static class PurchasableItemSelectorShowArrowPatch
{
    private static FieldInfo s_leftArrowField;
    private static FieldInfo s_rightArrowField;
    private static FieldInfo s_itemsField;

    private static bool Prepare()
    {
        var t = typeof(PurchasableItemSelector);
        s_leftArrowField  = AccessTools.Field(t, "m_leftArrow");
        s_rightArrowField = AccessTools.Field(t, "m_rightArrow");
        s_itemsField      = AccessTools.Field(t, "m_purchasableItems");

        bool ok = s_leftArrowField != null && s_rightArrowField != null && s_itemsField != null;

        if (ok)
            PatchLogger.LogInfo("[WrapNav] PurchasableItemSelector.showArrow をパッチしました（常時両矢印表示）");
        else
            PatchLogger.LogWarning("[WrapNav] showArrow パッチ: 必要なフィールドが見つからずスキップします");

        return ok;
    }

    private static void Postfix(PurchasableItemSelector __instance)
    {
        try
        {
            // アクティブなアイテム数を数える
            int num = 0;
            var itemsObj = s_itemsField.GetValue(__instance) as System.Collections.IEnumerable;
            if (itemsObj != null)
            {
                foreach (var item in itemsObj)
                {
                    var comp = item as Component;
                    if (comp != null && comp.gameObject.activeSelf)
                        num++;
                }
            }

            // アイテムが 2 個以上あれば両矢印を強制表示
            if (num <= 1) return;

            var leftArrow  = s_leftArrowField.GetValue(__instance)  as GameObject;
            var rightArrow = s_rightArrowField.GetValue(__instance) as GameObject;

            leftArrow?.SetActive(true);
            rightArrow?.SetActive(true);
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[WrapNav] showArrow Postfix で例外: {ex.Message}");
        }
    }
}

