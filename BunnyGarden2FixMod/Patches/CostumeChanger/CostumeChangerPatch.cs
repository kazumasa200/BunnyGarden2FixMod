using System.Linq;
using BunnyGarden2FixMod.Utils;
using GB;
using GB.Scene;
using GB.DLC;
using GB.Extra;
using GB.Game;
using HarmonyLib;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// <see cref="CharacterHandle.Preload"/> に Prefix/Postfix を掛け、
/// Prefix で Costume/Panties/Stocking の override を LoadArg へ注入、
/// Postfix で最終決定された各値を ViewHistory に記録する。
/// 設計書 § 「CostumeChangerPatch」参照。
/// </summary>
[HarmonyPatch(typeof(CharacterHandle), nameof(CharacterHandle.Preload))]
public static class CostumeChangerPatch
{
    // FittingRoom 検出キャッシュ。FindObjectOfType を毎 Preload で走らせないための最適化。
    // Unity の == null 演算子は破棄済みオブジェクトを null として扱うので、ここでの null チェックは安全。
    private static FittingRoom s_fittingRoomCache;

    // 本体 CostumeOverride 尊重でスキップした際のログ dedup（スパム防止）。id 粒度で 1 回だけ出す。
    private static CharID s_lastRespectSkipId = CharID.NUM;

    /// <summary>
    /// Wardrobe ピッカー (<see cref="UI.CostumePickerController"/>) をホストする
    /// DontDestroyOnLoad な永続 GameObject を生成する。
    /// 他の Patches 同様 <c>Plugin.Awake</c> から呼ぶ。
    /// <c>ConfigCostumeChangerEnabled</c> が false の場合は何もしない。
    /// </summary>
    public static void Initialize(GameObject parent)
    {
        if (!Plugin.ConfigCostumeChangerEnabled.Value) return;
        var pickerHost = new GameObject("BG2CostumePicker");
        Object.DontDestroyOnLoad(pickerHost);
        pickerHost.AddComponent<UI.CostumePickerController>();
        PatchLogger.LogInfo("[CostumeChangerPatch] CostumePickerController を生成しました");
    }

    static bool Prepare()
    {
        bool enabled = Plugin.ConfigCostumeChangerEnabled?.Value ?? true;
        PatchLogger.LogInfo($"[CostumeChangerPatch] 適用 (enabled={enabled})");
        return enabled;
    }

    // 注意: HarmonyX は引数名一致 or __0/__1 の序数束縛で bind する。逆コンパイル由来の
    // シグネチャで引数名が揺れる可能性を避けるため __0 / __1 の序数束縛を使う。
    // Preload の引数順序は (CharID id, LoadArg arg) であることを Task 3 Step 2 で grep 確認する。
    // arg は参照型なので、arg.Costume の書換はそのまま呼出元の LoadArg に反映される。
    static void Prefix(CharID __0, CharacterHandle.LoadArg __1)
    {
        var id = __0;
        var arg = __1;
        if (arg == null) return;
        if (id >= CharID.NUM) return;

        // FittingRoom が動作中は本体側の選択を尊重（競合回避）
        if (IsFittingRoomActive()) return;

        // RespectGameCostumeOverride: 本体が ForceXxx を設定中なら MOD override を一時停止
        if ((Plugin.ConfigRespectGameCostumeOverride?.Value ?? true)
            && GBSystem.Instance != null
            && GBSystem.Instance.GetCostumeOverride() != GBSystem.CostumeOverride.None)
        {
            if (s_lastRespectSkipId != id)
            {
                PatchLogger.LogInfo($"[CostumeChangerPatch] 本体 CostumeOverride 尊重でスキップ: {id} / {GBSystem.Instance.GetCostumeOverride()}");
                s_lastRespectSkipId = id;
            }
            return;
        }
        // スキップ抜け時は dedup をリセットして次回尊重スキップ時に再度ログを出す
        s_lastRespectSkipId = CharID.NUM;

        // Costume override
        if (CostumeOverrideStore.TryGet(id, out var overrideCostume))
        {
            // DLC 未所持なら override を破棄（整合性防御）
            if (overrideCostume.IsDLC() && !IsDLCInstalled(overrideCostume))
            {
                PatchLogger.LogWarning($"[CostumeChangerPatch] DLC 未所持で override 破棄: {id} / {overrideCostume}");
                CostumeOverrideStore.Clear(id);
            }
            else
            {
                arg.Costume = overrideCostume;
            }
        }

        // Panties override
        if (PantiesOverrideStore.TryGet(id, out var pType, out var pColor))
        {
            arg.PantiesType = pType;
            arg.PantiesColor = pColor;
        }

        // Stocking override
        if (StockingOverrideStore.TryGet(id, out var stocking))
        {
            arg.Stocking = stocking;
        }
    }

    // 注意: CharacterHandle.Preload は void で、内部に非同期ロードを開始する設計。
    // Postfix は非同期完了を待たずに「ロード要求が確定した直後」で走る。
    // 仕様の「シーン内で実際にキャラクターモデルがロードされた瞬間」を厳密に待つ場合は
    // LoadCharacter 側の await IsPreloadDone 後に記録する必要があるが、Preload が
    // キャンセル・例外で完了失敗するケースは稀なので、本 MOD では要求確定時点を
    // 「表示した」とみなす（シンプルさ優先）。
    static void Postfix(CharID __0, CharacterHandle.LoadArg __1)
    {
        if (__1 == null) return;
        var id = __0;
        var arg = __1;
        // 履歴対象か否かに関わらず、キャラ毎の「最後に Preload で適用された見た目」を記憶する。
        // 後で SetCurrentCast Postfix が新 current キャラの見た目を履歴へフラッシュするのに使う。
        WardrobeLastLoadArg.Set(id, arg.Costume, arg.PantiesType, arg.PantiesColor, arg.Stocking);
        // current キャラ以外は記録しない（Bar シーン等で横並びのキャラを Preload した
        // タイミングで履歴が勝手に埋まるのを防ぐ）。
        if (!WardrobeHistoryGate.ShouldRecord(id)) return;
        CostumeViewHistory.MarkViewed(id, arg.Costume);
        PantiesViewHistory.MarkViewed(id, arg.PantiesType, arg.PantiesColor);
        StockingViewHistory.MarkViewed(id, arg.Stocking);
    }

    /// <summary>FittingRoom が動作中かを外部から参照するための公開ヘルパ。</summary>
    internal static bool IsFittingRoomActiveExternal() => IsFittingRoomActive();

    private static bool IsFittingRoomActive()
    {
        if (s_fittingRoomCache == null)
        {
            s_fittingRoomCache = Object.FindObjectOfType<FittingRoom>();
        }
        // Unity の == null はシーン遷移で破棄された参照も null と判定する
        if (s_fittingRoomCache == null) return false;
        return s_fittingRoomCache.gameObject.activeInHierarchy;
    }

    private static bool IsDLCInstalled(CostumeType costume)
    {
        var sys = GBSystem.Instance;
        if (sys == null) return false;
        var installed = sys.QueryHasDLCCostume();
        if (installed == null) return false;
        return installed.Any(d => d.ToCostumeType() == costume);
    }
}
