using BunnyGarden2FixMod.Utils;
using GB.Game;
using System;
using System.Collections.Generic;
using UITKit;
using UITKit.Components;
using UnityEngine;
using UnityEngine.UIElements;

namespace BunnyGarden2FixMod.Patches.CastOrderUI;

/// <summary>
/// UI Toolkit (UIDocument) ベースのキャスト出勤順ビュー。
/// CostumePickerView と同じUITKitテーマを共用する。
/// </summary>
public class CastOrderView : MonoBehaviour
{
    public class RowData
    {
        public int Index;           // 0-based 順序インデックス
        public CharID CharId;       // キャストID
        public bool IsSelected;     // 現在操作中に選択中か
    }

    public class RenderData
    {
        public IReadOnlyList<RowData> Rows;
        public int SelectedIndex;   // -1: 未選択
        public bool AllLocked;      // 順番固定チェックボックス状態
    }

    public event Action OnCloseClicked;

    public event Action OnAllLockToggled;  // true=全固定ON, false=解除

    private UIDocument m_doc;
    private PanelSettings m_settings;
    private VisualElement m_root;
    private VisualElement m_panel;
    private Label m_headerText;
    private VisualElement m_listContainer;
    private Font m_font;
    private List<VisualElement> m_rowElements = new();
    private VisualElement m_allLockCheckbox;

    public bool IsShown => m_panel != null && m_panel.style.display != DisplayStyle.None;

    /// <summary>現在のマウス座標が panel 矩形内かを判定する。</summary>
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
        // PanelSettings.scale は EnsureBuilt() 時の値を保持するため、開く度に Configs.UIScale を反映する。
        if (m_settings != null) m_settings.scale = Configs.UIScale.Value;
        m_panel.style.display = DisplayStyle.Flex;
        Render(data);
    }

    public void Hide()
    {
        if (m_panel != null) m_panel.style.display = DisplayStyle.None;
    }

    /// <summary>全固定チェックボックスの状態を個別更新する。</summary>
    public void SetAllLockCheckbox(bool locked)
    {
        if (m_allLockCheckbox == null) return;
        UpdateCheckboxVisual(m_allLockCheckbox, locked);
    }

    public void Render(RenderData data)
    {
        if (m_panel == null || data == null) return;

        // 既存行をクリア
        m_rowElements.Clear();
        while (m_listContainer.childCount > 0)
            m_listContainer.Remove(m_listContainer[0]);

        if (data.Rows == null || data.Rows.Count == 0)
        {
            var empty = UITFactory.CreateLabel("（表示できません）", 12, UITTheme.Text.Secondary, m_font, TextAnchor.MiddleCenter);
            empty.style.flexGrow = 1;
            m_listContainer.Add(empty);
            return;
        }

        foreach (var row in data.Rows)
        {
            var rowEl = BuildRow(row, data.SelectedIndex);
            m_listContainer.Add(rowEl);
            m_rowElements.Add(rowEl);
        }

        // 全固定チェックボックス更新
        if (m_allLockCheckbox != null)
            UpdateCheckboxVisual(m_allLockCheckbox, data.AllLocked);
    }

    private void UpdateCheckboxVisual(VisualElement checkbox, bool locked)
    {
        // 古い子要素をクリア（✓マーク）
        while (checkbox.childCount > 0)
            checkbox.Remove(checkbox[0]);

        if (locked)
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
    }

    private VisualElement BuildRow(RowData rowData, int selectedIndex)
    {
        var row = UITFactory.CreateRow();
        row.style.height = 28;
        row.style.marginBottom = 3;
        row.style.alignItems = Align.Center;
        row.style.backgroundColor = rowData.IsSelected ? UITTheme.Tab.ActiveFill : UITTheme.Tab.InactiveFill;
        SetBorderRadius(row, 4f);

        // 順序番号 (数字キー対応: "1." "2." ...)
        var orderLabel = UITFactory.CreateLabel(
            $"{rowData.Index + 1}.",
            12,
            rowData.IsSelected ? Color.white : UITTheme.Text.Accent,
            m_font,
            TextAnchor.MiddleRight);
        orderLabel.style.width = 28;
        row.Add(orderLabel);

        // キャスト名（クリック不可、表示のみ）
        var nameLabel = UITFactory.CreateLabel(
            rowData.CharId.ToString(),
            12,
            rowData.IsSelected ? Color.white : UITTheme.Text.Primary,
            m_font,
            TextAnchor.MiddleLeft);
        nameLabel.style.flexGrow = 1;
        row.Add(nameLabel);

        return row;
    }

    private void EnsureBuilt()
    {
        if (m_panel != null) return;

        m_font = UITRuntime.ResolveJapaneseFont(out var fontNames);
        PatchLogger.LogInfo($"[CastOrderView] UI Toolkit Font: {(m_font != null ? m_font.name : "<null>")}");

        m_settings = UITRuntime.CreatePanelSettings();
        if (m_settings.themeStyleSheet == null)
            PatchLogger.LogWarning("[CastOrderView] themeStyleSheet を解決できませんでした");
        else
            PatchLogger.LogInfo($"[CastOrderView] themeStyleSheet: {m_settings.themeStyleSheet.name}");

        m_doc = UITRuntime.AttachDocument(gameObject, m_settings);
        m_root = m_doc.rootVisualElement;
        m_root.style.flexGrow = 1;
        m_root.focusable = false;

        m_panel = UITFactory.CreatePanel();
        m_panel.style.position = Position.Absolute;
        m_panel.style.right = 16;
        m_panel.style.top = 20;
        m_panel.style.width = 300;
        m_panel.style.height = Length.Percent(50);
        m_panel.style.overflow = Overflow.Hidden;
        m_panel.style.paddingTop = 12;
        m_panel.style.paddingRight = 12;
        m_panel.style.paddingBottom = 10;
        m_panel.style.paddingLeft = 12;
        m_root.Add(m_panel);

        // Header: [キャスト出勤順]                    ×
        var headerRow = UITFactory.CreateRow();
        headerRow.style.height = 22;
        headerRow.style.marginBottom = 6;
        headerRow.style.flexShrink = 0;
        headerRow.style.alignItems = Align.Center;
        m_panel.Add(headerRow);

        m_headerText = UITFactory.CreateLabel("キャスト出勤順", 13, UITTheme.Text.Accent, m_font, TextAnchor.MiddleLeft);
        m_headerText.style.flexGrow = 1;
        headerRow.Add(m_headerText);

        // × 閉じるボタン
        var close = UITFactory.CreateButton("×", () => OnCloseClicked?.Invoke(), 16, m_font);
        close.style.width = 22;
        close.style.height = 22;
        close.style.paddingLeft = 0;
        close.style.paddingRight = 0;
        close.style.paddingTop = 0;
        close.style.paddingBottom = 0;
        close.style.flexShrink = 0;
        headerRow.Add(close);

        // リストコンテナ（ScrollView 内）
        var scrollView = new ScrollView(ScrollViewMode.Vertical)
        {
            style =
            {
                flexGrow = 1,
                marginBottom = 6,
            }
        };
        m_panel.Add(scrollView);

        m_listContainer = UITFactory.CreateColumn();
        m_listContainer.style.flexGrow = 1;
        scrollView.contentContainer.Add(m_listContainer);

        // Footer
        var footer = UITFactory.CreateColumn();
        footer.style.marginTop = 6;
        footer.style.flexShrink = 0;
        m_panel.Add(footer);

        // キー操作説明
        var key1 = new UITKeyCapRow();
        key1.Setup(new (string, string)[] { ("W", ""), ("S", "選択"), ("1-6", "入れ替え") }, m_font);
        key1.style.marginBottom = 4;
        footer.Add(key1);

        var key2 = new UITKeyCapRow();
        key2.Setup(new (string, string)[] { ("Esc", "閉じる") }, m_font);
        key2.style.marginBottom = 6;
        footer.Add(key2);

        // 順番固定チェックボックス行
        var lockRow = UITFactory.CreateRow();
        lockRow.style.alignItems = Align.Center;
        lockRow.style.height = 24;
        footer.Add(lockRow);

        m_allLockCheckbox = new VisualElement();
        m_allLockCheckbox.style.width = UITTheme.Checkbox.Size;
        m_allLockCheckbox.style.height = UITTheme.Checkbox.Size;
        m_allLockCheckbox.style.marginRight = 8;
        m_allLockCheckbox.style.flexShrink = 0;
        m_allLockCheckbox.style.alignItems = Align.Center;
        m_allLockCheckbox.style.justifyContent = Justify.Center;
        SetBorderRadius(m_allLockCheckbox, UITTheme.Checkbox.Radius);
        m_allLockCheckbox.style.backgroundColor = UITTheme.Checkbox.DefaultFill;
        SetBorderAll(m_allLockCheckbox, UITTheme.Checkbox.DefaultBorder, UITTheme.Checkbox.BorderWidth);

        // チェックボックスクリックで全固定トグル
        m_allLockCheckbox.RegisterCallback<ClickEvent>(_ =>
        {
            OnAllLockToggled?.Invoke();
        });
        lockRow.Add(m_allLockCheckbox);

        var lockLabel = UITFactory.CreateLabel(
            "順番を固定する",
            10, UITTheme.Text.Secondary, m_font, TextAnchor.MiddleLeft);
        lockLabel.style.flexGrow = 1;
        lockRow.Add(lockLabel);

        // 説明テキスト
        var note = UITFactory.CreateLabel(
            "固定中は数字キーでの入れ替えが無効になります",
            9, UITTheme.Text.Secondary, m_font, TextAnchor.UpperLeft);
        note.style.whiteSpace = WhiteSpace.Normal;
        note.style.marginTop = 4;
        footer.Add(note);

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
