using BunnyGarden2FixMod.Utils;
using GB.Game;
using System;
using System.Collections.Generic;
using UITKit;
using UITKit.Components;
using UnityEngine;
using UnityEngine.UIElements;

namespace BunnyGarden2FixMod.Patches.CostumeChanger.UI;

/// <summary>
/// UI Toolkit (UIDocument) ベースの Wardrobe ビュー。
///
/// 構成: 本ファイル (共通) + 3 つの partial ファイルで分割。
///   - CostumePickerView.Picker.cs: 5 タブ (COSTUME/PANTIES/STOCKING/BOTTOMS/TOPS) のメインビュー
///   - CostumePickerView.Settings.cs: 解放状態リセット / すべて解放
///   - CostumePickerView.Debug.cs: mesh / MagicaCloth inspector (デバッグ専用)
///
/// Controller は public API (Show/Hide/Render*/IsShown + 各種 event + DTO + WardrobeTab)
/// のみに依存する前提。UGuiKit には依存しない。
/// </summary>
public partial class CostumePickerView : MonoBehaviour
{
    public enum WardrobeTab
    { Costume = 0, Panties = 1, Stocking = 2, Bottoms = 3, Tops = 4 }

    public event Action<int> OnCastClicked;

    public event Action OnCloseClicked;

    public event Action OnBackClicked;

    private UIDocument m_doc;
    private PanelSettings m_settings;
    private VisualElement m_root;             // UIDocument.rootVisualElement
    private VisualElement m_panel;            // 角丸パネル（サイド固定）
    private Label m_headerText;
    private Font m_font;
    private Button m_castPrevButton;          // ◀ キャスト切替ボタン
    private Button m_castNextButton;          // ▶ キャスト切替ボタン
    private Label m_castNameLabel;            // ヘッダー内キャラ名ラベル
    private int m_castSelectedIndex;          // Render() 時点での VisibleCastSelectedIndex
    private int m_castCount;                  // Render() 時点での VisibleCasts.Count（ループ計算用）
    private Button m_backButton;              // ←: Settings/Debug 中のみ可視

    private enum ViewMode
    { Picker, Settings, Debug }

    private ViewMode m_viewMode = ViewMode.Picker;

    public bool IsShown => m_panel != null && m_panel.style.display != DisplayStyle.None;

    /// <summary>
    /// 現在のマウス座標が panel 矩形内かを判定する。
    /// Mouse.current.position は bottom-left origin、UI Toolkit panel は top-left origin。
    /// 実機で RuntimePanelUtils.ScreenToPanel が Y 反転しない挙動が観測されたので、
    /// ScreenToPanel に入れる前に手動で Y 反転する。
    /// </summary>
    public bool IsPointerOverPanel()
    {
        if (!IsShown || m_panel == null) return false;
        if (m_root == null || m_root.panel == null) return false;
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse == null) return false;
        var raw = mouse.position.ReadValue();
        var flipped = new Vector2(raw.x, Screen.height - raw.y);
        var panelPos = RuntimePanelUtils.ScreenToPanel(m_root.panel, flipped);
        return m_panel.worldBound.Contains(panelPos);
    }

    public void Show(RenderData data) => ShowPicker(data);

    public void Hide()
    {
        if (m_panel != null) m_panel.style.display = DisplayStyle.None;
    }

    // PanelSettings.scale は EnsureBuilt() 時の値を保持するため、開く度に Configs.UIScale を反映する。
    private void ApplyUIScale()
    {
        if (m_settings != null) m_settings.scale = Configs.UIScale.Value;
    }

    private void SetMode(ViewMode mode)
    {
        m_viewMode = mode;
        if (m_pickerContent != null)
            m_pickerContent.style.display = mode == ViewMode.Picker ? DisplayStyle.Flex : DisplayStyle.None;
        if (m_settingsContent != null)
            m_settingsContent.style.display = mode == ViewMode.Settings ? DisplayStyle.Flex : DisplayStyle.None;
        if (m_debugContent != null)
            m_debugContent.style.display = mode == ViewMode.Debug ? DisplayStyle.Flex : DisplayStyle.None;
        if (m_settingsButton != null)
            m_settingsButton.style.display = mode == ViewMode.Picker ? DisplayStyle.Flex : DisplayStyle.None;
        if (m_debugButton != null)
            m_debugButton.style.display = mode == ViewMode.Picker ? DisplayStyle.Flex : DisplayStyle.None;
        if (m_backButton != null)
            m_backButton.style.display = (mode == ViewMode.Settings || mode == ViewMode.Debug) ? DisplayStyle.Flex : DisplayStyle.None;
    }

    private void EnsureBuilt()
    {
        if (m_panel != null) return;

        m_font = UITRuntime.ResolveJapaneseFont(out var fontNames);
        PatchLogger.LogInfo($"[CostumePicker] UI Toolkit Font 候補: {string.Join(", ", fontNames)}");
        PatchLogger.LogInfo($"[CostumePicker] 選択 Font: {(m_font != null ? m_font.name : "<null>")}");
        if (m_font == null || m_font.name.StartsWith("LegacyRuntime", StringComparison.OrdinalIgnoreCase))
        {
            PatchLogger.LogInfo("[CostumePicker] 日本語対応 Font が見つかりませんでした（LegacyRuntime fallback を使用）");
        }

        m_settings = UITRuntime.CreatePanelSettings();
        var otherPanels = UITRuntime.DumpOtherPanelSettings(m_settings);
        PatchLogger.LogInfo($"[CostumePicker] 既存 PanelSettings: {(otherPanels.Count == 0 ? "<none>" : string.Join(", ", otherPanels))}");
        if (m_settings.themeStyleSheet == null)
            PatchLogger.LogInfo("[CostumePicker] themeStyleSheet を解決できませんでした — UI が描画されない可能性があります");
        else
            PatchLogger.LogInfo($"[CostumePicker] themeStyleSheet 借用: {m_settings.themeStyleSheet.name}");

        m_doc = UITRuntime.AttachDocument(gameObject, m_settings);
        m_root = m_doc.rootVisualElement;
        m_root.style.flexGrow = 1;
        m_root.focusable = false;

        m_panel = UITFactory.CreatePanel();
        m_panel.style.position = Position.Absolute;
        m_panel.style.right = 16;
        m_panel.style.top = 20;
        m_panel.style.width = 280;
        m_panel.style.height = Length.Percent(50);
        m_panel.style.overflow = Overflow.Hidden;
        m_panel.style.paddingTop = 12;
        m_panel.style.paddingRight = 12;
        m_panel.style.paddingBottom = 10;
        m_panel.style.paddingLeft = 12;
        m_root.Add(m_panel);

        // Header（全モード共用）: [衣装変更] [◀] [キャラ名] [▶]
        var headerRow = UITFactory.CreateRow();
        headerRow.style.height = 22;
        headerRow.style.marginBottom = 6;
        headerRow.style.marginRight = 96;  // D(right=64, w=22) 左端=86px + 10px バッファ
        headerRow.style.flexShrink = 0;
        headerRow.style.alignItems = Align.Center;
        m_panel.Add(headerRow);
        m_headerText = UITFactory.CreateLabel("衣装変更", 13, UITTheme.Text.Accent, m_font, TextAnchor.MiddleLeft);
        m_headerText.style.flexShrink = 0;
        m_headerText.style.marginRight = 6;
        headerRow.Add(m_headerText);

        m_castPrevButton = UITFactory.CreateButton("◀",
            () => { if (m_castCount > 0) OnCastClicked?.Invoke(m_castSelectedIndex > 0 ? m_castSelectedIndex - 1 : m_castCount - 1); },
            10, m_font);
        m_castPrevButton.style.paddingLeft = 4;
        m_castPrevButton.style.paddingRight = 4;
        m_castPrevButton.style.paddingTop = 1;
        m_castPrevButton.style.paddingBottom = 1;
        m_castPrevButton.style.flexShrink = 0;
        m_castPrevButton.style.display = DisplayStyle.None;
        headerRow.Add(m_castPrevButton);

        m_castNameLabel = UITFactory.CreateLabel("", 12, UITTheme.Text.Primary, m_font, TextAnchor.MiddleCenter);
        m_castNameLabel.style.flexGrow = 1;
        headerRow.Add(m_castNameLabel);

        m_castNextButton = UITFactory.CreateButton("▶",
            () => { if (m_castCount > 0) OnCastClicked?.Invoke(m_castSelectedIndex < m_castCount - 1 ? m_castSelectedIndex + 1 : 0); },
            10, m_font);
        m_castNextButton.style.paddingLeft = 4;
        m_castNextButton.style.paddingRight = 4;
        m_castNextButton.style.paddingTop = 1;
        m_castNextButton.style.paddingBottom = 1;
        m_castNextButton.style.flexShrink = 0;
        m_castNextButton.style.display = DisplayStyle.None;
        headerRow.Add(m_castNextButton);

        m_pickerContent = UITFactory.CreateColumn();
        m_pickerContent.style.flexGrow = 1;
        m_panel.Add(m_pickerContent);
        BuildPickerContent();

        // 設定用コンテナ（初期は Hidden）
        m_settingsContent = UITFactory.CreateColumn();
        m_settingsContent.style.flexGrow = 1;
        m_settingsContent.style.display = DisplayStyle.None;
        m_panel.Add(m_settingsContent);
        BuildSettingsContent();

        // Debug 用コンテナ（初期は Hidden）
        m_debugContent = UITFactory.CreateColumn();
        m_debugContent.style.flexGrow = 1;
        m_debugContent.style.display = DisplayStyle.None;
        m_panel.Add(m_debugContent);
        BuildDebugContent();

        BuildHeaderButtons();

        m_panel.style.display = DisplayStyle.None;
        SetMode(ViewMode.Picker);
    }

    /// <summary>
    /// 配置 (right から):
    ///   × right=8     (常時)
    ///   ⚙ right=36    (Picker のみ) ※ 配置詳細は <see cref="BuildSettingsButton"/> (Settings.cs)
    ///   D right=64    (Picker のみ、Debug ビューへ) ※ 配置詳細は <see cref="BuildDebugButton"/> (Debug.cs)
    ///   ← right=64    (Settings/Debug 中、D と同位置で排他)
    /// </summary>
    private void BuildHeaderButtons()
    {
        BuildSettingsButton();   // ⚙
        BuildDebugButton();      // [D]

        // ← 戻るボタン（Settings / Debug 中に表示、D と同位置で排他）
        var backTex = EmbeddedTexture.Load("BunnyGarden2FixMod.Resources.arrow-big-left.png");
        m_backButton = UITFactory.CreateTextureButton(backTex, () => OnBackClicked?.Invoke(), m_font);
        m_backButton.style.position = Position.Absolute;
        m_backButton.style.right = 64;
        m_backButton.style.top = 8;
        m_backButton.style.width = 22;
        m_backButton.style.height = 22;
        m_panel.Add(m_backButton);

        // × 閉じる（常時表示）
        var close = UITFactory.CreateButton("×", () => OnCloseClicked?.Invoke(), 16, m_font);
        close.style.position = Position.Absolute;
        close.style.right = 8;
        close.style.top = 8;
        close.style.width = 22;
        close.style.height = 22;
        close.style.paddingLeft = 0;
        close.style.paddingRight = 0;
        close.style.paddingTop = 0;
        close.style.paddingBottom = 0;
        m_panel.Add(close);
    }

    /// <summary>
    /// ◀▶ ナビの表示制御とフィールドを更新する。Render*/RenderSettings/RenderDebug 全モードから呼ぶ。
    /// </summary>
    private void UpdateNavState(IReadOnlyList<CharID> visibleCasts, int selectedIndex)
    {
        if (m_castNameLabel != null && visibleCasts != null && selectedIndex >= 0 && selectedIndex < visibleCasts.Count)
            m_castNameLabel.text = visibleCasts[selectedIndex].ToString();

        bool showNav = visibleCasts != null && visibleCasts.Count >= 2;
        if (m_castPrevButton != null)
            m_castPrevButton.style.display = showNav ? DisplayStyle.Flex : DisplayStyle.None;
        if (m_castNextButton != null)
            m_castNextButton.style.display = showNav ? DisplayStyle.Flex : DisplayStyle.None;
        m_castSelectedIndex = selectedIndex;
        m_castCount = visibleCasts?.Count ?? 0;
    }

    private void OnDestroy()
    {
        if (m_settings != null)
        {
            Destroy(m_settings);
            m_settings = null;
        }
    }
}
