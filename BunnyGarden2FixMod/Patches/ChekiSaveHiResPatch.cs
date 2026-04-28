using BunnyGarden2FixMod.ExSave;
using BunnyGarden2FixMod.Utils;
using GB.Game;
using HarmonyLib;
using System;
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
    private static AccessTools.FieldRef<GameData, GameData.ChekiData[]> s_chekiDataRef;

    public static string KeyFor(int slot) => $"cheki.hires.{slot}";

    /// <summary>
    /// 指定された <paramref name="gameData"/> のチェキスロット数を返す。
    /// <c>GameData.m_chekiData</c>（protected）の配列長を FieldRef 経由で取得するため、
    /// ゲーム本体が将来スロット数を変更してもコード修正なしに追従する。
    /// FieldRef 未初期化・インスタンス null・配列 null の場合は -1 を返す。
    /// </summary>
    public static int GetSlotCount(GameData gameData)
    {
        if (gameData == null || s_chekiDataRef == null) return -1;
        var arr = s_chekiDataRef(gameData);
        return arr?.Length ?? -1;
    }

    private static bool Prepare()
    {
        try
        {
            s_chekiDataRef = AccessTools.FieldRefAccess<GameData, GameData.ChekiData[]>("m_chekiData");
        }
        catch (Exception ex)
        {
            PatchLogger.LogError($"[ChekiSaveHiResPatch] FieldRef 初期化失敗、パッチ無効化: {ex.Message}");
            return false;
        }
        PatchLogger.LogInfo("[ChekiSaveHiResPatch] GameData.SaveCheki をパッチしました（ExSave 書込）");
        return true;
    }

    private static void Postfix(GameData __instance, int slot)
    {
        if (!Plugin.ConfigChekiHighResEnabled.Value)
        {
            // Config OFF: sidecar は使わない。残留があれば破棄。
            ChekiHiResSidecar.DestroyIfAny();
            return;
        }

        // slot 範囲外は受け付けない（異常系防御）。配列長は本体から動的取得。
        int slotCount = GetSlotCount(__instance);
        if (slotCount < 0)
        {
            PatchLogger.LogWarning($"[ChekiSaveHiResPatch] スロット数取得失敗、hi-res 保存をスキップ");
            ChekiHiResSidecar.DestroyIfAny();
            return;
        }
        if (slot < 0 || slot >= slotCount)
        {
            PatchLogger.LogWarning($"[ChekiSaveHiResPatch] slot 範囲外 slot={slot} max={slotCount}、hi-res 保存をスキップ");
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
                    // ConfigChekiJpgQuality は YAML range [1,100] により BepInEx が起動時に
                    // AcceptableValueRange でクランプ済みのため、ここで Mathf.Clamp は不要。
                    return ImageConversion.EncodeToJPG(tex, Plugin.ConfigChekiJpgQuality.Value);

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
