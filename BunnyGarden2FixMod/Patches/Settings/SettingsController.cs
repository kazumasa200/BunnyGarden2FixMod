using BunnyGarden2FixMod.Patches;
using BunnyGarden2FixMod.Utils;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BunnyGarden2FixMod.Patches.Settings;

/// <summary>
/// F9 設定パネルのライフサイクル / 入力ハンドラ。
/// SettingsView を保持し、F9 / Esc / ↑↓←→ Shift Tab Space Enter 1-9 を解釈する。
/// マウス入力抑制判定 (ShouldSuppressMouseInput) は Plugin.cs のマウス系 Suppress パッチから参照される。
/// </summary>
public class SettingsController : MonoBehaviour
{
    public static SettingsController Instance { get; private set; }

    public static void Initialize(GameObject parent)
    {
        var host = new GameObject("BG2SettingsController");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<SettingsController>();
    }

    private SettingsView m_view;

    // ── キャプチャ状態 ────────────────────────────────────────────────────────
    private bool m_isCapturingKey;
    private UIEntryMeta m_capturingEntry; // row index ではなく entry 参照を持つ (RenderContent で row が再構築されても entry は安定)

    /// <summary>このコントローラーがキャプチャ中かどうか。</summary>
    public bool IsCapturingKey => m_isCapturingKey;

    /// <summary>キャプチャ中のエントリ参照。SettingsView の UI 操作に使用。</summary>
    internal UIEntryMeta CapturingEntry => m_capturingEntry;

    /// <summary>
    /// いずれかのキーバインド行でキャプチャ中かどうかを返す。
    /// HotkeyConfig / Suppress パッチからキャプチャ中の入力遮断判定に使用する。
    /// </summary>
    public static bool IsAnyCapturing =>
        Instance != null && Instance.m_isCapturingKey;

    // キャプチャ中の Key 走査用キャッシュ。Enum.GetValues は呼び出しごとに新規配列を返し、
    // foreach で boxing も発生するため、起動時に 1 度だけ取得して使い回す。
    // None / AnyKey / IMESelected は仮想キー・特殊状態のため事前除外する。
    private static readonly Key[] s_capturableKeys = BuildCapturableKeys();

    private static Key[] BuildCapturableKeys()
    {
        var all = (Key[])System.Enum.GetValues(typeof(Key));
        var list = new System.Collections.Generic.List<Key>(all.Length);
        foreach (var key in all)
        {
            if (key == Key.None) continue;
            // IMESelected は [Obsolete] enum 値、AnyKey は仮想キー。Unity 版差で名前が変わっても
            // 列挙ベース判定なら build break しない。
            var keyName = key.ToString();
            if (keyName == "IMESelected" || keyName == "AnyKey") continue;
            list.Add(key);
        }
        return list.ToArray();
    }

    /// <summary>キーバインドキャプチャ中、もしくは Plugin の Suppress 期間中。Mod / 本体の入力を遮断する判定として使用。</summary>
    public static bool ShouldSuppressHotkey()
    {
        return InputGate.HotkeysLocked;
    }

    public static bool ShouldSuppressMouseInput()
    {
        return Instance != null && Instance.m_view != null && Instance.m_view.IsPointerOverPanel();
    }

    // ── キャプチャ操作 API ────────────────────────────────────────────────────

    /// <summary>指定エントリの KB キャプチャモードを開始する。</summary>
    public void StartKeyCapture(UIEntryMeta entry)
    {
        if (m_isCapturingKey) return; // 再入禁止
        if (entry == null || entry.Kind != UIKind.KeyBinding) return;
        m_isCapturingKey = true;
        m_capturingEntry = entry;
        m_view?.OnCaptureStarted(entry);
    }

    /// <summary>キャプチャモードをキャンセルする。キャプチャ中でなければ何もしない冪等な実装。</summary>
    public void CancelKeyCapture()
    {
        if (!m_isCapturingKey) return;
        var entry = m_capturingEntry;
        m_isCapturingKey = false;
        m_capturingEntry = null;
        m_view?.OnCaptureEnded(entry);
    }

    /// <summary>
    /// キャプチャ中のキー押下を受け取って確定処理を行う。
    /// Esc → キャンセル, Backspace/Delete → None, 他 → そのキーを確定。
    /// 確定後は全 KeyBinding 行に対し衝突スワップ → 1 フレーム ゲーム入力抑止 → 再描画。
    /// </summary>
    public void HandleCapturedKey(UnityEngine.InputSystem.Key k)
    {
        if (!m_isCapturingKey) return;
        var entry = m_capturingEntry;
        if (entry == null) { m_isCapturingKey = false; return; }

        if (k == UnityEngine.InputSystem.Key.Escape)
        {
            CancelKeyCapture();
            return;
        }

        var newKey = (k == UnityEngine.InputSystem.Key.Backspace || k == UnityEngine.InputSystem.Key.Delete)
            ? UnityEngine.InputSystem.Key.None
            : k;

        var hotkey = entry.HotkeyProvider?.Invoke();
        if (hotkey?.KeyConfig == null)
        {
            CancelKeyCapture();
            return;
        }

        // 値代入の前に Suppress を呼ぶ（代入で SettingChanged 発火が即時 hotkey 評価につながる経路を塞ぐ）
        InputGate.ArmPadGrace();

        var oldKey = hotkey.KeyConfig.Value;
        hotkey.KeyConfig.Value = newKey;

        // 衝突スワップ: 全 KeyBinding 行を走査し、newKey と被るエントリの値を oldKey に書き換える
        if (newKey != UnityEngine.InputSystem.Key.None)
        {
            foreach (var other in Configs.UIEntries)
            {
                if (other.Kind != UIKind.KeyBinding) continue;
                if (ReferenceEquals(other, entry)) continue;
                var otherHk = other.HotkeyProvider?.Invoke();
                if (otherHk?.KeyConfig == null) continue;
                if (otherHk.KeyConfig.Value == newKey)
                {
                    otherHk.KeyConfig.Value = oldKey;
                    PatchLogger.LogInfo($"[KeyBinding] キー衝突: '{other.Label}' を {newKey}→{oldKey} にスワップ");
                }
            }
        }

        m_isCapturingKey = false;
        m_capturingEntry = null;
        m_view?.OnCaptureEnded(entry);
        m_view?.RequestRebuild();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            PatchLogger.LogWarning("[SettingsController] 既に存在するため新規生成をキャンセルします");
            Destroy(gameObject);
            return;
        }
        Instance = this;
        m_view = gameObject.AddComponent<SettingsView>();
    }

    private void OnDestroy()
    {
        if (ReferenceEquals(Instance, this)) Instance = null;
    }

    private void Update()
    {
        if (m_view == null) return;
        var kb = Keyboard.current;
        if (kb == null) return;

        // ── F9: 開閉トグル ─────────────────
        if (kb[Key.F9].wasPressedThisFrame)
        {
            if (m_view.IsShown) m_view.Hide();
            else                m_view.Show();
            return;
        }

        if (!m_view.IsShown) return;

        // ── KeyBinding キャプチャ中: 通常ナビゲーションをスキップしてキー押下を処理する ──
        if (m_isCapturingKey)
        {
            // パネル外でのマウスクリックでもキャプチャを解除する (パネル内クリックは
            // SettingsView の MouseDownEvent ハンドラ側で処理され、ここには届かない)。
            var mouse = Mouse.current;
            if (mouse != null)
            {
                bool clicked = mouse.leftButton.wasPressedThisFrame
                            || mouse.rightButton.wasPressedThisFrame
                            || mouse.middleButton.wasPressedThisFrame;
                if (clicked && !m_view.IsPointerOverPanel())
                {
                    CancelKeyCapture();
                    return;
                }
            }

            // 起動時にキャッシュした全 Key 配列を走査 (Unity InputSystem 版差に強い)。
            foreach (var key in s_capturableKeys)
            {
                try
                {
                    if (kb[key].wasPressedThisFrame)
                    {
                        HandleCapturedKey(key);
                        return;
                    }
                }
                catch
                {
                    // 一部の Key 値は kb[key] でアクセスエラーになる可能性 (Unity 版差) → 無視
                }
            }
            return;
        }

        // ── Esc: 閉じる ───────────────────
        if (kb[Key.Escape].wasPressedThisFrame)
        {
            m_view.Hide();
            return;
        }

        bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;

        // ── ↑↓ で行移動 ──────────────────
        if (kb[Key.UpArrow].wasPressedThisFrame)   m_view.HandleKeyArrowUp();
        if (kb[Key.DownArrow].wasPressedThisFrame) m_view.HandleKeyArrowDown();

        // ── ←→ で slider 値増減 ──────────
        if (kb[Key.LeftArrow].wasPressedThisFrame)  m_view.HandleKeyArrowLeft(shift);
        if (kb[Key.RightArrow].wasPressedThisFrame) m_view.HandleKeyArrowRight(shift);

        // ── Space / Enter で toggle ───────
        if (kb[Key.Space].wasPressedThisFrame || kb[Key.Enter].wasPressedThisFrame)
            m_view.HandleKeyConfirm();

        // ── Tab / Shift+Tab でカテゴリ循環 ─
        if (kb[Key.Tab].wasPressedThisFrame)
        {
            if (shift) m_view.HandleKeyTabPrev();
            else       m_view.HandleKeyTabNext();
        }

        // ── 1-9 でカテゴリ直接ジャンプ ────
        // 注意: UnityEngine.InputSystem.Key の Digit0..Digit9 は連続値である前提（連番依存）。
        // Unity InputSystem の API 安定性に依存する箇所なので、Unity アップデート時は要確認。
        for (int i = 0; i < 9; i++)
        {
            var key = Key.Digit1 + i;
            if (kb[key].wasPressedThisFrame)
            {
                m_view.HandleKeyCategoryJump(i + 1);
                break;
            }
        }
    }
}
