using UnityEngine;
using UnityEngine.UIElements;

namespace UITKit.Components;

/// <summary>
/// 1 行分の VisualElement。構造は Row(flex row) → Checkbox(左) + Label(flex-grow:1)。
/// Checkbox が current(適用中) / default(未適用) / locked(未開放) の 3 状態を表現する。
/// hover / selected の背景枠は UITStyles に委譲。locked 行は hover 反応させない。
/// </summary>
public class UITListRow : VisualElement
{
    private Label m_label;
    private bool m_isSelected;
    private bool m_isLocked;

    public UITListRow()
    {
        style.flexDirection = FlexDirection.Row;
        style.alignItems = Align.Center;
        style.height = UITTheme.Row.Height;
        // ScrollView 内で親 height が小さくなった時に行を潰さず固定高 + overflow scroll で見せる。
        style.flexShrink = 0;
        style.marginTop = 0;
        style.marginBottom = 1;
        style.paddingLeft = 8;
        style.paddingRight = 8;
        style.borderTopLeftRadius = UITTheme.Row.Radius;
        style.borderTopRightRadius = UITTheme.Row.Radius;
        style.borderBottomLeftRadius = UITTheme.Row.Radius;
        style.borderBottomRightRadius = UITTheme.Row.Radius;
        UITStyles.ApplyRowNormal(this);
    }

    public void Setup(string labelText, bool isSelected, bool isCurrent, bool isLocked, Font font = null)
    {
        Clear();
        m_isSelected = isSelected;
        m_isLocked = isLocked;

        var checkState = isLocked
            ? UITFactory.CheckboxState.Locked
            : isCurrent
                ? UITFactory.CheckboxState.Checked
                : UITFactory.CheckboxState.Default;
        var checkbox = UITFactory.CreateCheckbox(checkState, font);
        Add(checkbox);

        var textColor = isLocked ? UITTheme.Text.Locked : UITTheme.Text.Primary;
        m_label = UITFactory.CreateLabel(labelText, 12, textColor, font);
        m_label.style.flexGrow = 1;
        Add(m_label);

        if (isSelected) UITStyles.ApplyRowSelected(this);
        else UITStyles.ApplyRowNormal(this);
    }

    public void SetHover(bool hover)
    {
        if (m_isSelected) return;  // 選択中は hover 表現しない
        if (m_isLocked) return;    // 未開放は hover 表現しない
        if (hover) UITStyles.ApplyRowHover(this);
        else UITStyles.ApplyRowNormal(this);
    }
}
