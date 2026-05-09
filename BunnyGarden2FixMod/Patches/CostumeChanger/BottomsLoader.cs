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
    private static readonly Dictionary<(int InstanceId, string Kind), TargetKindSnapshot> s_targetSnapshots = new();

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
        PatchLogger.LogInfo("[BottomsLoader] Initialized (lazy preload mode)");
    }

    private void OnDestroy()
    {
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
    }

    /// <summary>
    /// Bottoms 候補判定。<c>mesh_costume_*</c> で始まり、名前に <c>skirt</c> / <c>pants</c> /
    /// <c>frill</c> のいずれかを含む SMR を Bottoms とみなす。<see cref="TopsLoader.IsTopsCandidate"/>
    /// と相互排他。
    /// 例: mesh_costume_skirt / mesh_costume_skirt_sensitivemode /
    ///     mesh_costume_skirtfrill / mesh_costume_pants / mesh_costume_frill → Bottoms。
    ///     mesh_costume / mesh_costume_ribbon / mesh_costume_sleeve → Tops（false を返す）。
    ///     mesh_costume_skirt_trp 等の <c>_trp</c> 透過レイヤは FittingRoom Panties 閲覧専用で
    ///     VIP では常に hidden が原状のため除外（候補に含めると injection 経路で active=true で
    ///     注入され rest pose で透過 skirt が visible 化する）。
    /// "mesh_costume" 単体（接頭辞 + 区切り文字無し）は Tops のメイン上衣なので除外。
    /// </summary>
    public static bool IsBottomsCandidate(SkinnedMeshRenderer smr)
    {
        if (smr == null) return false;
        var n = smr.name;
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

        // UniTask は完了後にプールへ戻り単一 await のみ許可されるため、複数 caller が
        // 同 key の in-flight task を再 await できるよう Preserve() で multi-await 可にする。
        // (https://github.com/Cysharp/UniTask/issues/93)
        var task = PreloadDonorInternal(donor, costume).Preserve();
        s_inFlight[key] = task;
        return task;
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

            PatchLogger.LogInfo($"[BottomsLoader] lazy donor preloaded: {donor}/{costume} (allSMR={allSmrs.Count}, bottomsCandidates={bottomsSmrs.Count}, names=[{string.Join(", ", bottomsSmrs.Select(s => s.name))}])");
            return bottomsSmrs.Count > 0;
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[BottomsLoader] preload 失敗: {donor}/{costume}: {ex}");
            return false;
        }
        finally
        {
            s_inFlight.Remove(key);
        }
    }

    // シーン遷移で target GameObject が破棄されると次シーンで InstanceID が再採番されるため、
    // Applied フラグを Clear して新シーンの target に対して Apply が再発火するようにする。
    // donor 側の s_donors はキャッシュ保持（KneeSocksLoader と同じ）。
    private static void OnSceneUnloaded(Scene scene)
    {
        s_applied.Clear();
        // 新シーンでは target GameObject の InstanceID が再採番されるためスナップショットも無効化。
        // 注入された SMR は target GameObject の破棄に追随して破棄されるので追加処理不要。
        s_targetSnapshots.Clear();
        MagicaClothRebuilder.ClearAllSnapshots();
        // s_inFlight は意図的に保持: donorParent は DontDestroyOnLoad 配下なので scene 跨ぎでも安全。
        // in-flight の task を Clear すると完了後に s_donors へ登録されず次 Apply で再 preload が走る。
    }

    /// <summary>
    /// <see cref="CharacterHandle.setup"/> Postfix から呼ぶ。
    /// SwimWear / Bunnygirl は VIP で donor config による skirt 物理注入 (TryCreateSkirtCloth +
    /// RemapColliderRefs mirror) を行うため除外しない。Bunnygirl の <c>mesh_costume_full</c> は
    /// <see cref="IsBottomsCandidate"/> で除外されるため (a)(c) ループは no-op、(b) のみで donor
    /// skirt SMR が overlay 注入されて Bunnygirl 全身 SMR は hide されない。
    /// donor 自身が Bunnygirl の場合は <c>donor.BottomsSmrs</c> も空 (IsBottomsCandidate で除外) のため
    /// (a)(b)(c) いずれも no-op。target Bottoms 候補も空なので hide も走らず安全に何もしない。
    /// donor 自身の setup() Postfix（preload host 配下）は IsChildOf ガードで skip する。
    /// </summary>
    public static void ApplyIfOverridden(CharacterHandle handle)
    {
        if (handle?.Chara == null) return;

        // donor 自身の setup() Postfix が走るケースを除外。preload host 配下の
        // GameObject は SetActive(false) で配置されるため通常は描画されないが、
        // setup() 自体は呼ばれるためここで弾く（in-flight 並行 preload 下でも安全）。
        if (s_loaderHostRoot != null && handle.Chara.transform.IsChildOf(s_loaderHostRoot.transform)) return;

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
        PatchLogger.LogInfo($"[BottomsLoader] donor 未ロード、先行 preload を起動: {donorChar}/{donorCostume} (target={id})");
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
    /// Wardrobe (F7) Bottoms タブ確定時に呼ぶ。target の既存 GameObject に対して再適用許可フラグを
    /// セットしてから <see cref="Apply"/> する。reload 経由 (env.LoadCharacter) は同 costume だと
    /// no-op で setup() Postfix が発火しないためこちらを使う。
    ///
    /// 同一 target に対して donor 切替で連続呼び出しされても、<see cref="CaptureSnapshotIfFirst"/>
    /// の ContainsKey ガードにより snapshot は **最初の Apply 以前の素状態** を保持し続ける。
    /// このため <see cref="RestoreFor"/> を間に挟まずに連続切替しても、最終的な Restore は
    /// 一貫して素状態へ戻せる（donor A → donor B → Restore で donor A の中間状態が漏れない）。
    /// </summary>
    public static void ApplyDirectly(GameObject character, CharID donorChar, CostumeType donorCostume)
    {
        if (character == null) return;
        s_applied.Remove(character.GetInstanceID());
        // donor 切替で Bottoms 由来の grafted clone subtree が孤児として残るのを防ぐ。
        // snapshot は素状態を保持し続けるが、grafted bone は補助構造で snapshot 独立のため
        // 都度 destroy → 再 graft して整合を取る。
        // owner = "BottomsLoader" でフィルタし Tops の graft は触らない (per-loader isolation)。
        BoneGrafter.DestroyGrafted(character, "BottomsLoader");
        Apply(character, donorChar, donorCostume);
    }

    public static void Apply(GameObject character, CharID donorChar, CostumeType donorCostume)
    {
        if (character == null) return;
        if (!s_donors.TryGetValue((donorChar, donorCostume), out var donor))
        {
            // preload 未完了 vs 本当に donor 未登録（再起動が必要）を区別してログを出す。
            // preload 完了前に setup() Postfix が走るタイミング（GBSystem 待機中など）でも
            // 誤誘導 warning を出さない。
            if (IsLoaded)
                PatchLogger.LogWarning($"[BottomsLoader] donor 未ロード: {donorChar}/{donorCostume}（map に追加した場合は再起動が必要）");
            else
                PatchLogger.LogInfo($"[BottomsLoader] preload 未完了のため Apply スキップ: {donorChar}/{donorCostume}（後続 setup を待機）");
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

        var renderers = character.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var targetBottomsList = renderers.Where(IsBottomsCandidate).ToList();

        // 同名 SMR 重複の検出。COSTUME 切替直後は古い衣装の SMR (祖先 inactive で activeInHierarchy=false な orphan) と
        // 新衣装の SMR (生身) が同名で重複するケースがある。単純な「先勝ち」だと iteration order により orphan を採用してしまい、
        // SwapSmr が orphan に donor mesh を swap → 生身 SMR は prefab mesh のまま → 物理 OFF に見える。
        // activeInHierarchy=true な SMR を優先し、無ければ activeInHierarchy=false (orphan) も拾う (fallback)。
        var targetByName = new Dictionary<string, SkinnedMeshRenderer>();
        foreach (var smr in targetBottomsList)
        {
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

        bool didSomething = false;

        // (a) 共通: target 既存 SMR に donor の sharedMesh / bones / materials を swap
        foreach (var kv in donorByName)
        {
            if (!targetByName.TryGetValue(kv.Key, out var targetSmr)) continue;
            CaptureSnapshotIfFirst((instanceId, kv.Key), wasInjected: false, smr: targetSmr, injectedGo: null);
            SwapSmr(targetSmr, kv.Value, character, kv.Key);
            didSomething = true;
        }

        // (b) donor のみ持つ: target に新規 SMR を注入して swap
        foreach (var kv in donorByName)
        {
            if (targetByName.ContainsKey(kv.Key)) continue;
            var injected = InjectSmrLogged(character, kv.Key, renderers);
            CaptureSnapshotIfFirst((instanceId, kv.Key), wasInjected: true, smr: null, injectedGo: injected.gameObject);
            SwapSmr(injected, kv.Value, character, kv.Key + "(injected)");
            didSomething = true;
        }

        // (c) target のみ持つ: donor の Bottoms 構成に整合させるため hide
        // donor.BottomsSmrs が空のケースもこの経路で全 hide される（一体型 donor 用途）。
        foreach (var kv in targetByName)
        {
            if (donorByName.ContainsKey(kv.Key)) continue;
            // 既に inactive ならログ抑制（冪等）
            if (!kv.Value.gameObject.activeSelf) continue;
            CaptureSnapshotIfFirst((instanceId, kv.Key), wasInjected: false, smr: kv.Value, injectedGo: null);
            kv.Value.gameObject.SetActive(false);
            PatchLogger.LogInfo($"[BottomsLoader] target の {kv.Key} を隠す: {character.name}（donor 側に無いため）");
            didSomething = true;
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
        PatchLogger.LogInfo("[BottomsLoader] MagicaCloth2 type 未解決 (1 回限り走査)");
        return null;
    }

    /// <summary>
    /// Bottoms swap 後、skirt 系 MeshCloth の proxy mesh が build 時 cache のまま乖離して
    /// 物理が止まる問題を解決するため <see cref="MagicaClothRebuilder.RebuildSkirtCloth"/> に委譲する。
    ///
    /// 設計: SMR.bones の入れ替えに伴う Transform 参照ずれは Hair/Breast/Ribbon 等
    /// (BoneCloth) では発生しない (Bottoms swap は skirt SMR の bones だけ touch するため)。
    /// 唯一影響を受ける skirt 系 MeshCloth は単なる Transform 補正では再駆動できない
    /// (proxy mesh 自体を再生成する必要あり) ため、donor 側 serializeData で完全再 build する。
    ///
    /// <c>activeSelf</c> ガード: Bar / Ahhn 中など物理 disable 状態のシーンで意図せず触らないよう
    /// 構造的に防ぐ。
    /// </summary>
    private static void RebindMagicaClothIfActive(GameObject character, DonorEntry donor)
    {
        var magicaRoot = character.transform.Find("MagicaCloth");
        if (magicaRoot == null || !magicaRoot.gameObject.activeSelf) return;

        MagicaClothRebuilder.RebuildSkirtCloth(character, donor.DonorParent);
    }

    /// <summary>
    /// 同 (instanceId, kind) で既にスナップショットがあれば何もしない。
    /// 初回 Apply 時のみ target SMR の元状態を保存し、後続 Restore で素状態へ戻せるようにする。
    /// </summary>
    private static void CaptureSnapshotIfFirst(
        (int InstanceId, string Kind) key, bool wasInjected,
        SkinnedMeshRenderer smr, GameObject injectedGo)
    {
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
    /// target に新規 Bottoms SMR を注入する。親は mesh_skin_lower の親（同階層）。
    /// reference SMR が見つからなければ character 直下。
    /// rootBone / localBounds / updateWhenOffscreen は reference SMR (mesh_skin_lower) から流用し、
    /// frustum culling や AABB 計算が安定するようにする（SwimWearStockingPatch.CreateInjected と同方針）。
    /// 既存 SMR を swap する経路（SwapSmr 単独）は rootBone を変更しない。
    /// </summary>
    /// <param name="renderers">
    /// 呼び出し側で取得済みの SMR スナップショット。reference SMR 検索目的のみで使用する。
    /// 注入された SMR はこの配列に含まれない（古いスナップショット）ことに注意。
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

        PatchLogger.LogInfo($"[BottomsLoader] {name} を注入: {character.name} (parent={(parent != null ? parent.name : "<null>")}, refBy={(costumeRef != null ? "costume" : skinRef != null ? "skin_lower" : "none")})");
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
}
