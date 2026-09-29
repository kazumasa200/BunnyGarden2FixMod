using BunnyGarden2FixMod.Utils;
using GB.Bar.MiniGame;
using HarmonyLib;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.Ultrawide;

/// <summary>
/// カラオケのリザルト背景（直前フレームのキャプチャ）を、撮影したままの比率で表示する。
///
/// <para>
/// CameraCapture は画面と同じ大きさの RenderTexture に撮るため、ウルトラワイド中の
/// キャプチャは横長になる。一方、受け皿の RawImage はプレハブで 16:9 に固定されているので、
/// そのまま差すと横方向に潰れて表示される。テクスチャを差した直後に RawImage の幅を
/// 「高さ × テクスチャの比率」へ揃える。16:9 のキャプチャなら比率が一致するので何もしない。
/// </para>
/// </summary>
[HarmonyPatch(typeof(BackGround), nameof(BackGround.SetTexture))]
internal static class KaraokeResultCapturePatch
{
    private static void Postfix(BackGround __instance, Texture2D texture2D)
    {
        var image = __instance.m_image;
        if (image == null || texture2D == null || texture2D.height <= 0)
            return;

        var rt = image.rectTransform;
        var rect = rt.rect;
        if (rect.height <= 0f)
            return;

        float textureAspect = (float)texture2D.width / texture2D.height;
        float rectAspect = rect.width / rect.height;
        if (Mathf.Abs(textureAspect - rectAspect) < 0.01f)
            return;

        // アンカー設定に依らず横幅だけを変える（高さと位置はプレハブのまま）
        rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, rect.height * textureAspect);
        PatchLogger.LogInfo(
            $"[Ultrawide] カラオケのリザルト背景を撮影比率に合わせました: {texture2D.width}x{texture2D.height} → 幅 {rect.height * textureAspect:F0}");
    }
}
