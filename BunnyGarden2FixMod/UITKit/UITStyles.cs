using UnityEngine;
using UnityEngine.UIElements;

namespace UITKit;

/// <summary>
/// USS の :hover / .selected クラス相当を inline style 差分で表現する state 適用関数。
/// VisualElement のサイズ・配置は Factory / Component 側で固定し、この関数は
/// 背景色・枠線色・枠線幅のみを切替える。
/// </summary>
public static class UITStyles
{
    public static void ApplyRowNormal(VisualElement v)
    {
        v.style.backgroundColor = UITTheme.Row.Normal;
        SetBorderColor(v, UITTheme.Row.TransparentBorder);
        SetBorderWidth(v, 0f);
    }

    public static void ApplyRowHover(VisualElement v)
    {
        v.style.backgroundColor = UITTheme.Row.Hover;
        SetBorderColor(v, UITTheme.Row.TransparentBorder);
        SetBorderWidth(v, 0f);
    }

    public static void ApplyRowSelected(VisualElement v)
    {
        v.style.backgroundColor = UITTheme.Row.SelectedFill;
        SetBorderColor(v, UITTheme.Row.SelectedBorder);
        SetBorderWidth(v, UITTheme.Row.SelectedBorderWidth);
    }

    public static void ApplyTabActive(VisualElement v)
    {
        v.style.backgroundColor = UITTheme.Tab.ActiveFill;
        SetBorderColor(v, UITTheme.Tab.Border);
        SetBorderWidth(v, UITTheme.Tab.BorderWidth);
    }

    public static void ApplyTabInactive(VisualElement v)
    {
        v.style.backgroundColor = UITTheme.Tab.InactiveFill;
        SetBorderColor(v, UITTheme.Tab.Border);
        SetBorderWidth(v, UITTheme.Tab.BorderWidth);
    }

    private static void SetBorderColor(VisualElement v, Color c)
    {
        v.style.borderTopColor = c;
        v.style.borderRightColor = c;
        v.style.borderBottomColor = c;
        v.style.borderLeftColor = c;
    }

    private static void SetBorderWidth(VisualElement v, float w)
    {
        v.style.borderTopWidth = w;
        v.style.borderRightWidth = w;
        v.style.borderBottomWidth = w;
        v.style.borderLeftWidth = w;
    }
}
