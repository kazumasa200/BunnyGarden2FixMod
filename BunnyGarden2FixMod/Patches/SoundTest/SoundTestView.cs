using System;
using System.Collections.Generic;
using BunnyGarden2FixMod.Utils;
using UITKit;
using UITKit.Components;
using UnityEngine;
using UnityEngine.UIElements;

namespace BunnyGarden2FixMod.Patches.SoundTest;

/// <summary>
/// サウンドテストの画面。F9 パネル・衣装ピッカーと同じ UITKit の部品で組む。
/// 左に曲リスト、右に曲名・使用場所・版の切り替え・歌詞・再生位置。状態は持たず、Controller が描画を指示する。
/// </summary>
public class SoundTestView : MonoBehaviour
{
    public event Action<int> OnRowClicked;
    public event Action<int> OnVersionClicked;
    public event Action<float> OnSeek;          // 0..1
    public event Action<float> OnVolumeChanged;   // %（0..200）、ドラッグ中も飛ぶ
    public event Action<float> OnVolumeCommitted; // %（0..200）、手を離したとき
    public event Action OnPlayClicked;
    public event Action OnRepeatClicked;
    public event Action OnCloseClicked;

    private PanelSettings m_settings;
    private UIDocument m_doc;
    private Font m_font;
    private VisualElement m_root;
    private VisualElement m_panel;

    private UITListView m_list;
    private Label m_title;
    private Label m_kind;
    private Label m_note;
    private VisualElement m_versionRow;
    private ScrollView m_lyricScroll;
    private readonly List<Label> m_lyricLabels = new();
    private int m_lyricCurrent = -1;
    private float m_holdAutoScrollUntil;   // 手動スクロール直後は自動追従を止める
    private UITSlider m_seek;
    private UITSlider m_volume;
    private Label m_time;
    private UITButton m_playButton;
    private VisualElement m_repeatSlot;   // [Y リピート: 1曲] のキーキャップを差し替える場所
    private Button m_repeatButton;
    private readonly Texture2D[] m_repeatIcons = new Texture2D[3];   // One / All / None
    private float m_length;

    public bool IsShown => m_root != null && m_root.style.display != DisplayStyle.None;

    public void Show()
    {
        EnsureBuilt();
        if (m_settings != null)
            m_settings.scale = Configs.UIScale.Value;
        m_root.style.display = DisplayStyle.Flex;
    }

    public void Hide()
    {
        if (m_root != null)
            m_root.style.display = DisplayStyle.None;
    }

    private void OnDestroy()
    {
        if (m_settings != null)
        {
            Destroy(m_settings);
            m_settings = null;
        }
    }

    private void EnsureBuilt()
    {
        if (m_panel != null)
            return;

        m_font = UITRuntime.ResolveJapaneseFont(out _);
        m_settings = UITRuntime.CreatePanelSettings(sortingOrder: 9995);
        m_doc = UITRuntime.AttachDocument(gameObject, m_settings);

        m_root = m_doc.rootVisualElement;
        m_root.style.flexGrow = 1;
        m_root.focusable = false;

        // 背後を暗くする幕。ゲームのサウンドテストと同じく、外側クリックで閉じる
        var backdrop = new VisualElement();
        backdrop.style.position = Position.Absolute;
        backdrop.style.left = backdrop.style.top = backdrop.style.right = backdrop.style.bottom = 0;
        backdrop.style.backgroundColor = new Color(0f, 0f, 0f, 0.55f);
        backdrop.RegisterCallback<ClickEvent>(_ => OnCloseClicked?.Invoke());
        m_root.Add(backdrop);

        m_panel = UITFactory.CreatePanel();
        m_panel.style.position = Position.Absolute;
        m_panel.style.width = 960;
        m_panel.style.height = 600;
        m_panel.style.left = Length.Percent(50);
        m_panel.style.top = Length.Percent(50);
        m_panel.style.translate = new Translate(Length.Percent(-50), Length.Percent(-50));
        m_panel.style.flexDirection = FlexDirection.Row;
        m_panel.style.paddingTop = m_panel.style.paddingBottom = 14;
        m_panel.style.paddingLeft = m_panel.style.paddingRight = 14;
        m_panel.style.overflow = Overflow.Hidden;
        m_root.Add(m_panel);

        m_panel.Add(BuildListColumn());
        m_panel.Add(BuildDetailColumn());
    }

    private VisualElement BuildListColumn()
    {
        var column = UITFactory.CreateColumn();
        column.style.width = 320;
        column.style.flexShrink = 0;

        var header = UITFactory.CreateLabel(Loc.Tr("サウンドテスト"), 14, UITTheme.Text.Accent, m_font);
        header.style.marginBottom = 6;
        column.Add(header);

        m_list = new UITListView();
        m_list.Setup(m_font);
        m_list.OnRowClicked += i => OnRowClicked?.Invoke(i);
        column.Add(m_list);
        return column;
    }

    private VisualElement BuildDetailColumn()
    {
        var column = UITFactory.CreateColumn();
        column.style.flexGrow = 1;
        column.style.flexShrink = 1;
        column.style.minWidth = 0;
        column.style.minHeight = 0;
        column.style.marginLeft = 16;

        // 歌詞欄より上は全て固定高にして、曲によって欄の位置と高さが変わらないようにする
        m_title = UITFactory.CreateLabel("", 18, UITTheme.Text.Primary, m_font);
        m_title.style.height = 28;
        m_title.style.flexShrink = 0;
        m_title.style.whiteSpace = WhiteSpace.NoWrap;
        m_title.style.overflow = Overflow.Hidden;
        m_title.style.textOverflow = TextOverflow.Ellipsis;
        column.Add(m_title);

        m_kind = UITFactory.CreateLabel("", 10, UITTheme.Text.Secondary, m_font);
        m_kind.style.height = 16;
        m_kind.style.flexShrink = 0;
        m_kind.style.marginTop = 2;
        column.Add(m_kind);

        m_versionRow = UITFactory.CreateRow();
        m_versionRow.style.height = 24;
        m_versionRow.style.flexShrink = 0;
        m_versionRow.style.marginTop = 8;
        m_versionRow.style.alignItems = Align.Center;
        column.Add(m_versionRow);

        m_note = UITFactory.CreateLabel("", 11, UITTheme.Text.Secondary, m_font);
        m_note.style.height = 34;   // 2 行ぶん
        m_note.style.flexShrink = 0;
        m_note.style.whiteSpace = WhiteSpace.Normal;
        m_note.style.overflow = Overflow.Hidden;
        m_note.style.marginTop = 8;
        column.Add(m_note);

        m_lyricScroll = new ScrollView(ScrollViewMode.Vertical);
        // 歌詞の量でパネルが伸びないよう、残り領域に収めて溢れた分はスクロールさせる
        // 基準高を 0 にして「残り領域を埋める」だけにする。内容の量が兄弟行の高さに影響しなくなる
        m_lyricScroll.style.flexBasis = 0;
        m_lyricScroll.style.flexGrow = 1;
        m_lyricScroll.style.flexShrink = 1;
        m_lyricScroll.style.minHeight = 0;
        m_lyricScroll.style.marginTop = 10;
        m_lyricScroll.style.backgroundColor = new Color(1f, 1f, 1f, 0.04f);
        m_lyricScroll.style.paddingTop = m_lyricScroll.style.paddingBottom = 8;
        m_lyricScroll.style.paddingLeft = m_lyricScroll.style.paddingRight = 10;
        m_lyricScroll.style.borderTopLeftRadius = m_lyricScroll.style.borderTopRightRadius = 6;
        m_lyricScroll.style.borderBottomLeftRadius = m_lyricScroll.style.borderBottomRightRadius = 6;
        m_lyricScroll.verticalScrollerVisibility = ScrollerVisibility.Auto;
        m_lyricScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
        m_lyricScroll.mouseWheelScrollSize = 30;
        // テーマ USS が無い環境では内容コンテナが縮んで収まろうとし、溢れない＝スクロールしない。
        // 内容は伸ばし放題にして、溢れた分は欄の矩形で切る（ビューポートも自前ではクリップしない）
        m_lyricScroll.contentContainer.style.flexShrink = 0;
        m_lyricScroll.style.overflow = Overflow.Hidden;
        m_lyricScroll.contentViewport.style.overflow = Overflow.Hidden;
        column.Add(m_lyricScroll);

        var seekRow = UITFactory.CreateRow();
        seekRow.style.alignItems = Align.Center;
        seekRow.style.flexShrink = 0;
        seekRow.style.marginTop = 10;
        m_seek = new UITSlider();
        m_seek.Setup(Loc.Tr("再生位置"), 0f, 1f, m_font, v => FormatTime(v * m_length));
        m_seek.OnValueCommitted += v => OnSeek?.Invoke(v);
        seekRow.Add(m_seek);
        m_time = UITFactory.CreateLabel("", 11, UITTheme.Text.Secondary, m_font);
        m_time.style.marginLeft = 8;
        m_time.style.flexShrink = 0;
        seekRow.Add(m_time);
        column.Add(seekRow);

        var volumeRow = UITFactory.CreateRow();
        volumeRow.style.alignItems = Align.Center;
        volumeRow.style.flexShrink = 0;
        volumeRow.style.marginTop = 4;
        m_volume = new UITSlider();
        m_volume.Setup(Loc.Tr("音量"), 0f, 200f, m_font, v => $"{Mathf.RoundToInt(v)}%");
        m_volume.SetStep(5f);
        m_volume.OnValueChanged += v => OnVolumeChanged?.Invoke(v);
        m_volume.OnValueCommitted += v => OnVolumeCommitted?.Invoke(v);
        volumeRow.Add(m_volume);
        column.Add(volumeRow);

        var bottom = UITFactory.CreateRow();
        bottom.style.alignItems = Align.Center;
        bottom.style.flexShrink = 0;
        bottom.style.marginTop = 8;
        m_playButton = new UITButton().Setup(Loc.Tr("再生"), () => OnPlayClicked?.Invoke(), m_font);
        bottom.Add(m_playButton);

        // リピート切り替え（マウス用）。絵はモードごとに差し替える
        m_repeatIcons[0] = EmbeddedTexture.Load("BunnyGarden2FixMod.Resources.soundtest.repeat_one.png");
        m_repeatIcons[1] = EmbeddedTexture.Load("BunnyGarden2FixMod.Resources.soundtest.repeat_all.png");
        m_repeatIcons[2] = EmbeddedTexture.Load("BunnyGarden2FixMod.Resources.soundtest.repeat_off.png");
        m_repeatButton = UITFactory.CreateTextureButton(m_repeatIcons[0], () => OnRepeatClicked?.Invoke(), m_font);
        m_repeatButton.style.width = 26;
        m_repeatButton.style.height = 26;
        m_repeatButton.style.marginLeft = 8;
        bottom.Add(m_repeatButton);

        var hints = UITFactory.CreateRow();
        hints.style.flexGrow = 1;
        hints.style.justifyContent = Justify.FlexEnd;
        hints.style.alignItems = Align.Center;
        // パッドのボタン（A/B/Y）は○、方向と L/R/ZL/ZR は□（キーボードのキーと誤解されないように）
        hints.Add(CreatePadCap(new[] { "↑↓" }, Loc.Tr("選択"), circle: false));
        hints.Add(CreatePadCap(new[] { "←→" }, Loc.Tr("バージョン"), circle: false));
        m_repeatSlot = UITFactory.CreateRow();
        hints.Add(m_repeatSlot);
        hints.Add(CreatePadCap(new[] { "L", "R", "ZL", "ZR" }, Loc.Tr("音量"), circle: false));
        hints.Add(CreatePadCap(new[] { "A" }, Loc.Tr("再生/停止"), circle: true));
        hints.Add(CreatePadCap(new[] { "B" }, Loc.Tr("閉じる"), circle: true));
        bottom.Add(hints);
        column.Add(bottom);
        return column;
    }

    public void RenderList(IReadOnlyList<string> labels, int selected, int playing, IReadOnlyList<bool> locked)
    {
        var rows = new List<UITListView.RowModel>(labels.Count);
        for (int i = 0; i < labels.Count; i++)
        {
            rows.Add(new UITListView.RowModel
            {
                Label = labels[i],
                IsSelected = i == selected,
                IsCurrent = i == playing,
                IsLocked = locked[i],
            });
        }
        m_list.Rebuild(rows);
        m_list.ScrollToRow(selected);
    }

    public void RenderDetail(string title, string kind, string note, string[] versions, int versionIndex)
    {
        m_title.text = title;
        m_kind.text = kind;
        m_note.text = note;

        m_versionRow.Clear();
        m_versionRow.style.visibility = versions == null ? Visibility.Hidden : Visibility.Visible;
        if (versions == null)
            return;
        for (int i = 0; i < versions.Length; i++)
        {
            int captured = i;
            var chip = UITFactory.CreateLabel(versions[i], 11, UITTheme.Text.Primary, m_font, TextAnchor.MiddleCenter);
            chip.style.paddingTop = chip.style.paddingBottom = 3;
            chip.style.paddingLeft = chip.style.paddingRight = 10;
            chip.style.marginRight = 6;
            chip.style.borderTopLeftRadius = chip.style.borderTopRightRadius = 4;
            chip.style.borderBottomLeftRadius = chip.style.borderBottomRightRadius = 4;
            if (i == versionIndex)
                UITStyles.ApplyTabActive(chip);
            else
                UITStyles.ApplyTabInactive(chip);
            chip.RegisterCallback<ClickEvent>(_ => OnVersionClicked?.Invoke(captured));
            m_versionRow.Add(chip);
        }
    }

    /// <summary>歌詞を並べ直す。lines が空なら emptyMessage を淡く出す。</summary>
    public void RenderLyrics(IReadOnlyList<string> lines, string emptyMessage)
    {
        m_lyricScroll.Clear();
        m_lyricLabels.Clear();
        m_lyricCurrent = -1;
        if (lines.Count == 0)
        {
            if (!string.IsNullOrEmpty(emptyMessage))
                m_lyricScroll.Add(UITFactory.CreateLabel(emptyMessage, 11, UITTheme.Text.Locked, m_font));
            return;
        }
        foreach (var line in lines)
        {
            var label = UITFactory.CreateLabel(line, 13, UITTheme.Text.Secondary, m_font);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.flexShrink = 0;   // 行を潰させない（潰れると溢れずスクロールできない）
            label.style.marginBottom = 4;
            m_lyricScroll.Add(label);
            m_lyricLabels.Add(label);
        }
    }

    /// <summary>今歌っている行を目立たせ、見える位置へ送る。-1 で解除。</summary>
    public void SetLyricCurrent(int index)
    {
        if (index == m_lyricCurrent)
            return;
        if (m_lyricCurrent >= 0 && m_lyricCurrent < m_lyricLabels.Count)
        {
            var old = m_lyricLabels[m_lyricCurrent];
            old.style.color = UITTheme.Text.Secondary;
            old.style.unityFontStyleAndWeight = FontStyle.Normal;
        }
        m_lyricCurrent = index;
        if (index < 0 || index >= m_lyricLabels.Count)
            return;
        var now = m_lyricLabels[index];
        now.style.color = UITTheme.Text.Accent;
        now.style.unityFontStyleAndWeight = FontStyle.Bold;
        if (Time.unscaledTime < m_holdAutoScrollUntil)
            return;
        CenterLyric(now);
    }

    /// <summary>行を欄の中央へ。レイアウト前（高さが NaN）のときは何もしない。次の行の切り替えで直る。</summary>
    private void CenterLyric(VisualElement line)
    {
        float viewHeight = m_lyricScroll.contentViewport.layout.height;
        var rect = line.layout;
        if (float.IsNaN(viewHeight) || float.IsNaN(rect.y))
            return;
        SetLyricOffset(rect.y + rect.height * 0.5f - viewHeight * 0.5f);
    }

    /// <summary>右スティックやホイールで歌詞を送る。しばらく自動追従を止める。</summary>
    public void ScrollLyrics(float delta)
    {
        m_holdAutoScrollUntil = Time.unscaledTime + 2f;
        SetLyricOffset(m_lyricScroll.scrollOffset.y + delta);
    }

    private void SetLyricOffset(float y)
    {
        float viewHeight = m_lyricScroll.contentViewport.layout.height;
        float contentHeight = m_lyricScroll.contentContainer.layout.height;
        if (float.IsNaN(viewHeight) || float.IsNaN(contentHeight))
            return;
        var offset = m_lyricScroll.scrollOffset;
        offset.y = Mathf.Clamp(y, 0f, Mathf.Max(0f, contentHeight - viewHeight));
        m_lyricScroll.scrollOffset = offset;
    }

    public void SetVolume(float percent) => m_volume.SetValue(percent);

    public void SetRepeat(int modeIndex, string modeLabel)
    {
        m_repeatSlot.Clear();
        m_repeatSlot.Add(CreatePadCap(new[] { "Y" }, $"{Loc.Tr("リピート")}: {modeLabel}", circle: true));
        var icon = m_repeatIcons[Mathf.Clamp(modeIndex, 0, 2)];
        if (icon != null)
            m_repeatButton.style.backgroundImage = new StyleBackground(icon);
        m_repeatButton.tooltip = $"{Loc.Tr("リピート")}: {modeLabel}";
    }

    public void SetProgress(float time, float length, bool playing)
    {
        m_length = length;
        m_seek.SetValue(length > 0f ? Mathf.Clamp01(time / length) : 0f);
        m_time.text = FormatTime(length);
        m_playButton.SetText(playing ? Loc.Tr("停止") : Loc.Tr("再生"));
    }

    /// <summary>パッド用のキーレジェンド。circle なら○ボタン、そうでなければキーごとの□。</summary>
    private VisualElement CreatePadCap(string[] keys, string label, bool circle)
    {
        var wrap = UITFactory.CreateRow();
        wrap.style.alignItems = Align.Center;
        wrap.style.marginRight = 8;

        foreach (var key in keys)
        {
            var cap = new VisualElement();
            const float size = 20f;
            cap.style.height = size;
            cap.style.width = circle ? size : (key.Length > 1 ? 24f : size);
            cap.style.marginRight = 3;
            cap.style.alignItems = Align.Center;
            cap.style.justifyContent = Justify.Center;
            cap.style.backgroundColor = UITTheme.KeyCap.Fill;
            float radius = circle ? size / 2f : UITTheme.KeyCap.Radius;
            cap.style.borderTopLeftRadius = cap.style.borderTopRightRadius = radius;
            cap.style.borderBottomLeftRadius = cap.style.borderBottomRightRadius = radius;
            cap.style.borderTopWidth = cap.style.borderBottomWidth = UITTheme.KeyCap.BorderWidth;
            cap.style.borderLeftWidth = cap.style.borderRightWidth = UITTheme.KeyCap.BorderWidth;
            cap.style.borderTopColor = cap.style.borderBottomColor = UITTheme.KeyCap.Border;
            cap.style.borderLeftColor = cap.style.borderRightColor = UITTheme.KeyCap.Border;
            cap.Add(UITFactory.CreateLabel(key, key.Length > 1 ? 9 : 10, UITTheme.Text.Primary, m_font, TextAnchor.MiddleCenter));
            wrap.Add(cap);
        }

        if (!string.IsNullOrEmpty(label))
        {
            var text = UITFactory.CreateLabel(label, 10, UITTheme.Text.Secondary, m_font);
            text.style.marginLeft = 2;
            wrap.Add(text);
        }
        return wrap;
    }

    private static string FormatTime(float seconds)
    {
        if (seconds < 0f || float.IsNaN(seconds))
            seconds = 0f;
        int total = Mathf.FloorToInt(seconds);
        return $"{total / 60}:{total % 60:d2}";
    }
}
