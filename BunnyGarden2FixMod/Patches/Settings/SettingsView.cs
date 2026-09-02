using System;
using System.Collections.Generic;
using System.Linq;
using BunnyGarden2FixMod.Utils;
using UITKit;
using UITKit.Components;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace BunnyGarden2FixMod.Patches.Settings;

/// <summary>
/// F9 設定パネルのビュー。
/// サイドバー（カテゴリ一覧） + コンテンツ（選択カテゴリの行群）の 2 カラム構成。
/// Configs.UIEntries を foreach して UITSwitch / UITSlider / UITDropdown 行を生成する。
/// </summary>
public class SettingsView : MonoBehaviour
{
    public bool IsShown => m_root != null && m_root.style.display != DisplayStyle.None;

    private UIDocument m_doc;
    private PanelSettings m_settings;
    private VisualElement m_root;
    private VisualElement m_sidebar;
    private ScrollView m_content;
    private Font m_font;

    /// <summary>パネル固定高さ (panel px, PanelSettings.scale で拡縮)。内容がこれを超えると content が縦スクロールする。</summary>
    private const float kPanelHeight = 480f;

    private List<string> m_categories;
    private int m_selectedCategoryIndex = 0;
    private int m_selectedRowIndex = 0;
    private readonly List<RowHandle> m_currentRows = new();

    // ヘッダ（タイトル＋操作ヒント）。多言語対応のため Show() 時に再生成する。
    private VisualElement m_header;

    // キャプチャ中ヒント表示用 Label（BuildPanel で生成、非表示で待機）
    private Label m_captureHintLabel;

    // 自前 tooltip 用フィールド（Unity UI Toolkit の tooltip プロパティは BepInEx ランタイムで表示されない）
    private Label m_tooltipLabel;
    private IVisualElementScheduledItem m_tooltipShowTimer;
    private string m_tooltipPendingText;
    private Vector2 m_tooltipPendingMousePos;
    private VisualElement m_tooltipPendingRow;

    private struct RowHandle
    {
        public UIEntryMeta Entry;
        public VisualElement Row;
        public UITSwitch Switch;       // Toggle のときのみ非 null
        public UITSlider Slider;       // Slider のときのみ非 null
        public UITDropdown Dropdown;   // Dropdown のときのみ非 null
        public UITButton KeyCapBtn;    // KeyBinding の KB 側のみ非 null
        public UITDropdown PadDropdown;// KeyBinding の Pad 側のみ非 null
    }

    private void Awake()
    {
        // HideMoneyUIView と同じ方式: UITRuntime ヘルパで PanelSettings を生成し
        // themeStyleSheet をゲームから借用する。sortingOrder=9999 で常に最前面。
        m_font = UITRuntime.ResolveJapaneseFont(out _);
        PatchLogger.LogInfo($"[SettingsView] フォント: {(m_font != null ? m_font.name : "<null>")}");

        m_settings = UITRuntime.CreatePanelSettings(sortingOrder: 9999);
        if (m_settings.themeStyleSheet == null)
            PatchLogger.LogWarning("[SettingsView] themeStyleSheet を解決できませんでした");

        m_doc = UITRuntime.AttachDocument(gameObject, m_settings);

        BuildPanel();
        Hide(); // 起動時は非表示
    }

    private void OnDestroy()
    {
        // スケジュール済みの tooltip 表示タイマを停止（panel 破棄後に空 label を触るのを防ぐ）
        m_tooltipShowTimer?.Pause();
        m_tooltipShowTimer = null;
        if (m_settings != null)
        {
            UnityEngine.Object.Destroy(m_settings);
            m_settings = null;
        }
    }

    private void BuildPanel()
    {
        m_root = m_doc.rootVisualElement;
        m_root.style.position = Position.Absolute;
        m_root.style.right = 16;
        m_root.style.top = 20;
        m_root.style.width = 460;
        m_root.style.height = kPanelHeight; // ウィンドウサイズ固定。超過分は content の ScrollView でスクロール
        m_root.style.backgroundColor = new Color(0.118f, 0.133f, 0.188f, 1f); // #1e2230
        m_root.style.borderTopWidth = 1;
        m_root.style.borderRightWidth = 1;
        m_root.style.borderBottomWidth = 1;
        m_root.style.borderLeftWidth = 1;
        var borderColor = new Color(0.227f, 0.259f, 0.341f, 1f); // #3a4257
        m_root.style.borderTopColor = borderColor;
        m_root.style.borderRightColor = borderColor;
        m_root.style.borderBottomColor = borderColor;
        m_root.style.borderLeftColor = borderColor;
        m_root.style.borderTopLeftRadius = 8;
        m_root.style.borderTopRightRadius = 8;
        m_root.style.borderBottomLeftRadius = 8;
        m_root.style.borderBottomRightRadius = 8;

        // ── ヘッダ ───────────────────────────
        var header = new VisualElement();
        header.style.flexDirection = FlexDirection.Row;
        header.style.justifyContent = Justify.SpaceBetween;
        header.style.paddingLeft = 12;
        header.style.paddingRight = 12;
        header.style.paddingTop = 8;
        header.style.paddingBottom = 8;
        // 固定ヘッダー: 縮小させない。body 側の contentContainer が flex-shrink:0 で自然高を保つと
        // body の自然高が固定高 m_root を超え、既定 flex-shrink=1 のままだと header も比率圧縮されて
        // テキスト上部が見切れる。header を flex-shrink:0 で保護し、超過分は body のみに吸収させる。
        header.style.flexShrink = 0;

        m_header = header;
        PopulateHeader(); // タイトル＋ヒント。多言語対応のため Show() でも再生成する。
        m_root.Add(header);

        // ── 本体 (sidebar + content) ─────────
        // 固定高 m_root の残り（header 下）を body が埋め、その中で content (ScrollView) が縦スクロールする。
        var body = new VisualElement();
        body.style.flexDirection = FlexDirection.Row;
        body.style.flexGrow = 1;
        body.style.minHeight = 0; // 子 ScrollView がはみ出さず内部スクロールへ回るように 0 を許可
        // themeStyleSheet が null の環境では ScrollView 内部の既定 overflow:hidden USS が効かず、
        // スクロール内容が panel 外へ描画される。クリップは 2 段で行う:
        //   (1) 外側 wrapper である body（ここ。panel 矩形外への漏れを止める最終クリップ）
        //   (2) ScrollView 内部の contentViewport（後述 m_content 生成箇所）
        // ScrollView 要素 (m_content) 自身には overflow を付けない: 内部レイアウトが干渉して
        // contentContainer が viewport 高に拘束され、group 内 row やリセットボタンが flex-shrink で
        // 圧縮されスクロールが効かなくなるため。内部の contentViewport には付けてよい（別要素）。
        // m_root には掛けない: m_tooltipLabel が m_root 直下で row 下端付近に絶対配置されるため、
        // m_root をクリップすると最下段 row の tooltip が切れる。
        body.style.overflow = Overflow.Hidden;
        m_root.Add(body);

        m_sidebar = new VisualElement();
        m_sidebar.style.width = 130;
        m_sidebar.style.backgroundColor = new Color(0.094f, 0.110f, 0.153f, 1f); // #181c27
        m_sidebar.style.paddingTop = 6;
        m_sidebar.style.paddingBottom = 6;
        body.Add(m_sidebar);

        m_content = new ScrollView(ScrollViewMode.Vertical);
        m_content.style.flexGrow = 1;
        m_content.style.minHeight = 0;
        // 矩形外漏れのクリップは ScrollView 本体ではなく親 body に掛ける（上記 body のコメント参照）。
        // ここで m_content 自身に overflow を付けると contentContainer が圧縮されスクロールが壊れる。
        m_content.verticalScrollerVisibility = ScrollerVisibility.Auto;
        m_content.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
        // themeStyleSheet が null の環境では OnScrollWheel が ReadSingleLineHeight() で
        // NullReferenceException を投げるため mouseWheelScrollSize を明示する（UITListView と同じ回避策）。
        m_content.mouseWheelScrollSize = 30f;
        // themeStyleSheet が null だと ScrollView 内部の既定 USS（viewport の flex-grow/overflow、
        // contentContainer の flex-shrink）が適用されず、contentContainer が viewport 高に拘束されて
        // 子（group 行・リセットボタン）が flex-shrink で潰れ、超過分がスクロールしない。
        // 不足分を手動配線する:
        //   - viewport を ScrollView いっぱいに広げ overflow:Hidden でクリップ
        //   - contentContainer は flex-shrink:0 で自然高を保ち、超過分を scroller に回す
        m_content.contentViewport.style.flexGrow = 1;
        m_content.contentViewport.style.overflow = Overflow.Hidden;
        m_content.contentContainer.style.flexShrink = 0;
        // padding は viewport ではなく contentContainer 側に置いて従来の見た目を維持する。
        m_content.contentContainer.style.paddingTop = 8;
        m_content.contentContainer.style.paddingBottom = 8;
        m_content.contentContainer.style.paddingLeft = 12;
        m_content.contentContainer.style.paddingRight = 12;
        body.Add(m_content);

        // ↑↓←→ Tab Esc Space Enter は SettingsController が InputSystem 経由で処理するので、
        // UI Toolkit 側のナビゲーション伝播は止める（slider 等の二重反応を防ぐ）。
        m_root.RegisterCallback<KeyDownEvent>(evt =>
        {
            switch (evt.keyCode)
            {
                case KeyCode.UpArrow:
                case KeyCode.DownArrow:
                case KeyCode.LeftArrow:
                case KeyCode.RightArrow:
                case KeyCode.Tab:
                case KeyCode.Escape:
                case KeyCode.Space:
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    evt.StopPropagation();
                    break;
            }
        }, TrickleDown.TrickleDown);

        // キャプチャ中にパネル内の別の場所をクリックしたらキャプチャを解除する。
        // TrickleDown で子要素の ClickEvent より先に拾い、StartKeyCapture の再入ガードと二重防御する。
        // ただし現在キャプチャ中のボタン自身またはその祖先がクリックされた場合は解除しない。
        m_root.RegisterCallback<MouseDownEvent>(evt =>
        {
            var ctrl = SettingsController.Instance;
            if (ctrl == null || !ctrl.IsCapturingKey) return;
            // 現在キャプチャ中の KeyCapBtn を特定する
            var activeBtn = FindKeyCapBtn(ctrl.CapturingEntry);
            // クリック対象が activeBtn またはその子孫の場合は解除しない
            if (activeBtn != null && evt.target is VisualElement ve)
            {
                var node = ve;
                while (node != null)
                {
                    if (ReferenceEquals(node, activeBtn)) return;
                    node = node.parent;
                }
            }
            ctrl.CancelKeyCapture();
        }, TrickleDown.TrickleDown);

        // 自前 tooltip 用のフローティング Label を m_root に追加する。
        // Unity UI Toolkit の `tooltip` プロパティは BepInEx ランタイムでは表示されないことがあるため、
        // MouseEnter / Leave イベントで自前管理する。
        m_tooltipLabel = new Label();
        m_tooltipLabel.style.position = Position.Absolute;
        m_tooltipLabel.style.maxWidth = 320;
        m_tooltipLabel.style.whiteSpace = WhiteSpace.Normal;
        m_tooltipLabel.style.backgroundColor = new Color(0.10f, 0.12f, 0.16f, 0.95f);
        m_tooltipLabel.style.color = new Color(0.92f, 0.94f, 0.97f, 1f);
        m_tooltipLabel.style.borderTopWidth = m_tooltipLabel.style.borderRightWidth =
            m_tooltipLabel.style.borderBottomWidth = m_tooltipLabel.style.borderLeftWidth = 1;
        var tipBorder = new Color(0.30f, 0.34f, 0.42f, 1f);
        m_tooltipLabel.style.borderTopColor = m_tooltipLabel.style.borderRightColor =
            m_tooltipLabel.style.borderBottomColor = m_tooltipLabel.style.borderLeftColor = tipBorder;
        m_tooltipLabel.style.borderTopLeftRadius = m_tooltipLabel.style.borderTopRightRadius =
            m_tooltipLabel.style.borderBottomLeftRadius = m_tooltipLabel.style.borderBottomRightRadius = 4;
        m_tooltipLabel.style.paddingTop = m_tooltipLabel.style.paddingBottom = 6;
        m_tooltipLabel.style.paddingLeft = m_tooltipLabel.style.paddingRight = 8;
        m_tooltipLabel.style.fontSize = 10;
        m_tooltipLabel.style.display = DisplayStyle.None;
        m_tooltipLabel.pickingMode = PickingMode.Ignore;
        if (m_font != null) m_tooltipLabel.style.unityFont = m_font;
        m_root.Add(m_tooltipLabel);

        // キャプチャ中ヒント: パネル下端に固定表示。StartKeyCapture で表示、解除時に非表示。
        m_captureHintLabel = new Label(Loc.Tr("任意のキーを押してください  Esc=キャンセル / BS,Del=未割当"));
        m_captureHintLabel.style.color = new Color(1f, 0.85f, 0.4f, 1f); // 黄色系ヒント色
        m_captureHintLabel.style.fontSize = 10;
        m_captureHintLabel.style.paddingTop = 4;
        m_captureHintLabel.style.paddingBottom = 4;
        m_captureHintLabel.style.paddingLeft = 12;
        m_captureHintLabel.style.paddingRight = 12;
        m_captureHintLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
        m_captureHintLabel.style.backgroundColor = new Color(0.094f, 0.110f, 0.153f, 1f); // sidebar と同色
        m_captureHintLabel.style.display = DisplayStyle.None;
        if (m_font != null) m_captureHintLabel.style.unityFont = m_font;
        m_root.Add(m_captureHintLabel);

        BuildSidebar();
        RenderContent();
    }

    private void ShowTooltipAtCursor()
    {
        if (m_tooltipLabel == null || m_root == null) return;
        if (string.IsNullOrEmpty(m_tooltipPendingText)) return;
        m_tooltipLabel.text = m_tooltipPendingText;
        m_tooltipLabel.style.display = DisplayStyle.Flex;

        var rootBounds = m_root.worldBound;

        // X = カーソルの X 座標を root 相対系に変換（マウス追従はせず enter 時の位置で固定）
        var cursorRelX = m_tooltipPendingMousePos.x - rootBounds.x;

        // Y = 行の直下に表示（行下端 + 4px のマージン）
        float relY;
        if (m_tooltipPendingRow != null)
        {
            var rowBounds = m_tooltipPendingRow.worldBound;
            relY = (rowBounds.y - rootBounds.y) + rowBounds.height + 4f;
        }
        else
        {
            relY = (m_tooltipPendingMousePos.y - rootBounds.y) + 18f;
        }

        // パネル右端からはみ出す場合はカーソルの少し左に寄せる
        const float maxTooltipWidth = 320f;
        var panelW = m_root.layout.width;
        var leftCandidate = cursorRelX;
        if (leftCandidate + maxTooltipWidth > panelW)
            leftCandidate = Mathf.Max(4f, panelW - maxTooltipWidth - 4f);
        if (leftCandidate < 4f) leftCandidate = 4f;

        m_tooltipLabel.style.left = leftCandidate;
        m_tooltipLabel.style.top = relY;
    }

    private void HideTooltip()
    {
        if (m_tooltipLabel == null) return;
        m_tooltipLabel.style.display = DisplayStyle.None;
    }

    private void BuildSidebar()
    {
        m_categories = Configs.UIEntries
            .Select(e => e.Category)
            .Distinct()
            .ToList();

        m_sidebar.Clear();
        for (int i = 0; i < m_categories.Count; i++)
        {
            int idx = i;

            var btn = new VisualElement();
            btn.style.flexDirection = FlexDirection.Row;
            btn.style.alignItems = Align.Center;
            btn.style.paddingLeft = 12;
            btn.style.paddingRight = 8;
            btn.style.paddingTop = 6;
            btn.style.paddingBottom = 6;

            var nameLabel = new Label(Loc.Tr(m_categories[i]));
            nameLabel.style.color = new Color(0.84f, 0.87f, 0.91f, 1f);
            nameLabel.style.fontSize = 11;
            nameLabel.style.flexGrow = 1;
            nameLabel.style.whiteSpace = WhiteSpace.NoWrap;
            nameLabel.style.overflow = Overflow.Hidden;
            if (m_font != null) nameLabel.style.unityFont = m_font;
            btn.Add(nameLabel);

            // 1-9 のキージャンプに対応する番号を KeyCap で右端に表示
            if (i < 9)
            {
                var cap = UITKit.UITFactory.CreateKeyCap($"{i + 1}", string.Empty, m_font);
                cap.style.marginRight = 0;
                btn.Add(cap);
            }

            btn.RegisterCallback<ClickEvent>(_ => SelectCategory(idx));
            m_sidebar.Add(btn);
        }
        ApplySidebarHighlight();
    }

    private void ApplySidebarHighlight()
    {
        for (int i = 0; i < m_sidebar.childCount; i++)
        {
            var item = m_sidebar[i];
            item.style.backgroundColor = (i == m_selectedCategoryIndex)
                ? new Color(0.18f, 0.21f, 0.29f, 1f)
                : new Color(0, 0, 0, 0);
        }
    }

    public void SelectCategory(int index)
    {
        if (index < 0 || index >= m_categories.Count) return;
        SettingsController.Instance?.CancelKeyCapture(); // カテゴリ切替時はキャプチャ状態を解除
        m_selectedCategoryIndex = index;
        m_selectedRowIndex = 0;
        ApplySidebarHighlight();
        RenderContent();
    }

    private void RenderContent()
    {
        // RenderContent はキャプチャ確定からも呼ばれるが、CancelKeyCapture は冪等のため
        // ここでは呼ばない。別経路 (SelectCategory / ResetCurrentCategory / Hide) がキャンセル済み。

        // 旧行が dropdown / PadDropdown を開いていた場合、Clear する前にポップアップ overlay を回収する
        // （ポップアップは row 階層ではなく panel.visualTree 直下に挿入されているため）。
        foreach (var h in m_currentRows)
        {
            if (h.Dropdown != null && h.Dropdown.IsPopupOpen) h.Dropdown.ClosePopup();
            if (h.PadDropdown != null && h.PadDropdown.IsPopupOpen) h.PadDropdown.ClosePopup();
        }
        m_content.Clear();
        m_currentRows.Clear();
        // カテゴリ切替・再描画ごとにスクロール位置を先頭へ戻す（前カテゴリの scrollOffset 残留を防ぐ）。
        m_content.scrollOffset = Vector2.zero;
        // カテゴリ切替時に残留 tooltip を非表示にする
        HideTooltip();

        if (m_categories == null || m_categories.Count == 0) return;
        var category = m_categories[m_selectedCategoryIndex];

        // ── 行 ──────────────────────────────────────
        // YAML 宣言順 (= UIEntries の配列順) がそのままカテゴリ内表示順になる。
        // group が指定された連続ランは折りたためるグループにまとめる。group 未指定行は従来どおりフラット。
        var entries = Configs.UIEntries
            .Where(e => e.Category == category)
            .ToList();

        int idx = 0;
        while (idx < entries.Count)
        {
            var group = entries[idx].Group;
            if (string.IsNullOrEmpty(group))
            {
                var handle = BuildRow(entries[idx]);
                m_content.Add(handle.Row);
                m_currentRows.Add(handle);
                idx++;
                continue;
            }

            int runStart = idx;
            while (idx < entries.Count && entries[idx].Group == group) idx++;

            bool collapsed = SettingsCollapseState.IsCollapsed(category, group);
            m_content.Add(BuildGroupHeader(category, group, collapsed));

            if (!collapsed)
            {
                var container = new VisualElement();
                container.style.marginLeft = 8; // グループ内をインデント
                for (int j = runStart; j < idx; j++)
                {
                    var handle = BuildRow(entries[j]);
                    container.Add(handle.Row);
                    m_currentRows.Add(handle);
                }
                m_content.Add(container);
            }
        }

        // 折りたたみで行数が減った場合に選択 index がはみ出さないようクランプする。
        // 注: マウスでヘッダーを折りたたんだ際の選択行追従は行わない（キーボード移動のスキップのみ要件）。
        //     畳んだ結果 index がずれてハイライトが別行に移ることは許容する。
        m_selectedRowIndex = (m_currentRows.Count == 0)
            ? 0
            : Mathf.Clamp(m_selectedRowIndex, 0, m_currentRows.Count - 1);

        ApplyRowHighlight();

        // ── ページ下部にリセットボタン ──────────────
        var resetBtn = new UITButton();
        resetBtn.Setup(Loc.Tr("初期値に戻す"), () => ResetCurrentCategory(), m_font);
        resetBtn.SetVariant(UITButton.Variant.Subtle);
        resetBtn.style.marginTop = 12;
        resetBtn.style.alignSelf = Align.Center;
        resetBtn.tooltip = "このカテゴリの全項目を既定値に戻します"; // 念のため（実表示は自前 tooltip 経由）
        m_content.Add(resetBtn);
    }

    private void ResetCurrentCategory()
    {
        if (m_categories == null || m_categories.Count == 0) return;
        // キャプチャ中に「初期値に戻す」が押されてもキャプチャ状態が残ると stale entry を指すため明示的にキャンセル。
        SettingsController.Instance?.CancelKeyCapture();
        var category = m_categories[m_selectedCategoryIndex];
        foreach (var entry in Configs.UIEntries.Where(e => e.Category == category))
        {
            if (entry.Kind == UIKind.KeyBinding)
            {
                var hotkey = entry.HotkeyProvider?.Invoke();
                if (hotkey?.KeyConfig != null)
                {
                    var def = (UnityEngine.InputSystem.Key)((BepInEx.Configuration.ConfigEntryBase)hotkey.KeyConfig).DefaultValue;
                    hotkey.KeyConfig.Value = def;
                }
                if (hotkey?.ButtonConfig != null)
                {
                    var def = (ControllerButton)((BepInEx.Configuration.ConfigEntryBase)hotkey.ButtonConfig).DefaultValue;
                    hotkey.ButtonConfig.Value = def;
                }
            }
            else
            {
                entry.Accessor.ResetToDefault();
            }
        }
        // 新しい値を UI に反映するため再描画
        RenderContent();
    }

    private RowHandle BuildRow(UIEntryMeta entry)
    {
        var row = new VisualElement();
        row.style.flexDirection = FlexDirection.Row;
        row.style.alignItems = Align.Center;
        // height ではなく minHeight にすることで、UITSlider の名前ラベルが折り返した際に
        // 行高さが追従して伸びる（短いラベルなら 28px のまま）。
        row.style.minHeight = 28;
        row.style.marginBottom = 2;
        row.style.paddingLeft = 8;
        row.style.paddingRight = 8;
        row.style.backgroundColor = new Color(0.145f, 0.169f, 0.227f, 1f); // #252b3a
        row.style.borderTopLeftRadius = 3;
        row.style.borderTopRightRadius = 3;
        row.style.borderBottomLeftRadius = 3;
        row.style.borderBottomRightRadius = 3;

        // 自前 tooltip: 行に hover → 500ms 後にマウス位置に description を表示
        // Unity UI Toolkit の tooltip プロパティは BepInEx ランタイムでは表示されないため自前管理する
        if (!string.IsNullOrEmpty(entry.Desc))
        {
            var desc = Loc.Tr(entry.Desc);
            row.RegisterCallback<MouseEnterEvent>(evt =>
            {
                m_tooltipShowTimer?.Pause();
                m_tooltipPendingText = desc;
                m_tooltipPendingMousePos = evt.mousePosition;
                m_tooltipPendingRow = row;
                m_tooltipShowTimer = m_tooltipLabel.schedule.Execute(ShowTooltipAtCursor).StartingIn(500);
            });
            row.RegisterCallback<MouseLeaveEvent>(_ =>
            {
                m_tooltipShowTimer?.Pause();
                HideTooltip();
            });
        }

        if (entry.Kind == UIKind.Toggle)
        {
            var sw = new UITSwitch();
            sw.Setup(Loc.Tr(entry.Label), entry.Accessor.GetFloat() >= 0.5f, m_font);
            sw.OnValueChanged += v => entry.Accessor.SetFloat(v ? 1f : 0f);
            // row の paddingLeft=8/Right=8 帯は UITSwitch が覆わないため、その細い余白クリックも
            // 取りこぼさないよう row 側で Toggle を呼ぶ。UITSwitch 内クリックは sw 自身が処理済み
            // なので、target が row 自身のときだけ拾うことで二重 toggle を防ぐ。
            row.RegisterCallback<ClickEvent>(evt => { if (evt.target == row) sw.Toggle(); });
            row.Add(sw);
            return new RowHandle { Entry = entry, Row = row, Switch = sw };
        }
        else if (entry.Kind == UIKind.Dropdown)
        {
            var dd = new UITDropdown();
            dd.Setup(Loc.Tr(entry.Label), entry.DropdownOptions, m_font);
            // EnumAccessor.GetFloat() は Enum.GetValues 上の index を返す。
            dd.SetIndex((int)Math.Round(entry.Accessor.GetFloat()));
            dd.OnValueChanged += i => entry.Accessor.SetFloat(i);
            // row 余白クリックでもポップアップを開けるようにする（ボタン外側帯のクリックを取りこぼさない）。
            row.RegisterCallback<ClickEvent>(evt => { if (evt.target == row) dd.TogglePopup(); });
            row.Add(dd);
            return new RowHandle { Entry = entry, Row = row, Dropdown = dd };
        }
        else if (entry.Kind == UIKind.KeyBinding)
        {
            // ── ラベル ──
            var label = new Label(Loc.Tr(entry.Label));
            label.style.color = new Color(0.84f, 0.87f, 0.91f, 1f);
            label.style.fontSize = 11;
            label.style.flexGrow = 1;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.style.overflow = Overflow.Hidden;
            if (m_font != null) label.style.unityFont = m_font;
            row.Add(label);

            var hotkey = entry.HotkeyProvider?.Invoke();
            if (hotkey == null)
            {
                PatchLogger.LogWarning($"[SettingsView] KeyBinding 行 '{entry.Label}' の HotkeyProvider が null のためスキップ");
                return new RowHandle { Entry = entry, Row = row };
            }

            // ── KB 側ボタン（クリックでキャプチャ開始）幅 50px 固定 ──
            var kbBtn = new UITButton();
            var kbText = FormatKeyText(hotkey.KeyConfig?.Value);
            kbBtn.Setup(kbText, () => SettingsController.Instance?.StartKeyCapture(entry), m_font);
            kbBtn.SetVariant(UITButton.Variant.Subtle);
            kbBtn.SetWidth(50);
            kbBtn.style.flexGrow = 0;
            kbBtn.style.flexShrink = 0;
            kbBtn.style.marginLeft = 4;
            // 内部 label の fontSize を縮小して "Backspace" 等の長テキストを収める。
            // 50px に収まらない場合は ellipsis で末尾省略（"Backspace" は "Backsp..." 等になり得る）。
            var kbInnerLabel = kbBtn.Q<Label>();
            if (kbInnerLabel != null)
            {
                kbInnerLabel.style.fontSize = 9;
                kbInnerLabel.style.whiteSpace = WhiteSpace.NoWrap;
                kbInnerLabel.style.overflow = Overflow.Hidden;
                kbInnerLabel.style.textOverflow = TextOverflow.Ellipsis;
            }
            row.Add(kbBtn);

            // ── 区切り "/" ──
            var sep = new Label("/");
            sep.style.color = new Color(0.5f, 0.55f, 0.65f, 1f);
            sep.style.fontSize = 11;
            sep.style.marginLeft = 4;
            sep.style.marginRight = 4;
            if (m_font != null) sep.style.unityFont = m_font;
            row.Add(sep);

            // ── Pad 側 dropdown 幅 50px 固定 ──
            var padDd = new UITDropdown();
            padDd.Setup(string.Empty, entry.DropdownOptions, m_font);
            int padIdx = ResolvePadIndex(entry.DropdownOptions, hotkey.ButtonConfig?.Value);
            padDd.SetIndex(padIdx);
            padDd.OnValueChanged += i => OnPadChanged(entry, i);
            padDd.SetWidth(50);
            padDd.SetButtonFontSize(9);
            row.Add(padDd);

            return new RowHandle { Entry = entry, Row = row, KeyCapBtn = kbBtn, PadDropdown = padDd };
        }
        else
        {
            var sl = new UITSlider();
            // IntAccessor の場合は表示も int に丸める。UITSlider 内部値は float のため、
            // formatter に raw float を渡すと "{0}x" が "3.51x" のように小数を含んで描画される。
            // ストレージ側は IntAccessor.SetFloat で snap されるので保存値と表示値の不整合を防ぐ意味でも
            // ここで丸めを適用する。
            Func<float, string> formatter = entry.Accessor is IntAccessor
                ? (v => string.Format(entry.Format, (int)Math.Round(v)))
                : (v => string.Format(entry.Format, v));
            sl.Setup(Loc.Tr(entry.Label), entry.SliderMin, entry.SliderMax, m_font, formatter);
            // SetValue は m_suppressEvents により OnValueChanged を発火させない安全な初期値設定
            sl.SetValue(entry.Accessor.GetFloat());
            // F9 パネルではスライダー上のホイールで値を変えず、ScrollView のスクロールに使う。
            // SetStep を呼ばない（m_step==0）と UITSlider.OnWheel は StopPropagation せず親へバブルする。
            // キーボード ←→ の値変更は NudgeSelectedSlider が entry.SliderStep を直接使うため影響しない。
            sl.OnValueChanged += v => entry.Accessor.SetFloat(v);
            row.Add(sl);
            return new RowHandle { Entry = entry, Row = row, Slider = sl };
        }
    }

    /// <summary>折りたたみグループのヘッダー行を生成する。クリックで開閉をトグルし RenderContent で再描画する。</summary>
    private VisualElement BuildGroupHeader(string section, string group, bool collapsed)
    {
        var header = new VisualElement();
        header.focusable = false; // マウス専用。キーボードナビ(↑↓/Space)対象外（m_currentRows にも積まない）。
        header.style.flexDirection = FlexDirection.Row;
        header.style.alignItems = Align.Center;
        header.style.minHeight = 24;
        header.style.marginTop = 4;
        header.style.marginBottom = 2;
        header.style.paddingLeft = 6;
        header.style.paddingRight = 8;
        header.style.backgroundColor = new Color(0.094f, 0.110f, 0.153f, 1f); // #181c27 (sidebar と同色)
        header.style.borderTopLeftRadius = 3;
        header.style.borderTopRightRadius = 3;
        header.style.borderBottomLeftRadius = 3;
        header.style.borderBottomRightRadius = 3;

        var arrow = new Label(collapsed ? "▶" : "▼");
        arrow.style.color = new Color(0.6f, 0.65f, 0.75f, 1f);
        arrow.style.fontSize = 10;
        arrow.style.width = 12;
        arrow.style.marginRight = 6;
        if (m_font != null) arrow.style.unityFont = m_font;
        header.Add(arrow);

        var name = new Label(Loc.Tr(group));
        name.style.color = new Color(0.84f, 0.87f, 0.91f, 1f);
        name.style.fontSize = 11;
        name.style.flexGrow = 1;
        name.style.whiteSpace = WhiteSpace.NoWrap;
        name.style.overflow = Overflow.Hidden;
        if (m_font != null) name.style.unityFont = m_font;
        header.Add(name);

        header.RegisterCallback<ClickEvent>(_ =>
        {
            // ヘッダー生成時の collapsed クロージャ値ではなく現在値を再読して反転する。
            SettingsCollapseState.SetCollapsed(section, group, !SettingsCollapseState.IsCollapsed(section, group));
            RenderContent();
        });

        return header;
    }

    private void ApplyRowHighlight()
    {
        for (int i = 0; i < m_currentRows.Count; i++)
        {
            var bg = (i == m_selectedRowIndex)
                ? new Color(0.176f, 0.204f, 0.290f, 1f)  // #2d344a
                : new Color(0.145f, 0.169f, 0.227f, 1f); // #252b3a
            m_currentRows[i].Row.style.backgroundColor = bg;
        }
    }

    /// <summary>
    /// ヘッダ（タイトル＋操作ヒント）を現在言語で生成する。BuildPanel は Awake 時に走り Loc 未解決の
    /// ことがあるため、Show() でも再生成して言語を反映する。
    /// </summary>
    private void PopulateHeader()
    {
        if (m_header == null) return;
        m_header.Clear();

        var title = new Label(Loc.Tr("FixMod 設定"));
        title.style.color = new Color(0.84f, 0.87f, 0.91f, 1f);
        title.style.fontSize = 13;
        if (m_font != null) title.style.unityFont = m_font;
        m_header.Add(title);

        var hints = new UITKeyCapRow();
        hints.Setup(new (string, string)[]
        {
            ("F9",    Loc.Tr("閉じる")),
            ("↑↓",   Loc.Tr("移動")),
            ("←→",   Loc.Tr("値")),
            ("Space", Loc.Tr("切替")),
            ("Tab",   Loc.Tr("カテゴリ")),
        }, m_font);
        m_header.Add(hints);
    }

    public void Show()
    {
        if (m_root == null) return;
        // 開くたびに最新のゲーム言語を解決して UI 文字列の訳を選び直す（多言語対応, issue #52）。
        Loc.Refresh();
        PopulateHeader(); // ヘッダは Awake 構築時に Loc 未解決のことがあるため開く度に再生成する。
        BuildSidebar(); // サイドバーも同様に Loc 未解決のことがあるため開く度に再生成する。
        // PanelSettings.scale は Awake 時の値を保持するため、開く度に Configs.UIScale を反映する。
        if (m_settings != null) m_settings.scale = Configs.UIScale.Value;
        // 毎回先頭カテゴリ・先頭行から開始する。
        // 注: グループ折りたたみ状態は SettingsCollapseState でディスク永続化され、ここではリセットしない。
        m_selectedCategoryIndex = 0;
        m_selectedRowIndex = 0;
        ApplySidebarHighlight();
        m_root.style.display = DisplayStyle.Flex;
        // 開く度に最新値を反映するため再描画（.cfg 直編集との同期）
        RenderContent();
    }

    public void Hide()
    {
        if (m_root == null) return;
        // キャプチャ中にパネルが閉じられた場合は状態をリセット（再表示時のフラグ残留を防ぐ）
        SettingsController.Instance?.CancelKeyCapture();
        // 開いている dropdown / PadDropdown ポップアップは panel.visualTree 直下にあるため、
        // パネル display を None にしても残ることがある。明示的に閉じる。
        foreach (var h in m_currentRows)
        {
            if (h.Dropdown != null && h.Dropdown.IsPopupOpen) h.Dropdown.ClosePopup();
            if (h.PadDropdown != null && h.PadDropdown.IsPopupOpen) h.PadDropdown.ClosePopup();
        }
        m_root.style.display = DisplayStyle.None;
    }

    public bool IsPointerOverPanel()
    {
        if (m_root == null || !IsShown) return false;
        if (m_root.panel == null) return false;
        var mouse = Mouse.current;
        if (mouse == null) return false;
        var raw = mouse.position.ReadValue();
        // UI Toolkit の worldBound はパネル座標系（Y 原点が左上）のため ScreenToPanel で変換する
        var flipped = new Vector2(raw.x, Screen.height - raw.y);
        var panelPos = RuntimePanelUtils.ScreenToPanel(m_root.panel, flipped);
        return m_root.worldBound.Contains(panelPos);
    }

    // ── キーボード操作 ─────────────────────────

    public void HandleKeyArrowUp()
    {
        m_selectedRowIndex = Mathf.Max(0, m_selectedRowIndex - 1);
        ApplyRowHighlight();
        ScrollSelectedRowIntoView();
    }

    public void HandleKeyArrowDown()
    {
        // Count==0（全折りたたみ）でも -1 に落ちないよう Max(0, …) で下限を固定する。
        m_selectedRowIndex = Mathf.Max(0, Mathf.Min(m_currentRows.Count - 1, m_selectedRowIndex + 1));
        ApplyRowHighlight();
        ScrollSelectedRowIntoView();
    }

    /// <summary>固定高 ScrollView 化に伴い、↑↓ で viewport 外へ移動した選択行を可視域へスクロールする。</summary>
    private void ScrollSelectedRowIntoView()
    {
        if (m_content == null) return;
        if (m_selectedRowIndex < 0 || m_selectedRowIndex >= m_currentRows.Count) return;
        var row = m_currentRows[m_selectedRowIndex].Row;
        if (row != null) m_content.ScrollTo(row);
    }

    public void HandleKeyArrowLeft(bool shift)
    {
        if (m_selectedRowIndex < 0 || m_selectedRowIndex >= m_currentRows.Count) return;
        var h = m_currentRows[m_selectedRowIndex];
        if (h.Slider != null)        NudgeSelectedSlider(shift ? -10 : -1);
        else if (h.Dropdown != null) h.Dropdown.Cycle(-1); // shift は無視（要素数が少ないため）
        else if (h.Switch != null)   h.Switch.SetValue(false);
    }

    public void HandleKeyArrowRight(bool shift)
    {
        if (m_selectedRowIndex < 0 || m_selectedRowIndex >= m_currentRows.Count) return;
        var h = m_currentRows[m_selectedRowIndex];
        if (h.Slider != null)        NudgeSelectedSlider(shift ? +10 : +1);
        else if (h.Dropdown != null) h.Dropdown.Cycle(+1);
        else if (h.Switch != null)   h.Switch.SetValue(true);
    }

    public void HandleKeyConfirm()
    {
        if (m_selectedRowIndex < 0 || m_selectedRowIndex >= m_currentRows.Count) return;
        var h = m_currentRows[m_selectedRowIndex];
        if (h.Switch != null)             h.Switch.Toggle();
        else if (h.Dropdown != null)      h.Dropdown.Cycle(+1);
        else if (h.KeyCapBtn != null)     SettingsController.Instance?.StartKeyCapture(h.Entry); // Space/Enter で KB キャプチャ開始
        // KeyBinding 行の Slider は null のため HandleKeyArrowLeft/Right も自然に何もしない
    }

    public void HandleKeyTabNext()
    {
        if (m_categories == null || m_categories.Count == 0) return;
        SettingsController.Instance?.CancelKeyCapture(); // Tab でカテゴリ移動時はキャプチャを解除
        m_selectedCategoryIndex = (m_selectedCategoryIndex + 1) % m_categories.Count;
        m_selectedRowIndex = 0;
        ApplySidebarHighlight();
        RenderContent();
    }

    public void HandleKeyTabPrev()
    {
        if (m_categories == null || m_categories.Count == 0) return;
        SettingsController.Instance?.CancelKeyCapture(); // Shift+Tab でカテゴリ移動時はキャプチャを解除
        m_selectedCategoryIndex = (m_selectedCategoryIndex - 1 + m_categories.Count) % m_categories.Count;
        m_selectedRowIndex = 0;
        ApplySidebarHighlight();
        RenderContent();
    }

    public void HandleKeyCategoryJump(int oneBasedIndex)
    {
        SelectCategory(oneBasedIndex - 1);
    }

    private void NudgeSelectedSlider(int steps)
    {
        if (m_selectedRowIndex < 0 || m_selectedRowIndex >= m_currentRows.Count) return;
        var h = m_currentRows[m_selectedRowIndex];
        if (h.Slider == null) return;
        var entry = h.Entry;
        var current = entry.Accessor.GetFloat();
        var next = Mathf.Clamp(current + steps * entry.SliderStep, entry.SliderMin, entry.SliderMax);
        entry.Accessor.SetFloat(next);
        // step snap 後の最終値で UI を同期（イベント抑制付き SetValue）
        h.Slider.SetValue(entry.Accessor.GetFloat());
    }

    // ── Controller から呼ばれる UI 操作 API ──────────────────────────────────

    /// <summary>キャプチャ開始時に Controller から呼ばれる UI 反映。</summary>
    public void OnCaptureStarted(UIEntryMeta entry)
    {
        if (entry == null) return;
        var btn = FindKeyCapBtn(entry);
        if (btn != null) btn.SetText("...");
        // キャプチャ開始時にヒントを表示する
        if (m_captureHintLabel != null) m_captureHintLabel.style.display = DisplayStyle.Flex;
    }

    /// <summary>キャプチャ終了時に Controller から呼ばれる UI 反映 (Cancel / Confirm 共通)。</summary>
    public void OnCaptureEnded(UIEntryMeta entry)
    {
        if (entry != null)
        {
            var btn = FindKeyCapBtn(entry);
            if (btn != null)
            {
                var hotkey = entry.HotkeyProvider?.Invoke();
                btn.SetText(FormatKeyText(hotkey?.KeyConfig?.Value));
            }
        }
        // キャプチャ解除時にヒントを非表示にする
        if (m_captureHintLabel != null) m_captureHintLabel.style.display = DisplayStyle.None;
    }

    /// <summary>HandleCapturedKey 確定後に Controller から呼ばれる: 行を再描画して新値を反映。</summary>
    public void RequestRebuild()
    {
        // RenderContent はイベント発火中の VisualElement 破棄を避けるため次フレームに遅延する。
        m_root?.schedule.Execute(RenderContent).StartingIn(0);
    }

    // ── KeyBinding ヘルパ ──────────────────────────

    /// <summary>指定エントリに対応する KeyCapBtn を m_currentRows から検索する。</summary>
    private UITButton FindKeyCapBtn(UIEntryMeta entry)
    {
        if (entry == null) return null;
        foreach (var h in m_currentRows)
        {
            if (ReferenceEquals(h.Entry, entry)) return h.KeyCapBtn;
        }
        return null;
    }

    /// <summary>Key 値をボタン表示用テキストに変換する。None は "Unbound"。</summary>
    private static string FormatKeyText(UnityEngine.InputSystem.Key? key)
    {
        if (key == null || key.Value == UnityEngine.InputSystem.Key.None) return "Unbound";
        return key.Value.ToString();
    }

    /// <summary>ControllerButton 値を DropdownOptions 配列の index に変換する。見つからない場合は 0。</summary>
    private static int ResolvePadIndex(string[] options, ControllerButton? value)
    {
        if (options == null || options.Length == 0) return 0;
        if (value == null) return 0;
        var name = value.Value.ToString();
        for (int i = 0; i < options.Length; i++)
        {
            if (options[i] == name) return i;
        }
        return 0;
    }

    /// <summary>Pad ドロップダウン変更時の処理。衝突時はスワップして次フレームに再描画。</summary>
    private void OnPadChanged(UIEntryMeta entry, int newIndex)
    {
        var hotkey = entry.HotkeyProvider?.Invoke();
        if (hotkey?.ButtonConfig == null) return;
        if (newIndex < 0 || entry.DropdownOptions == null || newIndex >= entry.DropdownOptions.Length) return;
        if (!System.Enum.TryParse<ControllerButton>(entry.DropdownOptions[newIndex], out var newBtn)) return;

        var oldBtn = hotkey.ButtonConfig.Value;
        if (oldBtn == newBtn) return;
        hotkey.ButtonConfig.Value = newBtn;

        // 衝突スワップ
        if (newBtn != ControllerButton.None)
        {
            foreach (var other in Configs.UIEntries)
            {
                if (other.Kind != UIKind.KeyBinding) continue;
                if (ReferenceEquals(other, entry)) continue;
                var otherHk = other.HotkeyProvider?.Invoke();
                if (otherHk?.ButtonConfig == null) continue;
                if (otherHk.ButtonConfig.Value == newBtn)
                {
                    otherHk.ButtonConfig.Value = oldBtn;
                    PatchLogger.LogInfo($"[SettingsView] Pad 衝突: '{other.Label}' を {newBtn}→{oldBtn} にスワップ");
                }
            }
        }

        // UITDropdown の OnValueChanged 発火中に RenderContent で popup を破棄すると
        // 例外/popup 残留の懸念があるため、次フレームへ遅延する。
        m_root?.schedule.Execute(RenderContent).StartingIn(0);
    }
}
