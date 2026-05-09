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
/// 別キャラ・別コスチュームの下衣メッシュ群（mesh_costume_* のうち skirt / pants / frill 系）を
/// ターゲットキャラへ移植する。
///
/// 設計方針 (bottoms-variable-list 計画):
///   - Wardrobe (F7) の Bottoms タブで donor (CharID + CostumeType) が選択されると
///     <see cref="PreloadDonorAsync"/> が呼ばれ、未ロードならその場で preload する。
///     並行性ガードは in-flight タスクキャッシュ (<see cref="s_inFlight"/>) で実現。
///   - <see cref="CharacterHandle.setup"/> Postfix（<see cref="BottomsSetupPatch"/>）から
///     <see cref="ApplyIfOverridden"/> を呼び、target の Bottoms 候補 SMR を donor の同名 SMR で
///     sharedMesh + bones (リマップ) + materials を差し替える。donor のみ持つ SMR は注入し、
///     target のみ持つ SMR は SetActive(false) で hide する（TopsLoader と同形）。
///   - donor 自身の setup() Postfix が走った場合は、handle.Chara が
///     <see cref="s_loaderHostRoot"/> 配下なら ApplyIfOverridden 側でガードする。
///
/// 制約 (受容):
///   - 物理ボーン (skirt_swaying_lp 等) は移植しない。target 側に同名ボーンが無ければ
///     rootBone へ fallback（KneeSocksLoader / TopsLoader と同手順）。
///   - 注入された新 SMR の rootBone は reference SMR (mesh_skin_lower) の rootBone を流用。
///     既存 SMR の swap 時は target の元 rootBone を変更しない。
///   - mesh_skin_lower の blendShape は触らない（KneeSocks と独立に共存可能）。
///   - target が SwimWear / Bunnygirl の状態でも Apply は走る。これらは VIP シーンで donor config
///     による skirt 物理注入 (TryCreateSkirtCloth + RemapColliderRefs mirror) を実施する経路で支える。
///     Bunnygirl の <c>mesh_costume_full</c> は IsBottomsCandidate で除外されるため target の bottoms
///     候補は空 → (a)(c) ループは no-op、(b) のみで donor skirt SMR が overlay 注入される。
///   - donor が Bottoms 候補を 1 件も持たない場合は target の Bottoms 候補をすべて hide する経路を走る
///     （RIN/MIUKA SwimWear のような一体型 donor で「下半身素」を意図的に選ぶ用途を維持。
///     TopsLoader は逆に Count==0 で early return するが、Bottoms は target を消しても致命的に
///     ならないため hide 経路を維持する）。
///   - 同名 SMR 重複は最初の 1 つだけ採用し警告ログ（TopsLoader と同形）。
///
/// GC ガード:
///   - donor preload 用 GameObject は <see cref="Initialize"/> で渡される pickerHost
///     （CostumeChangerPatch で DontDestroyOnLoad 済み）配下に <c>SetActive(false)</c> で配置。
///   - <see cref="DonorEntry.Handle"/> を <see cref="s_donors"/> 辞書で参照保持し
///     CharacterHandle インスタンスが GC されないようにする。
/// </summary>
public class BottomsLoader : MonoBehaviour
{
    private struct DonorEntry
    {
        public CharacterHandle Handle;                      // GC 防止のため辞書から参照保持
        public GameObject DonorParent;                      // Donor_{donor}_{costume} GameObject (MagicaCloth 等 component 解決用 host)
        public List<SkinnedMeshRenderer> AllSmrs;           // verbose ログ用全 SMR スナップショット
        public List<SkinnedMeshRenderer> BottomsSmrs;       // Bottoms 候補 (IsBottomsCandidate フィルタ後)
    }

    /// <summary>
    /// Apply 時に target SMR の元状態を保存し、Restore 時に復元するためのスナップショット。
    /// 1 instanceId × kind (= SMR 名) ごとに 1 エントリ。同 instance 上で複数回 Apply
    /// しても初回 Apply 時のみ保存し、以後は上書きしない（Restore で素の状態に戻すため）。
    /// </summary>
    private struct TargetKindSnapshot
    {
        public bool WasInjected;          // true なら Restore 時に GameObject を Destroy
        public GameObject InjectedGo;     // WasInjected 用
        public bool OriginalActive;
        public bool OriginalEnabled;      // Renderer.enabled (描画 ON/OFF)
        // OriginalMesh は addressables 所有のため Destroy されず、参照保持のみで安全に Restore できる
        // （SwimWearStockingPatch / KneeSocksLoader / TopsLoader と同方針）。
        public Mesh OriginalMesh;
        public Transform[] OriginalBones;
        public Material[] OriginalMaterials;
    }

    private static readonly Dictionary<(CharID Donor, CostumeType Costume), DonorEntry> s_donors = new();
    // key の IsInjected は additive モードで target 既存 SMR と inject SMR が同名で並ぶケースの識別用
    // (例: target LUNA SwimWear の mesh_costume_frill + donor inject の mesh_costume_frill)。
    // 通常モードでも swap (IsInjected=false) と inject (IsInjected=true) は元々 kind 集合 disjoint だが、
    // additive 導入で同名衝突が起きうるため型に組み込む (TopsLoader と同形)。
    private static readonly Dictionary<(int InstanceId, string Kind, bool IsInjected), TargetKindSnapshot> s_targetSnapshots = new();

    // 進行中の preload タスクをキーごとに共有することで、同一 (donor, costume) への
    // 重複呼出が二重 GameObject 生成や CharacterHandle.Preload 多重発火を起こさないようにする。
    // 単一スレッド (Unity main thread) 前提のため lock 不要。
    private static readonly Dictionary<(CharID Donor, CostumeType Costume), UniTask<bool>> s_inFlight = new();

    // donor preload 用のホスト GameObject。Initialize で同期生成し、pickerHost の
    // DontDestroyOnLoad を継承するためシーン遷移をまたいで生存する。
    private static GameObject s_loaderHostRoot;

    // 多重 Apply ガード用: setup() Postfix がシーン内で複数回呼ばれても、Apply 済みの
    // GameObject InstanceID なら early return する（KneeSocksLoader の snapshot 多重ガード相当）。
    private static readonly HashSet<int> s_applied = new();

    /// <summary>Initialize 完了 (= s_loaderHostRoot 生成済み)。Apply 側の警告分岐に使用。</summary>
    public static bool IsLoaded => s_loaderHostRoot != null;

    /// <summary>
    /// BottomsLoader が transplant する Bottoms 候補 SMR の name 集合を返す。
    /// per-loader isolation: TopsLoader (c2) が target 列挙からこれらを除外し
    /// BottomsLoader 所有 SMR を不可視化するために参照する。setup() Postfix race-free。
    /// donor preload 未完了 → 空集合。skirt も含む (BottomsLoader は skirt を所有する)。
    /// </summary>
    internal static IEnumerable<string> GetTransplantedBottomsKinds(CharID donorChar, CostumeType donorCostume)
    {
        if (!s_donors.TryGetValue((donorChar, donorCostume), out var donor)) yield break;
        if (donor.BottomsSmrs == null) yield break;
        foreach (var smr in donor.BottomsSmrs)
        {
            if (smr == null) continue;
            yield return smr.name;
        }
    }

    /// <summary>
    /// 指定の parent GameObject が donor preload host (s_loaderHostRoot 配下) かを判定。
    /// CostumeChangerPatch.Prefix から donor preload 経路の override 適用を抑止するために参照。
    /// </summary>
    internal static bool IsDonorPreloadParent(GameObject parent)
    {
        if (parent == null || s_loaderHostRoot == null) return false;
        return parent.transform.IsChildOf(s_loaderHostRoot.transform);
    }

    /// <summary>
    /// DIAGNOSTIC ONLY — SwimWear 物理診断用。
    /// preload 済みの donor GameObject を取得する。
    /// preload が未完了 or 失敗した場合は false を返す。
    /// </summary>
    internal static bool TryGetDonorParent(CharID donor, CostumeType costume, out GameObject parent)
    {
        if (s_donors.TryGetValue((donor, costume), out var entry) && entry.DonorParent != null)
        {
            parent = entry.DonorParent;
            return true;
        }
        parent = null;
        return false;
    }

    public static void Initialize(GameObject parent)
    {
        if (s_loaderHostRoot != null)
        {
            PatchLogger.LogWarning("[BottomsLoader] 既に Initialize 済みです");
            return;
        }
        var loader = parent.AddComponent<BottomsLoader>();
        s_loaderHostRoot = new GameObject(nameof(BottomsLoader) + "Hosts");
        s_loaderHostRoot.transform.SetParent(loader.transform, false);
        s_loaderHostRoot.SetActive(false);
        SceneManager.sceneUnloaded += OnSceneUnloaded;
        // BottomsSkinShrink 系は live tune (F9 設定パネル等) で値が変わると即時反映する。
        if (Configs.BottomsSkinShrink != null)
            Configs.BottomsSkinShrink.SettingChanged += OnBottomsSkinShrinkParamChanged;
        if (Configs.BottomsSkinShrinkFalloffRadius != null)
            Configs.BottomsSkinShrinkFalloffRadius.SettingChanged += OnBottomsSkinShrinkParamChanged;
        if (Configs.BottomsSkinShrinkSampleRadius != null)
            Configs.BottomsSkinShrinkSampleRadius.SettingChanged += OnBottomsSkinShrinkParamChanged;
        PatchLogger.LogInfo("[BottomsLoader] Initialized (lazy preload mode)");
    }

    private void OnDestroy()
    {
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
        if (Configs.BottomsSkinShrink != null)
            Configs.BottomsSkinShrink.SettingChanged -= OnBottomsSkinShrinkParamChanged;
        if (Configs.BottomsSkinShrinkFalloffRadius != null)
            Configs.BottomsSkinShrinkFalloffRadius.SettingChanged -= OnBottomsSkinShrinkParamChanged;
        if (Configs.BottomsSkinShrinkSampleRadius != null)
            Configs.BottomsSkinShrinkSampleRadius.SettingChanged -= OnBottomsSkinShrinkParamChanged;
    }

    /// <summary>
    /// Bottoms 候補判定。<c>mesh_costume_*</c> 接頭 + <c>skirt</c>/<c>pants</c>/<c>frill</c> 含む SMR。
    /// <see cref="TopsLoader.IsTopsCandidate"/> と相互排他。
    /// <c>_trp</c> 透過レイヤは VIP で hidden が原状のため除外（注入で active=true 化すると visible 化）。
    /// "mesh_costume" 単体は Tops メイン上衣なので除外。
    /// </summary>
    public static bool IsBottomsCandidate(SkinnedMeshRenderer smr)
    {
        if (smr == null) return false;
        return IsBottomsCandidateName(smr.name);
    }

    /// <summary>
    /// SMR 名から Bottoms 候補かを判定する。<see cref="IsBottomsCandidate(SkinnedMeshRenderer)"/> の名前版。
    /// TopsLoader が snapshot key (= SMR 名) から Bottoms 候補名のみを抽出する用途で参照する
    /// (per-loader isolation)。
    /// </summary>
    public static bool IsBottomsCandidateName(string n)
    {
        if (string.IsNullOrEmpty(n)) return false;
        if (!n.StartsWith("mesh_costume_", StringComparison.Ordinal)) return false;
        if (n.IndexOf("_trp", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        if (n.IndexOf("skirt", StringComparison.Ordinal) >= 0) return true;
        if (n.IndexOf("pants", StringComparison.Ordinal) >= 0) return true;
        if (n.IndexOf("frill", StringComparison.Ordinal) >= 0) return true;
        return false;
    }

    /// <summary>
    /// 指定 donor (CharID + Costume) を必要なら preload してキャッシュする。
    /// 既にキャッシュ済みなら即時 true を返す。in-flight の場合は同じ task を共有する。
    /// 戻り値は「donor が Bottoms 候補 SMR を 1 つ以上持つか」。両方無い場合は false。
    /// </summary>
    public static UniTask<bool> PreloadDonorAsync(CharID donor, CostumeType costume)
    {
        var key = (donor, costume);
        if (s_donors.TryGetValue(key, out var cached))
            return UniTask.FromResult(cached.BottomsSmrs != null && cached.BottomsSmrs.Count > 0);
        if (s_inFlight.TryGetValue(key, out var pending))
            return pending;

        // UniTaskCompletionSource は内部 continuation を multi-cast 保持するため
        // pre-completion で複数の await/Forget/ContinueWith が安全。
        // .Preserve() の MemoizeSource は OnCompleted を underlying source に forward するだけで
        // 単一 continuation slot 制約を解消しないため使用不可。
        var tcs = new UniTaskCompletionSource<bool>();
        s_inFlight[key] = tcs.Task;
        RunPreloadWorker(donor, costume, tcs).Forget();
        return tcs.Task;
    }

    private static async UniTaskVoid RunPreloadWorker(CharID donor, CostumeType costume, UniTaskCompletionSource<bool> tcs)
    {
        var key = (donor, costume);
        bool result = false;
        try
        {
            result = await PreloadDonorInternal(donor, costume);
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[BottomsLoader] preload worker 例外: {donor}/{costume}: {ex}");
        }
        finally
        {
            // TrySetResult を先に呼ぶことで、同期的に発火する continuation 中も
            // s_inFlight に entry が残り、再帰的な PreloadDonorAsync(同 key) は
            // 完了済みの tcs.Task を即座に返すため二重 worker 起動を防ぐ。
            tcs.TrySetResult(result);
            s_inFlight.Remove(key);
        }
    }

    private static async UniTask<bool> PreloadDonorInternal(CharID donor, CostumeType costume)
    {
        var key = (donor, costume);
        try
        {
            if (s_loaderHostRoot == null)
            {
                PatchLogger.LogWarning($"[BottomsLoader] preload 前に Initialize が必要: {donor}/{costume}");
                return false;
            }

            await UniTask.WaitUntil(() => GBSystem.Instance != null && GBSystem.Instance.RefSaveData() != null);

            var donorParent = new GameObject($"Donor_{donor}_{costume}");
            donorParent.transform.SetParent(s_loaderHostRoot.transform, false);
            donorParent.SetActive(false);

            var handle = new CharacterHandle(donorParent);
            handle.Preload(donor, new CharacterHandle.LoadArg { Costume = costume });
            await UniTask.WaitUntil(() => handle.IsPreloadDone());

            // await 再開後に別 caller が同 key を先に登録している場合は自分の donorParent を破棄して
            // 既存エントリに合流する（Wardrobe での donor 連打による二重 preload 防止）。
            if (s_donors.TryGetValue(key, out var raceWinner))
            {
                UnityEngine.Object.Destroy(donorParent);
                return raceWinner.BottomsSmrs != null && raceWinner.BottomsSmrs.Count > 0;
            }

            var allSmrs = donorParent.GetComponentsInChildren<SkinnedMeshRenderer>(true).ToList();
            var bottomsSmrs = allSmrs.Where(IsBottomsCandidate).ToList();
            s_donors[key] = new DonorEntry { Handle = handle, DonorParent = donorParent, AllSmrs = allSmrs, BottomsSmrs = bottomsSmrs };

            PatchLogger.LogDebug($"[BottomsLoader] lazy donor preloaded: {donor}/{costume} (allSMR={allSmrs.Count}, bottomsCandidates={bottomsSmrs.Count}, names=[{string.Join(", ", bottomsSmrs.Select(s => s.name))}])");
            return bottomsSmrs.Count > 0;
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[BottomsLoader] preload 失敗: {donor}/{costume}: {ex}");
            return false;
        }
    }

    // 全 state を scene 跨ぎで保持する (TopsLoader.OnSceneUnloaded と同方針)。
    // m_holeScene の char は env scene 切替で preserve されるため同 InstanceID で Apply trigger
    // (BottomsSetupPatch) が再発火する。s_targetSnapshots を Clear すると、scene 2 での再 Apply 時に
    // CaptureSnapshotIfFirst が現在 (= donor 補正済み) の SMR.sharedMesh を OriginalMesh として
    // 誤記録し、Wardrobe Restore が donor mesh に戻す壊れた挙動になる。s_applied / MagicaCloth
    // snapshot も同理由で保持。session 内では Unity の InstanceID は再利用されないため、旧シーンの
    // 破棄済み target に紐づく stale entry は harmless に残るのみで誤検知しない。
    // s_inFlight は意図的に保持: donorParent は DontDestroyOnLoad 配下なので scene 跨ぎでも安全。
    // SkinShrinkCoordinator.s_entries は ClearScene で破棄するが、後続 Apply で Register* が再構築する。
    private static void OnSceneUnloaded(Scene scene)
    {
        SkinShrinkCoordinator.ClearScene();
    }

    /// <summary>
    /// BottomsSkinShrink 系 Configs (<see cref="Configs.BottomsSkinShrink"/> /
    /// <see cref="Configs.BottomsSkinShrinkFalloffRadius"/> / <see cref="Configs.BottomsSkinShrinkSampleRadius"/>)
    /// 変更時にキャッシュを破棄して登録済み全 target を再適用する。
    /// picker (F7) が閉じている状態でも F9 設定パネル等から値変更があれば即時反映される。
    /// Tops は distance preserve 機構と handler 共有 (OnDistancePreserveParamChanged) だが、
    /// Bottoms は distance preserve を持たないため SkinShrink 専用 handler。
    /// </summary>
    private static void OnBottomsSkinShrinkParamChanged(object sender, System.EventArgs e)
    {
        SkinShrinkCoordinator.InvalidateCache();

        // 同伴イベント等 (env != HoleScene) で live tune が走った場合、env.FindCharacter は env 側の char しか返さず、
        // HoleScene preserved の Bar char は再 Apply 対象から外れる。InvalidateCache で destroyed Mesh となった
        // mesh_skin_* の sharedMesh を保持したまま Bar 復帰すると skin が消失するため、env と HoleScene の両方で
        // FindCharacter する (TopsLoader.OnDistancePreserveParamChanged と同方針)。
        var sys = GBSystem.Instance;
        if (sys == null) return;
        var env = sys.GetActiveEnvScene();
        var holeScene = sys.GetHoleScene();
        if (env == null && holeScene == null) return;

        // 列挙中の操作で例外が出ないよう ToList でスナップショット
        var snapshot = BottomsOverrideStore.EnumerateOverrides().ToList();
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
        // Bottoms が register されていない (Tops 単独) target の cache を InvalidateCache で消したまま
        // にしないため、全 entry を refresh して整合を取る。Bottoms 側 ApplyDirectly で touch 済 entry も
        // 重ねて refresh されるが cache hit で軽量。
        SkinShrinkCoordinator.RefreshAllByConfig();
        PatchLogger.LogDebug($"[BottomsLoader] BottomsSkinShrink param 変更 → {reapplied} 個 再適用 (登録 {snapshot.Count}, push={Configs.BottomsSkinShrink.Value:F4}m, fade={Configs.BottomsSkinShrinkFalloffRadius.Value:F4}m, sampleR={Configs.BottomsSkinShrinkSampleRadius.Value:F3}m)");
    }

    private static void TryReapply(GameObject charObj, CharID target, BottomsOverrideStore.Entry entry,
        HashSet<int> seen, ref int reapplied)
    {
        if (charObj == null) return;
        if (!seen.Add(charObj.GetInstanceID())) return;
        try
        {
            // ApplyDirectly が Apply → SkinShrinkCoordinator.RegisterBottoms を呼ぶ。Coordinator が
            // 内部で skin SMR を素 mesh に rewind してから新 param で push し直すため累積補正は防がれる。
            ApplyDirectly(charObj, entry.DonorChar, entry.DonorCostume);
            reapplied++;
        }
        catch (System.Exception ex)
        {
            PatchLogger.LogWarning($"[BottomsLoader] live tune 再適用失敗: target={target}, donor={entry.DonorChar}/{entry.DonorCostume}: {ex}");
        }
    }

    /// <summary>
    /// <see cref="CharacterHandle.setup"/> Postfix から呼ぶ。
    /// SwimWear / Bunnygirl も donor skirt 物理注入対象のため除外しない。Bunnygirl の
    /// <c>mesh_costume_full</c> は IsBottomsCandidate で除外され (a)(c) は no-op、(b) のみで overlay 注入。
    /// donor 自身の setup() Postfix（preload host 配下）は IsChildOf ガードで skip する。
    /// </summary>
    public static void ApplyIfOverridden(CharacterHandle handle)
    {
        if (handle?.Chara == null) return;

        // donor 自身の setup() Postfix を除外。preload host 配下は SetActive(false) でも setup() は走る。
        // 自分の host + TopsLoader の preload host 配下も skip 必須: 同 CharID で Bottoms override 登録時、
        // donor の skin SMR.sharedMesh が SkinShrink で transient Mesh に置換 → InvalidateCache で
        // destroyed → target Apply の swap source を巻き込み破壊する (実機 diag 2026-05-08)。
        if (s_loaderHostRoot != null && handle.Chara.transform.IsChildOf(s_loaderHostRoot.transform)) return;
        if (TopsLoader.IsDonorPreloadParent(handle.Chara)) return;

        var id = handle.GetCharID();
        if (!BottomsOverrideStore.TryGet(id, out var entry)) return;

        var donorKey = (entry.DonorChar, entry.DonorCostume);
        if (s_donors.ContainsKey(donorKey))
        {
            Apply(handle.Chara, entry.DonorChar, entry.DonorCostume);
            return;
        }

        // donor 未ロード: ExSave rehydrate 経路では Wardrobe UI の PreloadDonorAsync が走らないため
        // Apply が donor lookup で skip してしまう。先に preload を起動し、完了後に re-apply する。
        var chara = handle.Chara;
        var donorChar = entry.DonorChar;
        var donorCostume = entry.DonorCostume;
        PatchLogger.LogDebug($"[BottomsLoader] donor 未ロード、先行 preload を起動: {donorChar}/{donorCostume} (target={id})");
        PreloadDonorAsync(donorChar, donorCostume).ContinueWith(success =>
        {
            if (!success) return;
            if (chara == null) return; // Unity の == null は破棄済みを true にする
            // store 側で override が変わっている可能性があるので最新を取り直す
            if (!BottomsOverrideStore.TryGet(id, out var freshEntry)) return;
            if (freshEntry.DonorChar != donorChar || freshEntry.DonorCostume != donorCostume) return;
            Apply(chara, freshEntry.DonorChar, freshEntry.DonorCostume);
        }).Forget();
    }

    /// <summary>
    /// Wardrobe (F7) Bottoms タブ確定時に呼ぶ。reload 経由は同 costume で no-op になるためこちらを使う。
    /// 連続 donor 切替で前回 inject SMR を確実に清掃するため <see cref="RestoreFor"/> で素状態へ戻してから
    /// Apply する (TopsLoader.ApplyDirectly と同方針)。additive モード導入で snapshot key 集合が
    /// donor / target 状態依存で変動するため (例: Babydoll donor で inject した frill snapshot が
    /// Casual donor 切替時に残る)、素状態リセットが必須。
    /// RestoreFor 内で <c>s_applied.Remove</c> と <see cref="BoneGrafter.DestroyGrafted"/> も実行されるため
    /// 重複処理不要。
    /// </summary>
    public static void ApplyDirectly(GameObject character, CharID donorChar, CostumeType donorCostume)
    {
        if (character == null) return;
        RestoreFor(character);
        Apply(character, donorChar, donorCostume);
    }

    public static void Apply(GameObject character, CharID donorChar, CostumeType donorCostume)
    {
        if (character == null) return;

        // base-aware additive モード (TopsLoader.Apply と対称設計):
        //   target が SwimWear / Bunnygirl のときは target の Bottoms 候補 SMR をそのまま温存し、
        //   donor の Bottoms SMR を inject 経路で重ねる。target の元 frill 等を維持できる。
        //   donor=SwimWear (Bottoms に SwimWear donor を当てるレアケース) は従来 (a)(b)(c) で処理。
        // m_lastLoadArg が race 等で null の場合は additive=false (= 既存通常モード) にフォールバック。
        var targetHandle = ResolveTargetHandle(character);
        var targetCostume = targetHandle?.m_lastLoadArg?.Costume;
        bool additiveMode = (targetCostume == CostumeType.SwimWear || targetCostume == CostumeType.Bunnygirl)
                            && donorCostume != CostumeType.SwimWear;

        if (!s_donors.TryGetValue((donorChar, donorCostume), out var donor))
        {
            // preload 未完了 vs 本当に donor 未登録（再起動が必要）を区別してログを出す。
            // preload 完了前に setup() Postfix が走るタイミング（GBSystem 待機中など）でも
            // 誤誘導 warning を出さない。
            if (IsLoaded)
                PatchLogger.LogWarning($"[BottomsLoader] donor 未ロード: {donorChar}/{donorCostume}（map に追加した場合は再起動が必要）");
            else
                PatchLogger.LogDebug($"[BottomsLoader] preload 未完了のため Apply スキップ: {donorChar}/{donorCostume}（後続 setup を待機）");
            return;
        }

        var instanceId = character.GetInstanceID();
        if (s_applied.Contains(instanceId)) return; // 多重 Apply ガード

        // CaptureSnapshotIfFirst が SMR.sharedMesh を捕獲するより前に、target の MagicaCloth が
        // SMR に書き込んだ customMesh を originalMesh (build 時 cache asset) に巻き戻しておく。
        // これで snap.OriginalMesh が customMesh dispose の影響を受けない stable Mesh asset になる。
        MagicaClothRebuilder.NormalizeSmrMeshBeforeSwap(character);

        // donor が bottoms を一切持たない場合（例: RIN/MIUKA SwimWear はワンピース型で skirt/pants 無し）も
        // hide 経路を走らせて target の bottoms を非表示にする。
        // 「下半身を素にする」ことが意図的な選択肢として有効なため。
        // TopsLoader は逆に Count==0 で early return するが、Bottoms は target を消しても
        // 致命的にならないため hide 経路を維持する。

        // per-loader isolation: TopsLoader (c2) が SwimWear donor 経由で植えた / 上書きした
        // Bottoms 候補 SMR は BottomsLoader から透過扱い。target 側のみ除外し donor 側は除外しない
        // → LUNA frill (TopsLoader 所有) と donor frill (BottomsLoader 注入) が独立 GameObject として共存。
        //
        // GameObject InstanceID で識別 (name 単位ではない): name ベースだと BottomsLoader 自身が前回 bottoms donor で
        // inject した同名 SMR (例: 前 donor=Babydoll で frill 注入後、新 donor=Casual に切替) も誤って巻き添え除外され、
        // (c) hide 経路で清掃されず孤児として残留してしまうため。snapshot ベースで TopsLoader が touch した GO のみ識別する。
        var topsOwnedGoIds = new HashSet<int>(TopsLoader.GetOwnedBottomsCandidateGoIds(character));

        var renderers = character.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var targetBottomsList = renderers.Where(IsBottomsCandidate).ToList();

        // 同名 SMR 重複の検出。COSTUME 切替直後は古い衣装の SMR (祖先 inactive で activeInHierarchy=false な orphan) と
        // 新衣装の SMR (生身) が同名で重複するケースがある。単純な「先勝ち」だと iteration order により orphan を採用してしまい、
        // SwapSmr が orphan に donor mesh を swap → 生身 SMR は prefab mesh のまま → 物理 OFF に見える。
        // activeInHierarchy=true な SMR を優先し、無ければ activeInHierarchy=false (orphan) も拾う (fallback)。
        var targetByName = new Dictionary<string, SkinnedMeshRenderer>();
        foreach (var smr in targetBottomsList)
        {
            // TopsLoader 所有 GameObject は per-loader isolation で除外。重複検出ループにも入れず WARN 抑制。
            // BottomsLoader 自身が前回 donor で inject した同名 SMR は GO InstanceID が異なるためここで除外されず、
            // 後続 (a)(b)(c) ループで通常通り処理される (donor 切替で清掃される)。
            if (topsOwnedGoIds.Contains(smr.gameObject.GetInstanceID())) continue;
            if (!targetByName.TryGetValue(smr.name, out var existing))
            {
                targetByName[smr.name] = smr;
                continue;
            }
            // 既存が orphan で新しいのが生身 → 入替
            if (!existing.gameObject.activeInHierarchy && smr.gameObject.activeInHierarchy)
            {
                targetByName[smr.name] = smr;
            }
            else
            {
                PatchLogger.LogWarning($"[BottomsLoader] target 同名 Bottoms SMR 重複: {character.name}/{smr.name}（採用={targetByName[smr.name].gameObject.activeInHierarchy}/skipped={smr.gameObject.activeInHierarchy}）");
            }
        }
        var donorByName = new Dictionary<string, SkinnedMeshRenderer>();
        if (donor.BottomsSmrs != null)
        {
            foreach (var smr in donor.BottomsSmrs)
            {
                if (!donorByName.ContainsKey(smr.name))
                    donorByName[smr.name] = smr;
            }
        }

        if (PatchLogger.IsDebugEnabled)
        {
            PatchLogger.LogDebug($"[BottomsLoader] target={character.name} mode={(additiveMode ? "additive" : "swap")} bottoms candidates: {(targetByName.Count == 0 ? "(none)" : string.Join(", ", targetByName.Keys))}");
            if (additiveMode)
            {
                // additive 経路では target/donor 同名でも target を温存し donor 側を inject 経路で追加する。
                // common 集合は「target 既存と並ぶ overlay」として表示。swap/hide は走らないので独立列挙はしない。
                var donorAll = donorByName.Keys.OrderBy(s => s).ToList();
                var overlapping = donorByName.Keys.Intersect(targetByName.Keys).OrderBy(s => s).ToList();
                PatchLogger.LogDebug($"[BottomsLoader] inject (all donor, additive): {(donorAll.Count == 0 ? "(none)" : string.Join(", ", donorAll))}");
                PatchLogger.LogDebug($"[BottomsLoader] overlap with target (kept as-is): {(overlapping.Count == 0 ? "(none)" : string.Join(", ", overlapping))}");
            }
            else
            {
                var common = donorByName.Keys.Intersect(targetByName.Keys).OrderBy(s => s).ToList();
                var donorOnly = donorByName.Keys.Except(targetByName.Keys).OrderBy(s => s).ToList();
                var targetOnly = targetByName.Keys.Except(donorByName.Keys).OrderBy(s => s).ToList();
                PatchLogger.LogDebug($"[BottomsLoader] swap (common): {(common.Count == 0 ? "(none)" : string.Join(", ", common))}");
                PatchLogger.LogDebug($"[BottomsLoader] inject (donor-only): {(donorOnly.Count == 0 ? "(none)" : string.Join(", ", donorOnly))}");
                PatchLogger.LogDebug($"[BottomsLoader] hide (target-only): {(targetOnly.Count == 0 ? "(none)" : string.Join(", ", targetOnly))}");
            }
        }

        bool didSomething = false;
        // BottomsSkinShrink 用に swap した skirt SMR ペアを捕捉する。
        // 通常モードは (a) 既存 swap + (b) inject swap の両方が含まれ、additive モードは (b) inject のみ。
        // (target SMR, donor 元 SMR) のペアを後段で skin shrink に渡す。
        var swappedBottomsPairs = new List<(SkinnedMeshRenderer Target, SkinnedMeshRenderer Donor)>();

        if (additiveMode)
        {
            // additive: target の Bottoms SMR は全て温存し、donor の Bottoms SMR を全て inject 経路で追加する。
            // 同名 SMR が並ぶケース (target.mesh_costume_frill と donor.mesh_costume_frill) でも snapshot key の
            // IsInjected=true で識別され、Restore は InjectedGo 参照で Destroy するため名前衝突しない。
            // (a) swap / (c) hide はスキップ。target frill 等を「元 SwimWear のまま温存する」のが本モードの目的。
            foreach (var kv in donorByName)
            {
                var injected = InjectSmrLogged(character, kv.Key, renderers);
                CaptureSnapshotIfFirst((instanceId, kv.Key), wasInjected: true, smr: null, injectedGo: injected.gameObject);
                SwapSmr(injected, kv.Value, character, kv.Key + "(injected,additive)");
                swappedBottomsPairs.Add((injected, kv.Value));
                didSomething = true;
            }
        }
        else
        {
            // (a) 共通: target 既存 SMR に donor の sharedMesh / bones / materials を swap
            foreach (var kv in donorByName)
            {
                if (!targetByName.TryGetValue(kv.Key, out var targetSmr)) continue;
                CaptureSnapshotIfFirst((instanceId, kv.Key), wasInjected: false, smr: targetSmr, injectedGo: null);
                SwapSmr(targetSmr, kv.Value, character, kv.Key);
                swappedBottomsPairs.Add((targetSmr, kv.Value));
                didSomething = true;
            }

            // (b) donor のみ持つ: target に新規 SMR を注入して swap
            foreach (var kv in donorByName)
            {
                if (targetByName.ContainsKey(kv.Key)) continue;
                var injected = InjectSmrLogged(character, kv.Key, renderers);
                CaptureSnapshotIfFirst((instanceId, kv.Key), wasInjected: true, smr: null, injectedGo: injected.gameObject);
                SwapSmr(injected, kv.Value, character, kv.Key + "(injected)");
                swappedBottomsPairs.Add((injected, kv.Value));
                didSomething = true;
            }

            // (c) target のみ持つ: donor の Bottoms 構成に整合させるため hide
            // donor.BottomsSmrs が空のケースもこの経路で全 hide される（一体型 donor 用途）。
            // 注: additive モードでは (c) を skip して target frill 等を温存する (上記 if 分岐参照)。
            foreach (var kv in targetByName)
            {
                if (donorByName.ContainsKey(kv.Key)) continue;
                // 既に inactive ならログ抑制（冪等）
                if (!kv.Value.gameObject.activeSelf) continue;
                CaptureSnapshotIfFirst((instanceId, kv.Key), wasInjected: false, smr: kv.Value, injectedGo: null);
                kv.Value.gameObject.SetActive(false);
                PatchLogger.LogDebug($"[BottomsLoader] target の {kv.Key} を隠す: {character.name}（donor 側に無いため）");
                didSomething = true;
            }
        }

        // (e) BottomsSkinShrink: target の mesh_skin_lower / mesh_skin_upper を skirt より内側へ push して skin 突き抜けを解消。
        // SkinShrinkCoordinator が Tops contribution と統合管理し、両 SMR を素 mesh に rewind してから
        // 両 contribution を順次 push するため、Tops/Bottoms 同時適用や片方 Restore で他方が崩れない。
        // skirt mesh / MagicaCloth 物理は touch しないため RebindMagicaClothIfActive との順序は問わない。
        if (didSomething && swappedBottomsPairs.Count > 0)
        {
            SkinShrinkCoordinator.RegisterBottoms(
                character,
                swappedBottomsPairs.Select(p => p.Target),
                Configs.BottomsSkinShrink?.Value ?? 0f,
                Configs.BottomsSkinShrinkFalloffRadius?.Value ?? 0f,
                Configs.BottomsSkinShrinkSampleRadius?.Value ?? 0f);
        }
        else
        {
            // (c) の hide のみ (donor が bottoms 持たない RIN/MIUKA SwimWear) → 古い Bottoms contribution が
            // あれば削除し、Tops も無ければ skin SMR を素 mesh に戻す。
            SkinShrinkCoordinator.UnregisterBottoms(character);
        }

        // didSomething に関わらず Applied 登録（冪等性確保、毎フレーム再走査回避）。
        // s_applied は SceneManager.sceneUnloaded で Clear されるので新シーンでは再評価される。
        s_applied.Add(instanceId);
        if (didSomething)
        {
            PatchLogger.LogInfo($"[BottomsLoader] 適用: {character.name} ← {donorChar}/{donorCostume}");
            // didSomething=true は SMR.bones / sharedMesh / sharedMaterials のいずれかを実書換えしたことを
            // 必ず含意する。bones 入れ替えなしなら MagicaCloth binding ずれも発生しないため rebind 不要。
            RebindMagicaClothIfActive(character, donor);
        }
    }

    private static System.Type s_magicaClothType;
    private static bool s_magicaClothTypeResolveAttempted;

    /// <summary>
    /// <c>MagicaCloth2.MagicaCloth</c> 型を AppDomain から動的解決する (DLL 直接 reference を避けるため)。
    /// 初回呼出でキャッシュ。型未解決時は null で再走査もしない (試行 1 回限り)。
    /// </summary>
    private static System.Type ResolveMagicaClothType()
    {
        if (s_magicaClothType != null) return s_magicaClothType;
        if (s_magicaClothTypeResolveAttempted) return null;
        s_magicaClothTypeResolveAttempted = true;
        foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType("MagicaCloth2.MagicaCloth", false);
                if (t != null) { s_magicaClothType = t; return t; }
            }
            catch { /* 一部 dynamic assembly で GetType 例外あり、無視 */ }
        }
        PatchLogger.LogDebug("[BottomsLoader] MagicaCloth2 type 未解決 (1 回限り走査)");
        return null;
    }

    /// <summary>
    /// Bottoms swap 後の skirt MeshCloth proxy mesh 乖離を <see cref="MagicaClothRebuilder.RebuildSkirtCloth"/>
    /// で解決する。SMR.bones swap の Transform ずれは BoneCloth (Hair/Breast/Ribbon) には影響せず、
    /// skirt MeshCloth のみ proxy mesh 再生成が必要なため donor serializeData で完全再 build。
    /// <c>activeSelf</c> ガード: Bar / Ahhn 中など物理 disable シーンで触らない構造的防御。
    /// </summary>
    private static void RebindMagicaClothIfActive(GameObject character, DonorEntry donor)
    {
        var magicaRoot = character.transform.Find("MagicaCloth");
        if (magicaRoot == null || !magicaRoot.gameObject.activeSelf) return;

        MagicaClothRebuilder.RebuildSkirtCloth(character, donor.DonorParent);
    }

    /// <summary>
    /// 同 (instanceId, kind, wasInjected) で既にスナップショットがあれば何もしない。
    /// 初回 Apply 時のみ target SMR の元状態を保存し、後続 Restore で素状態へ戻せるようにする。
    /// 呼び出し側は <c>baseKey = (InstanceId, Kind)</c> のみを渡し、内部で <c>wasInjected</c> を
    /// 3 要素目に組み込んで保存する (TopsLoader と同形)。
    /// </summary>
    private static void CaptureSnapshotIfFirst(
        (int InstanceId, string Kind) baseKey, bool wasInjected,
        SkinnedMeshRenderer smr, GameObject injectedGo)
    {
        var key = (baseKey.InstanceId, baseKey.Kind, wasInjected);
        if (s_targetSnapshots.ContainsKey(key)) return;
        var snap = new TargetKindSnapshot
        {
            WasInjected = wasInjected,
            InjectedGo = injectedGo,
        };
        if (smr != null)
        {
            snap.OriginalActive = smr.gameObject.activeSelf;
            snap.OriginalEnabled = smr.enabled;
            snap.OriginalMesh = smr.sharedMesh;
            // bones / sharedMaterials は defensive copy（target.bones = donor.bones で上書きされるため
            // 元配列インスタンスがそのまま残らないと restore で donor の値を戻すことになる）
            snap.OriginalBones = smr.bones != null ? (Transform[])smr.bones.Clone() : null;
            snap.OriginalMaterials = smr.sharedMaterials != null ? (Material[])smr.sharedMaterials.Clone() : null;
        }
        s_targetSnapshots[key] = snap;
    }

    /// <summary>
    /// 指定 target の Bottoms SMR 状態を Apply 前のスナップショットへ復元する。
    /// 注入した SMR は GameObject ごと Destroy。既存 SMR は mesh / bones / materials / activeSelf を復元。
    /// 同 instance への applied フラグも解除し、再 Apply 可能にする（TopsLoader.RestoreFor と同形）。
    /// </summary>
    public static void RestoreFor(GameObject character)
    {
        if (character == null) return;
        var instanceId = character.GetInstanceID();
        var renderers = character.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        bool restoredAny = false;

        // ToList でスナップショットを keys を一旦コピー（foreach 中の Remove で例外を避ける）
        var keysForThisInstance = s_targetSnapshots.Keys.Where(k => k.InstanceId == instanceId).ToList();

        // BoneGrafter で植え替えた Bottoms 由来の bone subtree のみ Destroy (per-loader isolation)。
        // Restore 前に行うことで snapshot の OriginalBones (元 bone 配列) を smr.bones に戻したあとも
        // grafted bone が残らない。
        BoneGrafter.DestroyGrafted(character, "BottomsLoader");

        foreach (var key in keysForThisInstance)
        {
            if (!s_targetSnapshots.TryGetValue(key, out var snap)) continue;
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
                var smr = renderers.FirstOrDefault(m => m.name == key.Kind);
                if (smr != null)
                {
                    smr.gameObject.SetActive(snap.OriginalActive);
                    smr.enabled = snap.OriginalEnabled;
                    smr.sharedMesh = snap.OriginalMesh;
                    if (snap.OriginalBones != null) smr.bones = snap.OriginalBones;
                    if (snap.OriginalMaterials != null) smr.sharedMaterials = snap.OriginalMaterials;
                }
            }
            s_targetSnapshots.Remove(key);
        }

        // BottomsSkinShrink で書き換えた mesh_skin_lower / mesh_skin_upper を SkinShrinkCoordinator に
        // 通知する。Coordinator が contribution を削除し、Tops も無ければ素 mesh に戻す。Tops 残存なら
        // Tops contribution だけで refresh される。MagicaCloth rebuild は cloth 側のみで skin 状態に
        // 依存しないので順序は弱依存だが、慣習として「SMR 復旧 → Coordinator 同期 → cloth rebuild」の順。
        SkinShrinkCoordinator.UnregisterBottoms(character);

        s_applied.Remove(instanceId);
        if (restoredAny)
        {
            PatchLogger.LogInfo($"[BottomsLoader] 復元: {character.name}");
            // SMR を素状態に戻したあと、Apply 時に置換した MagicaCloth_Skirt も元 config で再 build する。
            // snapshot 無 (一度も rebuild してない) なら no-op で安全。
            MagicaClothRebuilder.RestoreSkirtCloth(character);
        }
    }

    /// <summary>
    /// target に新規 Bottoms SMR を注入する。親は mesh_skin_lower の親（reference 無なら character 直下）。
    /// rootBone / localBounds / updateWhenOffscreen は reference SMR から流用し frustum culling/AABB を
    /// 安定化（SwimWearStockingPatch.CreateInjected と同方針）。SwapSmr 単独経路は rootBone 不変更。
    /// </summary>
    /// <param name="renderers">
    /// 呼び出し側で取得済みの SMR スナップショット (reference 検索のみ)。注入後 SMR は含まれない点に注意。
    /// </param>
    private static SkinnedMeshRenderer InjectSmrLogged(
        GameObject character, string name, SkinnedMeshRenderer[] renderers)
    {
        // parent 選択優先順位:
        //   1. 既存 bottoms 系 SMR (mesh_costume_*) の parent
        //      → 衣装ごとに mesh_costume_* が "Erisa/costume/" 等のサブ container 配下にあるケースで、
        //        注入 SMR を同 container 配下に置くことで MagicaCloth_Skirt の MeshCloth proxy が
        //        正しく構築される (異なる parent transform 混在で simulation が走らない症状を回避)。
        //   2. mesh_skin_lower の parent (fallback、衣装に bottoms 系 SMR が一切無い場合)
        //   3. character 直下 (最終 fallback)
        // rootBone / localBounds / updateWhenOffscreen は同じく reference から継承。
        var costumeRef = renderers.FirstOrDefault(m =>
            m != null && m.name != null && m.name.StartsWith("mesh_costume_", StringComparison.Ordinal));
        var skinRef = renderers.FirstOrDefault(m => m != null && m.name == "mesh_skin_lower");
        var reference = costumeRef ?? skinRef;
        var parent = reference != null ? reference.transform.parent : character.transform;

        if (reference == null)
            PatchLogger.LogWarning($"[BottomsLoader] reference SMR 不在で character 直下へ注入: {name}/{character.name}（描画/culling 不整合の可能性）");

        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        // Unity Layer は SetParent で継承されないため明示設定。Layer 0 (Default) のままだと
        // ゲーム本体のキャラ向け lighting / camera culling mask (本タイトルは layer 8) から外れて
        // 環境光だけでレンダリングされ、フリル等が grey っぽく描画される bug の真因。
        go.layer = reference != null ? reference.gameObject.layer : character.layer;
        var smr = go.AddComponent<SkinnedMeshRenderer>();
        smr.rootBone = reference != null ? reference.rootBone : character.transform;
        if (reference != null)
        {
            // localBounds / updateWhenOffscreen のデフォルトだと frustum culling で
            // 描画フレームが落ちる可能性があるため reference から継承する。
            smr.localBounds = reference.localBounds;
            smr.updateWhenOffscreen = reference.updateWhenOffscreen;
        }

        PatchLogger.LogDebug($"[BottomsLoader] {name} を注入: {character.name} (parent={(parent != null ? parent.name : "<null>")}, refBy={(costumeRef != null ? "costume" : skinRef != null ? "skin_lower" : "none")})");
        return smr;
    }

    private static void SwapSmr(SkinnedMeshRenderer target, SkinnedMeshRenderer donor, GameObject character, string label)
    {
        // ボーン対応付け: キャラ階層内の Transform を name(小文字) でインデックス化し、
        // donor SMR の bone 名で名前マップ。未一致は target の rootBone（無ければキャラルート）へ fallback。
        var bones = new Dictionary<string, Transform>();
        foreach (var b in character.GetComponentsInChildren<Transform>(true))
            bones[b.name.ToLowerInvariant()] = b;

        // donor 固有 bone (SwimWear の skirt_*_SW 等) は target に存在せず、
        // 親追従だけだと bindpose ずれで verts が体外に飛ぶ。
        // BoneGrafter で正規化マッピング (target standard 骨へ) と graft (Transform-only clone) を
        // 行ってから bone 名解決すれば bindpose × bone.localToWorld が donor bind 時と整合し正しく描画される。
        BoneGrafter.ResolveAndGraft(donor, character, bones, "BottomsLoader");

        var fallback = target.rootBone ?? character.transform;
        var donorBones = donor.bones ?? Array.Empty<Transform>();
        var mappedBones = donorBones
            .Select(b =>
            {
                if (b == null) return fallback;
                if (bones.TryGetValue(b.name.ToLowerInvariant(), out var t)) return t;
                return fallback;
            })
            .ToArray();

        // _trp 系 SMR (透過レイヤ、e.g. mesh_costume_skirt_trp) は通常 hidden が原状で、
        // FittingRoom Panties 閲覧モード等の特定 UI でのみ ENABLED される。FixMod が強制表示すると
        // MagicaCloth_Skirt の MeshCloth 駆動対象外 (sourceRenderers 不在 + customSkinningSetting.enable=false)
        // のため rest pose 固定の透過 skirt が visible 化し VIP で bug として露呈する。
        // 元の Active/Enabled 状態を維持し、強制 ON はその他の SMR にのみ適用する。
        var isTransparentLayer = target.gameObject.name.IndexOf("_trp", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!isTransparentLayer)
        {
            target.gameObject.SetActive(true);
            // 元 SMR が Renderer.enabled=false で隠されているケース (target 衣装に該当 bottoms が無い costume等)
            // でも描画されるよう強制 true。snapshot.OriginalEnabled で Restore は元に戻る。
            target.enabled = true;
        }
        target.sharedMesh = donor.sharedMesh;
        target.bones = mappedBones;
        // donor の Material を「共有」する意図的な代入。donor は preload host 配下で SetActive(false)
        // のため通常変更されない。ゲーム本体が donor.sharedMaterials[i] を直接書き換える経路があれば
        // 全 target に伝播する点に注意（A1 から不変、SwimWearStockingPatch も同方針）。
        target.sharedMaterials = donor.sharedMaterials;

        // 注入経路では InjectSmrLogged で rootBone を必ず設定するが、想定外の経路で null のまま
        // ここに到達した場合の防御として、bones 設定後に rootBone null を fallback で埋める。
        // 既存 SMR の swap (rootBone 既設) では fallback と一致しないため上書きせず A1 互換。
        if (target.rootBone == null) target.rootBone = fallback;
    }

    /// <summary>
    /// env と HoleScene の m_characters から character の <see cref="CharacterHandle"/> を逆引き解決する。
    /// 失敗時は null。additive モード判定 (<see cref="Apply"/> 内 targetCostume 取得) に使用。
    /// 同伴イベント等 (env != HoleScene) で HoleScene preserved char に対し <see cref="ApplyDirectly"/> が
    /// 走るケースで env のみ走査だと targetCostume 解決失敗 → additive モード判定が誤り、Bunnygirl ガードも
    /// 効かない問題を解消。
    /// TODO: <see cref="TopsLoader"/> 側にも同等ロジックがあるため、共有 helper (CharaResolver 等) に
    /// 統合する。重複は本 2 件以内に留め、3 件目を入れる前に抽出リファクタを実施する。
    /// </summary>
    private static CharacterHandle ResolveTargetHandle(GameObject character)
    {
        var sys = GBSystem.Instance;
        if (sys == null) return null;
        var env = sys.GetActiveEnvScene();
        var handle = env?.m_characters?.FirstOrDefault(x => x != null && x.Chara == character);
        if (handle != null) return handle;
        var holeScene = sys.GetHoleScene();
        if (ReferenceEquals(holeScene, env)) return null;
        return holeScene?.m_characters?.FirstOrDefault(x => x != null && x.Chara == character);
    }
}
