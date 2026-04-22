using System;
using System.Collections.Generic;
using System.Linq;
using BunnyGarden2FixMod.Utils;
using Cysharp.Threading.Tasks;
using GB;
using GB.DLC;
using GB.Game;
using GB.Scene;
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

    // タブ状態
    private CostumePickerView.WardrobeTab m_activeTab = CostumePickerView.WardrobeTab.Costume;
    private int m_costumeSelected = -1;
    private int m_pantiesSelected = -1;
    private int m_stockingSelected = -1;

    // 各タブの選択肢（Locked=true は未開放。モック準拠で "???" 表示、選択・適用不可）
    private List<(CostumeType Costume, bool Locked)> m_costumeItems = new();
    private List<(int Type, int Color, bool Locked)> m_pantiesItems = new();
    private List<(int Type, bool Locked)> m_stockingItems = new();

    public static CostumePickerController Instance { get; private set; }

    /// <summary>View が表示中かを外部から参照する。</summary>
    public bool IsPickerShown => m_view != null && m_view.IsShown;

    /// <summary>現在のマウス座標が Wardrobe パネル矩形内かを外部（クリック抑制パッチ）から参照する。</summary>
    public bool IsCursorOverPicker => m_view != null && m_view.IsPointerOverPanel();

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
        m_view.OnCloseClicked += HandleCloseClicked;
    }

    private void OnDestroy()
    {
        if (m_view != null)
        {
            m_view.OnTabClicked -= HandleTabClicked;
            m_view.OnRowClicked -= HandleRowClicked;
            m_view.OnCloseClicked -= HandleCloseClicked;
        }
        if (Instance == this) Instance = null;
    }

    private void HandleTabClicked(int index)
    {
        if (!m_view.IsShown) return;
        if (index < 0 || index > 2) return;
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
        }
        // 行クリックは即トグル適用（cur==selected なら解除、違えば apply）。
        // キー操作 (W/S/↑/↓) は選択のみで、Enter で確定適用。全タブ共通挙動。
        DecideActiveTab();
    }

    private void Update()
    {
        if (Plugin.ConfigCostumeChangerEnabled == null) return;
        if (!Plugin.ConfigCostumeChangerEnabled.Value) return;
        // Awake で AddComponent<CostumePickerView>() が何らかの理由（例外）で失敗したケース防御。
        // m_view.IsShown などを触る前段で早期 return する。
        if (m_view == null) return;

        var kb = Keyboard.current;
        if (kb == null) return;

        // トグル
        if (kb[Plugin.ConfigCostumeChangerHotkey.Value].wasPressedThisFrame)
        {
            if (m_view.IsShown)
            {
                m_view.Hide();
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
            m_view.Hide();
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

        // FittingRoom 動作中はピッカーを開かない
        if (CostumeChangerPatch.IsFittingRoomActiveExternal()) return false;

        var gameData = sys.RefGameData();
        if (gameData == null) return false;
        var id = gameData.GetCurrentCast();
        if (id >= CharID.NUM) return false;
        if (env.FindCharacterIndex(id) < 0) return false;

        charId = id;
        return true;
    }

    private void OpenFor(CharID charId)
    {
        m_activeChar = charId;

        // Costume items — 全 CostumeType を列挙。DLC 未インストールは除外、viewed でないものは Locked
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

        // Panties items — 全 (type, color) 組を列挙、viewed でないものは Locked
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

        // Stocking items — 0..Max 全列挙、viewed でないものは Locked
        m_stockingItems = new List<(int, bool)>();
        for (int i = 0; i <= StockingOverrideStore.Max; i++)
        {
            bool locked = !StockingViewHistory.IsViewed(charId, i);
            m_stockingItems.Add((i, locked));
        }

        // Initial selection: override match → なければ最初の unlocked
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

        m_activeTab = CostumePickerView.WardrobeTab.Costume;
        m_view.Show(BuildRenderData());
        int cUnlock = m_costumeItems.Count(x => !x.Locked);
        int pUnlock = m_pantiesItems.Count(x => !x.Locked);
        int sUnlock = m_stockingItems.Count(x => !x.Locked);
        PatchLogger.LogInfo($"[CostumePicker] オープン: {charId} / 衣装{cUnlock}/{m_costumeItems.Count} / パンツ{pUnlock}/{m_pantiesItems.Count} / ストッキング{sUnlock}/{m_stockingItems.Count}");
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
            CostumeLocks = m_costumeItems.Select(x => x.Locked).ToList(),
            PantiesLocks = m_pantiesItems.Select(x => x.Locked).ToList(),
            StockingLocks = m_stockingItems.Select(x => x.Locked).ToList(),
            CostumeSelected = m_costumeSelected,
            PantiesSelected = m_pantiesSelected,
            StockingSelected = m_stockingSelected,
            CostumeCurrent = CostumeOverrideStore.TryGet(m_activeChar, out var oc)
                ? m_costumeItems.FindIndex(x => x.Costume == oc) : -1,
            PantiesCurrent = PantiesOverrideStore.TryGet(m_activeChar, out var opT, out var opC)
                ? m_pantiesItems.FindIndex(x => x.Type == opT && x.Color == opC) : -1,
            StockingCurrent = StockingOverrideStore.TryGet(m_activeChar, out var os)
                ? m_stockingItems.FindIndex(x => x.Type == os) : -1,
        };
    }

    private static char TypeLetter(int type) => (char)('A' + type);

    /// <summary>
    /// 衣装名をゲーム本体のローカライズされた表示名で解決する。
    /// FittingRoom と同じ MSGID_SPLIT_2.FITTING_ROOM_COSTUME_* を参照する。
    /// DLC 衣装は MSGID が存在しないので enum 名にフォールバック
    /// （DLC 名は Text/dlc_costume/*.txt に非同期ロードで入っているが、同期取得不可のため）。
    /// </summary>
    private static string ResolveCostumeName(CostumeType costume)
    {
        var msg = GBSystem.Instance?.RefMessage();
        if (msg == null) return costume.ToString();
        var mid = CostumeToMsgId(costume);
        if (mid == null) return costume.ToString();
        try { return msg.RefText(mid); }
        catch { return costume.ToString(); }
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
        _ => $"#{type}",
    };

    // DLC インストール HashSet は CostumeChangerPatch 側の lazy キャッシュを共有する。
    private static HashSet<CostumeType> GetInstalledDlcCostumes()
        => CostumeChangerPatch.GetDLCInstalledSet() ?? new HashSet<CostumeType>();

    private void ChangeTab(int delta)
    {
        int next = ((int)m_activeTab + delta + 3) % 3;
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
                    RestoreDefaultStocking(m_activeChar);
                    m_view.Render(BuildRenderData());
                    return;
                }
                ApplyStocking();
                break;
        }
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
        StockingOverrideStore.Set(m_activeChar, type);

        var env = GBSystem.Instance?.GetActiveEnvScene();
        if (env != null)
        {
            try
            {
                env.ApplyStockings(m_activeChar, type);
            }
            catch (Exception ex)
            {
                PatchLogger.LogWarning($"[CostumePicker] ストッキング切替失敗: {ex}");
            }
        }
        m_view.Render(BuildRenderData());
    }

    private void ResetAllTabs()
    {
        if (m_activeChar >= CharID.NUM) return;
        CostumeOverrideStore.Clear(m_activeChar);
        ReloadCurrentAsync(m_activeChar).Forget();
        PantiesOverrideStore.Clear(m_activeChar);
        RestoreDefaultPanties(m_activeChar);
        StockingOverrideStore.Clear(m_activeChar);
        RestoreDefaultStocking(m_activeChar);
        m_view.Render(BuildRenderData());
    }

    private void HandleCloseClicked()
    {
        if (!m_view.IsShown) return;
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
        // try/catch 防御: Unity バージョン差分で Update(0f) が disabled Animator 上で
        // 警告/例外を投げる可能性があるため、失敗時も ShowCharacter 呼出しを止めない。
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
}
