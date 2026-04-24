using BunnyGarden2FixMod.Utils;
using GB.Scene;
using HarmonyLib;
using System;
using TMPro;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// タイトル画面のゲームバージョン表示（<c>m_verText</c>）の 1 行上に
/// MOD バージョンラベルを追加するパッチ。
///
/// <para>
/// <c>TitleScene.Start()</c> は <c>async void</c> だが、<c>m_verText.text</c> の設定は
/// 最初の <c>await</c> より前に行われるため、Harmony がスタブに対して打つ Postfix が
/// 発火する時点ではすでにバージョン文字列が確定している。
/// </para>
///
/// <para>
/// 新しいラベルは <c>m_verText</c> の RectTransform・フォント・サイズ・カラー・
/// アライメントをそのままコピーし、anchoredPosition を 1 行分（fontSize × 1.3）上にずらす。
/// ゲームの表示スタイルと統一されるため、フォントや色がゲームアップデートで変わっても
/// 自動的に追従する。
/// </para>
/// </summary>
[HarmonyPatch(typeof(TitleScene), "Start")]
public static class TitleSceneVersionPatch
{
    private static void Postfix(TitleScene __instance)
    {
        try
        {
            // private フィールド m_verText を取得
            var field = AccessTools.Field(typeof(TitleScene), "m_verText");
            if (field == null)
            {
                PatchLogger.LogWarning("[TitleVersion] フィールド m_verText が見つかりませんでした");
                return;
            }

            var verText = field.GetValue(__instance) as TextMeshProUGUI;
            if (verText == null)
            {
                PatchLogger.LogWarning("[TitleVersion] m_verText が null または TextMeshProUGUI 型ではありませんでした");
                return;
            }

            // m_verText と同じ親に MOD バージョンラベルを追加する
            var verRt = verText.rectTransform;
            var parent = verRt.parent;

            var go = new GameObject("BG2ModVersionText");
            go.transform.SetParent(parent, false);

            // RectTransform を m_verText に揃える
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = verRt.anchorMin;
            rt.anchorMax = verRt.anchorMax;
            rt.pivot = verRt.pivot;
            rt.sizeDelta = verRt.sizeDelta;

            // 1 行分上にずらす（fontSize × 1.3 は TMP デフォルト行ピッチの目安）
            float lineStep = verText.fontSize * 1.3f;
            rt.anchoredPosition = verRt.anchoredPosition + new Vector2(0f, lineStep);

            // TextMeshProUGUI をゲームバージョンと同スタイルで追加
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.font = verText.font;
            tmp.fontSize = verText.fontSize;
            tmp.color = verText.color;
            tmp.alignment = verText.alignment;
            tmp.text = $"Mod v{MyPluginInfo.PLUGIN_VERSION}";

            PatchLogger.LogInfo($"[TitleVersion] MOD バージョンラベルを追加しました: Mod v{MyPluginInfo.PLUGIN_VERSION}");
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[TitleVersion] MOD バージョンラベルの追加に失敗しました: {ex.Message}");
        }
    }
}
