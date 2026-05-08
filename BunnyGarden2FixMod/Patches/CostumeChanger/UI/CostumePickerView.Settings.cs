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
/// CostumePickerView の Settings サブビュー（解放状態リセット / すべて解放）。
/// 共通フィールドは本体ファイルに、設定固有のボタンとロジックは本ファイルに局所化する。
/// </summary>
public partial class CostumePickerView
{
    public class SettingsData
    {
        public CharID CharId;

        /// <summary>「すべて解放」ボタンを enable にするか（= そのキャラの GoodEnd クリア済み）。</summary>
        public bool UnlockAllEnabled;

        /// <summary>現在表示中のキャスト一覧（ヘッダーの ◀▶ ナビ更新に使用）。</summary>
        public IReadOnlyList<CharID> VisibleCasts;

        /// <summary>VisibleCasts の中で現在ピッカーが対象としているキャストのインデックス。</summary>
        public int VisibleCastSelectedIndex;
    }

    public event Action OnSettingsClicked;

    public event Action OnResetAllClicked;

    public event Action OnUnlockAllClicked;

    private VisualElement m_settingsContent;
    private Button m_settingsButton;          // ⚙: ピッカー中のみ可視
    private Button m_resetAllButton;
    private Button m_unlockAllButton;
    private Label m_unlockAllNote;

    public void ShowSettings(SettingsData data)
    {
        EnsureBuilt();
        ApplyUIScale();
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
    }

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
    }

    /// <summary>⚙ 設定ボタンの構築。BuildHeaderButtons (本体) から呼ばれる。</summary>
    private void BuildSettingsButton()
    {
        var gearTex = EmbeddedTexture.Load("BunnyGarden2FixMod.Resources.settings.png");
        m_settingsButton = UITFactory.CreateTextureButton(gearTex, () => OnSettingsClicked?.Invoke(), m_font);
        m_settingsButton.style.position = Position.Absolute;
        m_settingsButton.style.right = 36;
        m_settingsButton.style.top = 8;
        m_settingsButton.style.width = 22;
        m_settingsButton.style.height = 22;
        m_panel.Add(m_settingsButton);
    }
}
