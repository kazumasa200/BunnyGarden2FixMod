using GB.Game;
using System;
using System.Collections.Generic;
using UITKit;
using UITKit.Components;
using UnityEngine;
using UnityEngine.UIElements;

namespace BunnyGarden2FixMod.Patches.CostumeChanger.UI;

/// <summary>
/// CostumePickerView の Picker サブビュー（COSTUME / PANTIES / STOCKING / BOTTOMS / TOPS の 5 タブ）。
/// 共通フィールド (m_panel, m_font, m_castNav 系) は本体ファイルに、タブ切替・行選択ロジックは本ファイルに局所化する。
/// </summary>
public partial class CostumePickerView
{
    public class RenderData
    {
        public CharID CharId;
        public WardrobeTab ActiveTab;
        public IReadOnlyList<string> CostumeLabels;
        public IReadOnlyList<string> PantiesLabels;
        public IReadOnlyList<string> StockingLabels;
        public IReadOnlyList<string> BottomsLabels;
        public IReadOnlyList<string> TopsLabels;
        public IReadOnlyList<bool> CostumeLocks;
        public IReadOnlyList<bool> PantiesLocks;
        public IReadOnlyList<bool> StockingLocks;
        public IReadOnlyList<bool> BottomsLocks;
        public IReadOnlyList<bool> TopsLocks;
        public int CostumeSelected;
        public int PantiesSelected;
        public int StockingSelected;
        public int BottomsSelected;
        public int TopsSelected;
        public int CostumeCurrent;
        public int PantiesCurrent;
        public int StockingCurrent;
        public int BottomsCurrent;
        public int TopsCurrent;

        /// <summary>現在表示中（Preload 済み + activeInHierarchy）のキャスト一覧。</summary>
        public IReadOnlyList<CharID> VisibleCasts;

        /// <summary>VisibleCasts の中で現在ピッカーが対象としているキャストのインデックス。</summary>
        public int VisibleCastSelectedIndex;
    }

    public event Action<int> OnTabClicked;

    public event Action<int> OnRowClicked;

    private VisualElement m_pickerContent;
    private UITTabStrip m_tabStrip;
    private UITListView m_listView;

    public void ShowPicker(RenderData data)
    {
        EnsureBuilt();
        ApplyUIScale();
        m_panel.style.display = DisplayStyle.Flex;
        SetMode(ViewMode.Picker);
        Render(data);
    }

    public void Render(RenderData data)
    {
        if (m_panel == null) return;
        UpdateNavState(data.VisibleCasts, data.VisibleCastSelectedIndex);
        m_tabStrip.SetActive((int)data.ActiveTab);
        m_tabStrip.SetBadges(new[] {
            data.CostumeCurrent >= 0,
            data.PantiesCurrent >= 0,
            data.StockingCurrent >= 0,
            data.BottomsCurrent >= 0,
            data.TopsCurrent >= 0,
        });

        var (labels, locks, selected, current) = data.ActiveTab switch
        {
            WardrobeTab.Panties => (data.PantiesLabels, data.PantiesLocks, data.PantiesSelected, data.PantiesCurrent),
            WardrobeTab.Stocking => (data.StockingLabels, data.StockingLocks, data.StockingSelected, data.StockingCurrent),
            WardrobeTab.Bottoms => (data.BottomsLabels, data.BottomsLocks, data.BottomsSelected, data.BottomsCurrent),
            WardrobeTab.Tops => (data.TopsLabels, data.TopsLocks, data.TopsSelected, data.TopsCurrent),
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

    private void BuildPickerContent()
    {
        m_tabStrip = new UITTabStrip();
        m_tabStrip.Setup(new[] { "衣装", "パンツ", "靴下", "下衣", "上衣" }, m_font);
        m_tabStrip.style.marginBottom = 6;
        m_tabStrip.style.flexShrink = 0;
        m_tabStrip.OnTabClicked += i => OnTabClicked?.Invoke(i);
        m_pickerContent.Add(m_tabStrip);

        m_listView = new UITListView();
        m_listView.Setup(m_font);
        m_listView.OnRowClicked += i => OnRowClicked?.Invoke(i);
        m_pickerContent.Add(m_listView);

        var footer = UITFactory.CreateColumn();
        footer.style.marginTop = 6;
        footer.style.flexShrink = 0;
        m_pickerContent.Add(footer);

        var key1 = new UITKeyCapRow();
        key1.Setup(new (string, string)[] { ("W", ""), ("S", "選択"), ("A", ""), ("D", "タブ") }, m_font);
        key1.style.marginBottom = 4;
        footer.Add(key1);

        var key2 = new UITKeyCapRow();
        key2.Setup(new (string, string)[] { ("Enter", "適用"), ("R", "Reset"), ("Esc", "閉じる") }, m_font);
        key2.style.marginBottom = 4;
        footer.Add(key2);

        var note = UITFactory.CreateLabel(
            "※ キーボード操作はカーソルがパネル上のときのみ有効",
            9, UITTheme.Text.Secondary, m_font, TextAnchor.UpperLeft);
        note.style.whiteSpace = WhiteSpace.Normal;
        footer.Add(note);

        var note2 = UITFactory.CreateLabel(
            "※ プラグイン有効後に、一度でも着用した衣装に切り替え可能",
            9, UITTheme.Text.Secondary, m_font, TextAnchor.UpperLeft);
        note2.style.whiteSpace = WhiteSpace.Normal;
        footer.Add(note2);
    }
}
