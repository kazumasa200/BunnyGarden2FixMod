using System.Collections.Generic;
using BunnyGarden2FixMod.Patches;
using BunnyGarden2FixMod.Utils;
using GB;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace BunnyGarden2FixMod.Patches.Ultrawide;

/// <summary>
/// ウルトラワイド表示中、16:9 前提でレイアウトされた全画面系 UI を横方向へ引き伸ばす。
///
/// <para>対象は 2 種類:</para>
/// <list type="bullet">
///   <item>ScaleWithScreenSize の CanvasScaler — 基準解像度の幅を倍率分広げ、
///         Expand マッチングにすることで UI が画面全体へ行き渡るようにする</item>
///   <item>フェード・レターボックス等の全画面マスク画像 — 名前または「ほぼ黒」の色で
///         推定し、横スケールを倍率分広げて左右に元の 16:9 領域の縁が見えるのを防ぐ</item>
/// </list>
///
/// <para>
/// 変更した値は元の状態と対で記録し、ウルトラワイド条件が外れたフレームで全て巻き戻す。
/// 駆動は GBSystem.Update の Postfix（本体初期化完了かつフルスクリーン時のみ）。
/// </para>
/// </summary>
internal static class UltrawideUiScaler
{
    /// <summary>この横幅・縦幅以上の Rect を「全画面級」とみなす（16:9 基準解像度換算）。</summary>
    private const float FullscreenRectMinWidth = 1600f;
    private const float FullscreenRectMinHeight = 900f;

    /// <summary>「ほぼ黒」と判定する RGB 上限と、無視する透明度の下限。</summary>
    private const float NearBlackMaxChannel = 0.08f;
    private const float VisibleAlphaMin = 0.01f;

    private const float WidenLogIntervalSeconds = 5f;

    private static readonly Dictionary<CanvasScaler, ScalerSnapshot> s_scalerBackup = new();
    private static readonly Dictionary<RectTransform, Vector3> s_rectBackup = new();
    private static float s_nextWidenLogTime;

    private readonly struct ScalerSnapshot
    {
        internal readonly Vector2 Reference;
        internal readonly CanvasScaler.ScreenMatchMode Match;
        internal readonly float MatchWeight;

        internal ScalerSnapshot(CanvasScaler s)
        {
            Reference = s.referenceResolution;
            Match = s.screenMatchMode;
            MatchWeight = s.matchWidthOrHeight;
        }
    }

    internal static void Tick()
    {
        // Active の評価より先に設定だけを見る（OFF 勢に走査コストを払わせない）
        if (!Configs.FullscreenUltrawideEnabled.Value)
        {
            RestoreAll();
            return;
        }

        if (!UltrawideRuntime.Active)
        {
            RestoreAll();
            return;
        }

        float scale = UltrawideRuntime.WidthScale;
        WidenCanvasScalers(scale);
        WidenFullscreenMasks(scale);
    }

    private static void WidenCanvasScalers(float scale)
    {
        var scalers = Object.FindObjectsByType<CanvasScaler>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var scaler in scalers)
        {
            if (scaler == null)
                continue;
            if (scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize)
                continue;

            if (!s_scalerBackup.TryGetValue(scaler, out var snap))
            {
                snap = new ScalerSnapshot(scaler);
                s_scalerBackup[scaler] = snap;
            }

            scaler.referenceResolution = new Vector2(snap.Reference.x * scale, snap.Reference.y);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        }
    }

    private static void WidenFullscreenMasks(float scale)
    {
        bool logThisPass = Time.unscaledTime >= s_nextWidenLogTime;

        var graphics = Object.FindObjectsByType<Graphic>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var g in graphics)
        {
            if (g == null || !g.gameObject.activeInHierarchy)
                continue;

            var rect = g.rectTransform;
            if (!LooksLikeFullscreenMask(g, rect))
                continue;

            if (!s_rectBackup.TryGetValue(rect, out var baseScale))
            {
                baseScale = rect.localScale;
                s_rectBackup[rect] = baseScale;
            }

            rect.localScale = new Vector3(baseScale.x * scale, baseScale.y, baseScale.z);

            if (logThisPass)
                PatchLogger.LogInfo($"[Ultrawide] マスクを拡幅: {HierarchyPath(rect)} size={rect.rect.size} scale={rect.localScale}");
        }

        if (logThisPass)
            s_nextWidenLogTime = Time.unscaledTime + WidenLogIntervalSeconds;
    }

    /// <summary>
    /// 全画面フェード・レターボックスらしき Graphic かを推定する。
    /// 「全画面級の大きさ」かつ「名前がそれっぽい or 表示中のほぼ黒」で判定。
    /// </summary>
    private static bool LooksLikeFullscreenMask(Graphic g, RectTransform rect)
    {
        var size = rect.rect;
        if (size.width < FullscreenRectMinWidth || size.height < FullscreenRectMinHeight)
            return false;

        var lower = rect.name.ToLowerInvariant();
        if (lower.Contains("mask") || lower.Contains("fade") || lower.Contains("black")
            || lower.Contains("letter") || lower.Contains("cinematic") || lower.Contains("rawimage"))
        {
            return true;
        }

        var c = g.color;
        return c.a > VisibleAlphaMin
            && c.r < NearBlackMaxChannel
            && c.g < NearBlackMaxChannel
            && c.b < NearBlackMaxChannel;
    }

    private static void RestoreAll()
    {
        if (s_scalerBackup.Count > 0)
        {
            foreach (var pair in s_scalerBackup)
            {
                var scaler = pair.Key;
                if (scaler == null)
                    continue;
                scaler.referenceResolution = pair.Value.Reference;
                scaler.screenMatchMode = pair.Value.Match;
                scaler.matchWidthOrHeight = pair.Value.MatchWeight;
            }
            s_scalerBackup.Clear();
        }

        if (s_rectBackup.Count > 0)
        {
            foreach (var pair in s_rectBackup)
            {
                if (pair.Key != null)
                    pair.Key.localScale = pair.Value;
            }
            s_rectBackup.Clear();
        }
    }

    private static string HierarchyPath(Component c)
    {
        var node = c.transform;
        var chain = node.name;
        for (node = node.parent; node != null; node = node.parent)
            chain = node.name + "/" + chain;
        return chain;
    }
}

/// <summary>
/// UI 補正の駆動役。本体の初期化完了 (m_initialized) をリフレクションで確認してから
/// 毎フレーム <see cref="UltrawideUiScaler.Tick"/> を呼ぶ。
/// </summary>
[HarmonyPatch(typeof(GBSystem), "Update")]
internal static class UltrawideUiDriverPatch
{
    private static readonly System.Reflection.FieldInfo s_initializedField =
        AccessTools.Field(typeof(GBSystem), "m_initialized");

    private static void Postfix(GBSystem __instance)
    {
        // ウィンドウの中心保持は本体の初期化状態に依らないので先に処理する
        WindowCenterKeeper.Tick();

        // ウィンドウ（拡張解像度）でも動かす。有効判定は Tick 内の UltrawideRuntime.Active に任せる
        if (!IsSystemReady(__instance))
            return;

        // サイリウムの描画テクスチャはウルトラワイドの有無に関わらず画面へ追従させる
        UltrawidePsylliumFitter.Tick();
        UltrawideUiScaler.Tick();
    }

    /// <summary>m_initialized (ReactiveProperty&lt;bool&gt;) の Value を取り出す。</summary>
    private static bool IsSystemReady(GBSystem system)
    {
        var holder = s_initializedField?.GetValue(system);
        var valueProp = holder?.GetType().GetProperty("Value");
        return valueProp?.GetValue(holder) is bool ready && ready;
    }
}
