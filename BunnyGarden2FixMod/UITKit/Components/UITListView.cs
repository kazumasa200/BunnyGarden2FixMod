using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace UITKit.Components;

/// <summary>
/// ScrollView ベースのリスト。Rebuild で行を再構築、OnRowClicked で click 通知。
/// 現状モデルは labels の完全差し替え前提（uGUI 版の「同構造なら差分更新」最適化は未移植・YAGNI）。
/// </summary>
public class UITListView : VisualElement
{
    public event Action<int> OnRowClicked;

    public class RowModel
    {
        public string Label;
        public bool IsSelected;
        public bool IsCurrent;
        public bool IsLocked;
    }

    private ScrollView m_scroll;
    private Label m_empty;
    private Font m_font;

    public void Setup(Font font = null)
    {
        m_font = font;
        // panel 内の残り領域を ListView が占有する。他 flex item が肥大化して
        // 押し潰されないよう minHeight を確保（極小まで潰れる症状対策）。
        // overflow: Hidden で ListView の矩形外への行漏れを防ぐ（内包 ScrollView の
        // viewport は内側で別途クリップする）。
        style.flexGrow = 1;
        style.flexShrink = 1;
        style.minHeight = 120;
        style.overflow = Overflow.Hidden;

        m_scroll = new ScrollView(ScrollViewMode.Vertical);
        m_scroll.style.flexGrow = 1;
        m_scroll.verticalScrollerVisibility = ScrollerVisibility.Auto;
        m_scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
        // themeStyleSheet が null の環境だと ScrollView.OnScrollWheel が
        // ReadSingleLineHeight() (内部で theme 値を参照) で NullReferenceException を投げる。
        // mouseWheelScrollSize を明示すると theme fallback を経由せず安全。
        // 量 = 1 ホイールあたりのスクロール px。行高さ 1 行分 (30px) にすると直感的。
        m_scroll.mouseWheelScrollSize = UITTheme.Row.Height;
        Add(m_scroll);
    }

    public void Rebuild(IReadOnlyList<RowModel> rows)
    {
        if (m_scroll == null) return; // Setup 未呼び出し時のガード
        ClearEmpty();
        m_scroll.Clear();
        if (rows == null || rows.Count == 0) return;

        for (int i = 0; i < rows.Count; i++)
        {
            int captured = i;
            var row = new UITListRow();
            row.Setup(rows[i].Label, rows[i].IsSelected, rows[i].IsCurrent, rows[i].IsLocked, m_font);
            if (!rows[i].IsLocked)
            {
                row.RegisterCallback<ClickEvent>(_ => OnRowClicked?.Invoke(captured));
                row.RegisterCallback<MouseEnterEvent>(_ => row.SetHover(true));
                row.RegisterCallback<MouseLeaveEvent>(_ => row.SetHover(false));
            }
            m_scroll.Add(row);
        }
    }

    public void ShowEmpty(string message)
    {
        if (m_scroll == null) return; // Setup 未呼び出し時のガード
        ClearEmpty();
        m_scroll.Clear();
        m_empty = UITFactory.CreateLabel(message, 12, new Color(1f, 1f, 1f, 0.5f), m_font, TextAnchor.MiddleCenter);
        m_empty.style.height = 40;
        m_scroll.Add(m_empty);
    }

    private void ClearEmpty()
    {
        if (m_empty != null)
        {
            m_empty.RemoveFromHierarchy();
            m_empty = null;
        }
    }
}
