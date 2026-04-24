using BunnyGarden2FixMod.Utils;
using System;
using UITKit;
using UITKit.Components;
using UnityEngine;
using UnityEngine.UIElements;

namespace BunnyGarden2FixMod.Patches.HideMoneyUI;

/// <summary>
/// UI非表示設定パネルのビュー。
/// 所持金非表示・ボタンガイド非表示を 2 行のトグルで管理する。
/// </summary>
public class HideMoneyUIView : MonoBehaviour
{
    public class RenderData
    {
        public bool HideMoneyInSpecialScenes;
        public bool HideButtonGuide;
    }

    public event Action OnCloseClicked;

    public event Action OnToggleMoneyHide;

    public event Action OnToggleButtonGuide;

    private UIDocument m_doc;
    private PanelSettings m_settings;
    private VisualElement m_root;
    private VisualElement m_panel;
    private VisualElement m_listContainer;
    private Font m_font;

    public bool IsShown => m_panel != null && m_panel.style.display != DisplayStyle.None;

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

    /// <summary>
    /// UIDocument を事前構築する（Start コルーチンから呼ばれる）。
    /// </summary>
    public void TryPreBuild()
    {
        EnsureBuilt();
    }

    public void Show(RenderData data)
    {
        EnsureBuilt();
        if (m_panel == null) return;
        m_panel.style.display = DisplayStyle.Flex;
        Render(data);
    }

    public void Hide()
    {
        if (m_panel != null) m_panel.style.display = DisplayStyle.None;
    }

    public void Render(RenderData data)
    {
        if (m_panel == null || data == null) return;

        while (m_listContainer.childCount > 0)
            m_listContainer.Remove(m_listContainer[0]);

        // 行1: 所持金非表示
        var row1 = BuildSettingRow("旅行・ラストシーンで所持金を非表示", data.HideMoneyInSpecialScenes);
        row1.RegisterCallback<ClickEvent>(_ => OnToggleMoneyHide?.Invoke());
        m_listContainer.Add(row1);

        // 行2: ボタンガイド非表示
        var row2 = BuildSettingRow("ボタンガイドを非表示", data.HideButtonGuide);
        row2.RegisterCallback<ClickEvent>(_ => OnToggleButtonGuide?.Invoke());
        m_listContainer.Add(row2);
    }

    private VisualElement BuildSettingRow(string label, bool enabled)
    {
        var row = UITFactory.CreateRow();
        row.style.height = 32;
        row.style.marginBottom = 4;
        row.style.alignItems = Align.Center;
        row.style.backgroundColor = UITTheme.Tab.ActiveFill;
        SetBorderRadius(row, 4f);

        // チェックボックス
        var checkbox = new VisualElement();
        checkbox.style.width = UITTheme.Checkbox.Size;
        checkbox.style.height = UITTheme.Checkbox.Size;
        checkbox.style.marginRight = 10;
        checkbox.style.flexShrink = 0;
        checkbox.style.alignItems = Align.Center;
        checkbox.style.justifyContent = Justify.Center;
        SetBorderRadius(checkbox, UITTheme.Checkbox.Radius);

        if (enabled)
        {
            checkbox.style.backgroundColor = UITTheme.Checkbox.CheckedFill;
            SetBorderAll(checkbox, UITTheme.Checkbox.CheckedBorder, UITTheme.Checkbox.BorderWidth);
            var mark = UITFactory.CreateLabel("✓", 12, UITTheme.Checkbox.CheckedMark, m_font, TextAnchor.MiddleCenter);
            mark.style.paddingLeft = 0;
            mark.style.paddingRight = 0;
            mark.style.paddingTop = 0;
            mark.style.paddingBottom = 0;
            checkbox.Add(mark);
        }
        else
        {
            checkbox.style.backgroundColor = UITTheme.Checkbox.DefaultFill;
            SetBorderAll(checkbox, UITTheme.Checkbox.DefaultBorder, UITTheme.Checkbox.BorderWidth);
        }

        row.Add(checkbox);

        // ラベル
        var labelEl = UITFactory.CreateLabel(label, 12, Color.white, m_font, TextAnchor.MiddleLeft);
        labelEl.style.flexGrow = 1;
        row.Add(labelEl);

        // ON / OFF
        var status = UITFactory.CreateLabel(
            enabled ? "ON" : "OFF",
            11,
            enabled ? Color.green : UITTheme.Text.Secondary,
            m_font,
            TextAnchor.MiddleRight);
        status.style.width = 40;
        row.Add(status);

        return row;
    }

    private void EnsureBuilt()
    {
        if (m_panel != null) return;

        m_font = UITRuntime.ResolveJapaneseFont(out _);
        PatchLogger.LogInfo($"[GameUIHider] UI Toolkit Font: {(m_font != null ? m_font.name : "<null>")}");

        m_settings = UITRuntime.CreatePanelSettings();
        if (m_settings.themeStyleSheet == null)
            PatchLogger.LogWarning("[GameUIHider] themeStyleSheet を解決できませんでした");

        m_doc = UITRuntime.AttachDocument(gameObject, m_settings);
        m_root = m_doc.rootVisualElement;
        m_root.style.flexGrow = 1;
        m_root.focusable = false;

        m_panel = UITFactory.CreatePanel();
        m_panel.style.position = Position.Absolute;
        m_panel.style.right = 16;
        m_panel.style.top = 20;
        m_panel.style.width = 340;
        m_panel.style.height = 170;  // 2行分
        m_panel.style.overflow = Overflow.Hidden;
        m_panel.style.paddingTop = 12;
        m_panel.style.paddingRight = 12;
        m_panel.style.paddingBottom = 10;
        m_panel.style.paddingLeft = 12;
        m_root.Add(m_panel);

        // ── ヘッダー ──────────────────────────────────────────
        var headerRow = UITFactory.CreateRow();
        headerRow.style.height = 22;
        headerRow.style.marginBottom = 8;
        headerRow.style.flexShrink = 0;
        headerRow.style.alignItems = Align.Center;
        m_panel.Add(headerRow);

        var titleText = UITFactory.CreateLabel(
            "UI非表示設定", 13, UITTheme.Text.Accent, m_font, TextAnchor.MiddleLeft);
        titleText.style.flexGrow = 1;
        headerRow.Add(titleText);

        var closeBtn = UITFactory.CreateButton("×", () => OnCloseClicked?.Invoke(), 16, m_font);
        closeBtn.style.width = 22;
        closeBtn.style.height = 22;
        closeBtn.style.paddingLeft = 0;
        closeBtn.style.paddingRight = 0;
        closeBtn.style.paddingTop = 0;
        closeBtn.style.paddingBottom = 0;
        closeBtn.style.flexShrink = 0;
        headerRow.Add(closeBtn);

        // ── リスト ────────────────────────────────────────────
        var scrollView = new ScrollView(ScrollViewMode.Vertical)
        {
            style = { flexGrow = 1, marginBottom = 6 }
        };
        m_panel.Add(scrollView);

        m_listContainer = UITFactory.CreateColumn();
        m_listContainer.style.flexGrow = 1;
        scrollView.contentContainer.Add(m_listContainer);

        // ── フッター（キー説明）─────────────────────────────────
        var footerEl = UITFactory.CreateColumn();
        footerEl.style.marginTop = 6;
        footerEl.style.flexShrink = 0;
        m_panel.Add(footerEl);

        var keyRow = new UITKeyCapRow();
        keyRow.Setup(new (string, string)[] { ("Space", "トグル"), ("Esc", "閉じる") }, m_font);
        footerEl.Add(keyRow);

        m_panel.style.display = DisplayStyle.None;
    }

    private static void SetBorderRadius(VisualElement v, float r)
    {
        v.style.borderTopLeftRadius = r;
        v.style.borderTopRightRadius = r;
        v.style.borderBottomLeftRadius = r;
        v.style.borderBottomRightRadius = r;
    }

    private static void SetBorderAll(VisualElement v, Color color, float width)
    {
        v.style.borderTopColor = color;
        v.style.borderRightColor = color;
        v.style.borderBottomColor = color;
        v.style.borderLeftColor = color;
        v.style.borderTopWidth = width;
        v.style.borderRightWidth = width;
        v.style.borderBottomWidth = width;
        v.style.borderLeftWidth = width;
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
