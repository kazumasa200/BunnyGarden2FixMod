using BunnyGarden2FixMod.ExSave;
using BunnyGarden2FixMod.Utils;
using GB.Game;
using GB.ListSelector;
using HarmonyLib;
using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.UI;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// <see cref="ChekiItem.SetupCheki"/> の Postfix で、ExSave に高解像度版があれば
/// <c>m_chekiTexture</c> / <c>m_chekiSprite</c> を差し替えるパッチ。
///
/// <para>
/// <b>フォールバック:</b>
/// <list type="bullet">
///   <item><see cref="Plugin.ConfigChekiHighResEnabled"/> が false → 何もしない（vanilla 320）</item>
///   <item><c>gameData.IsChekiValid(slot)</c> が false → 何もしない（vanilla の鍵アイコン表示）</item>
///   <item>ExSave に該当エントリが無い → 何もしない（vanilla 320）</item>
///   <item>ペイロード破損（未対応 magic / LoadImage 失敗 / サイズ範囲外）→ 何もしない（警告ログのみ）</item>
/// </list>
/// </para>
///
/// <para>
/// vanilla の 320 が先に貼られているので、差し替え時は旧 Texture2D/Sprite を Destroy してから
/// 新規を <c>m_chekiImg.sprite</c> に差し込む。<c>ChekiItem.PurgeTexture</c> が再撮影時に
/// 旧テクスチャを破棄してくれる構造なので、その後の整合性も保たれる。
/// </para>
/// </summary>
[HarmonyPatch(typeof(ChekiItem), nameof(ChekiItem.SetupCheki))]
public static class ChekiItemLoadHiResPatch
{
    internal const int SizeMin = 64;
    internal const int SizeMax = 2048;

    private static AccessTools.FieldRef<ChekiItem, Texture2D> s_textureRef;
    private static AccessTools.FieldRef<ChekiItem, Sprite> s_spriteRef;
    private static AccessTools.FieldRef<ChekiItem, Image> s_imgRef;

    private static bool Prepare()
    {
        try
        {
            s_textureRef = AccessTools.FieldRefAccess<ChekiItem, Texture2D>("m_chekiTexture");
            s_spriteRef = AccessTools.FieldRefAccess<ChekiItem, Sprite>("m_chekiSprite");
            s_imgRef = AccessTools.FieldRefAccess<ChekiItem, Image>("m_chekiImg");
        }
        catch (Exception ex)
        {
            PatchLogger.LogError($"[ChekiItemLoadHiResPatch] FieldRef 初期化失敗、パッチ無効化: {ex.Message}");
            return false;
        }
        PatchLogger.LogInfo("[ChekiItemLoadHiResPatch] ChekiItem.SetupCheki をパッチしました（ExSave からの hi-res 差し替え）");
        return true;
    }

    private static void Postfix(ChekiItem __instance, GameData gameData, int slot)
    {
        if (!Plugin.ConfigChekiHighResEnabled.Value) return;
        if (gameData == null) return;

        // slot 範囲外は受け付けない（異常系 / 悪意のある .exmod 防御）。
        // 配列長は ChekiSaveHiResPatch の FieldRef 経由で本体から動的取得。
        int slotCount = ChekiSaveHiResPatch.GetSlotCount(gameData);
        if (slotCount < 0 || slot < 0 || slot >= slotCount) return;

        string key = ChekiSaveHiResPatch.KeyFor(slot);

        // スロットが無効化されている場合は ExSave 側の残留エントリを掃除する（ディスク肥大防止）。
        if (!gameData.IsChekiValid(slot))
        {
            if (ExSaveStore.CurrentSession.Remove(key))
            {
                PatchLogger.LogInfo($"[ChekiItemLoadHiResPatch] 無効スロットの ExSave エントリを削除: {key}");
            }
            return;
        }

        if (!ExSaveStore.CurrentSession.TryGet(key, out byte[] payload) || payload == null || payload.Length < 4)
        {
            return;
        }

        Texture2D newTex = null;
        Sprite newSprite = null;
        try
        {
            newTex = DecodePayload(payload, slot);
            if (newTex == null) return;

            int w = newTex.width;
            int h = newTex.height;
            newSprite = Sprite.Create(newTex, new Rect(0f, 0f, w, h), Vector2.zero);

            // 既存 (vanilla 320) のテクスチャ・スプライトを破棄して差し替え。
            var oldTex = s_textureRef(__instance);
            var oldSprite = s_spriteRef(__instance);
            if (oldTex != null) UnityEngine.Object.Destroy(oldTex);
            if (oldSprite != null) UnityEngine.Object.Destroy(oldSprite);

            s_textureRef(__instance) = newTex;
            s_spriteRef(__instance) = newSprite;
            s_imgRef(__instance).sprite = newSprite;

            // 所有権移譲、finally での破棄対象から外す。
            newTex = null;
            newSprite = null;
        }
        catch (Exception ex)
        {
            PatchLogger.LogError($"[ChekiItemLoadHiResPatch] 差し替え失敗 slot={slot}: {ex.Message}");
        }
        finally
        {
            if (newTex != null) UnityEngine.Object.Destroy(newTex);
            if (newSprite != null) UnityEngine.Object.Destroy(newSprite);
        }
    }

    /// <summary>
    /// payload を magic byte で自動判別してデコードする。読み込み失敗・検証エラー時は null を返し、
    /// 呼び出し側は vanilla 320 にフォールバックする。成功時は呼び出し側が <c>Destroy</c> 責任を持つ。
    ///
    /// <para>
    /// <paramref name="logPrefix"/> は警告ログのプレフィックス（呼び出し元のパッチ名等）。
    /// 他パッチ（<c>EndingChekiSlideshowHiResPatch</c> 等）からも再利用できるよう internal で公開。
    /// </para>
    ///
    /// <list type="bullet">
    ///   <item>先頭 <c>89 50 4E 47</c> (PNG) → <c>ImageConversion.LoadImage</c></item>
    ///   <item>先頭 <c>FF D8 FF</c> (JPG) → <c>ImageConversion.LoadImage</c></item>
    ///   <item>それ以外 → 未対応フォーマット、警告ログ＋null 返却（vanilla 320 にフォールバック）</item>
    /// </list>
    /// </summary>
    internal static Texture2D DecodePayload(byte[] payload, int slot, string logPrefix = "[ChekiItemLoadHiResPatch]")
    {
        // PNG magic: 89 50 4E 47
        bool isPng = payload.Length >= 4
                     && payload[0] == 0x89 && payload[1] == 0x50
                     && payload[2] == 0x4E && payload[3] == 0x47;
        // JPG magic: FF D8 FF
        bool isJpg = payload.Length >= 3
                     && payload[0] == 0xFF && payload[1] == 0xD8 && payload[2] == 0xFF;

        if (!isPng && !isJpg)
        {
            PatchLogger.LogWarning($"{logPrefix} 未対応フォーマット slot={slot}、vanilla 320 にフォールバック");
            return null;
        }

        // LoadImage は target Texture2D のサイズを自動的にリサイズする。
        // 作成時のサイズ・フォーマットは placeholder 扱い。
        var tex = new Texture2D(2, 2, GraphicsFormat.R8G8B8A8_SRGB, 0, TextureCreationFlags.None);
        bool ok;
        try
        {
            ok = ImageConversion.LoadImage(tex, payload, markNonReadable: false);
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"{logPrefix} LoadImage 例外 slot={slot}: {ex.Message}");
            UnityEngine.Object.Destroy(tex);
            return null;
        }
        if (!ok)
        {
            PatchLogger.LogWarning($"{logPrefix} LoadImage 失敗 slot={slot}");
            UnityEngine.Object.Destroy(tex);
            return null;
        }
        // 破損 / 攻撃的 payload に備え、サイズを検証（正方形・範囲内）。
        if (tex.width != tex.height || tex.width < SizeMin || tex.width > SizeMax)
        {
            PatchLogger.LogWarning($"{logPrefix} デコード後サイズ不正 slot={slot} {tex.width}x{tex.height}、スキップ");
            UnityEngine.Object.Destroy(tex);
            return null;
        }
        return tex;
    }
}
