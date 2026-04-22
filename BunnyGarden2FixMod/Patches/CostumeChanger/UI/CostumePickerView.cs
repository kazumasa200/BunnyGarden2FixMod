using System;
using System.Collections.Generic;
using BunnyGarden2FixMod.Utils;
using GB.Game;
using UITKit;
using UITKit.Components;
using UnityEngine;
using UnityEngine.UIElements;

namespace BunnyGarden2FixMod.Patches.CostumeChanger.UI;

/// <summary>
/// UI Toolkit (UIDocument) ベースの Wardrobe ビュー。
/// Controller はこの public API (Show/Hide/Render/IsShown + 3 events + RenderData + WardrobeTab)
/// のみに依存する前提。UGuiKit には依存しない。
/// </summary>
public class CostumePickerView : MonoBehaviour
{
    public enum WardrobeTab { Costume = 0, Panties = 1, Stocking = 2 }

    public class RenderData
    {
        public CharID CharId;
        public WardrobeTab ActiveTab;
        public IReadOnlyList<string> CostumeLabels;
        public IReadOnlyList<string> PantiesLabels;
        public IReadOnlyList<string> StockingLabels;
        public IReadOnlyList<bool> CostumeLocks;
        public IReadOnlyList<bool> PantiesLocks;
        public IReadOnlyList<bool> StockingLocks;
        public int CostumeSelected;
        public int PantiesSelected;
        public int StockingSelected;
        public int CostumeCurrent;
        public int PantiesCurrent;
        public int StockingCurrent;
    }

    public event Action<int> OnTabClicked;
    public event Action<int> OnRowClicked;
    public event Action OnCloseClicked;

    private UIDocument m_doc;
    private PanelSettings m_settings;
    private VisualElement m_root;         // UIDocument.rootVisualElement
    private VisualElement m_panel;        // 角丸パネル（サイド固定）
    private Label m_headerText;
    private UITTabStrip m_tabStrip;
    private UITListView m_listView;
    private Font m_font;

    public bool IsShown => m_panel != null && m_panel.style.display != DisplayStyle.None;

    /// <summary>
    /// 現在のマウス座標が panel 矩形内かを判定する。
    /// Mouse.current.position は bottom-left origin、UI Toolkit panel は top-left origin。
    /// 実機で RuntimePanelUtils.ScreenToPanel が Y 反転しない挙動が観測されたので、
    /// ScreenToPanel に入れる前に手動で Y 反転する。
    /// </summary>
    public bool IsPointerOverPanel()
    {
        if (!IsShown || m_panel == null) return false;
        if (m_root == null || m_root.panel == null) return false;
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse == null) return false;
        var raw = mouse.position.ReadValue();
        var flipped = new Vector2(raw.x, Screen.height - raw.y);
        var panelPos = RuntimePanelUtils.ScreenToPanel(m_root.panel, flipped);
        return m_panel.worldBound.Contains(panelPos);
    }

    public void Show(RenderData data)
    {
        EnsureBuilt();
        m_panel.style.display = DisplayStyle.Flex;
        Render(data);
    }

    public void Hide()
    {
        if (m_panel != null) m_panel.style.display = DisplayStyle.None;
    }

    public void Render(RenderData data)
    {
        if (m_panel == null) return;
        m_headerText.text = $"WARDROBE — {data.CharId}";
        m_tabStrip.SetActive((int)data.ActiveTab);

        var (labels, locks, selected, current) = data.ActiveTab switch
        {
            WardrobeTab.Panties => (data.PantiesLabels, data.PantiesLocks, data.PantiesSelected, data.PantiesCurrent),
            WardrobeTab.Stocking => (data.StockingLabels, data.StockingLocks, data.StockingSelected, data.StockingCurrent),
            _ => (data.CostumeLabels, data.CostumeLocks, data.CostumeSelected, data.CostumeCurrent),
        };

        if (labels == null || labels.Count == 0)
        {
            m_listView.ShowEmpty("（履歴なし）");
            return;
        }

        var rows = new List<UITListView.RowModel>(labels.Count);
        for (int i = 0; i < labels.Count; i++)
        {
            rows.Add(new UITListView.RowModel
            {
                Label = labels[i],
                IsSelected = i == selected,
                IsCurrent = i == current,
                IsLocked = locks != null && i < locks.Count && locks[i],
            });
        }
        m_listView.Rebuild(rows);
    }

    private void EnsureBuilt()
    {
        if (m_panel != null) return;

        m_font = UITRuntime.ResolveJapaneseFont(out var fontNames);
        PatchLogger.LogInfo($"[CostumePicker] UI Toolkit Font 候補: {string.Join(", ", fontNames)}");
        PatchLogger.LogInfo($"[CostumePicker] 選択 Font: {(m_font != null ? m_font.name : "<null>")}");
        if (m_font == null || m_font.name.StartsWith("LegacyRuntime", StringComparison.OrdinalIgnoreCase))
        {
            // LegacyRuntime.ttf 自体は ASCII のみだが、UI Toolkit の dynamic font fallback で
            // 日本語描画に成功している実測例があるため、ここは警告だけに留める。
            PatchLogger.LogWarning("[CostumePicker] 日本語対応 Font が見つかりませんでした（LegacyRuntime fallback を使用）");
        }

        m_settings = UITRuntime.CreatePanelSettings();
        var otherPanels = UITRuntime.DumpOtherPanelSettings(m_settings);
        PatchLogger.LogInfo($"[CostumePicker] 既存 PanelSettings: {(otherPanels.Count == 0 ? "<none>" : string.Join(", ", otherPanels))}");
        if (m_settings.themeStyleSheet == null)
        {
            PatchLogger.LogWarning("[CostumePicker] themeStyleSheet を解決できませんでした — UI が描画されない可能性があります");
        }
        else
        {
            PatchLogger.LogInfo($"[CostumePicker] themeStyleSheet 借用: {m_settings.themeStyleSheet.name}");
        }

        m_doc = UITRuntime.AttachDocument(gameObject, m_settings);
        m_root = m_doc.rootVisualElement;
        m_root.style.flexGrow = 1;
        m_root.focusable = false;

        // サイドパネル root。縦幅は画面高さの 50% に収め、アイテムが多い時は
        // ListView (ScrollView 内包) 側で縦スクロールが自動表示される。
        // UI Toolkit の VisualElement はデフォルト overflow:visible なので、明示的に
        // Hidden にしないと flex で収まらない子がパネル境界を突き抜ける。
        m_panel = UITFactory.CreatePanel();
        m_panel.style.position = Position.Absolute;
        m_panel.style.right = 16;
        m_panel.style.top = 20;
        m_panel.style.width = 280;
        m_panel.style.height = Length.Percent(50);
        m_panel.style.overflow = Overflow.Hidden;
        m_panel.style.paddingTop = 12;
        m_panel.style.paddingRight = 12;
        m_panel.style.paddingBottom = 10;
        m_panel.style.paddingLeft = 12;
        m_root.Add(m_panel);

        // Header（固定高、flex 計算で縮まない）
        m_headerText = UITFactory.CreateLabel("WARDROBE", 13, UITTheme.Text.Accent, m_font, TextAnchor.MiddleLeft);
        m_headerText.style.height = 22;
        m_headerText.style.marginBottom = 6;
        m_headerText.style.flexShrink = 0;
        m_panel.Add(m_headerText);

        // TabStrip（タブボタンの高さ分のみ使う固定要素）
        m_tabStrip = new UITTabStrip();
        m_tabStrip.Setup(new[] { "COSTUME", "PANTIES", "STOCKING" }, m_font);
        m_tabStrip.style.marginBottom = 6;
        m_tabStrip.style.flexShrink = 0;
        m_tabStrip.OnTabClicked += i => OnTabClicked?.Invoke(i);
        m_panel.Add(m_tabStrip);

        // List (flex-grow: 1 で残りを使い切る)
        m_listView = new UITListView();
        m_listView.Setup(m_font);
        m_listView.OnRowClicked += i => OnRowClicked?.Invoke(i);
        m_panel.Add(m_listView);

        // Footer（note が wrap すると高さが伸びるが、flex 計算で ListView を
        // 押し潰さないよう flexShrink:0 で自分のコンテンツ高さを保つ）
        var footer = UITFactory.CreateColumn();
        footer.style.marginTop = 6;
        footer.style.flexShrink = 0;
        m_panel.Add(footer);

        var key1 = new UITKeyCapRow();
        key1.Setup(new (string, string)[] { ("W", ""), ("S", "選択"), ("A", ""), ("D", "タブ") }, m_font);
        key1.style.marginBottom = 4;
        footer.Add(key1);

        var key2 = new UITKeyCapRow();
        key2.Setup(new (string, string)[] { ("Enter", "適用"), ("R", "Reset"), ("Esc", "閉じる") }, m_font);
        key2.style.marginBottom = 4;
        footer.Add(key2);

        var note = UITFactory.CreateLabel(
            "※ 行クリック or Enter で適用/解除",
            9, UITTheme.Text.Secondary, m_font, TextAnchor.UpperLeft);
        note.style.whiteSpace = WhiteSpace.Normal;
        footer.Add(note);

        // × close button（絶対配置）
        var close = UITFactory.CreateButton("×", () => OnCloseClicked?.Invoke(), 16, m_font);
        close.style.position = Position.Absolute;
        close.style.right = 8;
        close.style.top = 8;
        close.style.width = 22;
        close.style.height = 22;
        close.style.paddingLeft = 0;
        close.style.paddingRight = 0;
        close.style.paddingTop = 0;
        close.style.paddingBottom = 0;
        m_panel.Add(close);

        m_panel.style.display = DisplayStyle.None;
    }

    private void OnDestroy()
    {
        if (m_settings != null)
        {
            Destroy(m_settings);
            m_settings = null;
        }
    }
}
