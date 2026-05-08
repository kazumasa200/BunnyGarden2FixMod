using GB.Game;
using System;
using System.Collections.Generic;
using UITKit;
using UITKit.Components;
using UnityEngine;
using UnityEngine.UIElements;

namespace BunnyGarden2FixMod.Patches.CostumeChanger.UI;

/// <summary>
/// CostumePickerView の Debug サブビュー (mesh / MagicaCloth inspector)。
/// SMR / MAGICA 2 つの内部サブタブを持ち、UITTabStrip で切替する。
/// SMR 行はクリックで toggle (Controller 側で MeshHighlighter.Highlight/Unhighlight 連動)、
/// MagicaCloth 行は read-only (click 購読なし)。
/// </summary>
public partial class CostumePickerView
{
    public class DebugData
    {
        public CharID CharId;
        public IReadOnlyList<string> SmrLabels;     // SMR.name
        public IReadOnlyList<string> SmrPaths;      // chara root 相対 path
        public IReadOnlyList<bool> SmrChecked;
        public IReadOnlyList<string> MagicaLabels;  // "path | clothType=..."
        public IReadOnlyList<CharID> VisibleCasts;
        public int VisibleCastSelectedIndex;
    }

    /// <summary>[D] ボタン押下: Picker → Debug ビュー遷移。</summary>
    public event Action OnDebugClicked;

    /// <summary>Debug: SMR 行クリック (引数 = 表示順 index)。</summary>
    public event Action<int> OnDebugSmrToggleClicked;

    /// <summary>Debug: CLEAR ALL ボタン。</summary>
    public event Action OnDebugClearAllClicked;

    private enum DebugSubTab
    { Smr = 0, Magica = 1 }

    // panel close → 再 open でも維持する。調査作業フロー上、SMR/MAGICA の選択を都度リセットされない方が自然。
    private DebugSubTab m_debugSubTab = DebugSubTab.Smr;

    private Button m_debugButton;             // [D]: ピッカー中のみ可視
    private VisualElement m_debugContent;
    private UITTabStrip m_debugSubTabStrip;
    private UITListView m_debugSmrListView;
    private UITListView m_debugMagicaListView;

    public void ShowDebug(DebugData data)
    {
        EnsureBuilt();
        ApplyUIScale();
        m_panel.style.display = DisplayStyle.Flex;
        SetMode(ViewMode.Debug);
        RenderDebug(data);
    }

    public void RenderDebug(DebugData data)
    {
        if (m_debugContent == null) return;
        UpdateNavState(data.VisibleCasts, data.VisibleCastSelectedIndex);

        int smrCount = data.SmrLabels?.Count ?? 0;
        int magicaCount = data.MagicaLabels?.Count ?? 0;

        // タブラベルに件数を埋め込む。Setup は内部で Clear するので click handler が二重登録されない。
        // 再 Setup 後に SetActive を呼ばないと active highlight が消えるので毎回付け直す。
        m_debugSubTabStrip.Setup(new[] { $"SMR ({smrCount})", $"MAGICA ({magicaCount})" }, m_font);
        m_debugSubTabStrip.SetActive((int)m_debugSubTab);

        // SMR 行 rebuild (IsCurrent をチェック状態として流用、Checkbox に ✓ が出る)
        if (smrCount == 0)
        {
            m_debugSmrListView.ShowEmpty("（対象キャラ未設定 / SMR なし）");
        }
        else
        {
            var rows = new List<UITListView.RowModel>(smrCount);
            for (int i = 0; i < smrCount; i++)
            {
                bool isChecked = data.SmrChecked != null && i < data.SmrChecked.Count && data.SmrChecked[i];
                string label = data.SmrLabels[i];
                string path = (data.SmrPaths != null && i < data.SmrPaths.Count) ? data.SmrPaths[i] : "";
                string text = string.IsNullOrEmpty(path) ? label : $"{label}  ({path})";
                rows.Add(new UITListView.RowModel
                {
                    Label = text,
                    IsSelected = false,
                    IsCurrent = isChecked,
                    IsLocked = false,
                });
            }
            m_debugSmrListView.Rebuild(rows);
        }

        // MagicaCloth 行 rebuild (全行 normal、click 購読なしで read-only)
        if (magicaCount == 0)
        {
            m_debugMagicaListView.ShowEmpty("（なし）");
        }
        else
        {
            var rows = new List<UITListView.RowModel>(magicaCount);
            for (int i = 0; i < magicaCount; i++)
            {
                rows.Add(new UITListView.RowModel
                {
                    Label = data.MagicaLabels[i],
                    IsSelected = false,
                    IsCurrent = false,
                    IsLocked = false,
                });
            }
            m_debugMagicaListView.Rebuild(rows);
        }

        ApplyDebugSubTabVisibility();
    }

    private void BuildDebugContent()
    {
        // サブタブ (SMR / MAGICA)。click はビュー内部のみで処理し Controller には伝えない。
        m_debugSubTabStrip = new UITTabStrip();
        m_debugSubTabStrip.Setup(new[] { "SMR", "MAGICA" }, m_font);
        m_debugSubTabStrip.SetActive((int)m_debugSubTab);
        m_debugSubTabStrip.style.marginBottom = 6;
        m_debugSubTabStrip.style.flexShrink = 0;
        m_debugSubTabStrip.OnTabClicked += HandleDebugSubTabClicked;
        m_debugContent.Add(m_debugSubTabStrip);

        m_debugSmrListView = new UITListView();
        m_debugSmrListView.Setup(m_font);
        m_debugSmrListView.style.flexGrow = 1;
        m_debugSmrListView.OnRowClicked += i => OnDebugSmrToggleClicked?.Invoke(i);
        m_debugContent.Add(m_debugSmrListView);

        m_debugMagicaListView = new UITListView();
        m_debugMagicaListView.Setup(m_font);
        m_debugMagicaListView.style.flexGrow = 1;
        m_debugContent.Add(m_debugMagicaListView);

        var clearAll = UITFactory.CreateButton("CLEAR ALL", () => OnDebugClearAllClicked?.Invoke(), 11, m_font);
        clearAll.style.marginTop = 6;
        clearAll.style.flexShrink = 0;
        m_debugContent.Add(clearAll);

        ApplyDebugSubTabVisibility();
    }

    private void HandleDebugSubTabClicked(int idx)
    {
        if (idx < 0 || idx > 1) return;
        m_debugSubTab = (DebugSubTab)idx;
        if (m_debugSubTabStrip != null) m_debugSubTabStrip.SetActive(idx);
        ApplyDebugSubTabVisibility();
    }

    private void ApplyDebugSubTabVisibility()
    {
        if (m_debugSmrListView != null)
            m_debugSmrListView.style.display = m_debugSubTab == DebugSubTab.Smr ? DisplayStyle.Flex : DisplayStyle.None;
        if (m_debugMagicaListView != null)
            m_debugMagicaListView.style.display = m_debugSubTab == DebugSubTab.Magica ? DisplayStyle.Flex : DisplayStyle.None;
    }

    /// <summary>[D] デバッグボタンの構築。BuildHeaderButtons (本体) から呼ばれる。</summary>
    private void BuildDebugButton()
    {
        m_debugButton = UITFactory.CreateButton("D", () => OnDebugClicked?.Invoke(), 12, m_font);
        m_debugButton.style.position = Position.Absolute;
        m_debugButton.style.right = 64;
        m_debugButton.style.top = 8;
        m_debugButton.style.width = 22;
        m_debugButton.style.height = 22;
        m_debugButton.style.paddingLeft = 0;
        m_debugButton.style.paddingRight = 0;
        m_debugButton.style.paddingTop = 0;
        m_debugButton.style.paddingBottom = 0;
        m_panel.Add(m_debugButton);
    }
}
