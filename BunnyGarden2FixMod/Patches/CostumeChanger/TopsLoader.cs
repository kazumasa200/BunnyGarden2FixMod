using BunnyGarden2FixMod.Patches.CostumeChanger.Internal;
using BunnyGarden2FixMod.Utils;
using Cysharp.Threading.Tasks;
using GB;
using GB.Game;
using GB.Scene;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// 別キャラ・別コスチュームの上衣メッシュ群（mesh_costume, mesh_costume_*（skirt/pants 系除く））を
/// ターゲットキャラへ移植する。
///
/// 設計方針 (tops-transplant-c2-full §4):
///   - Wardrobe (F7) の Tops タブで donor (CharID + CostumeType) が選択されると
///     <see cref="PreloadDonorAsync"/> が呼ばれ、未ロードならその場で preload する。
///   - <see cref="CharacterHandle.setup"/> Postfix（<see cref="TopsSetupPatch"/>）から
///     <see cref="ApplyIfOverridden"/> 経由で <see cref="Apply"/> し、target の Tops 候補を
///     donor の同名 SMR で sharedMesh + bones (リマップ) + materials swap する。
///   - donor のみ持つ SMR は SwimWearStockingPatch.CreateInjected 同手法で動的注入。
///     target のみ持つ SMR は SetActive(false) で hide。
///   - donor 自身の setup() Postfix は handle.Chara が <see cref="s_loaderHostRoot"/> 配下なら ApplyIfOverridden 側でガード。
///
/// 制約 (受容):
///   - 物理ボーン (chest_swaying_lp 等) は移植しない。target 側に同名ボーンが無ければ
///     rootBone へ fallback（KneeSocksLoader / BottomsLoader と同手順）。
///   - 注入された新 SMR の rootBone は reference SMR (mesh_skin_upper) の rootBone を流用。
///     既存 SMR の swap 時は target の元 rootBone を変更しない。
///   - target がフルボディ衣装 (Bunnygirl / フルボディ DLC) 状態では Apply スキップ
///     （フルボディスーツで構造差大）。
///     SwimWear は除外（donor=SwimWear のとき ApplySwimWearBottomsPhase で full-body swap が成立。
///     Bottoms 領域も Tops 経由で扱うため SwimWearStockingPatch と競合しない）。
///   - mesh_costume_skirt* / mesh_costume_pants* は Bottoms 領域として除外（IsTopsCandidate）。
///   - 同名 SMR 重複は最初の 1 つだけ採用し警告ログ。2 つ目以降は swap / hide / inject いずれの
///     対象にもならず素のまま残る (Phase 0 ログで重複ゼロを確認済みの前提)。
///     検出は Apply の verbose ログ + 警告ログから追跡可能。
///
/// GC ガード:
///   - donor preload 用 GameObject は <see cref="Initialize"/> で渡される pickerHost
///     （CostumeChangerPatch で DontDestroyOnLoad 済み）の配下に <c>SetActive(false)</c> で配置。
///   - <see cref="DonorEntry.Handle"/> を <see cref="s_cache"/> 内辞書で参照保持し
///     CharacterHandle インスタンスが GC されないようにする。
/// </summary>
public class TopsLoader : MonoBehaviour
{
    private struct DonorEntry
    {
        public CharacterHandle Handle;                      // GC 防止のため辞書から参照保持
        public List<SkinnedMeshRenderer> AllSmrs;           // verbose ログ用全 SMR スナップショット
        public List<SkinnedMeshRenderer> TopsSmrs;          // Tops 候補 (IsTopsCandidate フィルタ後)
    }

    /// <summary>
    /// mesh_skin_upper の skin donor に固定で使う costume。
    /// Babydoll は他衣装より露出が多く blendShape も汎用的に効くため、Tops swap 後の境界整合がもっとも安定する。
    /// 将来 user-configurable 化する場合は <c>static readonly</c> field に切り替える（const は assembly 境界で
    /// 値型インライン化されるため）。本 MOD は単一 dll 配布なので現時点では const で問題ない。
    /// </summary>
    public const CostumeType SkinDonorCostume = CostumeType.Babydoll;

    private static readonly DonorPreloadCache<DonorEntry> s_cache = new(
        "[TopsLoader]",
        BuildDonorEntry,
        e => e.TopsSmrs != null && e.TopsSmrs.Count > 0);
    private static readonly HashSet<int> s_applied = new();
    private static GameObject s_loaderHostRoot;

    // per-vert distance preservation (MeshDistancePreserver) の出力キャッシュ。
    // キー: (donor Tops mesh の InstanceID, donor skin_upper id, donor skin_lower id, target skin_upper id, target skin_lower id)
    // 値: 補正済み donor mesh（push 不要なら null）
    // s_resolvedAppliedIds は補正済み mesh の二重補正防止用。
    // skin が無いケースは ID=0 を入れる。
    private static readonly Dictionary<(int donorMeshId, int dSkinUpId, int dSkinLoId, int tSkinUpId, int tSkinLoId), Mesh> s_resolvedCache = new();
    private static readonly HashSet<int> s_resolvedAppliedIds = new();

    /// <summary>Initialize 完了 (= s_loaderHostRoot 生成済み)。Apply 側の警告分岐に使用。</summary>
    public static bool IsLoaded => s_loaderHostRoot != null;

    public static void Initialize(GameObject parent)
    {
        if (s_loaderHostRoot != null)
        {
            PatchLogger.LogWarning("[TopsLoader] 既に Initialize 済みです");
            return;
        }
        var loader = parent.AddComponent<TopsLoader>();
        s_loaderHostRoot = new GameObject("BunnyGarden2FixMod_TopsLoaderHost");
        s_loaderHostRoot.transform.SetParent(loader.transform, false);
        s_loaderHostRoot.SetActive(false);
        s_cache.SetHostRoot(s_loaderHostRoot);
        DonorPreloadRegistry.Register(s_cache.IsHostParent);
        SceneManager.sceneUnloaded += OnSceneUnloaded;
        // 距離保存パラメータの live tuning。picker UI 状態に依存しないよう Loader 側で配線する。
        if (Configs.TopsDistancePreserveRange != null)
            Configs.TopsDistancePreserveRange.SettingChanged += OnDistancePreserveParamChanged;
        if (Configs.TopsSkinMinOffset != null)
            Configs.TopsSkinMinOffset.SettingChanged += OnDistancePreserveParamChanged;
        if (Configs.TopsSkinSampleRadius != null)
            Configs.TopsSkinSampleRadius.SettingChanged += OnDistancePreserveParamChanged;
        if (Configs.TopsSkinWeightFalloff != null)
            Configs.TopsSkinWeightFalloff.SettingChanged += OnDistancePreserveParamChanged;
        // SkinShrink 系も同 handler に乗せる: target skin 側の補正だが、再 Apply 経路は共通で問題ない。
        if (Configs.TopsSkinShrink != null)
            Configs.TopsSkinShrink.SettingChanged += OnDistancePreserveParamChanged;
        if (Configs.TopsSkinShrinkFalloffRadius != null)
            Configs.TopsSkinShrinkFalloffRadius.SettingChanged += OnDistancePreserveParamChanged;
        if (Configs.TopsSkinShrinkSampleRadius != null)
            Configs.TopsSkinShrinkSampleRadius.SettingChanged += OnDistancePreserveParamChanged;
        PatchLogger.LogInfo("[TopsLoader] Initialized");
    }

    private void OnDestroy()
    {
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
        if (Configs.TopsDistancePreserveRange != null)
            Configs.TopsDistancePreserveRange.SettingChanged -= OnDistancePreserveParamChanged;
        if (Configs.TopsSkinMinOffset != null)
            Configs.TopsSkinMinOffset.SettingChanged -= OnDistancePreserveParamChanged;
        if (Configs.TopsSkinSampleRadius != null)
            Configs.TopsSkinSampleRadius.SettingChanged -= OnDistancePreserveParamChanged;
        if (Configs.TopsSkinWeightFalloff != null)
            Configs.TopsSkinWeightFalloff.SettingChanged -= OnDistancePreserveParamChanged;
        if (Configs.TopsSkinShrink != null)
            Configs.TopsSkinShrink.SettingChanged -= OnDistancePreserveParamChanged;
        if (Configs.TopsSkinShrinkFalloffRadius != null)
            Configs.TopsSkinShrinkFalloffRadius.SettingChanged -= OnDistancePreserveParamChanged;
        if (Configs.TopsSkinShrinkSampleRadius != null)
            Configs.TopsSkinShrinkSampleRadius.SettingChanged -= OnDistancePreserveParamChanged;
    }

    /// <summary>
    /// 距離保存系 Configs (<see cref="Configs.TopsDistancePreserveRange"/> / <see cref="Configs.TopsSkinMinOffset"/>)
    /// 変更時にキャッシュを破棄して登録済み全 target を再適用する。
    /// picker (F7) が閉じている状態でも F9 設定パネル等から値変更があれば即時反映される。
    /// </summary>
    private static void OnDistancePreserveParamChanged(object sender, System.EventArgs e)
    {
        InvalidateDistancePreserveCache();
        // SkinShrink 系 Configs (TopsSkinShrink / Falloff / SampleR) も同 handler に subscribe している。
        // SkinShrinkCoordinator の cache を破棄して、後続 ApplyDirectly → Apply → RegisterTops で再 push される。
        // Bottoms-only の target (この loop で touch しない) を孤児化させないため、loop 後に
        // RefreshAllByConfig で全 entry を refresh する。
        SkinShrinkCoordinator.InvalidateCache();

        // 同伴イベント等 (env != HoleScene) で live tune が走った場合、env.FindCharacter は env 側の char しか返さず、
        // HoleScene preserved の Bar char は再 Apply 対象から外れる。InvalidateDistancePreserveCache /
        // InvalidateCache で destroyed Mesh となった sharedMesh (mesh_costume / mesh_skin_*) を保持したまま
        // Bar 復帰し SMR が消失する症状を防ぐため、env と HoleScene の両方で FindCharacter する。
        var sys = GBSystem.Instance;
        if (sys == null) return;
        var env = sys.GetActiveEnvScene();
        var holeScene = sys.GetHoleScene();
        if (env == null && holeScene == null) return;

        // 列挙中の操作で例外が出ないよう ToList でスナップショット
        var snapshot = TopsOverrideStore.EnumerateOverrides().ToList();
        if (snapshot.Count == 0) return;

        int reapplied = 0;
        // env と HoleScene の FindCharacter が同一 GameObject を返す場合 (Bar 滞在中の通常経路) に
        // 二重 Apply を防ぐ。InstanceID が別なら両者をそのまま Apply する (companion event の Talk2DScene char と
        // HoleScene preserved char は別 GameObject)。
        var seen = new HashSet<int>();
        foreach (var kv in snapshot)
        {
            var target = kv.Key;
            var entry = kv.Value;
            TryReapply(env?.FindCharacter(target), target, entry, seen, ref reapplied);
            if (!ReferenceEquals(env, holeScene))
                TryReapply(holeScene?.FindCharacter(target), target, entry, seen, ref reapplied);
        }
        // Tops が register されていない (Bottoms 単独) target の cache を InvalidateCache で消したまま
        // にしないため、全 entry を refresh して整合を取る。Tops 側 ApplyDirectly で touch 済 entry も
        // 重ねて refresh されるが cache hit で軽量。
        SkinShrinkCoordinator.RefreshAllByConfig();
        PatchLogger.LogDebug($"[TopsLoader] distance preserve param 変更 → {reapplied} 個 再適用 (登録 {snapshot.Count}, range={Configs.TopsDistancePreserveRange.Value:F4}m, minOffset={Configs.TopsSkinMinOffset.Value:F4}m, skinSampleR={Configs.TopsSkinSampleRadius.Value:F4}m, weightFalloff={Configs.TopsSkinWeightFalloff.Value:F4}m)");
    }

    private static void TryReapply(GameObject charObj, CharID target, TopsOverrideStore.Entry entry,
        HashSet<int> seen, ref int reapplied)
    {
        if (charObj == null) return;
        if (!seen.Add(charObj.GetInstanceID())) return;
        try
        {
            // ApplyDirectly が内部で RestoreFor を呼ぶため事前 Restore は不要 (二重実行防止)。
            // RestoreFor は GetComponentsInChildren + snapshot 全 key 線形列挙を伴うため、target 数 N で
            // 2 倍コストになる。1 回に集約することで live tune 経路 (param 変更時に N 回ループ) のコストを半減。
            ApplyDirectly(charObj, entry.DonorChar, entry.DonorCostume);
            reapplied++;
        }
        catch (System.Exception ex)
        {
            PatchLogger.LogWarning($"[TopsLoader] live tune 再適用失敗: target={target}, donor={entry.DonorChar}/{entry.DonorCostume}: {ex}");
        }
    }

    /// <summary>
    /// Tops 候補判定。ホワイトリスト: <c>mesh_costume</c> または <c>mesh_costume_*</c>。
    /// ブラックリスト: 名前に <c>skirt</c> / <c>pants</c> / <c>frill</c> を含むものは Bottoms 領域
    /// （<see cref="BottomsLoader.IsBottomsCandidate"/> と相互排他）。
    /// 例: mesh_costume / mesh_costume_ribbon / mesh_costume_sleeve → Tops。
    ///     mesh_costume_skirt / mesh_costume_skirt_trp / mesh_costume_skirt_sensitivemode /
    ///     mesh_costume_skirtfrill / mesh_costume_frill / mesh_costume_pants → Bottoms。
    /// skin / face / eye / foot / shoes / socks / stockings 系は <c>mesh_costume_</c> で始まらないので自動除外。
    /// </summary>
    public static bool IsTopsCandidate(SkinnedMeshRenderer smr)
    {
        if (smr == null) return false;
        var n = smr.name;
        if (string.IsNullOrEmpty(n)) return false;
        if (n != "mesh_costume" && !n.StartsWith("mesh_costume_", StringComparison.Ordinal)) return false;
        // skirt/pants/frill を名前のどこかに含む派生は Bottoms 領域として一括除外。
        // mesh_costume_frill (RIN Babydoll の下半身フリル) も Bottoms 扱い。
        if (n.IndexOf("skirt", StringComparison.Ordinal) >= 0) return false;
        if (n.IndexOf("pants", StringComparison.Ordinal) >= 0) return false;
        if (n.IndexOf("frill", StringComparison.Ordinal) >= 0) return false;
        return true;
    }

    /// <summary>
    /// 指定 donor (CharID + Costume) を必要なら preload してキャッシュする。
    /// 既にキャッシュ済みなら即時 true を返す。in-flight の場合は同じ task を共有する。
    /// 戻り値は「donor が Tops 候補 SMR を 1 つ以上持つか」。false なら呼出側で apply 中止する想定。
    /// </summary>
    public static UniTask<bool> PreloadDonorAsync(CharID donor, CostumeType costume) =>
        s_cache.PreloadAsync(donor, costume);

    private static DonorEntry BuildDonorEntry(
        CharID donor, CostumeType costume, GameObject donorParent,
        CharacterHandle handle, List<SkinnedMeshRenderer> allSmrs)
    {
        var topsSmrs = allSmrs.Where(IsTopsCandidate).ToList();
        var entry = new DonorEntry { Handle = handle, AllSmrs = allSmrs, TopsSmrs = topsSmrs };

        PatchLogger.LogDebug($"[TopsLoader] lazy donor preloaded: {donor}/{costume} (allSMR={allSmrs.Count}, topsCandidates={topsSmrs.Count})");
        if (PatchLogger.IsDebugEnabled)
        {
            PatchLogger.LogDebug($"[TopsLoader] donor={donor}/{costume} SMRs: {string.Join(", ", allSmrs.Select(s => s.name))}");
            PatchLogger.LogDebug($"[TopsLoader] donor={donor}/{costume} tops candidates: {(topsSmrs.Count == 0 ? "(none)" : string.Join(", ", topsSmrs.Select(s => s.name)))}");
            var dupNames = topsSmrs.GroupBy(s => s.name).Where(g => g.Count() > 1).Select(g => $"{g.Key}x{g.Count()}").ToList();
            PatchLogger.LogDebug($"[TopsLoader] donor={donor}/{costume} tops SMR name duplicates: {(dupNames.Count == 0 ? "(none)" : string.Join(", ", dupNames))}");
        }
        return entry;
    }

    /// <summary>
    /// distance preservation の補正済み Mesh キャッシュを破棄する。
    /// <see cref="Configs.TopsDistancePreserveRange"/> など補正パラメータが変わった際に呼ぶ。
    /// 補正済み Mesh は <see cref="Object.Instantiate"/> 由来でネイティブ側の手動解放が必要。
    /// 呼び出し側は本メソッド後に Apply 系を再実行することで新パラメータの補正を反映させる。
    /// </summary>
    public static void InvalidateDistancePreserveCache()
    {
        foreach (var m in s_resolvedCache.Values)
        {
            if (m != null) UnityEngine.Object.Destroy(m);
        }
        s_resolvedCache.Clear();
        s_resolvedAppliedIds.Clear();
    }

    private static void OnSceneUnloaded(Scene scene)
    {
        // 全 state を保持し何もしない。session 内で InstanceID は再利用されないため
        // 破棄済み target に紐づく stale entry は harmless に残るのみで誤検知しない。
        //
        // - s_resolvedCache: 加算読込シーンの unload は別シーンで適用中 SMR が
        //   cache Mesh を sharedMesh で参照中に発火する。Destroy すると Tops が
        //   画面から消える。ChangeSceneAsync の new→old 順 unload でも、新シーン
        //   setup() Postfix が先行 cache 書込み直後に旧シーン unload が走るレース
        //   が起きうる。cache key は安定 InstanceID で再利用も正しい。
        // - SmrSnapshotStore / s_applied: m_holeScene の char は env で preserve され
        //   同 InstanceID で Apply trigger が再発火する。Clear すると Capture が
        //   donor 補正済み mesh を素として誤記録する。SmrSnapshotStore は意図的に
        //   Clear API を持たない（unbuildable で誤呼出を防止）。
        // - s_resolvedAppliedIds: cache 追従で保持。組合せ上限は数百規模で頭打ち。
        // - s_cache (donor preload): donorParent は DontDestroyOnLoad 配下なので scene 跨ぎ safe。
        //
        // GPU memory cleanup は InvalidateDistancePreserveCache() に集約。
    }

    /// <summary>
    /// <see cref="CharacterHandle.setup"/> Postfix から呼ぶ。
    /// フルボディ衣装 target ガード (Bunnygirl / フルボディ DLC, SwimWear 除く) は
    /// <see cref="Apply"/> 内で行う（<see cref="ApplyDirectly"/> 経路でも同様にガードしたいため）。
    /// donor 自身の setup() Postfix は IsChildOf ガードで除外。
    ///
    /// donor が未 preload（ExSave ロード復元時など Wardrobe UI を経由しない場合）は
    /// <see cref="PreloadDonorAsync"/> を起動し、完了後に re-apply する（fire-and-forget）。
    /// </summary>
    public static void ApplyIfOverridden(CharacterHandle handle)
    {
        if (handle?.Chara == null) return;

        // donor 自身の setup() Postfix が走るケースを除外（preload host 配下の GameObject）。
        // 自分の host だけでなく BottomsLoader 等の preload host 配下も skip する: 他 Loader が target の
        // skin / Bottoms 系 donor として preload した character の setup() でも当 patch は発火するため、
        // ここで弾かないと donor preload character に Tops override が誤適用される。target と同じ CharID で
        // override が登録されていると、preload donor character の skin SMR.sharedMesh が
        // SkinShrinkCoordinator 経由で transient pushed Mesh に上書きされ、後続の InvalidateCache で
        // destroyed Mesh となって target の Apply(d) swap source も巻き込み破壊する症状を踏んだ。
        if (DonorPreloadRegistry.IsAnyHostParent(handle.Chara)) return;

        // FittingRoom 動作中 / 本体 CostumeOverride 中 / ExtraScene 中は skip (CostumeChangerPatch.Prefix と揃える)。
        // setup() Postfix は Preload Prefix と独立経路なので、ここでも同じ guard が要る。
        // ExtraScene (Album / Cheki / MiniGame 等) は本体 FittingRoom が衣装を制御するため、
        // RespectGameCostumeOverride 有効時は MOD 適用しない (FR 退出後も FR の選択を保持)。
        if (CostumeChangerPatch.IsFittingRoomActiveExternal()) return;
        if (Configs.RespectGameCostumeOverride.Value)
        {
            if (GBSystem.Instance != null
                && GBSystem.Instance.GetCostumeOverride() != GBSystem.CostumeOverride.None) return;
            if (GBSystem.GetCurrentSceneName() == "ExtraScene") return;
        }

        var id = handle.GetCharID();
        if (!TopsOverrideStore.TryGet(id, out var entry)) return;

        // Tops Apply は 3 系統の donor を要する（ApplyTopsAsync と同じ流儀）:
        //   (a) main donor: (donor, costume) — donor の Tops mesh
        //   (b) target skin donor: (target, Babydoll) — target の mesh_skin_upper を Babydoll で swap
        //   (c) donor skin donor: (donor, Babydoll) — distance preservation の対称基準
        // すべて preload 済なら即時 Apply、未ロードなら deferred apply に回す。
        var donorKey = (entry.DonorChar, entry.DonorCostume);
        var targetSkinKey = (id, SkinDonorCostume);
        var donorSkinKey = (entry.DonorChar, SkinDonorCostume);
        bool needsDonorSkin = entry.DonorCostume != SkinDonorCostume;
        bool allReady = s_cache.ContainsKey(donorKey)
            && s_cache.ContainsKey(targetSkinKey)
            && (!needsDonorSkin || s_cache.ContainsKey(donorSkinKey));
        if (allReady)
        {
            Apply(handle.Chara, entry.DonorChar, entry.DonorCostume);
            return;
        }

        // donor 群のいずれかが未ロード: ExSave rehydrate 経路では Wardrobe UI の PreloadDonorAsync が走らないため、
        // Apply が donor lookup で skip / 警告する。3 系統すべて await してから re-apply。
        var chara = handle.Chara;
        var donorChar = entry.DonorChar;
        var donorCostume = entry.DonorCostume;
        PatchLogger.LogDebug($"[TopsLoader] donor 未ロード、3 系統 preload 起動: {donorChar}/{donorCostume} target={id}");
        PreloadAndReapplyAsync(chara, id, donorChar, donorCostume).Forget();
    }

    private static async UniTaskVoid PreloadAndReapplyAsync(GameObject chara, CharID target, CharID donorChar, CostumeType donorCostume)
    {
        bool mainOk = await PreloadDonorAsync(donorChar, donorCostume);
        if (!mainOk) return;

        bool targetSkinOk = await PreloadDonorAsync(target, SkinDonorCostume);
        if (!targetSkinOk)
            PatchLogger.LogWarning($"[TopsLoader] target skin donor ({target}/{SkinDonorCostume}) preload 失敗、partial apply で続行（Babydoll 基準の境界整合は得られない）");

        if (donorCostume != SkinDonorCostume)
        {
            bool donorSkinOk = await PreloadDonorAsync(donorChar, SkinDonorCostume);
            if (!donorSkinOk)
                PatchLogger.LogWarning($"[TopsLoader] donor skin donor ({donorChar}/{SkinDonorCostume}) preload 失敗、distance preservation skip で続行");
        }
        if (chara == null) return;  // Unity の == null は破棄済みを true にする
        if (!TopsOverrideStore.TryGet(target, out var freshEntry)) return;
        if (freshEntry.DonorChar != donorChar || freshEntry.DonorCostume != donorCostume) return;
        Apply(chara, freshEntry.DonorChar, freshEntry.DonorCostume);
    }

    /// <summary>
    /// Wardrobe (F7) Tops タブ確定時に呼ぶ。target の既存 GameObject に対して再適用フラグを
    /// セットしてから <see cref="Apply"/> する。reload 経由 (env.LoadCharacter) は同 costume だと
    /// no-op で setup() Postfix が発火しないためこちらを使う（BottomsLoader.ApplyDirectly と同方針）。
    /// </summary>
    public static void ApplyDirectly(GameObject character, CharID donorChar, CostumeType donorCostume)
    {
        if (character == null) return;
        // 設計契約: BottomsLoader と異なり Tops は (c2) SwimWear ブロックで Bottoms 候補 SMR も
        // touch するため snapshot key 集合が donor 依存で変動する (例: LUNA SwimWear → 非SwimWear donor へ
        // 切替で mesh_costume_frill 等が前回 snapshot のまま残留)。よって素状態保持の不変条件を維持するには
        // Apply 前に必ず RestoreFor で素状態へ戻す必要がある。
        // RestoreFor 内で applied フラグ解除 / grafted bone destroy も実行されるため重複処理不要。
        // s_resolvedCache / s_resolvedAppliedIds (distance preserve 結果キャッシュ) は touch せず維持
        // (同 donor 再 Apply で補正済み mesh を再利用するため、ここで invalidate するとキャッシュヒット率が下がる)。
        RestoreFor(character);
        Apply(character, donorChar, donorCostume);
    }

    public static void Apply(GameObject character, CharID donorChar, CostumeType donorCostume)
    {
        if (character == null) return;

        var targetHandle = Internal.CharacterResolver.ResolveHandle(character);
        var targetCostume = targetHandle?.m_lastLoadArg?.Costume;
        var targetCharID = targetHandle?.GetCharID() ?? CharID.NUM;

        // base-aware additive モード:
        //   target がフルボディ衣装（SwimWear / Bunnygirl / 分離型でない DLC）のときは
        //   target の mesh_costume をそのまま残し、donor の Tops SMR を inject 経路で重ねる。
        //   target の素肌 / 元衣装を維持できる。
        //   donor=SwimWear (= "SwimWear に override する") のときは従来どおり swap で
        //   target.mesh_costume を donor のものに置換し下半身パートも (c2) で transplant する。
        bool additiveMode = (targetCostume?.IsFullBodyCostume() ?? false)
                            && donorCostume != CostumeType.SwimWear;

        // target がフルボディ衣装 (Bunnygirl / フルボディ DLC) + 通常モードでは Apply スキップ。
        // フルボディスーツで構造差大のため Tops swap が中途半端に適用されると上半身だけ別衣装が乗る不整合が起きる。
        // additive モードでは inject のみで target を一切 touch しないため許可する。
        // target=SwimWear は除外: donor=SwimWear のとき ApplySwimWearBottomsPhase で full-body swap が成立するため。
        // ApplyIfOverridden / ApplyDirectly 両経路で同じガードが効くよう Apply 側に置く。
        if ((targetCostume?.IsFullBodyCostume() ?? false)
            && targetCostume != CostumeType.SwimWear
            && !additiveMode)
        {
            s_applied.Add(character.GetInstanceID()); // dedup ログを 1 回に抑える
            return;
        }

        if (!s_cache.TryGet((donorChar, donorCostume), out var donor))
        {
            // preload 未完了 vs 本当に donor 未登録を区別してログを出す（BottomsLoader と同方針）。
            if (IsLoaded)
                PatchLogger.LogWarning($"[TopsLoader] donor 未ロード: {donorChar}/{donorCostume}");
            else
                PatchLogger.LogDebug($"[TopsLoader] preload 未完了のため Apply スキップ: {donorChar}/{donorCostume}（後続 setup を待機）");
            return;
        }

        var instanceId = character.GetInstanceID();
        if (s_applied.Contains(instanceId)) return; // 多重 Apply ガード

        // donor が Tops を一切持たない場合、target を hide すると上半身が完全に消えるため、
        // 何もせずに applied 登録だけ行う（再走査抑止）。
        // 注意: ApplyTopsAsync は !donorOk で UI 経路を弾くため通常は到達しない。
        if (donor.TopsSmrs == null || donor.TopsSmrs.Count == 0)
        {
            // donor が Tops を持たない = Tops contribution 不在。古い contribution が残っていれば削除。
            SkinShrinkCoordinator.UnregisterTops(character);
            s_applied.Add(instanceId);
            return;
        }

        var ctx = new TopsApplyContext
        {
            Character = character,
            DonorChar = donorChar,
            DonorCostume = donorCostume,
            Donor = donor,
            TargetCharID = targetCharID,
            TargetCostume = targetCostume,
            AdditiveMode = additiveMode,
            InstanceId = instanceId,
            Renderers = character.GetComponentsInChildren<SkinnedMeshRenderer>(true),
            SwappedTopsPairs = new List<(SkinnedMeshRenderer Target, SkinnedMeshRenderer DonorPreload)>(),
            DidSomething = false,
        };

        ApplySmrPhase(ref ctx);

        ApplySwimWearBottomsPhase(ref ctx);

        ApplySkinUpperPhase(ref ctx);

        ApplyDistancePreservePhase(ref ctx);

        ApplySkinShrinkPhase(ref ctx);

        // didSomething に関わらず Applied 登録（冪等性確保、再 setup() trigger 時の毎フレーム再走査回避）。
        // s_applied は scene 跨ぎで保持される (memory feedback_scene_unload_snapshot_clear.md)。
        // session 内で InstanceID は再利用されないため stale entry は harmless。
        s_applied.Add(instanceId);
        if (ctx.DidSomething)
            PatchLogger.LogInfo($"[TopsLoader] 適用: {character.name} ← {donorChar}/{donorCostume}");
    }

    /// <summary>
    /// Apply 内で 5 phase 跨ぎに引き回す中間状態。フェーズ間の SMR ペア / DidSomething 流れを 1 構造体に集約。
    /// (c2) ApplySwimWearBottomsPhase で SwappedTopsPairs に Add した Bottoms 候補も
    /// (e) ApplyDistancePreservePhase で読まれる cross-phase data flow に注意。
    ///
    /// Renderers: Apply 開始時点の <c>GetComponentsInChildren&lt;SkinnedMeshRenderer&gt;</c> snapshot。
    /// (a)(b)(c2) で <see cref="InjectSmrLogged"/> された新規 SMR は含まない (元 Apply の挙動と等価)。
    /// </summary>
    private struct TopsApplyContext
    {
        public GameObject Character;
        public CharID DonorChar;
        public CostumeType DonorCostume;
        public DonorEntry Donor;
        public CharID TargetCharID;
        public CostumeType? TargetCostume;
        public bool AdditiveMode;
        public int InstanceId;
        public SkinnedMeshRenderer[] Renderers;
        public List<(SkinnedMeshRenderer Target, SkinnedMeshRenderer DonorPreload)> SwappedTopsPairs;
        public bool DidSomething;
    }

    /// <summary>
    /// (a)(b)(c) Tops SMR の swap / inject / hide。targetByName / donorByName 構築と debug ログを含む。
    /// </summary>
    private static void ApplySmrPhase(ref TopsApplyContext ctx)
    {
        var targetTopsList = ctx.Renderers.Where(IsTopsCandidate).ToList();

        // 同名 SMR 重複の検出。最初の 1 つだけ採用し、2 つ目以降は警告。
        var targetByName = new Dictionary<string, SkinnedMeshRenderer>();
        foreach (var smr in targetTopsList)
        {
            if (!targetByName.ContainsKey(smr.name))
                targetByName[smr.name] = smr;
            else
                PatchLogger.LogWarning($"[TopsLoader] target 同名 Tops SMR 重複: {ctx.Character.name}/{smr.name}（最初の 1 つを採用）");
        }
        var donorByName = new Dictionary<string, SkinnedMeshRenderer>();
        foreach (var smr in ctx.Donor.TopsSmrs)
        {
            if (!donorByName.ContainsKey(smr.name))
                donorByName[smr.name] = smr;
        }

        if (PatchLogger.IsDebugEnabled)
        {
            PatchLogger.LogDebug($"[TopsLoader] target={ctx.Character.name} mode={(ctx.AdditiveMode ? "additive" : "swap")} tops candidates: {(targetByName.Count == 0 ? "(none)" : string.Join(", ", targetByName.Keys))}");
            if (ctx.AdditiveMode)
            {
                // additive 経路では target/donor 同名でも target を温存し donor 側を inject 経路で追加する。
                // common 集合は「target 既存と並ぶ overlay」として表示。swap/hide は走らないので独立列挙はしない。
                var donorAll = donorByName.Keys.OrderBy(s => s).ToList();
                var overlapping = donorByName.Keys.Intersect(targetByName.Keys).OrderBy(s => s).ToList();
                PatchLogger.LogDebug($"[TopsLoader] inject (all donor, additive): {(donorAll.Count == 0 ? "(none)" : string.Join(", ", donorAll))}");
                PatchLogger.LogDebug($"[TopsLoader] overlap with target (kept as-is): {(overlapping.Count == 0 ? "(none)" : string.Join(", ", overlapping))}");
            }
            else
            {
                var common = donorByName.Keys.Intersect(targetByName.Keys).OrderBy(s => s).ToList();
                var donorOnly = donorByName.Keys.Except(targetByName.Keys).OrderBy(s => s).ToList();
                var targetOnly = targetByName.Keys.Except(donorByName.Keys).OrderBy(s => s).ToList();
                PatchLogger.LogDebug($"[TopsLoader] swap (common): {(common.Count == 0 ? "(none)" : string.Join(", ", common))}");
                PatchLogger.LogDebug($"[TopsLoader] inject (donor-only): {(donorOnly.Count == 0 ? "(none)" : string.Join(", ", donorOnly))}");
                PatchLogger.LogDebug($"[TopsLoader] hide (target-only): {(targetOnly.Count == 0 ? "(none)" : string.Join(", ", targetOnly))}");
            }
        }

        // (e) 距離保存補正に渡す Tops SMR ペアを集める（swap / inject 経由のもの）。
        // donor 側の SMR (preload エントリ) は元の bones[] / boneWeights を持つため、Pass 4 の boneWeight blend で参照する。
        // additive モードでも injected donor pair を distance preservation 対象に集める
        // (preload Babydoll 基準は live skin の swap 状態に依存しないため)。

        if (ctx.AdditiveMode)
        {
            // additive: target の Tops SMR は全て温存し、donor の Tops SMR を全て inject 経路で追加する。
            // 同名 SMR が並ぶケース (target.mesh_costume と donor.mesh_costume) でも snapshot key の
            // IsInjected=true で識別され、Restore は InjectedGo 参照で Destroy するため名前衝突しない。
            foreach (var kv in donorByName)
            {
                var injected = InjectSmrLogged(ctx.Character, kv.Key, ctx.Renderers);
                CaptureSnapshotIfFirst((ctx.InstanceId, kv.Key), wasInjected: true, smr: null, injectedGo: injected.gameObject);
                SwapSmr(injected, kv.Value, ctx.Character, kv.Key + "(injected,additive)");
                ctx.SwappedTopsPairs.Add((injected, kv.Value));
                ctx.DidSomething = true;
            }
        }
        else
        {
            // (a) 共通: target 既存 SMR に donor の sharedMesh / bones / materials を swap
            foreach (var kv in donorByName)
            {
                if (!targetByName.TryGetValue(kv.Key, out var targetSmr)) continue;
                CaptureSnapshotIfFirst((ctx.InstanceId, kv.Key), wasInjected: false, smr: targetSmr, injectedGo: null);
                SwapSmr(targetSmr, kv.Value, ctx.Character, kv.Key);
                ctx.SwappedTopsPairs.Add((targetSmr, kv.Value));
                ctx.DidSomething = true;
            }

            // (b) donor のみ持つ: target に新規 SMR を注入して swap
            foreach (var kv in donorByName)
            {
                if (targetByName.ContainsKey(kv.Key)) continue;
                var injected = InjectSmrLogged(ctx.Character, kv.Key, ctx.Renderers);
                CaptureSnapshotIfFirst((ctx.InstanceId, kv.Key), wasInjected: true, smr: null, injectedGo: injected.gameObject);
                SwapSmr(injected, kv.Value, ctx.Character, kv.Key + "(injected)");
                ctx.SwappedTopsPairs.Add((injected, kv.Value));
                ctx.DidSomething = true;
            }

            // (c) target のみ持つ: donor の Tops 構成に整合させるため hide
            foreach (var kv in targetByName)
            {
                if (donorByName.ContainsKey(kv.Key)) continue;
                // 既に inactive ならログ抑制（冪等）
                if (!kv.Value.gameObject.activeSelf) continue;
                CaptureSnapshotIfFirst((ctx.InstanceId, kv.Key), wasInjected: false, smr: kv.Value, injectedGo: null);
                kv.Value.gameObject.SetActive(false);
                PatchLogger.LogDebug($"[TopsLoader] target の {kv.Key} を隠す: {ctx.Character.name}（donor 側に無いため）");
                ctx.DidSomething = true;
            }
        }
    }

    /// <summary>
    /// (c2) SwimWear donor は Tops の延長として「下半身パート」も全部 transplant する。
    ///      LUNA なら mesh_costume_frill/frill2 を target に swap/inject し、
    ///      target 側にしか無い Bottoms 候補は hide する。
    ///      RIN/MIUKA/ERISA/KUON SwimWear のように donor が Bottoms 候補を持たないワンピース型なら
    ///      donor 側 0 件 → target 側を全 hide する経路で donor の mesh_costume (全身) のみ表示される。
    ///      Bottoms override が設定されている target は Bottoms 側に任せる（Bottoms override 優先）。
    ///      transplant した SMR は Tops snapshot 経由で RestoreFor が冪等に元に戻す。
    ///      mesh_costume_skirt は SwimWear donor 全般でループ内除外する（KANA SwimWear の bikini bottom もここで弾かれる）。
    /// donorCostume == SwimWear → additiveMode == false 確定 (定義より additive 不到達)。
    /// bottoms override 併用時も (c2) を走らせ順序非依存性を保証 (tops 先 / bottoms 先)。
    /// per-loader isolation: target 側のみ BottomsLoader 所有名を除外し、donor 側は除外しないため
    /// LUNA frill は inject 経路で重ねて両 frill 共存する。
    /// </summary>
    private static void ApplySwimWearBottomsPhase(ref TopsApplyContext ctx)
    {
        if (ctx.DonorCostume != CostumeType.SwimWear) return;

        // BottomsLoader が触る name 集合。bottoms 領域は BottomsLoader の責任範囲として尊重。
        var bottomsOwned = new HashSet<string>(StringComparer.Ordinal);
        if (ctx.TargetCharID < CharID.NUM && BottomsOverrideStore.TryGet(ctx.TargetCharID, out var bottomsEntry))
        {
            foreach (var n in BottomsLoader.GetTransplantedBottomsKinds(bottomsEntry.DonorChar, bottomsEntry.DonorCostume))
                bottomsOwned.Add(n);
        }

        var donorBottomsByName = new Dictionary<string, SkinnedMeshRenderer>();
        foreach (var smr in ctx.Donor.AllSmrs ?? System.Linq.Enumerable.Empty<SkinnedMeshRenderer>())
        {
            if (smr == null) continue;
            if (!BottomsLoader.IsBottomsCandidate(smr)) continue;
            // mesh_costume_skirt は仕様により Tops 経由では転写しない（ユーザ方針）。
            // Bottoms override 設定の有無に関わらず一律除外: Tops で来ることを期待しない UX に統一。
            // (cross-char SwimWear bottoms は物理破綻のため撤退済 — memory `project_kana_swimwear_bottoms_retreat.md` 参照)
            if (smr.name == "mesh_costume_skirt") continue;
            if (!donorBottomsByName.ContainsKey(smr.name))
                donorBottomsByName[smr.name] = smr;
        }
        var targetBottomsByName = new Dictionary<string, SkinnedMeshRenderer>();
        foreach (var smr in ctx.Renderers)
        {
            if (smr == null) continue;
            if (!BottomsLoader.IsBottomsCandidate(smr)) continue;
            // 上記 donor 側と対称: target.mesh_costume_skirt は Tops で触らず元のまま残す。
            if (smr.name == "mesh_costume_skirt") continue;
            // bottoms-owned: BottomsLoader 注入 SMR は (c2) hide 経路で隠さない (per-loader isolation)。
            // donor 側は除外しない (LUNA frill を inject 経路で重ねて両表示するため)。
            if (bottomsOwned.Contains(smr.name)) continue;
            if (!targetBottomsByName.ContainsKey(smr.name))
                targetBottomsByName[smr.name] = smr;
        }

        if (PatchLogger.IsDebugEnabled)
        {
            var common = donorBottomsByName.Keys.Intersect(targetBottomsByName.Keys).OrderBy(s => s).ToList();
            var donorOnly = donorBottomsByName.Keys.Except(targetBottomsByName.Keys).OrderBy(s => s).ToList();
            var targetOnly = targetBottomsByName.Keys.Except(donorBottomsByName.Keys).OrderBy(s => s).ToList();
            PatchLogger.LogDebug($"[TopsLoader] (c2) SwimWear bottoms swap (common): {(common.Count == 0 ? "(none)" : string.Join(", ", common))}");
            PatchLogger.LogDebug($"[TopsLoader] (c2) SwimWear bottoms inject (donor-only): {(donorOnly.Count == 0 ? "(none)" : string.Join(", ", donorOnly))}");
            PatchLogger.LogDebug($"[TopsLoader] (c2) SwimWear bottoms hide (target-only): {(targetOnly.Count == 0 ? "(none)" : string.Join(", ", targetOnly))}");
        }

        // swap (common)
        foreach (var kv in donorBottomsByName)
        {
            if (!targetBottomsByName.TryGetValue(kv.Key, out var targetSmr)) continue;
            CaptureSnapshotIfFirst((ctx.InstanceId, kv.Key), wasInjected: false, smr: targetSmr, injectedGo: null);
            SwapSmr(targetSmr, kv.Value, ctx.Character, kv.Key);
            ctx.SwappedTopsPairs.Add((targetSmr, kv.Value));
            ctx.DidSomething = true;
        }
        // inject (donor-only): Bottoms SMR なので mesh_skin_lower を reference に注入
        foreach (var kv in donorBottomsByName)
        {
            if (targetBottomsByName.ContainsKey(kv.Key)) continue;
            var injected = InjectSmrLogged(ctx.Character, kv.Key, ctx.Renderers, referenceName: "mesh_skin_lower");
            CaptureSnapshotIfFirst((ctx.InstanceId, kv.Key), wasInjected: true, smr: null, injectedGo: injected.gameObject);
            SwapSmr(injected, kv.Value, ctx.Character, kv.Key + "(injected)");
            ctx.SwappedTopsPairs.Add((injected, kv.Value));
            ctx.DidSomething = true;
        }
        // hide (target-only)
        foreach (var kv in targetBottomsByName)
        {
            if (donorBottomsByName.ContainsKey(kv.Key)) continue;
            if (!kv.Value.gameObject.activeSelf) continue;
            CaptureSnapshotIfFirst((ctx.InstanceId, kv.Key), wasInjected: false, smr: kv.Value, injectedGo: null);
            kv.Value.gameObject.SetActive(false);
            PatchLogger.LogDebug($"[TopsLoader] target の {kv.Key} を隠す: {ctx.Character.name}（SwimWear donor 側に対応 SMR 無し）");
            ctx.DidSomething = true;
        }
    }

    /// <summary>
    /// (d) target.mesh_skin_upper を target/Babydoll に swap。bottoms override 等が
    /// Babydoll 基準で扱える前提を整える。skin donor は ApplyTopsAsync 側で先行 preload 済み。
    /// skip した場合 (e) distance preservation は target 元 mesh_skin_upper 基準で走り、
    /// Babydoll 基準の境界整合は得られないが補正自体は破綻しない (フェイルセーフ)。
    /// </summary>
    private static void ApplySkinUpperPhase(ref TopsApplyContext ctx)
    {
        if (ctx.AdditiveMode)
        {
            // additive モードでは target の素肌を維持するため skin upper swap は skip。
            PatchLogger.LogDebug($"[TopsLoader] skin upper swap skip: additive mode ({ctx.Character.name})");
            return;
        }
        if (ctx.TargetCharID >= CharID.NUM)
        {
            PatchLogger.LogDebug($"[TopsLoader] skin upper swap skip: target.charID 解決失敗 ({ctx.Character.name})");
            return;
        }
        // targetCostume: target の現在ロード衣装 (m_lastLoadArg.Costume)。
        // SkinDonorCostume (= Babydoll) は skin donor preload で使う sentinel 衣装。
        // target が既に Babydoll なら donor と同一 mesh になり swap は冪等 → スキップ。
        if (ctx.TargetCostume == SkinDonorCostume)
        {
            PatchLogger.LogDebug($"[TopsLoader] skin upper swap skip: target が既に Babydoll (冪等)");
            return;
        }
        if (!s_cache.TryGet((ctx.TargetCharID, SkinDonorCostume), out var skinDonor) || skinDonor.AllSmrs == null)
        {
            // preload 失敗 or 未完了。Apply は続行するが境界整合不全の可能性を警告。
            PatchLogger.LogWarning($"[TopsLoader] skin donor (target/{SkinDonorCostume}) 未ロードで skin upper swap スキップ ({ctx.Character.name})");
            return;
        }

        var donorSkinUpper = skinDonor.AllSmrs.FirstOrDefault(s => s != null && s.name == "mesh_skin_upper");
        var targetSkinUpper = ctx.Renderers.FirstOrDefault(s => s != null && s.name == "mesh_skin_upper");
        if (donorSkinUpper != null && targetSkinUpper != null)
        {
            // ApplyDirectly (live tune) で 2 cycle に跨る連鎖 bug の入口防衛。
            // cycle N: RestoreFor → UnregisterTops の RefreshOne が HasBottoms 残存時に
            //   Bottoms push を upper にも適用し sharedMesh が transient Mesh に。直後の
            //   CaptureSnapshotIfFirst が transient を OriginalMesh として焼き込む。
            // cycle N+1: InvalidateCache が transient を Destroy → RestoreFor が
            //   destroyed Mesh を sharedMesh に書き戻し → skin_upper 描画破損。
            // capture 直前に Coordinator 保持の addressables stable asset へ明示 rewind し、
            // snapshot には常に stable 参照のみ焼く。RefreshOne 仕様自体はそのまま維持。
            SkinShrinkCoordinator.RestoreSkinUpperToOriginal(ctx.Character, targetSkinUpper);
            CaptureSnapshotIfFirst((ctx.InstanceId, "mesh_skin_upper"), wasInjected: false, smr: targetSkinUpper, injectedGo: null);
            SwapSmr(targetSkinUpper, donorSkinUpper, ctx.Character, "mesh_skin_upper");
            bool selfDonor = ctx.DonorChar == ctx.TargetCharID;
            PatchLogger.LogDebug($"[TopsLoader] skin upper swap: {ctx.TargetCharID}/{SkinDonorCostume} → {ctx.Character.name} (selfDonor={selfDonor}, tops={ctx.DonorChar}/{ctx.DonorCostume})");
            ctx.DidSomething = true;
        }
        else
        {
            PatchLogger.LogDebug($"[TopsLoader] skin upper swap skip: mesh_skin_upper SMR 不在 (donor={donorSkinUpper != null}, target={targetSkinUpper != null})");
        }
    }

    /// <summary>
    /// (e) per-vert distance preservation: donor / target 双方の preload Babydoll を基準に
    ///     d_donor / d_target を比較し、移植後も donor 元の浮き具合を target で再現する。
    ///     基準 mesh は (charID, Babydoll) の preload エントリから取得するため、
    ///     live target.mesh_skin_upper の swap 状態 (= (d) skip 有無) には依存しない。
    ///     additive モードでも injected donor pair を同手順で補正する。フルボディ衣装 additive
    ///     (Bunnygirl / フルボディ DLC) は live skin (variant) と Babydoll skin の頂点配置が乖離し
    ///     ユーザー判断でリスク受容
    ///     (実機で破綻が出たら別計画で skip ガード追加する。
    ///      参照: docs/superpowers/plans/2026-05-08-additive-tops-vertex-adjust.md)。
    ///     donor Babydoll preload 失敗 / mesh_skin_upper SMR 不在 / target 側不在のいずれかで skip (Apply 本体は続行)。
    /// </summary>
    private static void ApplyDistancePreservePhase(ref TopsApplyContext ctx)
    {
        // (a)(b)(c) ApplySmrPhase と (c2) ApplySwimWearBottomsPhase の両方で SwappedTopsPairs.Add されない
        // ケース (例: donor.TopsSmrs.Count == 0 は Apply 冒頭で early return するので到達しない / SwimWear donor で
        // Bottoms 候補も 0) のみ skip。それ以外では preload 失敗時に内部で warning + skip する。
        if (ctx.SwappedTopsPairs.Count == 0) return;

        // donor 側 Babydoll skin (upper + lower) を取得。
        // ワンピース型 donor (KANA SwimWear 等) の下半身頂点も適切な近傍を見つけられるよう、
        // mesh_skin_upper と mesh_skin_lower を結合した skin reference で K-NN する。
        // target 側も同様に target 自身の Babydoll preload エントリから upper + lower を取得し、
        // donor / target で対称な Babydoll 基準を構築する（target の現 mesh_skin_lower は costume 依存で
        // 一致しないため、Babydoll 基準で揃える方が距離保存の意味的整合が取れる）。
        if (!s_cache.TryGet((ctx.DonorChar, SkinDonorCostume), out var donorSkinDonor) || donorSkinDonor.AllSmrs == null)
        {
            PatchLogger.LogWarning($"[TopsLoader] donor skin donor ({ctx.DonorChar}/{SkinDonorCostume}) 未ロードで distance preservation スキップ ({ctx.Character.name})");
            return;
        }
        if (ctx.TargetCharID >= CharID.NUM
            || !s_cache.TryGet((ctx.TargetCharID, SkinDonorCostume), out var targetSkinDonor)
            || targetSkinDonor.AllSmrs == null)
        {
            PatchLogger.LogWarning($"[TopsLoader] target skin donor ({ctx.TargetCharID}/{SkinDonorCostume}) 未ロードで distance preservation スキップ ({ctx.Character.name})");
            return;
        }

        var donorSkinUpper = donorSkinDonor.AllSmrs.FirstOrDefault(s => s != null && s.name == "mesh_skin_upper");
        var donorSkinLower = donorSkinDonor.AllSmrs.FirstOrDefault(s => s != null && s.name == "mesh_skin_lower");
        var targetSkinUpper = targetSkinDonor.AllSmrs.FirstOrDefault(s => s != null && s.name == "mesh_skin_upper");
        var targetSkinLower = targetSkinDonor.AllSmrs.FirstOrDefault(s => s != null && s.name == "mesh_skin_lower");

        // upper / lower のどちらか一方でも取れていれば続行。両方無いと結合 verts=0 になり Preserve 内で warning + skip。
        if (donorSkinUpper == null && donorSkinLower == null)
        {
            PatchLogger.LogWarning($"[TopsLoader] donor Babydoll に mesh_skin_upper/lower どちらも不在で distance preservation スキップ ({ctx.DonorChar})");
            return;
        }
        if (targetSkinUpper == null && targetSkinLower == null)
        {
            PatchLogger.LogWarning($"[TopsLoader] target Babydoll に mesh_skin_upper/lower どちらも不在で distance preservation スキップ ({ctx.TargetCharID})");
            return;
        }

        var donorSkinSmrs = new[] { donorSkinUpper, donorSkinLower };
        var targetSkinSmrs = new[] { targetSkinUpper, targetSkinLower };
        foreach (var pair in ctx.SwappedTopsPairs)
        {
            ApplyDistancePreserveForTops(pair.Target, pair.DonorPreload, donorSkinSmrs, targetSkinSmrs);
        }
    }

    /// <summary>
    /// (f) Tops SkinShrink: target.mesh_skin_upper を tops より内側へ push して z-fighting / 貫通を解消。
    ///     SkinShrinkCoordinator が Bottoms contribution と統合管理し、両 contribution を素 mesh から
    ///     順次 push し直すため、Tops/Bottoms 同時適用や片方 Restore で他方が崩れない。
    ///     additive モードでは (d) skin_upper Babydoll swap が走らず RegisterTops の API 契約
    ///     (現 sharedMesh = Babydoll asset を OriginalSkinUpper として焼く) を満たせないため skip。
    ///     フルボディ衣装 (SwimWear / Bunnygirl / フルボディ DLC) では mesh_costume が body を覆うため
    ///     SkinShrink の視覚効果も限定的。
    ///     additive で SkinShrink を有効化する場合は Coordinator API の改修が必要 (別計画)。
    ///     swap 無し / Bottoms only donor 経路は contribution 不在 → UnregisterTops。
    /// </summary>
    private static void ApplySkinShrinkPhase(ref TopsApplyContext ctx)
    {
        if (!ctx.AdditiveMode && ctx.SwappedTopsPairs.Count > 0)
        {
            SkinShrinkCoordinator.RegisterTops(
                ctx.Character,
                ctx.SwappedTopsPairs.Where(p => IsTopsCandidate(p.Target)).Select(p => p.Target),
                Configs.TopsSkinShrink.Value,
                Configs.TopsSkinShrinkFalloffRadius.Value,
                Configs.TopsSkinShrinkSampleRadius.Value);
        }
        else
        {
            // 古い Tops contribution が残っていれば削除。Bottoms 残存なら Bottoms 単独で refresh される。
            SkinShrinkCoordinator.UnregisterTops(ctx.Character);
        }
    }

    /// <summary>
    /// 個別 Tops SMR に per-vert distance preservation を適用する。
    /// donor skin との距離を target skin で再現するよう頂点を補正する。
    /// 結果は <see cref="s_resolvedCache"/> にキャッシュ、二重補正は <see cref="s_resolvedAppliedIds"/> でガード。
    /// </summary>
    private static void ApplyDistancePreserveForTops(
        SkinnedMeshRenderer topSmr,
        SkinnedMeshRenderer donorPreloadSmr,        // donor 元 SMR (preload エントリ)。bones / boneWeights を持つ
        SkinnedMeshRenderer[] donorSkinSmrs,        // donor 側 Babydoll skin SMR 列 [upper, lower]（null 要素可）
        SkinnedMeshRenderer[] targetSkinSmrs)       // target 側 Babydoll skin SMR 列 [upper, lower]（null 要素可）
    {
        if (topSmr == null || topSmr.sharedMesh == null) return;
        if (donorPreloadSmr == null || donorPreloadSmr.sharedMesh == null) return;

        var donorMesh = topSmr.sharedMesh;

        // 既に補正済み mesh が刺さっていれば二重補正しない
        if (s_resolvedAppliedIds.Contains(donorMesh.GetInstanceID())) return;

        int SkinId(SkinnedMeshRenderer[] arr, int idx) => arr != null && arr.Length > idx && arr[idx] != null && arr[idx].sharedMesh != null ? arr[idx].sharedMesh.GetInstanceID() : 0;
        int dUp = SkinId(donorSkinSmrs, 0);
        int dLo = SkinId(donorSkinSmrs, 1);
        int tUp = SkinId(targetSkinSmrs, 0);
        int tLo = SkinId(targetSkinSmrs, 1);
        var cacheKey = (donorMesh.GetInstanceID(), dUp, dLo, tUp, tLo);

        // ribbon 系除外は MeshDistancePreserver.Preserve 内 (donorBoneIsRibbon mask) で per-vert 判定する。
        // SMR 名ベースだと同一 mesh 内 ribbon 部 + 非 ribbon 部混在に対応できないため。
        // cache hit でも entry が Destroy 済みの場合は recompute する (Unity null check で検出)。
        // 通常は InvalidateDistancePreserveCache() でしか Destroy されないが防御的ガード。
        if (!s_resolvedCache.TryGetValue(cacheKey, out var resolved) || resolved == null)
        {
            resolved = MeshDistancePreserver.Preserve(
                donorPreloadSmr, donorSkinSmrs, targetSkinSmrs,
                maxNeighborDist: Configs.TopsDistancePreserveRange.Value,
                minOffset: Configs.TopsSkinMinOffset.Value,
                skinSampleRadius: Configs.TopsSkinSampleRadius.Value,
                weightFalloffOuter: Configs.TopsSkinWeightFalloff.Value,
                logTag: "TopsLoader");
            s_resolvedCache[cacheKey] = resolved;
            // s_resolvedAppliedIds には resolved (補正後) Mesh の InstanceID のみを記録する。
            // donor 元 mesh の ID は入らないため、scene 跨ぎで Set を維持しても新 target の swap 直後
            // (topSmr.sharedMesh = donor 元 mesh) で冒頭ガードが誤発火することはない。
            if (resolved != null) s_resolvedAppliedIds.Add(resolved.GetInstanceID());
        }

        if (resolved != null) topSmr.sharedMesh = resolved;
    }

    /// <summary>
    /// 同 (instanceId, kind, wasInjected) で既にスナップショットがあれば何もしない。
    /// 呼び出し側は <c>baseKey = (InstanceId, Kind)</c> のみを渡し、内部で <c>wasInjected</c> を 3 要素目に
    /// 結合して実 key を組み立てる。これは additive モードで target 既存 SMR と inject SMR が同名で並ぶ
    /// ケース (target.mesh_costume と inject 後の mesh_costume) を一意識別するため。
    /// 初回 Apply 時のみ target SMR の元状態を保存し、後続 Restore で素状態へ戻せるようにする。
    /// </summary>
    private static void CaptureSnapshotIfFirst(
        (int InstanceId, string Kind) baseKey, bool wasInjected,
        SkinnedMeshRenderer smr, GameObject injectedGo) =>
        SmrSnapshotStore.Capture(SnapshotKind.Tops, baseKey.InstanceId, baseKey.Kind, wasInjected, smr, injectedGo);

    /// <summary>
    /// 指定 target の Tops SMR 状態を Apply 前のスナップショットへ復元する。
    /// 注入した SMR は GameObject ごと Destroy。既存 SMR は mesh / bones / materials / activeSelf を復元。
    /// 同 instance への applied フラグも解除し、再 Apply 可能にする。
    /// </summary>
    public static void RestoreFor(GameObject character)
    {
        if (character == null) return;
        var instanceId = character.GetInstanceID();
        var renderers = character.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        bool restoredAny = false;

        var entries = SmrSnapshotStore.EnumerateForInstance(SnapshotKind.Tops, instanceId);

        // BoneGrafter で植え替えた Tops 由来の bone subtree のみ Destroy (per-loader isolation)。
        BoneGrafter.DestroyGrafted(character, "TopsLoader");

        foreach (var (smrKind, isInjected, snap) in entries)
        {
            restoredAny = true;
            if (snap.WasInjected)
            {
                if (snap.InjectedGo != null)
                {
                    // Destroy は frame end 遅延のため、同フレーム内 Apply の GetComponentsInChildren が
                    // doomed SMR を拾うのを防ぐ目的で先に detach する（BoneGrafter.DestroyGrafted と同方針）。
                    snap.InjectedGo.transform.SetParent(null, false);
                    UnityEngine.Object.Destroy(snap.InjectedGo);
                }
            }
            else
            {
                var smr = renderers.FirstOrDefault(m => m.name == smrKind);
                if (smr != null)
                {
                    smr.gameObject.SetActive(snap.OriginalActive);
                    smr.enabled = snap.OriginalEnabled;
                    smr.sharedMesh = snap.OriginalMesh;
                    if (snap.OriginalBones != null) smr.bones = snap.OriginalBones;
                    if (snap.OriginalMaterials != null) smr.sharedMaterials = snap.OriginalMaterials;
                }
            }
            SmrSnapshotStore.Remove(SnapshotKind.Tops, instanceId, smrKind, isInjected);
        }
        // SkinShrinkCoordinator から Tops contribution を削除する。Bottoms 残存なら Coordinator 内で
        // skin_upper.sharedMesh を直前に戻した target 元 asset から Bottoms-only push し直す。
        // API 契約: 上記 foreach で snap.OriginalMesh を skin_upper.sharedMesh に書き戻し済みの状態で呼ぶ。
        SkinShrinkCoordinator.UnregisterTops(character);
        s_applied.Remove(instanceId);
        if (restoredAny)
            PatchLogger.LogInfo($"[TopsLoader] 復元: {character.name}");
    }

    /// <summary>
    /// TopsLoader が target に植えた / swap で touch した Bottoms 候補 SMR の GameObject InstanceID 集合。
    /// per-loader isolation 用: BottomsLoader が target 列挙からこれらを除外し TopsLoader 所有 SMR を不可視化
    /// (donor 側は非除外で両 frill 共存)。
    ///
    /// snapshot ベースで GO 単位識別。name 単位だと前回 bottoms donor で inject した同名 SMR が巻き添え除外され
    /// (c) hide で清掃されず孤児残留する。
    ///
    /// 不変条件: Apply (a)(b) は Tops 候補名のみ、(c2) のみ Bottoms 候補名を snapshot 投入で disjoint。
    /// (a)(b) で Bottoms 候補名を touch する変更時は本 API の semantic 再設計が必要 (沈黙の回帰リスク)。
    ///
    /// WasInjected=true は (c2) 新規 GameObject、=false は (c2) swap で donor mesh が焼かれた target 元 SMR
    /// (BottomsLoader が再 swap すると donor 見た目が壊れるため除外)。
    /// </summary>
    internal static IEnumerable<int> GetOwnedBottomsCandidateGoIds(GameObject character)
    {
        if (character == null) yield break;
        var charInstanceId = character.GetInstanceID();
        var entries = SmrSnapshotStore.EnumerateForInstance(SnapshotKind.Tops, charInstanceId);
        SkinnedMeshRenderer[] cachedRenderers = null;
        foreach (var (smrKind, isInjected, snap) in entries)
        {
            if (!BottomsLoader.IsBottomsCandidateName(smrKind)) continue;
            if (isInjected)
            {
                if (snap.InjectedGo != null)
                    yield return snap.InjectedGo.GetInstanceID();
            }
            else
            {
                // swap 経路: target 元 SMR を name で逆引きして GameObject を特定する。
                // GetComponentsInChildren は重いので 1 回だけ取得しキャッシュする。
                if (cachedRenderers == null)
                    cachedRenderers = character.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                var smr = cachedRenderers.FirstOrDefault(r => r != null && r.name == smrKind);
                if (smr != null)
                    yield return smr.gameObject.GetInstanceID();
            }
        }
    }

    /// <summary>
    /// target に新規 SMR を注入する。親は <paramref name="referenceName"/>（mesh_skin_upper / mesh_skin_lower）の親（同階層）、
    /// 見つからなければ character 直下。rootBone / localBounds / updateWhenOffscreen は
    /// reference SMR から流用し、frustum culling / AABB 計算を安定させる
    /// （SwimWearStockingPatch.CreateInjected / BottomsLoader.InjectSmrLogged と同方針）。
    /// Tops SMR (mesh_costume / mesh_costume_ribbon 等) は mesh_skin_upper を、
    /// Bottoms SMR (mesh_costume_skirt / pants / frill 等) は mesh_skin_lower を渡すこと。
    /// </summary>
    private static SkinnedMeshRenderer InjectSmrLogged(
        GameObject character, string name, SkinnedMeshRenderer[] renderers,
        string referenceName = "mesh_skin_upper")
    {
        var reference = renderers.FirstOrDefault(m => m.name == referenceName);
        var parent = reference != null ? reference.transform.parent : character.transform;

        if (reference == null)
            PatchLogger.LogWarning($"[TopsLoader] {referenceName} 不在で character 直下へ注入: {name}/{character.name}（描画/culling 不整合の可能性）");

        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        // Unity Layer は SetParent で継承されないため明示設定（BottomsLoader と対称、layer mismatch
        // で本体 lighting から外れて grey 描画になる bug 対応）。
        go.layer = reference != null ? reference.gameObject.layer : character.layer;
        var smr = go.AddComponent<SkinnedMeshRenderer>();
        smr.rootBone = reference != null ? reference.rootBone : character.transform;
        if (reference != null)
        {
            smr.localBounds = reference.localBounds;
            smr.updateWhenOffscreen = reference.updateWhenOffscreen;
        }

        PatchLogger.LogDebug($"[TopsLoader] {name} を注入: {character.name} (ref={referenceName})");
        return smr;
    }

    private static void SwapSmr(SkinnedMeshRenderer target, SkinnedMeshRenderer donor, GameObject character, string label) =>
        CostumeMeshSwapper.SwapSmr(target, donor, character, "TopsLoader", skipActivateForTransparentLayer: false);
}
