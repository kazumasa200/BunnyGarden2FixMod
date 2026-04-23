using UnityEngine;

namespace UITKit;

/// <summary>
/// UI Toolkit 版 Wardrobe の配色・寸法定数。
/// UGuiTheme と独立（同じ値を複製しているのは退役時に UGuiTheme 側を消すため）。
/// </summary>
public static class UITTheme
{
    public static class Panel
    {
        public static readonly Color Background = new(0.08f, 0.08f, 0.12f, 0.92f);
        public static readonly Color Border = new(0.75f, 0.55f, 0.85f, 0.6f);
        public const float Radius = 8f;
        public const float BorderWidth = 1f;
    }

    public static class Row
    {
        public static readonly Color Normal = new(0f, 0f, 0f, 0f);
        public static readonly Color Hover = new(1f, 1f, 1f, 0.06f);
        public static readonly Color SelectedFill = new(0.75f, 0.55f, 0.85f, 0.18f);
        public static readonly Color SelectedBorder = new(0.75f, 0.55f, 0.85f, 0.9f);
        public static readonly Color TransparentBorder = new(0.75f, 0.55f, 0.85f, 0f);
        public const float Height = 30f;
        public const float Radius = 6f;
        public const float SelectedBorderWidth = 2f;
    }

    /// <summary>
    /// 行の左側に置く小さな角丸ボックス。current(=適用中) は塗り + ✓、
    /// default(=未適用) は薄塗り + 枠、locked(=未開放) は透過 + dim 枠のみ。
    /// </summary>
    public static class Checkbox
    {
        public static readonly Color DefaultFill = new(0.75f, 0.55f, 0.85f, 0.25f);
        public static readonly Color DefaultBorder = new(0.75f, 0.55f, 0.85f, 0.55f);
        public static readonly Color CheckedFill = new(0.75f, 0.55f, 0.85f, 0.9f);
        public static readonly Color CheckedBorder = new(0.9f, 0.75f, 0.95f, 1f);
        public static readonly Color CheckedMark = new(1f, 1f, 1f, 1f);
        public static readonly Color LockedFill = new(0f, 0f, 0f, 0f);
        public static readonly Color LockedBorder = new(0.75f, 0.55f, 0.85f, 0.2f);
        public const float Size = 16f;
        public const float Radius = 3f;
        public const float BorderWidth = 1f;
    }

    public static class Text
    {
        public static readonly Color Primary = new(0.95f, 0.95f, 0.97f, 1f);
        public static readonly Color Secondary = new(0.75f, 0.75f, 0.8f, 1f);
        public static readonly Color Accent = new(0.95f, 0.85f, 1f, 1f);
        public static readonly Color Locked = new(0.95f, 0.95f, 0.97f, 0.35f);
    }

    public static class Tab
    {
        public static readonly Color InactiveFill = new(0.15f, 0.15f, 0.2f, 0.8f);
        public static readonly Color ActiveFill = new(0.75f, 0.55f, 0.85f, 0.85f);
        public static readonly Color Border = new(0.5f, 0.5f, 0.55f, 0.7f);
        public static readonly Color BadgeColor = new(1f, 0.85f, 0.3f, 1f);
        public const float Radius = 4f;
        public const float BorderWidth = 1f;
    }

    public static class KeyCap
    {
        public static readonly Color Fill = new(0.2f, 0.2f, 0.25f, 0.9f);
        public static readonly Color Border = new(0.5f, 0.5f, 0.55f, 0.7f);
        public const float Radius = 3f;
        public const float BorderWidth = 1f;
    }
}
