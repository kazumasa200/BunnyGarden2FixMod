using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace UITKit.Components;

/// <summary>
/// 横並びタブ。子は均等幅 (flex-grow:1)。SetActive で見た目切替、OnTabClicked で click 通知。
/// </summary>
public class UITTabStrip : VisualElement
{
    public event Action<int> OnTabClicked;

    private readonly System.Collections.Generic.List<VisualElement> m_tabs = new();
    private readonly System.Collections.Generic.List<VisualElement> m_dots = new();
    private int m_active = -1;

    public void Setup(string[] labels, Font font = null)
    {
        Clear();
        m_tabs.Clear();
        m_dots.Clear();
        style.flexDirection = FlexDirection.Row;
        style.height = 26;

        for (int i = 0; i < labels.Length; i++)
        {
            int captured = i;
            var tab = new VisualElement();
            tab.style.flexGrow = 1;
            tab.style.flexBasis = 0;
            tab.style.marginRight = i < labels.Length - 1 ? 4 : 0;
            tab.style.justifyContent = Justify.Center;
            tab.style.alignItems = Align.Center;
            tab.style.borderTopLeftRadius = UITTheme.Tab.Radius;
            tab.style.borderTopRightRadius = UITTheme.Tab.Radius;
            tab.style.borderBottomLeftRadius = UITTheme.Tab.Radius;
            tab.style.borderBottomRightRadius = UITTheme.Tab.Radius;
            UITStyles.ApplyTabInactive(tab);

            var label = UITFactory.CreateLabel(labels[i], 11, UITTheme.Text.Primary, font, TextAnchor.MiddleCenter);
            tab.Add(label);

            var dot = new VisualElement();
            dot.style.position = Position.Absolute;
            dot.style.top = 4;
            dot.style.right = 4;
            dot.style.width = 6;
            dot.style.height = 6;
            dot.style.borderTopLeftRadius = 3;
            dot.style.borderTopRightRadius = 3;
            dot.style.borderBottomLeftRadius = 3;
            dot.style.borderBottomRightRadius = 3;
            dot.style.backgroundColor = UITTheme.Tab.BadgeColor;
            dot.style.display = DisplayStyle.None;
            tab.Add(dot);
            m_dots.Add(dot);

            tab.RegisterCallback<ClickEvent>(_ => OnTabClicked?.Invoke(captured));

            Add(tab);
            m_tabs.Add(tab);
        }
    }

    public void SetActive(int index)
    {
        m_active = index;
        for (int i = 0; i < m_tabs.Count; i++)
        {
            if (i == index) UITStyles.ApplyTabActive(m_tabs[i]);
            else UITStyles.ApplyTabInactive(m_tabs[i]);
        }
    }

    /// <summary>各タブのバッジ表示を切り替える。flags が短い/null の場合、余ったタブは非表示。</summary>
    public void SetBadges(bool[] flags)
    {
        for (int i = 0; i < m_dots.Count; i++)
        {
            bool show = flags != null && i < flags.Length && flags[i];
            m_dots[i].style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }
}
