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
/// Controller はこの public API (Show/Hide/Render/IsShown + 3 events + RenderData + WardrobeTab)
/// のみに依存する前提。UGuiKit には依存しない。
/// </summary>
public class CostumePickerView : MonoBehaviour
{
    public enum WardrobeTab
    { Costume = 0, Panties = 1, Stocking = 2 }

    public class RenderData
    {
        public CharID CharId;
        public WardrobeTab ActiveTab;
        public IReadOnlyList<string> CostumeLabels;
        public IReadOnlyList<string> PantiesLabels;
        public IReadOnlyList<string> StockingLabels;
        public IReadOnlyList<bool> CostumeLocks;
        public IReadOnlyList<bool> PantiesLocks;
        public IReadOnlyList<bool> StockingLocks;
        public int CostumeSelected;
        public int PantiesSelected;
        public int StockingSelected;
        public int CostumeCurrent;
        public int PantiesCurrent;
        public int StockingCurrent;

        /// <summary>現在表示中（Preload 済み + activeInHierarchy）のキャスト一覧。</summary>
        public IReadOnlyList<CharID> VisibleCasts;

        /// <summary>VisibleCasts の中で現在ピッカーが対象としているキャストのインデックス。</summary>
        public int VisibleCastSelectedIndex;
    }

    public class SettingsData
    {
        public CharID CharId;

        /// <summary>「すべて解放」ボタンを enable にするか（= そのキャラの GoodEnd クリア済み）。</summary>
        public bool UnlockAllEnabled;

        /// <summary>現在表示中のキャスト一覧（ヘッダーの ◀▶ ナビ更新に使用）。</summary>
        public IReadOnlyList<CharID> VisibleCasts;

        /// <summary>VisibleCasts の中で現在ピッカーが対象としているキャストのインデックス。</summary>
        public int VisibleCastSelectedIndex;

        // パンスト微調整 (m 単位)
        public float StockingOffset;
        public float StockingSkinShrink;
        public float StockingFalloffRadius;
        public float StockingShapeFalloffRadius;

        // 各スライダーの最大値 (m 単位、Config.AcceptableValueRange と一致)
        public float StockingOffsetMax;
        public float StockingSkinShrinkMax;
        public float StockingFalloffRadiusMax;
        public float StockingShapeFalloffRadiusMax;
    }

    public event Action<int> OnTabClicked;

    public event Action<int> OnRowClicked;

    public event Action<int> OnCastClicked;

    public event Action OnCloseClicked;

    public event Action OnSettingsClicked;

    public event Action OnBackClicked;

    public event Action OnResetAllClicked;

    public event Action OnUnlockAllClicked;

    /// <summary>パンスト微調整スライダーの値変更 (引数は m 単位)。</summary>
    public event Action<float> OnStockingOffsetChanged;

    public event Action<float> OnStockingSkinShrinkChanged;

    public event Action<float> OnStockingFalloffChanged;
    public event Action<float> OnStockingShapeFalloffChanged;
    public event Action OnTuneResetClicked;

    private UIDocument m_doc;
    private PanelSettings m_settings;
    private VisualElement m_root;         // UIDocument.rootVisualElement
    private VisualElement m_panel;        // 角丸パネル（サイド固定）
    private Label m_headerText;
    private UITTabStrip m_tabStrip;
    private UITListView m_listView;
    private Font m_font;
    private VisualElement m_pickerContent;    // 既存の header/tabStrip/listView/footer を束ねるコンテナ
    private VisualElement m_settingsContent;  // 設定画面コンテナ
    private Button m_castPrevButton;          // ◀ キャスト切替ボタン
    private Button m_castNextButton;          // ▶ キャスト切替ボタン
    private Label m_castNameLabel;            // ヘッダー内キャラ名ラベル
    private int m_castSelectedIndex;          // Render() 時点での VisibleCastSelectedIndex
    private int m_castCount;                  // Render() 時点での VisibleCasts.Count（ループ計算用）
    private Button m_settingsButton;          // ⚙: ピッカー中のみ可視
    private Button m_backButton;              // ←: 設定中のみ可視
    private Button m_resetAllButton;          // W/S キー選択ハイライトのため保持
    private Button m_unlockAllButton;         // enable/disable 切替のため保持
    private Label m_unlockAllNote;            // 常時表示の説明ラベル（将来ラベル差替え予定なら必要）

    // パンスト微調整スライダー（設定画面下部）
    private UITSlider m_offsetSlider;
    private UITSlider m_skinShrinkSlider;
    private UITSlider m_falloffSlider;
    private UITSlider m_shapeFalloffSlider;
    private Button m_tuneResetButton;

    private enum ViewMode
    { Picker, Settings }

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

    public void ShowPicker(RenderData data)
    {
        EnsureBuilt();
        m_panel.style.display = DisplayStyle.Flex;
        SetMode(ViewMode.Picker);
        Render(data);
    }

    public void ShowSettings(SettingsData data)
    {
        EnsureBuilt();
        m_panel.style.display = DisplayStyle.Flex;
        SetMode(ViewMode.Settings);
        RenderSettings(data);
    }

    public void RenderSettings(SettingsData data)
    {
        if (m_settingsContent == null) return;
        UpdateNavState(data.VisibleCasts, data.VisibleCastSelectedIndex);
        if (m_unlockAllButton != null)
        {
            m_unlockAllButton.SetEnabled(data.UnlockAllEnabled);
            m_unlockAllButton.style.opacity = data.UnlockAllEnabled ? 1f : 0.4f;
        }
        UpdateSlider(m_offsetSlider, data.StockingOffset, data.StockingOffsetMax);
        UpdateSlider(m_skinShrinkSlider, data.StockingSkinShrink, data.StockingSkinShrinkMax);
        UpdateSlider(m_falloffSlider, data.StockingFalloffRadius, data.StockingFalloffRadiusMax);
        UpdateSlider(m_shapeFalloffSlider, data.StockingShapeFalloffRadius, data.StockingShapeFalloffRadiusMax);
    }

    private static void UpdateSlider(UITSlider s, float current, float max)
    {
        if (s == null) return;
        if (max > 0f) s.SetRange(0f, max);
        s.SetValue(current);
    }

    private static string FormatMm(float meters) => $"{meters * 1000f:F1}mm";

    /// <summary>
    /// 設定画面 2 ボタンのキー操作選択ハイライトを更新する。0=Reset, 1=UnlockAll。
    /// 選択中ボタンを Tab.ActiveFill で塗り、非選択を Tab.InactiveFill に戻す。
    /// </summary>
    public void SetSettingsSelection(int index)
    {
        if (m_resetAllButton != null)
            m_resetAllButton.style.backgroundColor = index == 0 ? UITTheme.Tab.ActiveFill : UITTheme.Tab.InactiveFill;
        if (m_unlockAllButton != null)
            m_unlockAllButton.style.backgroundColor = index == 1 ? UITTheme.Tab.ActiveFill : UITTheme.Tab.InactiveFill;
    }

    private void SetMode(ViewMode mode)
    {
        m_viewMode = mode;
        if (m_pickerContent != null)
            m_pickerContent.style.display = mode == ViewMode.Picker ? DisplayStyle.Flex : DisplayStyle.None;
        if (m_settingsContent != null)
            m_settingsContent.style.display = mode == ViewMode.Settings ? DisplayStyle.Flex : DisplayStyle.None;
        if (m_settingsButton != null)
            m_settingsButton.style.display = mode == ViewMode.Picker ? DisplayStyle.Flex : DisplayStyle.None;
        if (m_backButton != null)
            m_backButton.style.display = mode == ViewMode.Settings ? DisplayStyle.Flex : DisplayStyle.None;
    }

    public void Hide()
    {
        if (m_panel != null) m_panel.style.display = DisplayStyle.None;
    }

    public void Render(RenderData data)
    {
        if (m_panel == null) return;
        UpdateNavState(data.VisibleCasts, data.VisibleCastSelectedIndex);
        m_tabStrip.SetActive((int)data.ActiveTab);
        m_tabStrip.SetBadges(new[] { data.CostumeCurrent >= 0, data.PantiesCurrent >= 0, data.StockingCurrent >= 0 });

        var (labels, locks, selected, current) = data.ActiveTab switch
        {
            WardrobeTab.Panties => (data.PantiesLabels, data.PantiesLocks, data.PantiesSelected, data.PantiesCurrent),
            WardrobeTab.Stocking => (data.StockingLabels, data.StockingLocks, data.StockingSelected, data.StockingCurrent),
            _ => (data.CostumeLabels, data.CostumeLocks, data.CostumeSelected, data.CostumeCurrent),
        };

        if (labels == null || labels.Count == 0)
        {
            m_listView.ShowEmpty("（履歴なし）");
            return;
        }

        var rows = new List<UITListView.RowModel>(labels.Count);
        for (int i = 0; i < labels.Count; i++)
        {
            rows.Add(new UITListView.RowModel
            {
                Label = labels[i],
                IsSelected = i == selected,
                IsCurrent = i == current,
                IsLocked = locks != null && i < locks.Count && locks[i],
            });
        }
        m_listView.Rebuild(rows);
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

        // Header（両モード共用）: [衣装変更] [◀] [キャラ名] [▶]
        var headerRow = UITFactory.CreateRow();
        headerRow.style.height = 22;
        headerRow.style.marginBottom = 6;
        headerRow.style.marginRight = 68;  // ⚙(right=36, w=22) 左端=58px + 10px バッファ
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

        // ピッカー用コンテナ
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

        // ヘッダ右上のアクションボタン群
        BuildHeaderButtons();

        m_panel.style.display = DisplayStyle.None;
        SetMode(ViewMode.Picker);
    }

    private void BuildPickerContent()
    {
        // TabStrip
        m_tabStrip = new UITTabStrip();
        m_tabStrip.Setup(new[] { "COSTUME", "PANTIES", "STOCKING" }, m_font);
        m_tabStrip.style.marginBottom = 6;
        m_tabStrip.style.flexShrink = 0;
        m_tabStrip.OnTabClicked += i => OnTabClicked?.Invoke(i);
        m_pickerContent.Add(m_tabStrip);

        // List
        m_listView = new UITListView();
        m_listView.Setup(m_font);
        m_listView.OnRowClicked += i => OnRowClicked?.Invoke(i);
        m_pickerContent.Add(m_listView);

        // Footer
        var footer = UITFactory.CreateColumn();
        footer.style.marginTop = 6;
        footer.style.flexShrink = 0;
        m_pickerContent.Add(footer);

        var key1 = new UITKeyCapRow();
        key1.Setup(new (string, string)[] { ("W", ""), ("S", "選択"), ("A", ""), ("D", "タブ") }, m_font);
        key1.style.marginBottom = 4;
        footer.Add(key1);

        var key2 = new UITKeyCapRow();
        key2.Setup(new (string, string)[] { ("Enter", "適用"), ("R", "Reset"), ("Esc", "閉じる") }, m_font);
        key2.style.marginBottom = 4;
        footer.Add(key2);

        var note = UITFactory.CreateLabel(
            "※ キーボード操作はカーソルがパネル上のときのみ有効",
            9, UITTheme.Text.Secondary, m_font, TextAnchor.UpperLeft);
        note.style.whiteSpace = WhiteSpace.Normal;
        footer.Add(note);

        var note2 = UITFactory.CreateLabel(
            "※ プラグイン有効後に、一度でも着用した衣装に切り替え可能",
            9, UITTheme.Text.Secondary, m_font, TextAnchor.UpperLeft);
        note2.style.whiteSpace = WhiteSpace.Normal;
        footer.Add(note2);
    }

    private void BuildSettingsContent()
    {
        m_resetAllButton = UITFactory.CreateButton(
            "解放状態を初期化",
            () => OnResetAllClicked?.Invoke(),
            12, m_font);
        m_resetAllButton.style.marginTop = 12;
        m_resetAllButton.style.marginBottom = 16;
        m_resetAllButton.style.paddingTop = 6;
        m_resetAllButton.style.paddingBottom = 6;
        m_settingsContent.Add(m_resetAllButton);

        m_unlockAllButton = UITFactory.CreateButton(
            "すべて解放",
            () => OnUnlockAllClicked?.Invoke(),
            12, m_font);
        m_unlockAllButton.style.marginBottom = 4;
        m_unlockAllButton.style.paddingTop = 6;
        m_unlockAllButton.style.paddingBottom = 6;
        m_settingsContent.Add(m_unlockAllButton);

        m_unlockAllNote = UITFactory.CreateLabel(
            "※ このキャラのGoodEndを見ると有効になります",
            9, UITTheme.Text.Secondary, m_font, TextAnchor.UpperLeft);
        m_unlockAllNote.style.whiteSpace = WhiteSpace.Normal;
        m_settingsContent.Add(m_unlockAllNote);

        // ─── 水着のパンスト微調整セクション ───
        var tuneHeading = UITFactory.CreateLabel(
            "水着のパンスト微調整",
            11, UITTheme.Text.Accent, m_font, TextAnchor.MiddleLeft);
        tuneHeading.style.marginTop = 16;
        tuneHeading.style.marginBottom = 4;
        m_settingsContent.Add(tuneHeading);

        m_offsetSlider = AddTuneSlider("パンスト押し出し", 0.01f, v => OnStockingOffsetChanged?.Invoke(v));
        m_skinShrinkSlider = AddTuneSlider("肌押し込み", 0.01f, v => OnStockingSkinShrinkChanged?.Invoke(v));
        m_falloffSlider = AddTuneSlider("境界フェード", 0.01f, v => OnStockingFalloffChanged?.Invoke(v));
        // 肌縮みフェードは blendShape 全 frame の再構築を伴って重いので、ドラッグ完了時のみ反映する。
        m_shapeFalloffSlider = AddTuneSlider("肌縮みフェード", 0.01f, v => OnStockingShapeFalloffChanged?.Invoke(v), commitOnly: true);

        var tuneNote = UITFactory.CreateLabel(
            "※ パンスト押し出し: 水着の食い込みは出るが水着を貫通\n" +
            "※ 肌押し込み: 貫通はしないが水着の食い込みは出ない",
            9, UITTheme.Text.Secondary, m_font, TextAnchor.UpperLeft);
        tuneNote.style.whiteSpace = WhiteSpace.Normal;
        tuneNote.style.marginTop = 4;
        m_settingsContent.Add(tuneNote);

        m_tuneResetButton = UITFactory.CreateButton(
            "パンスト設定をリセット",
            () => OnTuneResetClicked?.Invoke(),
            11, m_font);
        m_tuneResetButton.style.marginTop = 8;
        m_tuneResetButton.style.paddingTop = 4;
        m_tuneResetButton.style.paddingBottom = 4;
        m_settingsContent.Add(m_tuneResetButton);
    }

    private UITSlider AddTuneSlider(string label, float maxMeters, Action<float> onChanged, bool commitOnly = false)
    {
        var s = new UITSlider();
        s.Setup(label, 0f, maxMeters, m_font, FormatMm);
        if (commitOnly) s.OnValueCommitted += onChanged;
        else s.OnValueChanged += onChanged;
        m_settingsContent.Add(s);
        return s;
    }

    private void BuildHeaderButtons()
    {
        // ⚙ 設定ボタン（ピッカー中のみ表示）
        var gearTex = EmbeddedTexture.Load("BunnyGarden2FixMod.Resources.settings.png");
        m_settingsButton = UITFactory.CreateTextureButton(gearTex, () => OnSettingsClicked?.Invoke(), m_font);
        m_settingsButton.style.position = Position.Absolute;
        m_settingsButton.style.right = 36;
        m_settingsButton.style.top = 8;
        m_settingsButton.style.width = 22;
        m_settingsButton.style.height = 22;
        m_panel.Add(m_settingsButton);

        // ← 戻るボタン（設定中のみ表示、⚙ と同位置で排他）
        var backTex = EmbeddedTexture.Load("BunnyGarden2FixMod.Resources.arrow-big-left.png");
        m_backButton = UITFactory.CreateTextureButton(backTex, () => OnBackClicked?.Invoke(), m_font);
        m_backButton.style.position = Position.Absolute;
        m_backButton.style.right = 36;
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
    /// ◀▶ ナビの表示制御とフィールドを更新する。Render/RenderSettings 両モードから呼ぶ。
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
