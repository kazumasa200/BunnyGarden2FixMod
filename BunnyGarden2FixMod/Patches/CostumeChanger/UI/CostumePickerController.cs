using BunnyGarden2FixMod.Patches.CostumeChanger.Internal;
using BunnyGarden2FixMod.Utils;
using Cysharp.Threading.Tasks;
using GB;
using GB.DLC;
using GB.Game;
using GB.Scene;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BunnyGarden2FixMod.Patches.CostumeChanger.UI;

/// <summary>
/// Wardrobe ピッカー（Costume/Panties/Stocking 3 タブ）のコントローラ。
/// タブ状態・選択状態を保持し、View を Render モデルで駆動する。
/// Costume は Enter で確定してモデル再ロード、Panties/Stocking は
/// 選択変更で即時 ApplyStockings / ReloadPanties する（ライブプレビュー）。
/// </summary>
public class CostumePickerController : MonoBehaviour
{
    private CostumePickerView m_view;
    private bool m_loading;
    private CharID m_activeChar = CharID.NUM;

    // 表示中キャスト管理
    private List<CharID> m_visibleCasts = new();

    private List<CharID> m_visibleCastsBuf = new();  // 毎フレーム比較用バッファ（GC 再利用）
    private bool m_followCurrentCast = true;          // true: currentCast 変化に自動追従

    private enum PickerMode
    { Picker, Settings }

    private PickerMode m_mode = PickerMode.Picker;
    private int m_settingsSelected = -1;  // -1: 未選択, 0: 初期化, 1: すべて解放
    private bool m_dialogPending;         // ConfirmDialog 呼出〜アクション完了までの多重実行防止

    // タブ状態
    private CostumePickerView.WardrobeTab m_activeTab = CostumePickerView.WardrobeTab.Costume;

    private int m_costumeSelected = -1;
    private int m_pantiesSelected = -1;
    private int m_stockingSelected = -1;
    private int m_bottomsSelected = -1;
    private int m_topsSelected = -1;

    // 各タブの選択肢（Locked=true は未開放。モック準拠で "???" 表示、選択・適用不可）
    private List<(CostumeType Costume, bool Locked)> m_costumeItems = new();

    private List<(int Type, int Color, bool Locked)> m_pantiesItems = new();
    private List<(int Type, bool Locked)> m_stockingItems = new();
    // Locked は donor キャラ × 衣装 の閲覧履歴 (CostumeViewHistory.IsViewed) で判定。
    // 既存 override の (donor, costume) は履歴未蓄積でも grandfather する（解除・再適用を可能にする）。
    private List<(CharID Donor, CostumeType Costume, bool Locked)> m_bottomsItems = new();
    private List<(CharID Donor, CostumeType Costume, bool Locked)> m_topsItems = new();

    public static CostumePickerController Instance { get; private set; }

    /// <summary>View が表示中かを外部から参照する。</summary>
    public bool IsPickerShown => m_view != null && m_view.IsShown;

    /// <summary>現在のマウス座標が Wardrobe パネル矩形内かを外部（クリック抑制パッチ）から参照する。</summary>
    public bool IsCursorOverPicker => m_view != null && m_view.IsPointerOverPanel();

    private static readonly HashSet<string> s_pickerActions = new HashSet<string>
    {
        "AButton",     // Enter → 適用
        "UpButton",    // W/↑ → 選択移動
        "DownButton",  // S/↓ → 選択移動
        "LeftButton",  // A/← → タブ切替
        "RightButton", // D/→ → タブ切替
        "StartButton", // Esc → 閉じる（Tab も同アクション）
        "XButton",     // R → リセット
        "Auto",        // keyboard 'A' が LeftButton と Auto を同時発火するため
    };

    /// <summary>ゲーム側入力（GBInput）をパッチで抑制すべき状態かを返す。</summary>
    public static bool ShouldSuppressGameInput()
    {
        if (Configs.CostumeChangerEnabled?.Value != true) return false;
        // ConfirmDialog 表示中は抑制解除（ダイアログが GBInput を読むため）
        if (ConfirmDialogHelper.IsActive()) return false;
        var ctrl = Instance;
        return ctrl != null && ctrl.IsPickerShown && ctrl.IsCursorOverPicker;
    }

    /// <summary>指定アクションがピッカー使用キーに該当し、かつ抑制すべき状態かを返す。</summary>
    public static bool ShouldSuppressGameInput(string actionName)
    {
        if (!ShouldSuppressGameInput()) return false;
        return actionName != null && s_pickerActions.Contains(actionName);
    }

    private void Awake()
    {
        // 二重生成ガード: Initialize が (プラグイン再ロード等で) 2 回呼ばれても
        // 旧インスタンスを orphan 化させず、新しく作られた側を Destroy する。
        if (Instance != null && Instance != this)
        {
            PatchLogger.LogWarning("[CostumePicker] CostumePickerController が既に存在するため新規生成をキャンセルします");
            Destroy(gameObject);
            return;
        }
        Instance = this;
        m_view = gameObject.AddComponent<CostumePickerView>();
        m_view.OnTabClicked += HandleTabClicked;
        m_view.OnRowClicked += HandleRowClicked;
        m_view.OnCastClicked += HandleCastClicked;
        m_view.OnCloseClicked += HandleCloseClicked;
        m_view.OnSettingsClicked += HandleSettingsClicked;
        m_view.OnBackClicked += HandleBackClicked;
        m_view.OnResetAllClicked += HandleResetAllClicked;
        m_view.OnUnlockAllClicked += HandleUnlockAllClicked;

        // F9 設定パネル等から Stocking 系 Config が変更された場合、picker open 中なら即時メッシュ反映する。
        // ShapeFalloff は blendShape 全 frame の再構築を伴って重いので Update() でデバウンスする。
        if (Configs.StockingOffset != null) Configs.StockingOffset.SettingChanged += OnStockingTuneChanged;
        if (Configs.StockingSkinShrink != null) Configs.StockingSkinShrink.SettingChanged += OnStockingTuneChanged;
        if (Configs.StockingSkinFalloffRadius != null) Configs.StockingSkinFalloffRadius.SettingChanged += OnStockingTuneChanged;
        if (Configs.StockingShapeFalloffRadius != null) Configs.StockingShapeFalloffRadius.SettingChanged += OnStockingShapeFalloffChanged;
        // Tops 距離保存の閾値変更は TopsLoader 側で全 target に適用するため、ここでは購読しない。
    }

    private void OnDestroy()
    {
        if (m_view != null)
        {
            m_view.OnTabClicked -= HandleTabClicked;
            m_view.OnRowClicked -= HandleRowClicked;
            m_view.OnCastClicked -= HandleCastClicked;
            m_view.OnCloseClicked -= HandleCloseClicked;
            m_view.OnSettingsClicked -= HandleSettingsClicked;
            m_view.OnBackClicked -= HandleBackClicked;
            m_view.OnResetAllClicked -= HandleResetAllClicked;
            m_view.OnUnlockAllClicked -= HandleUnlockAllClicked;
        }
        if (Configs.StockingOffset != null) Configs.StockingOffset.SettingChanged -= OnStockingTuneChanged;
        if (Configs.StockingSkinShrink != null) Configs.StockingSkinShrink.SettingChanged -= OnStockingTuneChanged;
        if (Configs.StockingSkinFalloffRadius != null) Configs.StockingSkinFalloffRadius.SettingChanged -= OnStockingTuneChanged;
        if (Configs.StockingShapeFalloffRadius != null) Configs.StockingShapeFalloffRadius.SettingChanged -= OnStockingShapeFalloffChanged;
        if (Instance == this) Instance = null;
    }

    /// <summary>Offset/SkinShrink/SkinFalloffRadius は SettingChanged を即時反映する。</summary>
    private void OnStockingTuneChanged(object _, System.EventArgs __)
    {
        // picker 非表示中は ReapplyStockingForTune が no-op になる。
        // 次回 picker open 時の RebuildItemsFor / ApplyStockings 経由で値が反映されるため、ここで何もしない。
        if (m_view == null || !m_view.IsShown) return;
        ReapplyStockingForTune();
    }

    /// <summary>ShapeFalloff の reapply は blendShape 全 frame 再構築を伴って重いため、デバウンスする。</summary>
    private void OnStockingShapeFalloffChanged(object _, System.EventArgs __)
    {
        if (m_view == null || !m_view.IsShown) return;
        m_shapeFalloffDirtyAtUnscaledTime = Time.unscaledTime + ShapeFalloffDebounceSec;
    }

    private const float ShapeFalloffDebounceSec = 0.2f;
    private float m_shapeFalloffDirtyAtUnscaledTime = -1f;

    private void HandleTabClicked(int index)
    {
        if (!m_view.IsShown) return;
        if (index < 0 || index > 4) return;
        m_activeTab = (CostumePickerView.WardrobeTab)index;
        m_view.Render(BuildRenderData());
    }

    private void HandleRowClicked(int rowIndex)
    {
        if (!m_view.IsShown) return;
        switch (m_activeTab)
        {
            case CostumePickerView.WardrobeTab.Costume:
                if (rowIndex < 0 || rowIndex >= m_costumeItems.Count) return;
                if (m_costumeItems[rowIndex].Locked) return;
                m_costumeSelected = rowIndex;
                break;

            case CostumePickerView.WardrobeTab.Panties:
                if (rowIndex < 0 || rowIndex >= m_pantiesItems.Count) return;
                if (m_pantiesItems[rowIndex].Locked) return;
                m_pantiesSelected = rowIndex;
                break;

            case CostumePickerView.WardrobeTab.Stocking:
                if (rowIndex < 0 || rowIndex >= m_stockingItems.Count) return;
                if (m_stockingItems[rowIndex].Locked) return;
                m_stockingSelected = rowIndex;
                break;

            case CostumePickerView.WardrobeTab.Bottoms:
                if (rowIndex < 0 || rowIndex >= m_bottomsItems.Count) return;
                if (m_bottomsItems[rowIndex].Locked) return;
                m_bottomsSelected = rowIndex;
                break;

            case CostumePickerView.WardrobeTab.Tops:
                if (rowIndex < 0 || rowIndex >= m_topsItems.Count) return;
                if (m_topsItems[rowIndex].Locked) return;
                m_topsSelected = rowIndex;
                break;
        }
        // 行クリックは即トグル適用（cur==selected なら解除、違えば apply）。
        // キー操作 (W/S/↑/↓) は選択のみで、Enter で確定適用。全タブ共通挙動。
        DecideActiveTab();
    }

    private void Update()
    {
        if (m_shapeFalloffDirtyAtUnscaledTime > 0f && Time.unscaledTime >= m_shapeFalloffDirtyAtUnscaledTime)
        {
            m_shapeFalloffDirtyAtUnscaledTime = -1f;
            ReapplyStockingForTune();
        }

        if (Configs.CostumeChangerEnabled == null) return;
        if (!Configs.CostumeChangerEnabled.Value) return;
        // Awake で AddComponent<CostumePickerView>() が何らかの理由（例外）で失敗したケース防御。
        // m_view.IsShown などを触る前段で早期 return する。
        if (m_view == null) return;

        // ConfirmDialog 表示中は Hotkey もピッカー操作も全て無視
        // (ダイアログは GBInput で A/B/Esc を直接読むため、こちらは一切動かさない)
        if (ConfirmDialogHelper.IsActive()) return;

        var kb = Keyboard.current;
        if (kb == null) return;

        // トグル
        if (Configs.CostumeChangerShow.IsTriggered())
        {
            if (m_view.IsShown)
            {
                HideView();
            }
            else if (CanOpen(out var charId))
            {
                OpenFor(charId);
            }
            else
            {
                PatchLogger.LogInfo("[CostumePicker] シーン条件不一致のため開けません");
            }
        }

        if (!m_view.IsShown) return;

        CheckCastChanged();  // カーソル位置に関係なくキャスト変化に追従する

        if (!IsCursorOverPicker) return;   // カーソルがパネル外の場合はキーボード操作を無視

        if (m_mode == PickerMode.Settings)
        {
            UpdateSettingsMode(kb);
            return;
        }

        // タブ切替（A/D・←/→）
        if (kb[Key.A].wasPressedThisFrame || kb[Key.LeftArrow].wasPressedThisFrame)
        {
            ChangeTab(-1);
            return;
        }
        if (kb[Key.D].wasPressedThisFrame || kb[Key.RightArrow].wasPressedThisFrame)
        {
            ChangeTab(1);
            return;
        }

        // 選択移動（W/S・↑/↓）
        if (kb[Key.W].wasPressedThisFrame || kb[Key.UpArrow].wasPressedThisFrame)
        {
            MoveSelection(-1);
            return;
        }
        if (kb[Key.S].wasPressedThisFrame || kb[Key.DownArrow].wasPressedThisFrame)
        {
            MoveSelection(1);
            return;
        }

        // Enter: 全タブでトグル（cur override == selected なら解除、違うなら apply）
        if (kb[Key.Enter].wasPressedThisFrame || kb[Key.NumpadEnter].wasPressedThisFrame)
        {
            DecideActiveTab();
            return;
        }

        if (kb[Key.Escape].wasPressedThisFrame)
        {
            HideView();
            return;
        }

        if (kb[Key.R].wasPressedThisFrame)
        {
            ResetAllTabs();
        }
    }

    private bool CanOpen(out CharID charId)
    {
        charId = CharID.NUM;
        var sys = GBSystem.Instance;
        if (sys == null || !sys.IsIngame) return false;
        var env = sys.GetActiveEnvScene();
        if (env == null) return false;

        if (CostumeChangerPatch.IsFittingRoomActiveExternal()) return false;

        var gameData = sys.RefGameData();
        if (gameData == null) return false;

        GetVisibleCastIds(m_visibleCastsBuf);
        if (m_visibleCastsBuf.Count == 0) return false;

        var currentCast = gameData.GetCurrentCast();
        charId = currentCast < CharID.NUM && m_visibleCastsBuf.Contains(currentCast)
            ? currentCast
            : m_visibleCastsBuf[0];
        return true;
    }

    private void OpenFor(CharID charId)
    {
        // CanOpen() が m_visibleCastsBuf を同フレームで既に更新済みなので直接コピー
        m_visibleCasts.Clear();
        m_visibleCasts.AddRange(m_visibleCastsBuf);

        var sys = GBSystem.Instance;
        var currentCast = sys?.RefGameData()?.GetCurrentCast() ?? CharID.NUM;
        m_followCurrentCast = charId == currentCast;

        m_activeTab = CostumePickerView.WardrobeTab.Costume;
        RebuildItemsFor(charId);
        m_mode = PickerMode.Picker;   // 毎回ピッカーから開始
        m_view.ShowPicker(BuildRenderData());
        int cUnlock = m_costumeItems.Count(x => !x.Locked);
        int pUnlock = m_pantiesItems.Count(x => !x.Locked);
        int sUnlock = m_stockingItems.Count(x => !x.Locked);
        int bUnlock = m_bottomsItems.Count(x => !x.Locked);
        int tUnlock = m_topsItems.Count(x => !x.Locked);
        PatchLogger.LogInfo($"[CostumePicker] オープン: {charId} / 衣装{cUnlock}/{m_costumeItems.Count} / パンツ{pUnlock}/{m_pantiesItems.Count} / ストッキング{sUnlock}/{m_stockingItems.Count} / ボトム{bUnlock}/{m_bottomsItems.Count} / トップス{tUnlock}/{m_topsItems.Count}");
        EnsurePrefetchDlcNamesAsync().Forget();
    }

    private void RebuildItemsFor(CharID charId)
    {
        m_activeChar = charId;

        var costumeViewedSet = new HashSet<CostumeType>(CostumeViewHistory.GetViewedList(charId));
        var installedDlc = GetInstalledDlcCostumes();
        m_costumeItems = new List<(CostumeType, bool)>();
        for (int i = 0; i < (int)CostumeType.Num; i++)
        {
            var c = (CostumeType)i;
            if (c.IsDLC() && !installedDlc.Contains(c)) continue;
            bool locked = !costumeViewedSet.Contains(c);
            m_costumeItems.Add((c, locked));
        }

        var pantiesViewedSet = new HashSet<(int, int)>();
        foreach (var p in PantiesViewHistory.GetViewedList(charId)) pantiesViewedSet.Add((p.Type, p.Color));
        m_pantiesItems = new List<(int, int, bool)>();
        for (int t = 0; t < PantiesOverrideStore.TypeCount; t++)
        {
            for (int c = 0; c < PantiesOverrideStore.ColorCount; c++)
            {
                bool locked = !pantiesViewedSet.Contains((t, c));
                m_pantiesItems.Add((t, c, locked));
            }
        }

        m_stockingItems = new List<(int, bool)>();
        for (int i = 0; i <= StockingOverrideStore.Max; i++)
        {
            // KneeSocks 系（5–7）はデフォルト解放済み（閲覧履歴に依存しない）
            bool locked = StockingOverrideStore.IsKneeSocksType(i)
                ? false
                : !StockingViewHistory.IsViewed(charId, i);
            m_stockingItems.Add((i, locked));
        }

        // Bottoms: フルボディ衣装 (SwimWear / Bunnygirl / フルボディ DLC) / Shirt は除外。
        // - SwimWear: ワンピース型で skirt/pants 構造が無い (KANA は bikini bottom を持つが
        //             cross-char 適用で物理破綻するため撤退済。memory `project_kana_swimwear_bottoms_retreat.md` 参照)。
        //             SwimWear donor 全身は Tops 経由 (full-body) で扱う方針。
        // - Bunnygirl / フルボディ DLC: フルボディスーツで構造差大。
        // - Shirt: 下半身に実体的な差分なし（Tops と対称）。
        // donor == target も許可（自身の他コスチューム由来の bottoms を素体に移植可能）。
        m_bottomsItems = new List<(CharID, CostumeType, bool)>();
        bool hasBottomsOverride = BottomsOverrideStore.TryGet(charId, out var curBottoms);
        for (int d = (int)CharID.KANA; d < (int)CharID.NUM; d++)
        {
            var donor = (CharID)d;
            for (int c = 0; c < (int)CostumeType.Num; c++)
            {
                var costume = (CostumeType)c;
                if (costume.IsFullBodyCostume()) continue;
                if (costume == CostumeType.Shirt) continue;
                if (costume.IsDLC() && !installedDlc.Contains(costume)) continue;
                bool bLocked = !CostumeViewHistory.IsViewed(donor, costume);
                if (bLocked && hasBottomsOverride
                    && curBottoms.DonorChar == donor && curBottoms.DonorCostume == costume)
                {
                    bLocked = false;
                }
                m_bottomsItems.Add((donor, costume, bLocked));
            }
        }

        // Tops: SwimWear donor を許可（ApplySwimWearBottomsPhase で full-body 移植経路がある）、
        // Bunnygirl / フルボディ DLC / Shirt は除外。
        // フルボディ DLC は SwimWear と異なり specialized donor 経路を持たないため、
        // Tops 経由の full-body 移植は未実装。安全側で除外する。
        // Shirt は mesh_costume_sleeve のみ実体的な Tops 差分で見栄えが薄いため UI から外す。
        // donor == target も許可（自身の他コスチューム由来の tops を素体に移植可能）。
        m_topsItems = new List<(CharID, CostumeType, bool)>();
        bool hasTopsOverride = TopsOverrideStore.TryGet(charId, out var curTops);
        for (int d = (int)CharID.KANA; d < (int)CharID.NUM; d++)
        {
            var donor = (CharID)d;
            for (int c = 0; c < (int)CostumeType.Num; c++)
            {
                var costume = (CostumeType)c;
                if (costume.IsFullBodyCostume() && costume != CostumeType.SwimWear) continue;
                if (costume == CostumeType.Shirt) continue;
                if (costume.IsDLC() && !installedDlc.Contains(costume)) continue;
                bool tLocked = !CostumeViewHistory.IsViewed(donor, costume);
                if (tLocked && hasTopsOverride
                    && curTops.DonorChar == donor && curTops.DonorCostume == costume)
                {
                    tLocked = false;
                }
                m_topsItems.Add((donor, costume, tLocked));
            }
        }

        m_costumeSelected = FindOverrideOrFirstUnlocked(
            m_costumeItems,
            CostumeOverrideStore.TryGet(charId, out var ovCostume),
            x => x.Costume == ovCostume,
            x => x.Locked);
        m_pantiesSelected = FindOverrideOrFirstUnlocked(
            m_pantiesItems,
            PantiesOverrideStore.TryGet(charId, out var ovPT, out var ovPC),
            x => x.Type == ovPT && x.Color == ovPC,
            x => x.Locked);
        m_stockingSelected = FindOverrideOrFirstUnlocked(
            m_stockingItems,
            StockingOverrideStore.TryGet(charId, out var ovStk),
            x => x.Type == ovStk,
            x => x.Locked);
        m_bottomsSelected = FindOverrideOrFirstUnlocked(
            m_bottomsItems,
            BottomsOverrideStore.TryGet(charId, out var ovBottoms),
            x => x.Donor == ovBottoms.DonorChar && x.Costume == ovBottoms.DonorCostume,
            x => x.Locked);
        m_topsSelected = FindOverrideOrFirstUnlocked(
            m_topsItems,
            TopsOverrideStore.TryGet(charId, out var ovTops),
            x => x.Donor == ovTops.DonorChar && x.Costume == ovTops.DonorCostume,
            x => x.Locked);
    }

    private void CheckCastChanged()
    {
        if (m_loading) return;  // ローディング中は追従しない

        var sys = GBSystem.Instance;
        if (sys == null || !sys.IsIngame) return;
        var env = sys.GetActiveEnvScene();
        if (env == null) return;
        var gameData = sys.RefGameData();
        if (gameData == null) return;

        // 表示中キャストを再取得してバッファで比較
        GetVisibleCastIds(m_visibleCastsBuf);

        // visible が 0 のとき → currentCast のみで代替表示（シーン遷移中の一時状態など）
        if (m_visibleCastsBuf.Count == 0)
        {
            var fallback = gameData.GetCurrentCast();
            if (fallback >= CharID.NUM)
            {
                HideView();
                return;
            }
            m_visibleCastsBuf.Add(fallback);
        }

        bool visibleChanged = !ListsEqual(m_visibleCasts, m_visibleCastsBuf);
        if (visibleChanged)
        {
            m_visibleCasts.Clear();
            m_visibleCasts.AddRange(m_visibleCastsBuf);
        }

        var currentCast = gameData.GetCurrentCast();

        // m_activeChar が visible から外れた場合のフォールバック
        if (!m_visibleCasts.Contains(m_activeChar))
        {
            CharID nextChar;
            if (m_followCurrentCast && currentCast < CharID.NUM && m_visibleCasts.Contains(currentCast))
                nextChar = currentCast;
            else
                nextChar = m_visibleCasts[0];
            RefreshForCast(nextChar);  // m_visibleCasts は既に更新済みなので strip も反映される
            return;
        }

        // m_followCurrentCast かつ currentCast が visible 内で変化した
        if (m_followCurrentCast && currentCast < CharID.NUM
            && currentCast != m_activeChar && m_visibleCasts.Contains(currentCast))
        {
            RefreshForCast(currentCast);
            return;
        }

        // visible のみ変化（キャスト切替なし）→ ストリップ + ヘッダー更新
        if (visibleChanged)
        {
            if (m_mode == PickerMode.Settings)
                m_view.RenderSettings(BuildSettingsData());
            else
                m_view.Render(BuildRenderData());
        }
    }

    /// <summary>
    /// 現在の EnvScene から「Preload 済み + activeInHierarchy」のキャスト一覧を result に詰める。
    /// 呼び出し元は事前に result.Clear() が済んでいることを保証する必要はない（内部で Clear する）。
    /// </summary>
    private static void GetVisibleCastIds(List<CharID> result)
    {
        result.Clear();
        var sys = GBSystem.Instance;
        if (sys == null || !sys.IsIngame) return;
        var env = sys.GetActiveEnvScene();
        if (env == null) return;
        for (int i = (int)CharID.KANA; i < (int)CharID.NUM; i++)
        {
            var id = (CharID)i;
            if (env.FindCharacterIndex(id) < 0) continue;
            var charObj = env.FindCharacter(id);
            if (charObj == null || !charObj.activeInHierarchy) continue;
            result.Add(id);
        }
    }

    /// <summary>キャストストリップのボタンクリックハンドラ。index は VisibleCasts 内のインデックス。</summary>
    private void HandleCastClicked(int index)
    {
        if (!m_view.IsShown) return;
        if (m_loading) return;
        if (index < 0 || index >= m_visibleCasts.Count) return;

        var newId = m_visibleCasts[index];

        var sys = GBSystem.Instance;
        var currentCast = sys?.RefGameData()?.GetCurrentCast() ?? CharID.NUM;
        m_followCurrentCast = newId == currentCast;

        if (newId == m_activeChar) return;  // 同じキャラの再クリックは no-op
        RefreshForCast(newId);
    }

    private static bool ListsEqual(List<CharID> a, List<CharID> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private void RefreshForCast(CharID newId)
    {
        var oldId = m_activeChar;
        // m_activeTab は意図的に引き継ぐ — キャスト切替時は現在タブを維持する。
        RebuildItemsFor(newId);
        if (m_mode == PickerMode.Settings)
            m_view.RenderSettings(BuildSettingsData());
        else
            m_view.Render(BuildRenderData());
        PatchLogger.LogInfo($"[CostumePicker] キャスト切替: {oldId} → {newId}");
    }

    private static int FindOverrideOrFirstUnlocked<T>(
        List<T> list, bool hasOverride, Func<T, bool> match, Func<T, bool> isLocked)
    {
        if (list.Count == 0) return -1;
        if (hasOverride)
        {
            for (int i = 0; i < list.Count; i++) if (match(list[i])) return i;
        }
        for (int i = 0; i < list.Count; i++) if (!isLocked(list[i])) return i;
        return 0; // 全て locked: 便宜上 0 を返す（適用ロジックは locked を弾く）
    }

    /// <summary>from を起点に delta 方向の最初の非 Locked を返す。見つからなければ from を据置。</summary>
    private static int MoveToUnlocked<T>(List<T> list, Func<T, bool> isLocked, int from, int delta)
    {
        if (list.Count == 0) return -1;
        int i = from + delta;
        while (i >= 0 && i < list.Count)
        {
            if (!isLocked(list[i])) return i;
            i += delta;
        }
        return from;
    }

    private CostumePickerView.RenderData BuildRenderData()
    {
        return new CostumePickerView.RenderData
        {
            CharId = m_activeChar,
            ActiveTab = m_activeTab,
            CostumeLabels = m_costumeItems.Select(x => x.Locked ? "???" : ResolveCostumeName(x.Costume)).ToList(),
            PantiesLabels = m_pantiesItems.Select(x => x.Locked ? "???" : ResolvePantiesName(m_activeChar, x.Type, x.Color)).ToList(),
            StockingLabels = m_stockingItems.Select(x => x.Locked ? "???" : ResolveStockingName(x.Type)).ToList(),
            BottomsLabels = m_bottomsItems.Select(x => x.Locked
                ? $"{ResolveCharName(x.Donor)}/???"
                : $"{ResolveCharName(x.Donor)}/{ResolveCostumeName(x.Costume)}").ToList(),
            TopsLabels = m_topsItems.Select(x => x.Locked
                ? $"{ResolveCharName(x.Donor)}/???"
                : $"{ResolveCharName(x.Donor)}/{ResolveCostumeName(x.Costume)}").ToList(),
            CostumeLocks = m_costumeItems.Select(x => x.Locked).ToList(),
            PantiesLocks = m_pantiesItems.Select(x => x.Locked).ToList(),
            StockingLocks = m_stockingItems.Select(x => x.Locked).ToList(),
            BottomsLocks = m_bottomsItems.Select(x => x.Locked).ToList(),
            TopsLocks = m_topsItems.Select(x => x.Locked).ToList(),
            CostumeSelected = m_costumeSelected,
            PantiesSelected = m_pantiesSelected,
            StockingSelected = m_stockingSelected,
            BottomsSelected = m_bottomsSelected,
            TopsSelected = m_topsSelected,
            CostumeCurrent = CostumeOverrideStore.TryGet(m_activeChar, out var oc)
                ? m_costumeItems.FindIndex(x => x.Costume == oc) : -1,
            PantiesCurrent = PantiesOverrideStore.TryGet(m_activeChar, out var opT, out var opC)
                ? m_pantiesItems.FindIndex(x => x.Type == opT && x.Color == opC) : -1,
            StockingCurrent = StockingOverrideStore.TryGet(m_activeChar, out var os)
                ? m_stockingItems.FindIndex(x => x.Type == os) : -1,
            BottomsCurrent = BottomsOverrideStore.TryGet(m_activeChar, out var ob)
                ? m_bottomsItems.FindIndex(x => x.Donor == ob.DonorChar && x.Costume == ob.DonorCostume) : -1,
            TopsCurrent = TopsOverrideStore.TryGet(m_activeChar, out var ot)
                ? m_topsItems.FindIndex(x => x.Donor == ot.DonorChar && x.Costume == ot.DonorCostume) : -1,
            VisibleCasts = m_visibleCasts.AsReadOnly(),
            VisibleCastSelectedIndex = m_visibleCasts.IndexOf(m_activeChar),
        };
    }

    private static char TypeLetter(int type) => (char)('A' + type);

    // DLC 衣装名キャッシュ。Text/dlc_costume/{DLCID}.txt の 1 行目から現在言語の名前を抽出。
    // 1 行目は "日本語名,,English Name,,中文,,..." の `,,` 区切りで多言語格納されているため、
    // HomeScene.showDLCAnnounce と同じ言語インデックス選択をする (FittingRoom は array[0] 直で
    // 多言語が混ざるバグがあるので真似しない)。プロセス永続: DLC は再起動まで不変。
    private static readonly Dictionary<CostumeType, string> s_dlcNameCache = new();

    // 連続 OpenFor で並列 prefetch が走らないようにする in-flight ガード。
    private static bool s_dlcPrefetchInFlight;

    private static string ResolveCostumeName(CostumeType costume)
    {
        if (costume.IsDLC() && s_dlcNameCache.TryGetValue(costume, out var dlcName))
            return dlcName;

        var msg = GBSystem.Instance?.RefMessage();
        if (msg == null) return costume.ToString();
        var mid = CostumeToMsgId(costume);
        if (mid == null) return costume.ToString();
        try { return msg.RefText(mid); }
        catch { return costume.ToString(); }
    }

    private async UniTaskVoid EnsurePrefetchDlcNamesAsync()
    {
        if (s_dlcPrefetchInFlight) return;

        var sys = GBSystem.Instance;
        if (sys == null) return;

        var installed = GetInstalledDlcCostumes();
        if (installed.Count == 0) return;
        if (installed.All(c => s_dlcNameCache.ContainsKey(c))) return;

        s_dlcPrefetchInFlight = true;
        try
        {
            bool any = false;
            foreach (var costume in installed)
            {
                if (s_dlcNameCache.ContainsKey(costume)) continue;
                try
                {
                    var dlcId = costume.ToDLCID();
                    var handle = sys.LoadDLCAsync<TextAsset>(
                        dlcId, "Text/dlc_costume/" + dlcId + ".txt");
                    await handle.ToUniTask();
                    var asset = handle.Result;
                    if (asset == null) continue;
                    var lines = asset.text.Split(
                        new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
                    if (lines.Length == 0 || string.IsNullOrEmpty(lines[0])) continue;

                    var langs = lines[0].Split(new[] { ",," }, StringSplitOptions.None);
                    var saveData = sys.RefSaveData();
                    int langIndex = saveData != null ? (int)saveData.GetLanguage() : 0;
                    if (langIndex < 0) langIndex = 0;
                    if (langIndex >= langs.Length) langIndex = langs.Length - 1;
                    var name = langs[langIndex];
                    if (string.IsNullOrEmpty(name)) continue;

                    s_dlcNameCache[costume] = name;
                    any = true;
                }
                catch (Exception e)
                {
                    PatchLogger.LogWarning($"[CostumePicker] DLC 名取得失敗: {costume} ({e.Message})");
                }
            }

            if (any && m_view != null && m_view.IsShown)
                m_view.Render(BuildRenderData());
        }
        finally
        {
            s_dlcPrefetchInFlight = false;
        }
    }

    /// <summary>
    /// CharID を本体ローカライズされた表示名で解決する。
    /// マッピングは <see cref="GB.Game.CharIDUtil.GetMSGID"/> を流用（本体側で更新があれば追従する）。
    /// </summary>
    private static string ResolveCharName(CharID id)
    {
        var mid = id.GetMSGID();
        if (mid == null || mid.ID == 0) return id.ToString();
        var msg = GBSystem.Instance?.RefMessage();
        if (msg == null) return id.ToString();
        try { return msg.RefText(mid); }
        catch { return id.ToString(); }
    }

    private static MSGID CostumeToMsgId(CostumeType c) => c switch
    {
        CostumeType.Uniform => (MSGID)MSGID_SPLIT_2.FITTING_ROOM_COSTUME_UNIFORM,
        CostumeType.Casual => (MSGID)MSGID_SPLIT_2.FITTING_ROOM_COSTUME_CASUAL,
        CostumeType.SwimWear => (MSGID)MSGID_SPLIT_2.FITTING_ROOM_COSTUME_SWIMWEAR,
        CostumeType.Babydoll => (MSGID)MSGID_SPLIT_2.FITTING_ROOM_COSTUME_BABYDOLL,
        CostumeType.Shirt => (MSGID)MSGID_SPLIT_2.FITTING_ROOM_COSTUME_SHIRT,
        CostumeType.Bunnygirl => (MSGID)MSGID_SPLIT_2.FITTING_ROOM_COSTUME_BUNNYGIRL,
        _ => null,
    };

    /// <summary>
    /// パンツ名を MSGID_SPLIT_2.FITTING_ROOM_PANTIES_{CHAR}_A_0 を起点に
    /// (type * ColorCount + color) のオフセットで解決する。
    /// </summary>
    private static string ResolvePantiesName(CharID id, int type, int color)
    {
        string fallback = $"Type {TypeLetter(type)} / Color {color}";
        var msg = GBSystem.Instance?.RefMessage();
        if (msg == null) return fallback;
        var begin = PantiesBeginMsgId(id);
        if (begin == null) return fallback;
        int idx = type * PantiesOverrideStore.ColorCount + color;
        try { return msg.RefText(begin.ID + idx); }
        catch { return fallback; }
    }

    private static MSGID PantiesBeginMsgId(CharID id) => id switch
    {
        CharID.KANA => (MSGID)MSGID_SPLIT_2.FITTING_ROOM_PANTIES_KANA_A_0,
        CharID.RIN => (MSGID)MSGID_SPLIT_2.FITTING_ROOM_PANTIES_RIN_A_0,
        CharID.MIUKA => (MSGID)MSGID_SPLIT_2.FITTING_ROOM_PANTIES_MIUKA_A_0,
        CharID.ERISA => (MSGID)MSGID_SPLIT_2.FITTING_ROOM_PANTIES_ERISA_A_0,
        CharID.KUON => (MSGID)MSGID_SPLIT_2.FITTING_ROOM_PANTIES_KUON_A_0,
        CharID.LUNA => (MSGID)MSGID_SPLIT_2.FITTING_ROOM_PANTIES_LUNA_A_0,
        _ => null,
    };

    /// <summary>
    /// ストッキング名を MSGID で解決する。type 4 (白網) は本体 FittingRoom でも
    /// 専用 MSGID が存在せず Bunnygirl 時に DEFAULT 選択で暗黙適用される位置付けなので、
    /// ここでは英語フォールバックを使う。
    /// </summary>
    private static string ResolveStockingName(int type)
    {
        string fallback = StockingFallbackName(type);
        var msg = GBSystem.Instance?.RefMessage();
        if (msg == null) return fallback;
        var mid = StockingToMsgId(type);
        if (mid == null) return fallback;
        try { return msg.RefText(mid); }
        catch { return fallback; }
    }

    private static MSGID StockingToMsgId(int type) => type switch
    {
        0 => (MSGID)MSGID_SPLIT_2.FITTING_ROOM_STOCKING_DEFAULT,
        1 => (MSGID)MSGID_SPLIT_2.FITTING_ROOM_STOCKING_PANSTO_BLACK,
        2 => (MSGID)MSGID_SPLIT_2.FITTING_ROOM_STOCKING_PANSTO_WHITE,
        3 => (MSGID)MSGID_SPLIT_2.FITTING_ROOM_STOCKING_PANSTO_FISHNET,
        _ => null,
    };

    private static string StockingFallbackName(int type) => type switch
    {
        0 => "None",
        1 => "Black Pansto",
        2 => "White Pansto",
        3 => "Black Fishnet",
        4 => "White Fishnet",
        5 => "瑠那のニーハイ",
        6 => "黒ニーハイ",
        7 => "白ニーハイ",
        _ => $"#{type}",
    };

    // DLC インストール HashSet は CostumeChangerPatch 側の lazy キャッシュを共有する。
    private static HashSet<CostumeType> GetInstalledDlcCostumes()
        => CostumeChangerPatch.GetDLCInstalledSet() ?? new HashSet<CostumeType>();

    private void ChangeTab(int delta)
    {
        int next = ((int)m_activeTab + delta + 5) % 5;
        m_activeTab = (CostumePickerView.WardrobeTab)next;
        m_view.Render(BuildRenderData());
    }

    private void MoveSelection(int delta)
    {
        // 全タブで W/S/↑/↓ は選択のみ。適用は Enter / 行クリック（DecideActiveTab）で行う。
        switch (m_activeTab)
        {
            case CostumePickerView.WardrobeTab.Costume:
                if (m_costumeItems.Count == 0) return;
                m_costumeSelected = MoveToUnlocked(m_costumeItems, x => x.Locked, m_costumeSelected, delta);
                break;

            case CostumePickerView.WardrobeTab.Panties:
                if (m_pantiesItems.Count == 0) return;
                m_pantiesSelected = MoveToUnlocked(m_pantiesItems, x => x.Locked, m_pantiesSelected, delta);
                break;

            case CostumePickerView.WardrobeTab.Stocking:
                if (m_stockingItems.Count == 0) return;
                m_stockingSelected = MoveToUnlocked(m_stockingItems, x => x.Locked, m_stockingSelected, delta);
                break;

            case CostumePickerView.WardrobeTab.Bottoms:
                if (m_bottomsItems.Count == 0) return;
                m_bottomsSelected = MoveToUnlocked(m_bottomsItems, x => x.Locked, m_bottomsSelected, delta);
                break;

            case CostumePickerView.WardrobeTab.Tops:
                if (m_topsItems.Count == 0) return;
                m_topsSelected = MoveToUnlocked(m_topsItems, x => x.Locked, m_topsSelected, delta);
                break;
        }
        m_view.Render(BuildRenderData());
    }

    private void DecideActiveTab()
    {
        // 意図: apply/release いずれも View を残し Render のみ行う。
        // 閉じるのは Esc キーまたは右上 × ボタンによる明示操作のみ。
        if (m_activeChar >= CharID.NUM) return;
        switch (m_activeTab)
        {
            case CostumePickerView.WardrobeTab.Costume:
                if (m_costumeSelected < 0 || m_costumeSelected >= m_costumeItems.Count) return;
                if (m_costumeItems[m_costumeSelected].Locked) return;
                var costume = m_costumeItems[m_costumeSelected].Costume;
                if (CostumeOverrideStore.TryGet(m_activeChar, out var curCostume) && curCostume == costume)
                {
                    CostumeOverrideStore.Clear(m_activeChar);
                    ReloadCurrentAsync(m_activeChar).Forget();
                    m_view.Render(BuildRenderData());
                    return;
                }
                ApplyCostumeOverrideAsync(m_activeChar, costume).Forget();
                m_view.Render(BuildRenderData());
                break;

            case CostumePickerView.WardrobeTab.Panties:
                if (m_pantiesSelected < 0 || m_pantiesSelected >= m_pantiesItems.Count) return;
                if (m_pantiesItems[m_pantiesSelected].Locked) return;
                var pItem = m_pantiesItems[m_pantiesSelected];
                int t = pItem.Type;
                int c = pItem.Color;
                if (PantiesOverrideStore.TryGet(m_activeChar, out var curT, out var curC) && curT == t && curC == c)
                {
                    PantiesOverrideStore.Clear(m_activeChar);
                    RestoreDefaultPanties(m_activeChar);
                    m_view.Render(BuildRenderData());
                    return;
                }
                ApplyPanties();
                break;

            case CostumePickerView.WardrobeTab.Stocking:
                if (m_stockingSelected < 0 || m_stockingSelected >= m_stockingItems.Count) return;
                if (m_stockingItems[m_stockingSelected].Locked) return;
                int stk = m_stockingItems[m_stockingSelected].Type;
                if (StockingOverrideStore.TryGet(m_activeChar, out var curStk) && curStk == stk)
                {
                    StockingOverrideStore.Clear(m_activeChar);
                    if (StockingOverrideStore.IsKneeSocksType(stk))
                    {
                        // KneeSocks 解除: Apply() の副作用（mesh_kneehigh/mesh_socks 非表示、blendShape）を復元
                        var env2 = GBSystem.Instance?.GetActiveEnvScene();
                        var charObj = env2?.FindCharacter(m_activeChar);
                        if (charObj != null) KneeSocksLoader.Restore(charObj);
                    }
                    // env.ApplyStockings が mesh_stockings.sharedMesh を上書きする。
                    RestoreDefaultStocking(m_activeChar);
                    m_view.Render(BuildRenderData());
                    return;
                }
                ApplyStocking();
                break;

            case CostumePickerView.WardrobeTab.Bottoms:
                if (m_bottomsSelected < 0 || m_bottomsSelected >= m_bottomsItems.Count) return;
                if (m_bottomsItems[m_bottomsSelected].Locked) return;
                var bItem = m_bottomsItems[m_bottomsSelected];
                if (BottomsOverrideStore.TryGet(m_activeChar, out var curB)
                    && curB.DonorChar == bItem.Donor && curB.DonorCostume == bItem.Costume)
                {
                    // トグル解除: store クリア + target SMR をスナップショットから素状態に復元。
                    // env.LoadCharacter は同 costume だと no-op で setup() Postfix が発火しないため
                    // 直接 Restore する。
                    BottomsOverrideStore.Clear(m_activeChar);
                    var envR = GBSystem.Instance?.GetActiveEnvScene();
                    var charObjR = envR?.FindCharacter(m_activeChar);
                    if (charObjR != null) BottomsLoader.RestoreFor(charObjR);
                    m_view.Render(BuildRenderData());
                    return;
                }
                ApplyBottomsAsync(m_activeChar, bItem.Donor, bItem.Costume).Forget();
                m_view.Render(BuildRenderData());
                break;

            case CostumePickerView.WardrobeTab.Tops:
                if (m_topsSelected < 0 || m_topsSelected >= m_topsItems.Count) return;
                if (m_topsItems[m_topsSelected].Locked) return;
                var tItem = m_topsItems[m_topsSelected];
                if (TopsOverrideStore.TryGet(m_activeChar, out var curTops)
                    && curTops.DonorChar == tItem.Donor && curTops.DonorCostume == tItem.Costume)
                {
                    // トグル解除: store クリア + target SMR をスナップショットから素状態に復元。
                    // env.LoadCharacter は同 costume だと no-op で setup() Postfix が発火しないため
                    // 直接 Restore する（Bottoms と同方針）。
                    TopsOverrideStore.Clear(m_activeChar);
                    var envR = GBSystem.Instance?.GetActiveEnvScene();
                    var charObjR = envR?.FindCharacter(m_activeChar);
                    if (charObjR != null) TopsLoader.RestoreFor(charObjR);
                    m_view.Render(BuildRenderData());
                    return;
                }
                ApplyTopsAsync(m_activeChar, tItem.Donor, tItem.Costume).Forget();
                m_view.Render(BuildRenderData());
                break;
        }
    }

    private async UniTaskVoid ApplyTopsAsync(CharID id, CharID donor, CostumeType costume)
    {
        if (m_loading) return;
        m_loading = true;
        bool hadPrev = TopsOverrideStore.TryGet(id, out var prev);
        try
        {
            // 1. lazy preload（既ロード or in-flight なら即返）
            bool donorOk = await TopsLoader.PreloadDonorAsync(donor, costume);
            if (!donorOk)
            {
                PatchLogger.LogWarning($"[CostumePicker] donor {donor}/{costume} に Tops SMR が無く apply 中止");
                return;
            }
            // 1.5. skin donor を target/Babydoll で固定先行 preload。
            //      Apply 内で target の mesh_skin_upper を target/Babydoll 版に差し替えるため。
            //      Babydoll が最も汎用的な mesh_skin_upper を持つので donor.costume に関係なく固定。
            //      戻り値 false (preload 失敗 or Tops 候補ゼロ) は警告ログ。Apply は続行するが、
            //      Apply (d) の skin upper swap がスキップされ Babydoll 基準の境界整合は得られない。
            bool skinDonorOk = await TopsLoader.PreloadDonorAsync(id, TopsLoader.SkinDonorCostume);
            if (!skinDonorOk)
                PatchLogger.LogWarning($"[CostumePicker] skin donor (target {id}/{TopsLoader.SkinDonorCostume}) preload 失敗、境界整合不全のまま apply 続行");
            // 1.6. donor 側 Babydoll skin reference を先行 preload。
            //      Apply (e) の per-vert distance preservation が「donor Babydoll skin / target Babydoll skin」
            //      の対称な基準で d_donor / d_target を比較するため。donorCostume が既に Babydoll なら
            //      PreloadDonorAsync は即返。失敗時は (e) で skip + warning が走る。
            if (costume != TopsLoader.SkinDonorCostume)
            {
                bool donorSkinDonorOk = await TopsLoader.PreloadDonorAsync(donor, TopsLoader.SkinDonorCostume);
                if (!donorSkinDonorOk)
                    PatchLogger.LogWarning($"[CostumePicker] donor skin donor ({donor}/{TopsLoader.SkinDonorCostume}) preload 失敗、距離保存補正は skip され apply 続行");
            }
            // 2. store 更新
            if (!TopsOverrideStore.Set(id, donor, costume))
            {
                PatchLogger.LogWarning($"[CostumePicker] TopsOverrideStore.Set 失敗: target={id} donor={donor} costume={costume}");
                return;
            }
            // 3. 直接 SMR 差し替え。env.LoadCharacter 経由の reload は同 costume だと
            //    no-op で setup() Postfix が発火しないため、target の既存 GameObject に直接適用する。
            var env = GBSystem.Instance?.GetActiveEnvScene();
            var charObj = env?.FindCharacter(id);
            if (charObj == null)
            {
                PatchLogger.LogWarning($"[CostumePicker] target {id} の GameObject が見つからず apply 中止");
                if (hadPrev) TopsOverrideStore.Set(id, prev.DonorChar, prev.DonorCostume);
                else TopsOverrideStore.Clear(id);
                return;
            }
            TopsLoader.ApplyDirectly(charObj, donor, costume);
            m_view.Render(BuildRenderData());
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[CostumePicker] 上衣移植失敗: {ex}");
            // 部分的に SMR 変更後の例外で乖離が起きないよう、SMR を素状態に戻してから
            // store を旧状態へ revert する（Bottoms と同方針）。
            var envC = GBSystem.Instance?.GetActiveEnvScene();
            var charObjC = envC?.FindCharacter(id);
            if (charObjC != null) TopsLoader.RestoreFor(charObjC);
            if (hadPrev) TopsOverrideStore.Set(id, prev.DonorChar, prev.DonorCostume);
            else TopsOverrideStore.Clear(id);
        }
        finally { m_loading = false; }
    }

    private async UniTaskVoid ApplyBottomsAsync(CharID id, CharID donor, CostumeType costume)
    {
        if (m_loading) return;
        m_loading = true;
        // 失敗時に旧 override を復元するため事前保存
        bool hadPrev = BottomsOverrideStore.TryGet(id, out var prev);
        try
        {
            // 1. lazy preload（既ロード or in-flight なら即返）
            // donorOk == false (donor に skirt/pants 無し) でも続行する。
            // Apply 内で hide 経路を走らせて target の bottoms を非表示にする
            // (RIN/MIUKA SwimWear などワンピース型 donor 選択時の意図的「下半身素」)。
            bool donorOk = await BottomsLoader.PreloadDonorAsync(donor, costume);
            if (!donorOk)
                PatchLogger.LogInfo($"[CostumePicker] donor {donor}/{costume} に bottoms SMR が無いため target の bottoms を非表示にします");
            // 2. store 更新（後続 setup() 系での再適用にも一貫性を持たせるため必ず更新）
            if (!BottomsOverrideStore.Set(id, donor, costume))
            {
                PatchLogger.LogWarning($"[CostumePicker] BottomsOverrideStore.Set 失敗: target={id} donor={donor} costume={costume}");
                return;
            }
            // 3. 直接 SMR 差し替え。env.LoadCharacter 経由の reload は同 costume だと
            //    no-op で setup() Postfix が発火しないため、target の既存 GameObject に直接適用する。
            var env = GBSystem.Instance?.GetActiveEnvScene();
            var charObj = env?.FindCharacter(id);
            if (charObj == null)
            {
                PatchLogger.LogWarning($"[CostumePicker] target {id} の GameObject が見つからず apply 中止");
                // store だけ新 donor を指す orphan を防ぐため revert
                if (hadPrev) BottomsOverrideStore.Set(id, prev.DonorChar, prev.DonorCostume);
                else BottomsOverrideStore.Clear(id);
                return;
            }
            BottomsLoader.ApplyDirectly(charObj, donor, costume);
            m_view.Render(BuildRenderData());
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[CostumePicker] 下衣移植失敗: {ex}");
            // ApplyDirectly が部分的に SMR を変更した後の例外で乖離が起きないよう、
            // SMR を素状態に戻してから override store を旧状態へ revert する。
            // RestoreFor は snapshot 未捕捉時は no-op。
            var envC = GBSystem.Instance?.GetActiveEnvScene();
            var charObjC = envC?.FindCharacter(id);
            if (charObjC != null) BottomsLoader.RestoreFor(charObjC);
            if (hadPrev) BottomsOverrideStore.Set(id, prev.DonorChar, prev.DonorCostume);
            else BottomsOverrideStore.Clear(id);
        }
        finally { m_loading = false; }
    }

    private void ApplyPanties()
    {
        if (m_activeChar >= CharID.NUM) return;
        if (m_pantiesSelected < 0 || m_pantiesSelected >= m_pantiesItems.Count) return;
        if (m_pantiesItems[m_pantiesSelected].Locked) return;
        var pItem = m_pantiesItems[m_pantiesSelected];
        int t = pItem.Type;
        int c = pItem.Color;
        PantiesOverrideStore.Set(m_activeChar, t, c);

        var env = GBSystem.Instance?.GetActiveEnvScene();
        if (env != null)
        {
            try
            {
                env.ReloadPanties(m_activeChar, c, t);
            }
            catch (Exception ex)
            {
                PatchLogger.LogWarning($"[CostumePicker] パンツ切替失敗: {ex}");
            }
        }
        m_view.Render(BuildRenderData());
    }

    private void ApplyStocking()
    {
        if (m_activeChar >= CharID.NUM) return;
        if (m_stockingSelected < 0 || m_stockingSelected >= m_stockingItems.Count) return;
        if (m_stockingItems[m_stockingSelected].Locked) return;
        int type = m_stockingItems[m_stockingSelected].Type;

        bool wasKneeSocks = StockingOverrideStore.TryGet(m_activeChar, out var prevStk)
                            && StockingOverrideStore.IsKneeSocksType(prevStk);
        StockingOverrideStore.Set(m_activeChar, type);

        var env = GBSystem.Instance?.GetActiveEnvScene();
        if (env != null)
        {
            bool isSwim = IsSwimWear(env, m_activeChar);

            if (isSwim)
            {
                // 水着は SwimWearStockingPatch が override store を source of truth として
                // 全タイプ（0=解除 / 1–4=パンスト / 5–7=ニーソックス）を同期処理する。
                // type 引数は SwimWearStockingPatch 側で無視されるので 0 を渡す。
                try
                {
                    env.ApplyStockings(m_activeChar, 0);
                }
                catch (Exception ex)
                {
                    PatchLogger.LogWarning($"[CostumePicker] 水着ストッキング切替失敗: {ex}");
                }
            }
            else if (StockingOverrideStore.IsKneeSocksType(type))
            {
                // ニーソックス系: 直接メッシュ差し替え（env.ApplyStockings は type 0–4 専用）
                var charObj = env.FindCharacter(m_activeChar);
                if (charObj != null)
                {
                    if (wasKneeSocks) KneeSocksLoader.Restore(charObj);
                    KneeSocksLoader.Apply(charObj, type);
                }
            }
            else
            {
                if (wasKneeSocks)
                {
                    // ニーソックスから別タイプへの切替: Apply() の副作用を先に復元
                    var charObj = env.FindCharacter(m_activeChar);
                    if (charObj != null) KneeSocksLoader.Restore(charObj);
                }
                // env.ApplyStockings が mesh_stockings.sharedMesh を上書きする。
                try
                {
                    env.ApplyStockings(m_activeChar, type);
                }
                catch (Exception ex)
                {
                    PatchLogger.LogWarning($"[CostumePicker] ストッキング切替失敗: {ex}");
                }
            }
        }
        m_view.Render(BuildRenderData());
    }

    /// <summary>
    /// 現在シーン内の <paramref name="id"/> の CharacterHandle から Costume を取得し、
    /// SwimWear かどうかを判定する。まだロードされていない／見つからない場合は false。
    /// </summary>
    private static bool IsSwimWear(EnvSceneBase env, CharID id)
    {
        if (env == null || env.m_characters == null) return false;
        var handle = env.m_characters.Find(x => x != null && x.GetCharID() == id);
        return handle?.m_lastLoadArg?.Costume == CostumeType.SwimWear;
    }

    private void ResetAllTabs()
    {
        if (m_activeChar >= CharID.NUM) return;
        CostumeOverrideStore.Clear(m_activeChar);
        // Bottoms / Tops: store クリア + target SMR を素状態に復元（reload 経路では同 costume の場合に
        // setup() Postfix が発火せず復元されないため直接 Restore）。両者は SMR 集合が排他なので順序依存なし。
        BottomsOverrideStore.Clear(m_activeChar);
        TopsOverrideStore.Clear(m_activeChar);
        var env = GBSystem.Instance?.GetActiveEnvScene();
        var charObj = env?.FindCharacter(m_activeChar);
        if (charObj != null)
        {
            BottomsLoader.RestoreFor(charObj);
            TopsLoader.RestoreFor(charObj);
        }
        ReloadCurrentAsync(m_activeChar).Forget();
        PantiesOverrideStore.Clear(m_activeChar);
        RestoreDefaultPanties(m_activeChar);
        // KneeSocks 系 override 中は Restore してから Clear（charObj は上で取得済みを再利用）
        if (StockingOverrideStore.TryGet(m_activeChar, out var stkForReset)
            && StockingOverrideStore.IsKneeSocksType(stkForReset)
            && charObj != null)
        {
            KneeSocksLoader.Restore(charObj);
        }
        StockingOverrideStore.Clear(m_activeChar);
        RestoreDefaultStocking(m_activeChar);
        m_view.Render(BuildRenderData());
    }

    internal void HideIfShown()
    {
        if (m_view == null || !m_view.IsShown) return;
        HideView();
    }

    private void HandleCloseClicked()
    {
        if (!m_view.IsShown) return;
        HideView();
    }

    /// <summary>パネル非表示にする統一経路。Esc / × / hotkey toggle / fallback など全 close 経路から呼ぶ。</summary>
    private void HideView()
    {
        m_view.Hide();
    }

    private static void RestoreDefaultPanties(CharID id)
    {
        var sys = GBSystem.Instance;
        var env = sys?.GetActiveEnvScene();
        var gd = sys?.RefGameData();
        if (env == null || gd == null) return;
        var (t, c) = gd.QueryPantiesType(id);
        try { env.ReloadPanties(id, c, t); }
        catch (Exception ex) { PatchLogger.LogWarning($"[CostumePicker] パンツ既定復元失敗: {ex}"); }
    }

    private static void RestoreDefaultStocking(CharID id)
    {
        var sys = GBSystem.Instance;
        var env = sys?.GetActiveEnvScene();
        var gd = sys?.RefGameData();
        if (env == null || gd == null) return;
        int stk = gd.QueryStockingType(id);
        try { env.ApplyStockings(id, stk); }
        catch (Exception ex) { PatchLogger.LogWarning($"[CostumePicker] ストッキング既定復元失敗: {ex}"); }
    }

    private async UniTaskVoid ApplyCostumeOverrideAsync(CharID id, CostumeType costume)
    {
        if (m_loading) return;
        m_loading = true;
        try
        {
            CostumeOverrideStore.Set(id, costume);
            await ReloadCurrentInternal(id);
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[CostumePicker] 衣装切替失敗: {ex}");
            CostumeOverrideStore.Clear(id);
        }
        finally { m_loading = false; }
    }

    private async UniTaskVoid ReloadCurrentAsync(CharID id)
    {
        if (m_loading) return;
        m_loading = true;
        try { await ReloadCurrentInternal(id); }
        catch (Exception ex) { PatchLogger.LogWarning($"[CostumePicker] リロード失敗: {ex}"); }
        finally { m_loading = false; }
    }

    private async UniTask ReloadCurrentInternal(CharID id)
    {
        var env = GBSystem.Instance?.GetActiveEnvScene();
        if (env == null) return;
        var index = env.FindCharacterIndex(id);
        if (index < 0) return;

        // Costume 差し替え前の Animator 状態を Layer 0/1/2 (Facial/Eye/Motion) で取得
        int motionHash = 0, facialHash = 0, eyeHash = 0;
        float motionTime = 0f;
        var oldChar = env.FindCharacter(id);
        var oldAnim = oldChar != null ? oldChar.GetComponent<Animator>() : null;
        if (oldAnim != null)
        {
            var m = oldAnim.GetCurrentAnimatorStateInfo(2);
            motionHash = m.fullPathHash;
            motionTime = m.normalizedTime;
            facialHash = oldAnim.GetCurrentAnimatorStateInfo(0).fullPathHash;
            eyeHash = oldAnim.GetCurrentAnimatorStateInfo(1).fullPathHash;
        }

        // 裏側で新モデル + 衣装 + アタッチを Preload。この間は旧キャラが見えたまま。
        // LoadCharacter は IsPreloadDone まで待って返る。active 化はまだしない。
        await env.LoadCharacter(index, id, null);

        // ShowCharacter (SetActive=true + SetupMagicaCloth) の前に Animator をシードする。
        // GameObject が非 active でも Animator.Play は state を仕込め、Animator.Update(0f) で
        // bone transform を正解ポーズに更新できる。これにより active 化フレームで T ポーズが
        // 見えず、SetupMagicaCloth も正解ポーズ基準で揺れもの初期化できる。
        // Unity バージョン差分で Update(0f) が disabled Animator 上で警告/例外を投げる
        // 可能性があるため try/catch でガードし、失敗時も ShowCharacter 呼出しを止めない。
        var newChar = env.FindCharacter(id);
        var newAnim = newChar != null ? newChar.GetComponent<Animator>() : null;
        if (newAnim != null && motionHash != 0)
        {
            try
            {
                newAnim.Play(motionHash, 2, motionTime);
                if (facialHash != 0) newAnim.Play(facialHash, 0, 0f);
                if (eyeHash != 0) newAnim.Play(eyeHash, 1, 0f);
                newAnim.Update(0f);
            }
            catch (Exception ex)
            {
                PatchLogger.LogWarning($"[CostumePicker] Animator 先行シード失敗（T ポーズ可能性あり）: {ex.Message}");
            }
        }

        env.ShowCharacter();
    }

    private void HandleSettingsClicked()
    {
        if (!m_view.IsShown) return;
        if (m_activeChar >= CharID.NUM) return;
        ShowSettings();
    }

    private void HandleBackClicked()
    {
        if (!m_view.IsShown) return;
        if (m_mode != PickerMode.Settings) return;
        ShowPicker();
    }

    private void ShowSettings()
    {
        m_mode = PickerMode.Settings;
        m_settingsSelected = -1;
        m_view.ShowSettings(BuildSettingsData());
        m_view.SetSettingsSelection(m_settingsSelected);
    }

    private void ShowPicker()
    {
        m_mode = PickerMode.Picker;
        m_view.ShowPicker(BuildRenderData());
    }

    private void HandleResetAllClicked()
    {
        if (!m_view.IsShown || m_mode != PickerMode.Settings) return;
        if (m_dialogPending || m_loading) return;
        if (m_activeChar >= CharID.NUM) return;
        ConfirmAndExecuteReset(m_activeChar).Forget();
    }

    private void HandleUnlockAllClicked()
    {
        if (!m_view.IsShown || m_mode != PickerMode.Settings) return;
        if (m_dialogPending || m_loading) return;
        if (m_activeChar >= CharID.NUM) return;
        if (!IsEndingClearedFor(m_activeChar))
        {
            // UI はグレーアウトしているが、キー操作経路でも弾く
            PatchLogger.LogInfo($"[CostumePicker] すべて解放: {m_activeChar} はエンディング未クリアのためスキップ");
            return;
        }
        ConfirmAndExecuteUnlockAll(m_activeChar).Forget();
    }

    private async UniTaskVoid ConfirmAndExecuteReset(CharID id)
    {
        m_dialogPending = true;
        try
        {
            bool ok = await ConfirmDialogHelper.ShowYesNoAsync(
                "解放状態を初期化しますか？\n（上書き中の衣装も既定に戻ります）",
                this.GetCancellationTokenOnDestroy());
            if (!ok) return;
            await ExecuteReset(id);
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[CostumePicker] ExecuteReset 失敗: {ex}");
        }
        finally { m_dialogPending = false; }
    }

    private async UniTaskVoid ConfirmAndExecuteUnlockAll(CharID id)
    {
        m_dialogPending = true;
        try
        {
            bool ok = await ConfirmDialogHelper.ShowYesNoAsync(
                "このキャラの全衣装・パンツ・ストッキングを解放しますか？",
                this.GetCancellationTokenOnDestroy());
            if (!ok) return;
            ExecuteUnlockAll(id);
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[CostumePicker] ExecuteUnlockAll 失敗: {ex}");
        }
        finally { m_dialogPending = false; }
    }

    private async UniTask ExecuteReset(CharID id)
    {
        if (m_loading) return;
        m_loading = true;
        try
        {
            CostumeOverrideStore.Clear(id);
            PantiesOverrideStore.Clear(id);
            StockingOverrideStore.Clear(id);
            BottomsOverrideStore.Clear(id);
            TopsOverrideStore.Clear(id);
            // Bottoms / Tops: reload 前に Restore（reload 経路は同 costume だと no-op になり SMR が戻らないため）
            var envB = GBSystem.Instance?.GetActiveEnvScene();
            var charObjB = envB?.FindCharacter(id);
            if (charObjB != null) BottomsLoader.RestoreFor(charObjB);
            if (charObjB != null) TopsLoader.RestoreFor(charObjB);
            await ReloadCurrentInternal(id);
            RestoreDefaultPanties(id);
            RestoreDefaultStocking(id);

            CostumeViewHistory.ClearAll(id);
            PantiesViewHistory.ClearAll(id);
            StockingViewHistory.ClearAll(id);

            RebuildItemsFor(id);
            // await 中にユーザが × でパネルを閉じた場合、勝手に再表示しない
            if (m_view != null && m_view.IsShown) ShowPicker();
            PatchLogger.LogInfo($"[CostumePicker] 初期化完了: {id}");
        }
        finally { m_loading = false; }
    }

    private void ExecuteUnlockAll(CharID id)
    {
        // m_costumeItems 等は ShowSettings 時点の RebuildItemsFor でキャスト分ビルド済み。
        // DLC 未導入衣装はそこで既にフィルタされているため、そのまま bulk API に渡せる。
        CostumeViewHistory.MarkViewedBulk(id, m_costumeItems.Select(x => x.Costume));
        PantiesViewHistory.MarkViewedBulk(id, m_pantiesItems.Select(x => (x.Type, x.Color)));
        StockingViewHistory.MarkViewedBulk(id, m_stockingItems.Select(x => x.Type));
        RebuildItemsFor(id);   // 解放反映のため再構築
        if (m_view != null && m_view.IsShown) ShowPicker();
        PatchLogger.LogInfo($"[CostumePicker] すべて解放完了: {id} 衣装{m_costumeItems.Count}/パンツ{m_pantiesItems.Count}/ストッキング{m_stockingItems.Count}");
    }

    private void UpdateSettingsMode(Keyboard kb)
    {
        // W/S/↑/↓: [-1=未選択, 0=初期化, 1=すべて解放] の 3 位置をクランプ移動。
        // W で -1 まで戻せるため、ハイライトを消して「何も選んでいない」状態にできる。
        if (kb[Key.W].wasPressedThisFrame || kb[Key.UpArrow].wasPressedThisFrame)
        {
            if (m_settingsSelected > -1)
            {
                m_settingsSelected--;
                m_view.SetSettingsSelection(m_settingsSelected);
            }
            return;
        }
        if (kb[Key.S].wasPressedThisFrame || kb[Key.DownArrow].wasPressedThisFrame)
        {
            if (m_settingsSelected < 1)
            {
                int next = m_settingsSelected + 1;
                // index 1 (すべて解放) は GoodEnd 未クリア時は無効表示なのでハイライトさせない
                if (next == 1 && !IsEndingClearedFor(m_activeChar)) return;
                m_settingsSelected = next;
                m_view.SetSettingsSelection(m_settingsSelected);
            }
            return;
        }

        // Enter: 選択中ボタン実行（未選択時は何もしない）
        if (kb[Key.Enter].wasPressedThisFrame || kb[Key.NumpadEnter].wasPressedThisFrame)
        {
            if (m_settingsSelected == 0) HandleResetAllClicked();
            else if (m_settingsSelected == 1) HandleUnlockAllClicked();
            return;
        }

        if (kb[Key.Escape].wasPressedThisFrame)
        {
            ShowPicker();
            return;
        }
    }

    private CostumePickerView.SettingsData BuildSettingsData()
    {
        return new CostumePickerView.SettingsData
        {
            CharId = m_activeChar,
            UnlockAllEnabled = IsEndingClearedFor(m_activeChar),
            VisibleCasts = m_visibleCasts.AsReadOnly(),
            VisibleCastSelectedIndex = m_visibleCasts.IndexOf(m_activeChar),
        };
    }

    /// <summary>
    /// 微調整スライダー値変更後のライブ再適用。
    /// 水着で stocking override が掛かっているキャラに対し、SwimWearStockingPatch の
    /// 食い込み解消を新パラメータで再構築する。非水着・override 無し・KneeSocks の場合は no-op。
    /// </summary>
    private void ReapplyStockingForTune()
    {
        if (m_activeChar >= CharID.NUM) return;
        if (!StockingOverrideStore.TryGet(m_activeChar, out var stk)) return;
        if (StockingOverrideStore.IsKneeSocksType(stk)) return;
        var env = GBSystem.Instance?.GetActiveEnvScene();
        if (env == null) return;
        if (!IsSwimWear(env, m_activeChar)) return;

        SwimWearStockingPatch.InvalidateForReapply(m_activeChar);
        try
        {
            env.ApplyStockings(m_activeChar, 0); // SwimWearStockingPatch は override store を見るので type 引数は無視
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[CostumePicker] tune 再適用失敗: {ex}");
        }
    }

    /// <summary>
    /// FittingRoom と同じ条件でキャラの GoodEnd クリア状況を判定する。
    /// （Assembly-CSharp/GB.Extra/Album.cs の enterFittingRoom と同じマッピング）
    /// </summary>
    private static bool IsEndingClearedFor(CharID id)
    {
        int routeIndex = id switch
        {
            CharID.KANA => 0,
            CharID.RIN => 1,
            CharID.MIUKA => 2,
            CharID.ERISA => 3,
            CharID.KUON => 4,
            CharID.LUNA => 5,
            _ => -1,
        };
        if (routeIndex < 0) return false;
        var sd = GBSystem.Instance?.RefSaveData();
        if (sd == null) return false;
        var routes = sd.GetClearRoute();
        if (routes == null || routeIndex >= routes.Length) return false;
        return routes[routeIndex];
    }
}
