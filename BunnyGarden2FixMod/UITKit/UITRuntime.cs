using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace UITKit;

/// <summary>
/// PanelSettings / UIDocument のランタイム生成。MOD は asset を同梱できないため、
/// ゲームがロード済みの PanelSettings からテーマを借用するのが最も確実。
/// themeStyleSheet の解決に失敗した場合は PanelSettings の ThemeStyleSheet が null
/// のままになり、Unity が "UI will not render properly" 警告を出す。
/// 呼び出し側（View）は返却値の themeStyleSheet を確認して log する責務を持つ。
/// </summary>
public static class UITRuntime
{
    public static PanelSettings CreatePanelSettings(int sortingOrder = 999)
    {
        var settings = ScriptableObject.CreateInstance<PanelSettings>();
        settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
        settings.referenceResolution = new Vector2Int(1920, 1080);
        settings.match = 0.5f;
        // ScaleWithScreenSize の上に乗る追加倍率。Configs.UIScale はユーザー設定（既定 1.0）。
        settings.scale = global::BunnyGarden2FixMod.Configs.UIScale.Value;
        settings.sortingOrder = sortingOrder;

        var existing = Resources.FindObjectsOfTypeAll<PanelSettings>()
            .FirstOrDefault(p => p != null && p != settings && p.themeStyleSheet != null);
        if (existing != null)
        {
            settings.themeStyleSheet = existing.themeStyleSheet;
            return settings;
        }

        var theme = Resources.FindObjectsOfTypeAll<ThemeStyleSheet>().FirstOrDefault();
        if (theme != null) settings.themeStyleSheet = theme;

        return settings;
    }

    public static UIDocument AttachDocument(GameObject host, PanelSettings settings)
    {
        var doc = host.AddComponent<UIDocument>();
        doc.panelSettings = settings;
        doc.visualTreeAsset = null;
        return doc;
    }

    /// <summary>
    /// ゲームがロード済みの Font から日本語対応っぽいものを 1 つ選ぶ。
    /// 優先: Noto Sans JP / NotoSans / JP / Japanese / CJK を名前に含む Font。
    /// 見つからなければ LegacyRuntime.ttf (fallback — 実測では UI Toolkit の
    /// dynamic font fallback で日本語描画に成功している)。
    /// </summary>
    public static Font ResolveJapaneseFont(out IReadOnlyList<string> allFontNames)
    {
        var all = Resources.FindObjectsOfTypeAll<Font>();
        allFontNames = all.Select(f => f != null ? f.name : "<null>").ToList();

        string[] prefer = { "NotoSansJP", "NotoSans JP", "NotoSans-JP", "Noto Sans JP", "Japanese", "CJK", " JP", "-JP", "_JP" };
        foreach (var p in prefer)
        {
            var hit = all.FirstOrDefault(f => f != null && f.name.IndexOf(p, System.StringComparison.OrdinalIgnoreCase) >= 0);
            if (hit != null) return hit;
        }

        var loose = all.FirstOrDefault(f => f != null && f.name.IndexOf("Noto", System.StringComparison.OrdinalIgnoreCase) >= 0);
        if (loose != null) return loose;

        return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
    }

    /// <summary>
    /// 他の PanelSettings 名と sortingOrder 一覧を返す（sortingOrder 衝突調査用）。
    /// </summary>
    public static IReadOnlyList<string> DumpOtherPanelSettings(PanelSettings exclude)
    {
        return Resources.FindObjectsOfTypeAll<PanelSettings>()
            .Where(p => p != null && p != exclude)
            .Select(p => $"{p.name}(sort={p.sortingOrder})")
            .ToList();
    }
}
