using System;
using BunnyGarden2FixMod.ExSave;
using BunnyGarden2FixMod.Utils;
using GB.Game;
using HarmonyLib;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// <see cref="GameData.SaveCheki"/> の Postfix で、
/// <see cref="ChekiHiResSidecar"/> に保管された高解像度 Texture2D を ExSave に書き込むパッチ。
///
/// <para>
/// フロー:
/// <list type="number">
///   <item><c>Saves.CaptureCheki</c>（<see cref="ChekiResolutionPatch"/> で差し替え）が sidecar に hi-res を保存</item>
///   <item><c>GBSystem.SaveCheki</c> が <c>UniTask.DelayFrame(1)</c> 後に <c>m_gameData.SaveCheki(slot, tex, ...)</c> を呼ぶ</item>
///   <item>vanilla の <c>ChekiData.Set</c> で 320x320 raw bytes が <c>m_rawData</c> にコピーされる</item>
///   <item>↑の直後、この Postfix が走り、sidecar の hi-res Texture2D を ExSave へ格納</item>
/// </list>
/// </para>
///
/// <para>
/// <b>ペイロード形式:</b> PNG または JPG エンコード済みバイト列。読み込み側は magic byte で自動判別する。
/// </para>
///
/// <para>
/// 鮮度チェック（<see cref="ChekiHiResSidecar.IsFresh"/>）で古い sidecar は破棄。
/// Config OFF のときや hi-res sidecar が無いとき（vanilla 経路）は何もしない。
/// </para>
/// </summary>
[HarmonyPatch(typeof(GameData), nameof(GameData.SaveCheki))]
public static class ChekiSaveHiResPatch
{
    /// <summary>
    /// チェキスロットの想定範囲（ゲーム本体で <c>new ChekiData[12]</c>）。
    ///
    /// <para>
    /// <b>注意:</b> この値はゲーム本体 <c>GameData.m_chekiData</c> の配列長と一致させる必要がある。
    /// ゲームアップデートで配列長が変わった場合はこの定数も合わせて更新すること。
    /// 動的取得（リフレクション等）は実装スコープ外のため、ハードコード値を採用している。
    /// </para>
    /// </summary>
    public const int MaxSlot = 12;

    public static string KeyFor(int slot) => $"cheki.hires.{slot}";

    static bool Prepare()
    {
        PatchLogger.LogInfo("[ChekiSaveHiResPatch] GameData.SaveCheki をパッチしました（ExSave 書込）");
        return true;
    }

    private static void Postfix(int slot)
    {
        if (!Plugin.ConfigChekiHighResEnabled.Value)
        {
            // Config OFF: sidecar は使わない。残留があれば破棄。
            ChekiHiResSidecar.DestroyIfAny();
            return;
        }

        // slot 範囲外は受け付けない（異常系防御）。
        if (slot < 0 || slot >= MaxSlot)
        {
            PatchLogger.LogWarning($"[ChekiSaveHiResPatch] slot 範囲外 slot={slot}、hi-res 保存をスキップ");
            ChekiHiResSidecar.DestroyIfAny();
            return;
        }

        Texture2D hiTex = ChekiHiResSidecar.TakeIfFresh(out int size);
        if (hiTex == null)
        {
            // このスロットには hi-res が無い（Config を途中 ON にした直後など）。
            // vanilla 320x320 のみが保存される経路。表示側はそちらにフォールバック。
            return;
        }

        try
        {
            byte[] payload = EncodePayload(hiTex);
            if (payload == null)
            {
                PatchLogger.LogWarning($"[ChekiSaveHiResPatch] エンコード失敗 slot={slot}、スキップ");
                return;
            }

            string key = KeyFor(slot);
            ExSaveStore.CurrentSession.Set(key, payload);
            PatchLogger.LogInfo($"[ChekiSaveHiResPatch] ExSave に格納: {key} ({size}x{size}, {Plugin.ConfigChekiFormat.Value}, {payload.Length} bytes)");
        }
        catch (Exception ex)
        {
            PatchLogger.LogError($"[ChekiSaveHiResPatch] ExSave 書込失敗 slot={slot}: {ex.Message}");
        }
        finally
        {
            // Texture2D は役目を終えたので確実に破棄。
            UnityEngine.Object.Destroy(hiTex);
        }
    }

    /// <summary>
    /// 設定された <see cref="Plugin.ConfigChekiFormat"/> に応じて Texture2D をバイト列にエンコードする。
    ///
    /// <para>
    /// 形式:
    /// <list type="bullet">
    ///   <item><b>PNG</b>: <c>ImageConversion.EncodeToPNG</c> 出力バイト列。先頭 4B が PNG シグネチャ <c>89 50 4E 47</c></item>
    ///   <item><b>JPG</b>: <c>ImageConversion.EncodeToJPG</c> 出力バイト列。先頭 3B が <c>FF D8 FF</c></item>
    /// </list>
    /// 読み込み側は magic byte で自動判別する。
    /// </para>
    /// </summary>
    private static byte[] EncodePayload(Texture2D tex)
    {
        var format = Plugin.ConfigChekiFormat.Value;
        try
        {
            switch (format)
            {
                case ChekiImageFormat.JPG:
                {
                    int quality = Mathf.Clamp(Plugin.ConfigChekiJpgQuality.Value, 1, 100);
                    return ImageConversion.EncodeToJPG(tex, quality);
                }

                case ChekiImageFormat.PNG:
                default:
                    return ImageConversion.EncodeToPNG(tex);
            }
        }
        catch (Exception ex)
        {
            PatchLogger.LogError($"[ChekiSaveHiResPatch] {format} エンコードで例外: {ex.Message}");
            return null;
        }
    }
}
