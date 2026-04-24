using System;
using System.Collections;
using BunnyGarden2FixMod.Utils;
using GB;
using GB.Bar;
using UITKit;
using UITKit.Components;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace BunnyGarden2FixMod.Patches.HideMoneyUI;

/// <summary>
/// UI非表示設定のコントローラー。
/// F9キーで設定パネルを開き、以下をON/OFFできる：
/// <list type="bullet">
///   <item>旅行・告白シーンでの所持金非表示（MoneyUI の CanvasGroup.alpha 制御）</item>
///   <item>ボタンガイド常時非表示（Footer の CanvasGroup.alpha 制御）</item>
/// </list>
///
/// <para>
/// <b>シーン検出（所持金）</b>
/// <list type="bullet">
///   <item>旅行シーン: <c>SceneManager.GetSceneByName("HolidayAfterScene").isLoaded</c></item>
///   <item>告白シーン: <c>SceneManager.GetSceneByName("AfterScene").isLoaded</c> かつ IsProposeAfter / IsHaremProposeAfter / IsBirthday</item>
///   <item>エピローグ: <c>GBSystem.IsEpilogue == true</c>（EpilogueScene 遷移後）</item>
/// </list>
/// </para>
///
/// <para>
/// <b>ボタンガイド非表示</b><br/>
/// <c>GB.Footer</c> コンポーネントを <c>FindFirstObjectByType</c> で取得し、
/// CanvasGroup.alpha = 0 で視覚的に隠す（SetActive は変更しない）。
/// Footer は動的にコンテンツを差し替えるため、シーン遷移後も null チェックで自動再取得する。
/// </para>
///
/// <para>
/// <b>好感度ゲージ非表示</b><br/>
/// <c>GB.LikabilityUI</c> コンポーネントを <c>FindFirstObjectByType</c> で取得し、
/// その親（<c>m_likabilityUIContainer</c> 相当）に CanvasGroup を追加して alpha = 0 で非表示にする。
/// </para>
/// </summary>
public class HideMoneyUIController : MonoBehaviour
{
    public static HideMoneyUIController Instance { get; private set; }

    public static void Initialize(GameObject parent)
    {
        var host = new GameObject("BG2HideMoneyUI");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<HideMoneyUIController>();
    }

    private HideMoneyUIView m_view;
    private CanvasGroup m_moneyCanvasGroup;
    private CanvasGroup m_footerCanvasGroup;
    private CanvasGroup m_likabilityCanvasGroup;

    public static bool ShouldSuppressMouseInput()
    {
        return Instance != null && Instance.IsPointerOverPanel();
    }

    public bool ShouldSuppressGameInput(string actionName)
    {
        if (!IsPointerOverPanel()) return false;
        return actionName != null && (actionName == "Move" || actionName == "Look" || actionName == "Sprint");
    }

    private bool IsPointerOverPanel()
    {
        return m_view != null && m_view.IsShown && m_view.IsPointerOverPanel();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            PatchLogger.LogWarning("[GameUIHider] 既に存在するため新規生成をキャンセルします");
            Destroy(gameObject);
            return;
        }
        Instance = this;
        m_view = gameObject.AddComponent<HideMoneyUIView>();
        m_view.OnCloseClicked          += HandleCloseClicked;
        m_view.OnToggleMoneyHide       += HandleToggleMoneyHide;
        m_view.OnToggleButtonGuide     += HandleToggleButtonGuide;
        m_view.OnToggleLikabilityGauge += HandleToggleLikabilityGauge;
    }

    /// <summary>
    /// 1 フレーム後に UIDocument を事前構築する。
    /// TitleScene / HomeScene では ThemeStyleSheet がロード済みのため成功し、
    /// 旅行シーンに遷移しても UIDocument が DontDestroyOnLoad で生き続ける。
    /// </summary>
    private IEnumerator Start()
    {
        yield return null;
        if (m_view != null)
            m_view.TryPreBuild();
    }

    private void OnDestroy()
    {
        if (m_view != null)
        {
            m_view.OnCloseClicked          -= HandleCloseClicked;
            m_view.OnToggleMoneyHide       -= HandleToggleMoneyHide;
            m_view.OnToggleButtonGuide     -= HandleToggleButtonGuide;
            m_view.OnToggleLikabilityGauge -= HandleToggleLikabilityGauge;
        }
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        if (Plugin.ConfigHideUIEnabled == null) return;
        if (!Plugin.ConfigHideUIEnabled.Value) return;
        if (m_view == null) return;

        var kb = Keyboard.current;
        if (kb == null) return;

        // F9 でパネルトグル
        if (kb[Key.F9].wasPressedThisFrame)
        {
            if (m_view.IsShown)
                m_view.Hide();
            else
                Open();
            return;
        }

        if (!m_view.IsShown) return;
        if (!m_view.IsPointerOverPanel()) return;

        // Esc: 閉じる
        if (kb[Key.Escape].wasPressedThisFrame)
        {
            m_view.Hide();
            return;
        }

        // Space/Enter: フォーカス中の行をトグル（カーソルがパネル上にあるとき最初の行）
        // 今は Space で所持金トグル、Tab で切り替えは複雑になるため Space = 所持金トグルを維持
        if (kb[Key.Space].wasPressedThisFrame || kb[Key.Enter].wasPressedThisFrame)
        {
            HandleToggleMoneyHide();
        }
    }

    private void LateUpdate()
    {
        if (!Plugin.ConfigHideUIEnabled?.Value ?? false) return;

        // ── 所持金 UI ──────────────────────────────────────────
        if (m_moneyCanvasGroup == null)
            FindMoneyUI();

        if (m_moneyCanvasGroup != null)
        {
            bool shouldHideMoney = ShouldHideMoneyUI();
            m_moneyCanvasGroup.alpha          = shouldHideMoney ? 0f : 1f;
            m_moneyCanvasGroup.interactable   = !shouldHideMoney;
            m_moneyCanvasGroup.blocksRaycasts = !shouldHideMoney;
        }

        // ── ボタンガイド（Footer）────────────────────────────────
        if (m_footerCanvasGroup == null)
            FindFooter();

        if (m_footerCanvasGroup != null)
        {
            bool shouldHideGuide = Plugin.ConfigHideButtonGuide?.Value == true;
            m_footerCanvasGroup.alpha          = shouldHideGuide ? 0f : 1f;
            m_footerCanvasGroup.interactable   = !shouldHideGuide;
            m_footerCanvasGroup.blocksRaycasts = !shouldHideGuide;
        }

        // ── 好感度ゲージ（LikabilityUI コンテナ）────────────────────
        if (m_likabilityCanvasGroup == null)
            FindLikabilityGauge();

        if (m_likabilityCanvasGroup != null)
        {
            bool shouldHideLikability = Plugin.ConfigHideLikabilityGauge?.Value == true;
            m_likabilityCanvasGroup.alpha          = shouldHideLikability ? 0f : 1f;
            m_likabilityCanvasGroup.interactable   = !shouldHideLikability;
            m_likabilityCanvasGroup.blocksRaycasts = !shouldHideLikability;
        }
    }

    // ── MoneyUI 取得 ───────────────────────────────────────────────

    private void FindMoneyUI()
    {
        try
        {
            var moneyUI = FindFirstObjectByType<MoneyUI>(FindObjectsInactive.Include);
            if (moneyUI != null)
            {
                m_moneyCanvasGroup = moneyUI.GetComponent<CanvasGroup>()
                                  ?? moneyUI.gameObject.AddComponent<CanvasGroup>();
                PatchLogger.LogInfo($"[GameUIHider] MoneyUI を発見: {moneyUI.gameObject.name}");
                return;
            }

            // フォールバック: Canvas 内を名前検索
            var canvases = FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var canvas in canvases)
            {
                if (canvas == null) continue;
                var moneyObj = FindDeep(canvas.transform, "Money")
                            ?? FindDeep(canvas.transform, "MoneyUI")
                            ?? FindDeep(canvas.transform, "Gold");
                if (moneyObj == null) continue;

                m_moneyCanvasGroup = moneyObj.GetComponent<CanvasGroup>()
                                  ?? moneyObj.gameObject.AddComponent<CanvasGroup>();
                PatchLogger.LogInfo($"[GameUIHider] 所持金UI を名前検索で発見: {moneyObj.name}");
                return;
            }
        }
        catch (Exception ex)
        {
            PatchLogger.LogError($"[GameUIHider] 所持金UI 検索エラー: {ex.Message}");
        }
    }

    // ── Footer（ボタンガイド）取得 ─────────────────────────────────

    private void FindFooter()
    {
        try
        {
            // GB.Footer は DontDestroyOnLoad 下の永続オブジェクトにある
            var footer = FindFirstObjectByType<Footer>(FindObjectsInactive.Include);
            if (footer != null)
            {
                m_footerCanvasGroup = footer.GetComponent<CanvasGroup>()
                                   ?? footer.gameObject.AddComponent<CanvasGroup>();
                PatchLogger.LogInfo($"[GameUIHider] Footer を発見: {footer.gameObject.name}");
                return;
            }
            // Footer が見つからない場合は次フレームで再試行（シーン遷移直後等）
        }
        catch (Exception ex)
        {
            PatchLogger.LogError($"[GameUIHider] Footer 検索エラー: {ex.Message}");
        }
    }

    // ── 好感度ゲージ（LikabilityUI コンテナ）取得 ──────────────────

    private void FindLikabilityGauge()
    {
        try
        {
            // LikabilityUI は UIManager の m_likabilityUIContainer 配下にある。
            // いずれかの LikabilityUI コンポーネントを見つけてその親をコンテナとして扱う。
            var likabilityUI = FindFirstObjectByType<LikabilityUI>(FindObjectsInactive.Include);
            if (likabilityUI != null && likabilityUI.transform.parent != null)
            {
                var container = likabilityUI.transform.parent.gameObject;
                m_likabilityCanvasGroup = container.GetComponent<CanvasGroup>()
                                       ?? container.AddComponent<CanvasGroup>();
                PatchLogger.LogInfo($"[GameUIHider] LikabilityUI コンテナを発見: {container.name}");
            }
        }
        catch (Exception ex)
        {
            PatchLogger.LogError($"[GameUIHider] LikabilityUI 検索エラー: {ex.Message}");
        }
    }

    private static Transform FindDeep(Transform parent, string name)
    {
        if (parent == null) return null;
        foreach (Transform child in parent)
        {
            if (child.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return child;
            var found = FindDeep(child, name);
            if (found != null) return found;
        }
        return null;
    }

    // ── シーン判定 ─────────────────────────────────────────────────

    /// <summary>
    /// 所持金UIを非表示にすべきシーンかを判定する。
    /// </summary>
    private static bool ShouldHideMoneyUI()
    {
        if (Plugin.ConfigHideMoneyInSpecialScenes?.Value != true) return false;

        // 旅行シーン: LoadSceneMode.Additive のため GetSceneByName で判定
        if (SceneManager.GetSceneByName("HolidayAfterScene").isLoaded) return true;

        // 告白シーン: AfterScene で PrePropose BGM が流れる条件
        // (IsProposeAfter / IsHaremProposeAfter / IsBirthday) のときに非表示
        if (SceneManager.GetSceneByName("AfterScene").isLoaded)
        {
            try
            {
                var sys = GBSystem.Instance;
                var gameData = sys?.RefGameData();
                if (gameData != null)
                {
                    var cast = gameData.GetCurrentCast();
                    if (gameData.IsProposeAfter(cast) || gameData.IsHaremProposeAfter() || gameData.IsBirthday(cast))
                        return true;
                }
            }
            catch { /* シーン遷移中のアクセスを安全に無視 */ }
        }

        // エピローグ: EpilogueScene.Start() が EnterEpilogue() を呼んだ後
        {
            var sys = GBSystem.Instance;
            if (sys != null && sys.IsEpilogue) return true;
        }

        return false;
    }

    // ── UI 操作 ────────────────────────────────────────────────────

    private void Open()
    {
        var data = new HideMoneyUIView.RenderData
        {
            HideMoneyInSpecialScenes = Plugin.ConfigHideMoneyInSpecialScenes?.Value ?? false,
            HideButtonGuide          = Plugin.ConfigHideButtonGuide?.Value ?? false,
            HideLikabilityGauge      = Plugin.ConfigHideLikabilityGauge?.Value ?? false,
        };
        m_view.Show(data);
        PatchLogger.LogInfo("[GameUIHider] パネルオープン");
    }

    private void HandleCloseClicked()
    {
        m_view.Hide();
        PatchLogger.LogInfo("[GameUIHider] パネル閉じる");
    }

    private void HandleToggleMoneyHide()
    {
        if (Plugin.ConfigHideMoneyInSpecialScenes == null) return;
        Plugin.ConfigHideMoneyInSpecialScenes.Value = !Plugin.ConfigHideMoneyInSpecialScenes.Value;
        PatchLogger.LogInfo($"[GameUIHider] 所持金非表示: {(Plugin.ConfigHideMoneyInSpecialScenes.Value ? "ON" : "OFF")}");
        Open(); // パネルを再描画
    }

    private void HandleToggleButtonGuide()
    {
        if (Plugin.ConfigHideButtonGuide == null) return;
        Plugin.ConfigHideButtonGuide.Value = !Plugin.ConfigHideButtonGuide.Value;
        PatchLogger.LogInfo($"[GameUIHider] ボタンガイド非表示: {(Plugin.ConfigHideButtonGuide.Value ? "ON" : "OFF")}");

        if (m_footerCanvasGroup == null) FindFooter();

        Open();
    }

    private void HandleToggleLikabilityGauge()
    {
        if (Plugin.ConfigHideLikabilityGauge == null) return;
        Plugin.ConfigHideLikabilityGauge.Value = !Plugin.ConfigHideLikabilityGauge.Value;
        PatchLogger.LogInfo($"[GameUIHider] 好感度ゲージ非表示: {(Plugin.ConfigHideLikabilityGauge.Value ? "ON" : "OFF")}");

        if (m_likabilityCanvasGroup == null) FindLikabilityGauge();

        Open();
    }
}
