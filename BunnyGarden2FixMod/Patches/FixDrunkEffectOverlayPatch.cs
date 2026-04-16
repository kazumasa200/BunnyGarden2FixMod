using BunnyGarden2FixMod.Utils;
using GB;
using HarmonyLib;
using System;
using System.Reflection;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// 酔いエフェクトの Filter_Drunk_Quarter スプライト4枚の
/// 境界ピクセルアーティファクトによる十字線を修正するパッチ。
///
/// Image コンポーネントを RawImage に置き換え、uvRect で
/// スプライトの境界1pxをトリミングして描画することで解決する。
/// </summary>
[HarmonyPatch(typeof(DrunkEffect), "Show")]
public static class FixDrunkEffectOverlayPatch
{
    private static bool s_fixed = false;

    private static readonly Type s_imageType    = Type.GetType("UnityEngine.UI.Image, UnityEngine.UI");
    private static readonly Type s_rawImageType = Type.GetType("UnityEngine.UI.RawImage, UnityEngine.UI");

    private static void Postfix(DrunkEffect __instance)
    {
        if (s_fixed) return;

        if (s_imageType == null || s_rawImageType == null)
        {
            PatchLogger.LogWarning("DrunkEffect: Image/RawImage 型が見つかりません");
            return;
        }

        FieldInfo field = typeof(DrunkEffect).GetField("m_drunkEffect",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null)
        {
            PatchLogger.LogWarning("DrunkEffect: m_drunkEffect フィールドが見つかりませんでした");
            return;
        }

        CanvasGroup canvasGroup = field.GetValue(__instance) as CanvasGroup;
        if (canvasGroup == null)
        {
            PatchLogger.LogWarning("DrunkEffect: CanvasGroup が null です");
            return;
        }

        const float inset = 1f; // UV削り量 (canvas単位で同量拡大して補正)

        int count = 0;
        for (int i = 0; i < canvasGroup.transform.childCount; i++)
        {
            Transform child = canvasGroup.transform.GetChild(i);
            RectTransform crt = child.GetComponent<RectTransform>();

            // 象限判定は ReplaceImageWithRawImage より前に行う
            bool isLeft   = crt != null && crt.offsetMin.x < 0;
            bool isBottom = crt != null && crt.offsetMin.y < 0;

            if (!ReplaceImageWithRawImage(child.gameObject, inset)) continue;

            if (crt != null)
            {
                // UV削り分だけ全方向に拡大
                crt.offsetMin -= new Vector2(inset, inset);
                crt.offsetMax += new Vector2(inset, inset);

                // 外側方向に1px追加シフト
                float shiftX = isLeft ? -1f : 1f;
                float shiftY = isBottom ? -1f : 1f;
                crt.offsetMin += new Vector2(shiftX, shiftY);
                crt.offsetMax += new Vector2(shiftX, shiftY);
            }
            count++;
        }

        PatchLogger.LogInfo($"DrunkEffect: {count}枚を RawImage に置き換えました");
        s_fixed = true;
    }

    private static bool ReplaceImageWithRawImage(GameObject go, float inset)
    {
        Component img = go.GetComponent(s_imageType);
        if (img == null) return false;

        // Image からスプライト・カラーを取得
        var spriteProp = s_imageType.GetProperty("sprite");
        var colorProp  = s_imageType.GetProperty("color");
        Sprite sprite  = spriteProp?.GetValue(img) as Sprite;
        Color color    = colorProp != null ? (Color)colorProp.GetValue(img) : Color.white;

        if (sprite == null)
        {
            PatchLogger.LogWarning($"DrunkEffect: [{go.name}] sprite が null のためスキップ");
            return false;
        }

        Texture2D tex  = sprite.texture;
        Rect texRect   = sprite.textureRect; // アトラス内のピクセル矩形

        // 境界をトリミングした UV 矩形を計算
        float u  = (texRect.x + inset) / tex.width;
        float v  = (texRect.y + inset) / tex.height;
        float uw = (texRect.width  - inset * 2f) / tex.width;
        float vh = (texRect.height - inset * 2f) / tex.height;
        Rect uvRect = new Rect(u, v, uw, vh);

        PatchLogger.LogInfo($"DrunkEffect: [{go.name}] uvRect={uvRect} (元 texRect={texRect})");

        // Image を即座に破棄してから RawImage を追加
        // (同一GameObject に Graphic は1つしか持てないため)
        UnityEngine.Object.DestroyImmediate(img);

        Component rawImg = go.AddComponent(s_rawImageType);
        if (rawImg == null)
        {
            PatchLogger.LogWarning($"DrunkEffect: [{go.name}] RawImage の追加に失敗しました");
            return false;
        }

        var texProp    = s_rawImageType.GetProperty("texture");
        var uvRectProp = s_rawImageType.GetProperty("uvRect");
        var rawColorProp = s_rawImageType.GetProperty("color");

        texProp?.SetValue(rawImg, tex);
        uvRectProp?.SetValue(rawImg, uvRect);
        rawColorProp?.SetValue(rawImg, color);

        return true;
    }
}
