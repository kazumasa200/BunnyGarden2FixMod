using UnityEngine;
using UnityEngine.UIElements;

namespace UITKit.Components;

/// <summary>
/// キーヒント (key, label) の横並び。flex row で各 cap を左詰め、親の preferredHeight 問題は無い（flex 基準）。
/// </summary>
public class UITKeyCapRow : VisualElement
{
    public void Setup((string Key, string Label)[] caps, Font font = null)
    {
        Clear();
        style.flexDirection = FlexDirection.Row;
        style.alignItems = Align.Center;
        style.height = 18;

        foreach (var cap in caps)
        {
            Add(UITFactory.CreateKeyCap(cap.Key, cap.Label, font));
        }
    }
}
