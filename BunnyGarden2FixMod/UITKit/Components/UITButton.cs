using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace UITKit.Components;

/// <summary>
/// テーマ統一されたクリック可能なボタンコンポーネント。
/// `Setup(text, onClick)` で初期化し、必要なら `SetWidth` / `SetVariant` で見た目を調整する。
/// Unity Button をラップせず、VisualElement ベースで構築する。
/// </summary>
public class UITButton : VisualElement
{
    public event Action OnClick;
    private Label m_label;

    private Color m_baseColor;
    private Color m_hoverColor;
    private Color m_pressedColor;
    private bool m_isHovering;
    private bool m_isPressing;

    public enum Variant
    {
        Default,   // 中性的な暗灰色
        Primary,   // 強調（オレンジ系アクセント）
        Subtle     // 控えめ（透過 + 薄枠）
    }

    public UITButton()
    {
        BuildBase();
    }

    private void BuildBase()
    {
        style.flexDirection = FlexDirection.Row;
        style.alignItems = Align.Center;
        style.justifyContent = Justify.Center;
        style.paddingTop = style.paddingBottom = 4;
        style.paddingLeft = style.paddingRight = 10;
        style.borderTopLeftRadius = style.borderTopRightRadius =
            style.borderBottomLeftRadius = style.borderBottomRightRadius = 3;
        style.borderTopWidth = style.borderRightWidth =
            style.borderBottomWidth = style.borderLeftWidth = 1;

        m_label = new Label();
        m_label.style.fontSize = 11;
        m_label.pickingMode = PickingMode.Ignore;
        Add(m_label);

        ApplyVariant(Variant.Default);

        RegisterCallback<ClickEvent>(evt =>
        {
            OnClick?.Invoke();
            evt.StopPropagation();
        });

        // マウスオーバー・押下時に背景色を変化させて視覚フィードバックを提供する
        RegisterCallback<MouseEnterEvent>(_ =>
        {
            m_isHovering = true;
            UpdateBackgroundColor();
        });
        RegisterCallback<MouseLeaveEvent>(_ =>
        {
            m_isHovering = false;
            m_isPressing = false; // hover が外れたら押下状態も解除
            UpdateBackgroundColor();
        });
        RegisterCallback<PointerDownEvent>(_ =>
        {
            m_isPressing = true;
            UpdateBackgroundColor();
        });
        RegisterCallback<PointerUpEvent>(_ =>
        {
            m_isPressing = false;
            UpdateBackgroundColor();
        });
    }

    /// <summary>テキストとクリックハンドラを設定する。</summary>
    public UITButton Setup(string text, Action onClick, Font font = null)
    {
        m_label.text = text;
        if (font != null) m_label.style.unityFont = font;
        OnClick = null; // 多重呼び出し時のイベント累積を防ぐ
        if (onClick != null) OnClick += onClick;
        return this;
    }

    /// <summary>ボタンの見た目バリアントを切り替える。</summary>
    public UITButton SetVariant(Variant v)
    {
        ApplyVariant(v);
        return this;
    }

    /// <summary>ボタンの幅を固定する（0 以下なら可変に戻す）。</summary>
    public UITButton SetWidth(float width)
    {
        if (width > 0)
        {
            style.width = width;
            style.minWidth = width;
            style.maxWidth = width;
        }
        else
        {
            style.width = StyleKeyword.Auto;
            style.minWidth = StyleKeyword.Auto;
            style.maxWidth = StyleKeyword.Auto;
        }
        return this;
    }

    private void ApplyVariant(Variant v)
    {
        switch (v)
        {
            case Variant.Primary:
                m_baseColor    = new Color(0.95f, 0.65f, 0.35f, 1f);
                m_hoverColor   = new Color(1.00f, 0.74f, 0.45f, 1f);
                m_pressedColor = new Color(0.82f, 0.55f, 0.28f, 1f);
                if (m_label != null) m_label.style.color = new Color(0.10f, 0.10f, 0.12f, 1f);
                SetBorderColor(m_baseColor);
                break;
            case Variant.Subtle:
                m_baseColor    = new Color(0, 0, 0, 0);
                m_hoverColor   = new Color(1, 1, 1, 0.06f);
                m_pressedColor = new Color(0, 0, 0, 0.18f);
                if (m_label != null) m_label.style.color = new Color(0.84f, 0.87f, 0.91f, 1f);
                SetBorderColor(new Color(0.30f, 0.34f, 0.42f, 1f));
                break;
            case Variant.Default:
            default:
                m_baseColor    = new Color(0.176f, 0.204f, 0.290f, 1f);
                m_hoverColor   = new Color(0.230f, 0.260f, 0.350f, 1f);
                m_pressedColor = new Color(0.130f, 0.155f, 0.235f, 1f);
                if (m_label != null) m_label.style.color = new Color(0.84f, 0.87f, 0.91f, 1f);
                SetBorderColor(new Color(0.30f, 0.34f, 0.42f, 1f));
                break;
        }
        UpdateBackgroundColor();
    }

    private void UpdateBackgroundColor()
    {
        if (m_isPressing)      style.backgroundColor = m_pressedColor;
        else if (m_isHovering) style.backgroundColor = m_hoverColor;
        else                   style.backgroundColor = m_baseColor;
    }

    private void SetBorderColor(Color c)
    {
        style.borderTopColor = style.borderRightColor =
            style.borderBottomColor = style.borderLeftColor = c;
    }
}
