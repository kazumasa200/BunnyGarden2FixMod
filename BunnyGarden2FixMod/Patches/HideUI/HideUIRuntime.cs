using System;
using BunnyGarden2FixMod.Utils;
using GB;
using GB.Bar;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BunnyGarden2FixMod.Patches.HideUI;

/// <summary>
/// CanvasGroup.alpha 操作で UI を視覚的に隠すランタイム。
/// F9 設定パネルとは独立し、Configs.HideMoneyInSpecialScenes / HideButtonGuide / HideLikabilityGauge を
/// 直接読んで毎フレーム反映する。
/// </summary>
public class HideUIRuntime : MonoBehaviour
{
    public static HideUIRuntime Instance { get; private set; }

    public static void Initialize(GameObject parent)
    {
        // parent は SettingsController.Initialize とシグネチャを揃えるためだけに受け取る（未使用）。
        _ = parent;
        var host = new GameObject("BG2HideUIRuntime");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<HideUIRuntime>();
    }

    private CanvasGroup m_moneyCanvasGroup;
    private CanvasGroup m_footerCanvasGroup;
    private CanvasGroup m_likabilityCanvasGroup;

    private bool m_moneyWasHidden;
    private bool m_footerWasHidden;
    private bool m_likabilityWasHidden;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            PatchLogger.LogWarning("[HideUIRuntime] 既に存在するため新規生成をキャンセルします");
            Destroy(gameObject);
            return;
        }
        Instance = this;
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
    }

    private void OnDestroy()
    {
        if (ReferenceEquals(Instance, this)) Instance = null;
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
    }

    private void OnActiveSceneChanged(Scene _, Scene __)
    {
        // CanvasGroup の対象 GameObject がシーン破棄で fake-null 化する可能性に備え、
        // キャッシュとフラグをリセット。次フレームの LateUpdate で必要なら再取得される。
        m_moneyCanvasGroup = null;
        m_footerCanvasGroup = null;
        m_likabilityCanvasGroup = null;
        m_moneyWasHidden = false;
        m_footerWasHidden = false;
        m_likabilityWasHidden = false;
    }

    /// <summary>
    /// CanvasGroup の表示状態を一括で設定するヘルパ。
    /// </summary>
    private static void ApplyHide(CanvasGroup g, bool hide)
    {
        g.alpha          = hide ? 0f : 1f;
        g.interactable   = !hide;
        g.blocksRaycasts = !hide;
    }

    private void LateUpdate()
    {
        // ── 所持金 UI ──────────────────────────────────────────
        bool moneyEnabled = Configs.HideMoneyInSpecialScenes?.Value == true;
        if (moneyEnabled || m_moneyWasHidden)
        {
            if (m_moneyCanvasGroup == null) FindMoneyUI();
            if (m_moneyCanvasGroup != null)
            {
                bool shouldHide = moneyEnabled && ShouldHideMoneyUI();
                ApplyHide(m_moneyCanvasGroup, shouldHide);
                m_moneyWasHidden = moneyEnabled;
            }
        }

        // ── ボタンガイド（Footer）────────────────────────────────
        bool guideEnabled = Configs.HideButtonGuide?.Value == true;
        if (guideEnabled || m_footerWasHidden)
        {
            if (m_footerCanvasGroup == null) FindFooter();
            if (m_footerCanvasGroup != null)
            {
                ApplyHide(m_footerCanvasGroup, guideEnabled);
                m_footerWasHidden = guideEnabled;
            }
        }

        // ── 好感度ゲージ（LikabilityUI コンテナ）────────────────────
        bool likabilityEnabled = Configs.HideLikabilityGauge?.Value == true;
        if (likabilityEnabled || m_likabilityWasHidden)
        {
            if (m_likabilityCanvasGroup == null) FindLikabilityGauge();
            if (m_likabilityCanvasGroup != null)
            {
                ApplyHide(m_likabilityCanvasGroup, likabilityEnabled);
                m_likabilityWasHidden = likabilityEnabled;
            }
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
                PatchLogger.LogInfo($"[HideUIRuntime] MoneyUI を発見: {moneyUI.gameObject.name}");
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
                PatchLogger.LogInfo($"[HideUIRuntime] 所持金UI を名前検索で発見: {moneyObj.name}");
                return;
            }
        }
        catch (Exception ex)
        {
            PatchLogger.LogError($"[HideUIRuntime] 所持金UI 検索エラー: {ex.Message}");
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
                PatchLogger.LogInfo($"[HideUIRuntime] Footer を発見: {footer.gameObject.name}");
                return;
            }
            // Footer が見つからない場合は次フレームで再試行（シーン遷移直後等）
        }
        catch (Exception ex)
        {
            PatchLogger.LogError($"[HideUIRuntime] Footer 検索エラー: {ex.Message}");
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
                PatchLogger.LogInfo($"[HideUIRuntime] LikabilityUI コンテナを発見: {container.name}");
            }
        }
        catch (Exception ex)
        {
            PatchLogger.LogError($"[HideUIRuntime] LikabilityUI 検索エラー: {ex.Message}");
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
        if (Configs.HideMoneyInSpecialScenes?.Value != true) return false;

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
}
