using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace UITKit;

/// <summary>
/// VisualElement 系 primitive の静的生成。Component (UITListView 等) はこれに依存しない。
/// font を受け取る API は日本語グリフ対応のため。null 渡しで Unity default font。
/// </summary>
public static class UITFactory
{
    public static Label CreateLabel(string text, int fontSize, Color color, Font font = null, TextAnchor anchor = TextAnchor.MiddleLeft)
    {
        var label = new Label(text);
        label.style.color = color;
        label.style.fontSize = fontSize;
        label.style.unityTextAlign = anchor;
        label.style.whiteSpace = WhiteSpace.NoWrap;
        label.style.overflow = Overflow.Hidden;
        if (font != null) label.style.unityFont = font;
        return label;
    }

    public static Button CreateButton(string text, Action onClick, int fontSize, Font font = null)
    {
        var btn = new Button(onClick) { text = text };
        btn.style.fontSize = fontSize;
        btn.style.color = UITTheme.Text.Primary;
        btn.style.unityTextAlign = TextAnchor.MiddleCenter;
        btn.style.marginTop = 0;
        btn.style.marginRight = 0;
        btn.style.marginBottom = 0;
        btn.style.marginLeft = 0;
        btn.style.paddingTop = 2;
        btn.style.paddingRight = 6;
        btn.style.paddingBottom = 2;
        btn.style.paddingLeft = 6;
        btn.style.backgroundColor = UITTheme.Tab.InactiveFill;
        SetBorderRadius(btn, UITTheme.Tab.Radius);
        SetBorderAll(btn, UITTheme.Tab.Border, UITTheme.Tab.BorderWidth);
        if (font != null) btn.style.unityFont = font;
        return btn;
    }

    /// <summary>
    /// 背景画像アイコンを持つボタン。ラベルテキストなし、パディング 0、
    /// 背景画像は 70% スケールで中央描画、Text.Primary でティント。
    /// 位置・サイズは呼び出し側で設定する前提。
    /// </summary>
    public static Button CreateTextureButton(Texture2D tex, Action onClick, Font font = null)
    {
        var btn = CreateButton("", onClick, 14, font);
        btn.style.paddingLeft = 0;
        btn.style.paddingRight = 0;
        btn.style.paddingTop = 0;
        btn.style.paddingBottom = 0;
        btn.style.backgroundImage = new StyleBackground(tex);
        btn.style.backgroundSize = new StyleBackgroundSize(
            new BackgroundSize(Length.Percent(70), Length.Percent(70)));
        btn.style.unityBackgroundImageTintColor = new StyleColor(UITTheme.Text.Primary);
        return btn;
    }

    /// <summary>背景色・角丸・枠線が Panel テーマに設定された空 VisualElement。</summary>
    public static VisualElement CreatePanel()
    {
        var v = new VisualElement();
        v.style.backgroundColor = UITTheme.Panel.Background;
        SetBorderRadius(v, UITTheme.Panel.Radius);
        SetBorderAll(v, UITTheme.Panel.Border, UITTheme.Panel.BorderWidth);
        v.style.flexDirection = FlexDirection.Column;
        return v;
    }

    public static VisualElement CreateRow()
    {
        var v = new VisualElement();
        v.style.flexDirection = FlexDirection.Row;
        return v;
    }

    public static VisualElement CreateColumn()
    {
        var v = new VisualElement();
        v.style.flexDirection = FlexDirection.Column;
        return v;
    }

    public enum CheckboxState { Default, Checked, Locked }

    /// <summary>
    /// 行の左側に置く小さな角丸チェックボックス。
    /// Checked: 塗り + ✓ マーク / Default: 薄塗り + 枠 / Locked: 透過 + dim 枠のみ。
    /// </summary>
    public static VisualElement CreateCheckbox(CheckboxState state, Font font = null)
    {
        var box = new VisualElement();
        box.style.width = UITTheme.Checkbox.Size;
        box.style.height = UITTheme.Checkbox.Size;
        box.style.marginRight = 8;
        box.style.flexShrink = 0;
        box.style.alignItems = Align.Center;
        box.style.justifyContent = Justify.Center;
        SetBorderRadius(box, UITTheme.Checkbox.Radius);

        switch (state)
        {
            case CheckboxState.Checked:
                box.style.backgroundColor = UITTheme.Checkbox.CheckedFill;
                SetBorderAll(box, UITTheme.Checkbox.CheckedBorder, UITTheme.Checkbox.BorderWidth);
                var mark = CreateLabel("✓", 12, UITTheme.Checkbox.CheckedMark, font, TextAnchor.MiddleCenter);
                mark.style.paddingLeft = 0;
                mark.style.paddingRight = 0;
                mark.style.paddingTop = 0;
                mark.style.paddingBottom = 0;
                box.Add(mark);
                break;
            case CheckboxState.Locked:
                box.style.backgroundColor = UITTheme.Checkbox.LockedFill;
                SetBorderAll(box, UITTheme.Checkbox.LockedBorder, UITTheme.Checkbox.BorderWidth);
                break;
            default: // Default
                box.style.backgroundColor = UITTheme.Checkbox.DefaultFill;
                SetBorderAll(box, UITTheme.Checkbox.DefaultBorder, UITTheme.Checkbox.BorderWidth);
                break;
        }
        return box;
    }

    public static VisualElement CreateKeyCap(string key, string label, Font font = null)
    {
        var wrap = CreateRow();
        wrap.style.alignItems = Align.Center;
        wrap.style.marginRight = 8;

        var cap = new VisualElement();
        cap.style.backgroundColor = UITTheme.KeyCap.Fill;
        SetBorderRadius(cap, UITTheme.KeyCap.Radius);
        SetBorderAll(cap, UITTheme.KeyCap.Border, UITTheme.KeyCap.BorderWidth);
        cap.style.paddingLeft = 4;
        cap.style.paddingRight = 4;
        cap.style.paddingTop = 0;
        cap.style.paddingBottom = 0;
        cap.style.minWidth = 14;
        cap.style.alignItems = Align.Center;
        cap.style.justifyContent = Justify.Center;

        var keyLabel = CreateLabel(key, 10, UITTheme.Text.Primary, font, TextAnchor.MiddleCenter);
        cap.Add(keyLabel);
        wrap.Add(cap);

        if (!string.IsNullOrEmpty(label))
        {
            var textLabel = CreateLabel(label, 10, UITTheme.Text.Secondary, font);
            textLabel.style.marginLeft = 4;
            wrap.Add(textLabel);
        }
        return wrap;
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
}
