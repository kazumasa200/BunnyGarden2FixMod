using System.Collections.Generic;
using BunnyGarden2FixMod.Utils;
using UITKit;
using UnityEngine;
using UnityEngine.UIElements;

namespace BunnyGarden2FixMod.Patches.Overlay;

/// <summary>
/// フリーカメラ・時間操作の状態と操作方法を、F9 パネルや衣装ピッカーと同じ見た目で画面左上に出す。
///
/// <para>
/// 以前は IMGUI (<c>GUILayout.Label</c>) の素の文字列だったものを UI Toolkit へ移した。
/// パネルは常駐させ、表示するセクションが 1 つも無いフレームはパネルごと隠す。
/// 文言はホットキー設定や状態から組み立て直すため、値が変わったときだけ作り直す。
/// </para>
/// <para>
/// 更新を LateUpdate で行うのは、スクリーンショット撮影（<c>Plugin</c> が同フレームに
/// オーバーレイを伏せる）に確実に間に合わせるため。
/// </para>
/// </summary>
public class ModOverlayView : MonoBehaviour
{
    private PanelSettings m_settings;
    private UIDocument m_doc;
    private Font m_font;

    private VisualElement m_panel;
    private VisualElement m_sections;

    /// <summary>直前に描いた内容。これと変わらない限り作り直さない。</summary>
    private string m_renderedKey;
    private bool m_visible;

    public static ModOverlayView Initialize(GameObject parent)
        => parent.AddComponent<ModOverlayView>();

    private void Awake()
    {
        m_font = UITRuntime.ResolveJapaneseFont(out _);
        // F9 パネル (9999) より下、ゲーム UI より上
        m_settings = UITRuntime.CreatePanelSettings(sortingOrder: 9990);
        m_doc = UITRuntime.AttachDocument(gameObject, m_settings);

        BuildPanel();
    }

    private void OnDestroy()
    {
        if (m_settings != null)
        {
            Destroy(m_settings);
            m_settings = null;
        }
    }

    private void BuildPanel()
    {
        var root = m_doc.rootVisualElement;
        root.pickingMode = PickingMode.Ignore;

        m_panel = UITFactory.CreatePanel();
        m_panel.style.position = Position.Absolute;
        m_panel.style.left = 16;
        m_panel.style.top = 16;
        m_panel.style.paddingLeft = 12;
        m_panel.style.paddingRight = 12;
        m_panel.style.paddingTop = 8;
        m_panel.style.paddingBottom = 8;
        // 操作の邪魔にならないよう、パネルはクリックを拾わない
        m_panel.pickingMode = PickingMode.Ignore;
        root.Add(m_panel);

        m_sections = new VisualElement();
        m_sections.pickingMode = PickingMode.Ignore;
        m_panel.Add(m_sections);

        // 生成直後は中身が無いので伏せておく。以降の出し入れは LateUpdate が判断する。
        m_panel.style.display = DisplayStyle.None;
    }

    private void LateUpdate()
    {
        if (m_panel == null)
            return;

        if (!Plugin.IsOverlayVisible)
        {
            SetVisible(false);
            return;
        }

        var key = BuildStateKey();
        if (key.Length == 0)
        {
            SetVisible(false);
            return;
        }

        if (key != m_renderedKey)
        {
            m_renderedKey = key;
            Rebuild();
        }
        SetVisible(true);
    }

    private void SetVisible(bool visible)
    {
        if (visible == m_visible)
            return;

        m_visible = visible;
        m_panel.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
    }

    /// <summary>
    /// 表示内容を一意に表す文字列。これが前フレームと同じならパネルを作り直さない。
    /// </summary>
    private static string BuildStateKey()
    {
        var key = string.Empty;
        if (FreeCamera.FreeCameraManager.IsActive)
        {
            key += $"cam:{FreeCamera.FreeCameraManager.IsFixed}:{Configs.FreeCamDisplayMode.Value}|";
        }
        if (TimeController.IsTimeStopped)
        {
            key += "time|";
        }
        return key;
    }

    private void Rebuild()
    {
        m_sections.Clear();

        if (FreeCamera.FreeCameraManager.IsActive)
            AddFreeCameraSection();

        if (TimeController.IsTimeStopped)
            AddTimeSection();
    }

    private void AddFreeCameraSection()
    {
        bool fixedMode = FreeCamera.FreeCameraManager.IsFixed;

        AddHeader(Loc.Tr("フリーカメラ"), $"{Configs.FreeCamToggle}");

        // 固定モード中はカメラが動かないので、移動・視点の説明は出さない
        if (!fixedMode)
        {
            AddControl(Loc.Tr("移動"), new[] { ("WASD", ""), ("←↑↓→", ""), (Loc.Tr("左スティック"), "") });
            AddControl(Loc.Tr("上下"), new[] { ("Q", ""), ("E", ""), ("ZL", ""), ("ZR", "") });
            AddControl(Loc.Tr("視点"), new[] { (Loc.Tr("マウス"), ""), (Loc.Tr("右スティック"), "") });
            AddControl(Loc.Tr("速度"), new[] { ("Shift", ""), ("Ctrl", ""), ("L", ""), ("R", "") });
        }

        AddStatus(Loc.Tr("固定モード"), fixedMode ? "ON" : "OFF", $"{Configs.FixedFreeCamToggle}");
        AddStatus(Loc.Tr("出力先"), $"{Configs.FreeCamDisplayMode.Value}", $"{Configs.FreeCamDisplayModeToggle}");
    }

    private void AddTimeSection()
    {
        AddHeader(Loc.Tr("時間停止"), $"{Configs.TimeStopToggle}");
        AddControl(Loc.Tr("コマ送り"), new[] { ($"{Configs.FrameAdvance}", "") });
        AddControl(Loc.Tr("早送り"), new[] { ($"{Configs.FastForward}", "") });
    }

    /// <summary>セクションの見出し。右端に解除用のキーを添える。</summary>
    private void AddHeader(string title, string toggleKey)
    {
        var row = UITFactory.CreateRow();
        row.style.alignItems = Align.Center;
        row.style.marginTop = m_sections.childCount > 0 ? 8 : 0;
        row.style.marginBottom = 4;
        row.pickingMode = PickingMode.Ignore;

        var label = UITFactory.CreateLabel(title, 12, UITTheme.Text.Accent, m_font);
        label.style.marginRight = 8;
        row.Add(label);
        row.Add(UITFactory.CreateKeyCap(toggleKey, Loc.Tr("解除"), m_font));

        m_sections.Add(row);
    }

    /// <summary>操作の説明。左にラベル、右にキーキャップを並べる。</summary>
    private void AddControl(string label, IEnumerable<(string Key, string Label)> caps)
    {
        var row = UITFactory.CreateRow();
        row.style.alignItems = Align.Center;
        row.style.marginBottom = 2;
        row.pickingMode = PickingMode.Ignore;

        var name = UITFactory.CreateLabel(label, 10, UITTheme.Text.Secondary, m_font);
        name.style.width = 64;
        row.Add(name);

        foreach (var cap in caps)
            row.Add(UITFactory.CreateKeyCap(cap.Key, cap.Label, m_font));

        m_sections.Add(row);
    }

    /// <summary>現在値つきの状態表示。右端に切り替えキーを添える。</summary>
    private void AddStatus(string label, string value, string toggleKey)
    {
        var row = UITFactory.CreateRow();
        row.style.alignItems = Align.Center;
        row.style.marginBottom = 2;
        row.pickingMode = PickingMode.Ignore;

        var name = UITFactory.CreateLabel(label, 10, UITTheme.Text.Secondary, m_font);
        name.style.width = 64;
        row.Add(name);

        var current = UITFactory.CreateLabel(value, 10, UITTheme.Text.Primary, m_font);
        current.style.marginRight = 8;
        row.Add(current);

        row.Add(UITFactory.CreateKeyCap(toggleKey, Loc.Tr("切替"), m_font));

        m_sections.Add(row);
    }
}
