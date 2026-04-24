using BunnyGarden2FixMod.Utils;
using GB.Save;
using HarmonyLib;
using System.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// 最後に撮影した高解像度チェキ Texture2D を一時的に保持するサイドカー。
///
/// <para>
/// 本体の保存形式 (<c>ChekiData.m_rawData = new byte[409600]</c>) は 320x320 固定のため、
/// <see cref="Saves.m_cheki"/> は必ず 320x320 のまま維持する（互換維持の不変条件）。
/// 高解像度版はこの静的ホルダーに一時保管し、直後に走る
/// <see cref="GB.Game.GameData.SaveCheki"/> の Postfix（<see cref="ChekiSaveHiResPatch"/>）で
/// ExSave に書き出される。
/// </para>
///
/// <para>
/// <b>鮮度チェック:</b> 連続撮影・シーン遷移でコルーチンが中断された場合に古い hi-res が
/// 次スロットに誤って書き込まれたり、Destroy されず GPU リソースが漏れるのを防ぐため、
/// <see cref="CapturedFrame"/> と現在フレーム差が <see cref="FreshnessFrameWindow"/> を超えた
/// エントリは「古い」と見なして破棄する。
/// </para>
/// </summary>
internal static class ChekiHiResSidecar
{
    /// <summary>
    /// 撮影からどのくらいのフレーム数で使用（GameData.SaveCheki Postfix）が発生することを許容するか。
    /// 通常は WaitForEndOfFrame 1 回 + UniTask.DelayFrame(1) なので 2〜3 フレームで消費される想定。
    /// 10 フレーム（≒0.17 秒 @60fps）は、ユーザー操作（タップ→ダイアログ）より十分短く
    /// 別スロットへの誤混入を物理的に不可能にする安全窓。
    /// </summary>
    public const int FreshnessFrameWindow = 10;

    public static Texture2D LastCaptured;
    public static int CapturedFrame = int.MinValue;
    public static int CapturedSize;

    public static bool IsFresh()
    {
        if (LastCaptured == null) return false;
        return Time.frameCount - CapturedFrame <= FreshnessFrameWindow;
    }

    public static void Store(Texture2D tex, int size)
    {
        // 古い Texture2D が残っていれば破棄（漏れ防止）。
        DestroyIfAny();
        LastCaptured = tex;
        CapturedSize = size;
        CapturedFrame = Time.frameCount;
    }

    public static Texture2D TakeIfFresh(out int size)
    {
        size = 0;
        if (!IsFresh())
        {
            DestroyIfAny();
            return null;
        }
        Texture2D tex = LastCaptured;
        size = CapturedSize;
        LastCaptured = null;
        CapturedFrame = int.MinValue;
        CapturedSize = 0;
        return tex;
    }

    public static void DestroyIfAny()
    {
        if (LastCaptured != null)
        {
            Object.Destroy(LastCaptured);
            LastCaptured = null;
        }
        CapturedFrame = int.MinValue;
        CapturedSize = 0;
    }
}

/// <summary>
/// <see cref="Saves.CaptureCheki"/>（IEnumerator コルーチン）を差し替えて、
/// vanilla の 320x320 キャプチャに加えて高解像度版も別ホルダーに確保するパッチ。
///
/// <para>
/// <b>互換維持の不変条件:</b> <c>Saves.m_cheki</c> は必ず 320x320 のままにする。
/// ゲーム本体の <c>ChekiData.Set</c> が <c>tex.GetRawTextureData()</c> を呼び
/// <c>m_rawData = new byte[409600]</c> (= 320*320*4) 固定バッファへ <c>Array.Copy</c> するため、
/// サイズが変わると <c>ArgumentException</c> で進行停止する。
/// </para>
///
/// <para>
/// <b>設計:</b> Prefix で <c>__result</c> を独自 IEnumerator に差し替える。
/// コンパイラ生成のステートマシン本体（<c>&lt;CaptureCheki&gt;d__N.MoveNext</c>）には触れない。
/// 独自コルーチンでは:
/// <list type="number">
///   <item>vanilla 相当の 320x320 キャプチャを実施し <c>m_cheki</c> に格納</item>
///   <item>同一のキャプチャ元テクスチャから、別 RT に Blit して高解像度 Texture2D を生成</item>
///   <item>高解像度版を <see cref="ChekiHiResSidecar"/> に一時保管</item>
/// </list>
/// </para>
///
/// <para>
/// <see cref="Plugin.ConfigChekiHighResEnabled"/> が false のときは元処理をそのまま走らせる
/// （Prefix が true を返す → vanilla が実行される）。
/// </para>
/// </summary>
[HarmonyPatch(typeof(Saves), nameof(Saves.CaptureCheki))]
public static class ChekiResolutionPatch
{
    private const int VanillaSize = 320;
    private const int SizeLowerClamp = 64;
    private const int SizeUpperClamp = 2048;

    private static AccessTools.FieldRef<Saves, Texture2D> s_chekiRef;
    private static AccessTools.FieldRef<Saves, Texture2D> s_captureRef;
    private static AccessTools.FieldRef<Saves, RenderTexture> s_rtRef;
    private static bool s_loggedApplied;

    private static bool Prepare()
    {
        // FieldRefAccess は static initializer で例外を投げる可能性があるので Prepare で防御。
        // フィールドが存在しないゲームバージョンでは MOD ロードを壊さずこのパッチだけ無効化する。
        try
        {
            s_chekiRef = AccessTools.FieldRefAccess<Saves, Texture2D>("m_cheki");
            s_captureRef = AccessTools.FieldRefAccess<Saves, Texture2D>("m_capture");
            s_rtRef = AccessTools.FieldRefAccess<Saves, RenderTexture>("m_renderTexture");
        }
        catch (System.Exception ex)
        {
            PatchLogger.LogError($"[ChekiResolutionPatch] FieldRef 初期化失敗、パッチ無効化: {ex.Message}");
            return false;
        }
        PatchLogger.LogInfo("[ChekiResolutionPatch] Saves.CaptureCheki をパッチしました（デュアル解像度キャプチャ）");
        return true;
    }

    private static bool Prefix(Saves __instance, ref IEnumerator __result)
    {
        if (!Plugin.ConfigChekiHighResEnabled.Value)
        {
            // 既定 OFF: vanilla 挙動。ホルダーは空に戻しておく（古い hi-res が残っていると誤使用の元）。
            ChekiHiResSidecar.DestroyIfAny();
            return true;
        }
        __result = CaptureDualRes(__instance);
        return false;
    }

    private static IEnumerator CaptureDualRes(Saves self)
    {
        int hiSize = Mathf.Clamp(Plugin.ConfigChekiSize.Value, SizeLowerClamp, SizeUpperClamp);

        if (!s_loggedApplied)
        {
            PatchLogger.LogInfo($"[ChekiResolutionPatch] 適用: vanilla 320 + hi-res {hiSize}x{hiSize}");
            s_loggedApplied = true;
        }

        // m_cheki を 320x320 で再生成（互換維持の不変条件）。
        if (s_chekiRef(self) != null)
        {
            Object.Destroy(s_chekiRef(self));
            s_chekiRef(self) = null;
        }
        s_chekiRef(self) = new Texture2D(
            VanillaSize, VanillaSize,
            GraphicsFormat.R8G8B8A8_SRGB,
            0,
            TextureCreationFlags.None);

        // 直前の m_capture は解放。
        if (s_captureRef(self) != null)
        {
            Object.Destroy(s_captureRef(self));
            s_captureRef(self) = null;
        }

        // 既存 hi-res sidecar はここで解放（使用されずに再撮影されたケースに備える）。
        ChekiHiResSidecar.DestroyIfAny();

        yield return new WaitForEndOfFrame();

        // 画面の実アスペクト比でキャプチャを取得。
        s_captureRef(self) = ScreenCapture.CaptureScreenshotAsTexture();
        var capture = s_captureRef(self);
        float captureAspect = (float)capture.width / Mathf.Max(1, capture.height);

        // --- vanilla 側: 568x320 RT に Blit → 320x320 中央クロップで m_cheki に格納 ---
        int vanillaRtWidth = VanillaSize * 16 / 9;
        int vanillaRtHeight = VanillaSize;
        var rtVanilla = s_rtRef(self);
        if (rtVanilla == null || rtVanilla.width != vanillaRtWidth || rtVanilla.height != vanillaRtHeight)
        {
            if (rtVanilla != null)
            {
                rtVanilla.Release();
                Object.Destroy(rtVanilla);
            }
            s_rtRef(self) = new RenderTexture(
                vanillaRtWidth, vanillaRtHeight,
                24,
                GraphicsFormat.R8G8B8A8_UNorm);
        }
        Graphics.Blit(capture, s_rtRef(self));

        var prevActive = RenderTexture.active;
        try
        {
            RenderTexture.active = s_rtRef(self);
            int srcX = Mathf.Max(0, (vanillaRtWidth - VanillaSize) / 2);
            s_chekiRef(self).ReadPixels(
                new Rect(srcX, 0f, VanillaSize, VanillaSize),
                0, 0,
                recalculateMipMaps: false);
            s_chekiRef(self).Apply();
        }
        finally
        {
            RenderTexture.active = prevActive;
        }

        // --- 高解像度側: 専用 RT を確保し、そこから正方形クロップ ---
        int hiRtWidth = Mathf.Max(hiSize, Mathf.RoundToInt(hiSize * captureAspect));
        int hiRtHeight = hiSize;

        // vanilla RT とは別インスタンス。毎回作って毎回破棄（撮影頻度は低いのでコスト許容）。
        RenderTexture hiRT = new RenderTexture(hiRtWidth, hiRtHeight, 24, GraphicsFormat.R8G8B8A8_UNorm);
        Texture2D hiTex = null;
        try
        {
            Graphics.Blit(capture, hiRT);
            hiTex = new Texture2D(hiSize, hiSize, GraphicsFormat.R8G8B8A8_SRGB, 0, TextureCreationFlags.None);

            var prev = RenderTexture.active;
            try
            {
                RenderTexture.active = hiRT;
                int hiSrcX = Mathf.Max(0, (hiRtWidth - hiSize) / 2);
                hiTex.ReadPixels(
                    new Rect(hiSrcX, 0f, hiSize, hiSize),
                    0, 0,
                    recalculateMipMaps: false);
                hiTex.Apply();
            }
            finally
            {
                RenderTexture.active = prev;
            }

            ChekiHiResSidecar.Store(hiTex, hiSize);
            hiTex = null; // ownership transferred to sidecar
        }
        finally
        {
            if (hiTex != null)
            {
                Object.Destroy(hiTex);
            }
            hiRT.Release();
            Object.Destroy(hiRT);
        }
    }
}
