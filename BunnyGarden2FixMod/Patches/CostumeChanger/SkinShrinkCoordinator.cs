using BunnyGarden2FixMod.Utils;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// Tops / Bottoms 双方の SkinShrink (skin SMR を cloth より内側へ push する補正) を統合し、
/// character 単位で素 skin Mesh と cloth contribution を保持して順次 push する。
///
/// 動機: 旧設計は <see cref="TopsLoader"/> と <see cref="BottomsLoader"/> がそれぞれ
/// `s_skinShrinkCache` / `s_skinShrinkOriginalMeshes` を持ち独立に skin SMR.sharedMesh を
/// 上書きしていた。両 override 同時適用すると以下の干渉が起きる:
///   - Bottoms apply が skin_upper を上書きして Tops 側の z-fighting 解消が消える
///   - 片方を Restore すると他方の登録済 originalMesh が stale となり Unity null 化で skin 描画消失
/// 本 Coordinator は素 mesh を 1 箇所で管理し、Refresh 時に両 skin SMR を素状態に rewind して
/// 全 contribution を順次 push し直すため上記干渉が起きない。
///
/// 呼び出し経路:
///   - <see cref="TopsLoader.Apply"/> 終端で <see cref="RegisterTops"/>
///   - <see cref="BottomsLoader.Apply"/> 終端で <see cref="RegisterBottoms"/>
///   - <see cref="TopsLoader.RestoreFor"/> 終端で <see cref="UnregisterTops"/>
///   - <see cref="BottomsLoader.RestoreFor"/> 終端で <see cref="UnregisterBottoms"/>
///   - 両 Loader の live tune handler から <see cref="InvalidateCache"/> + per-target
///     ApplyDirectly (これが内部で Register* を呼ぶ) で再同期
///   - <see cref="OnSceneUnloaded"/> 経路で <see cref="ClearScene"/>
///
/// 設計上の挙動 (素 mesh の意味):
///   - mesh_skin_upper の素は Tops 有無で異なる:
///       Tops 有: target Babydoll asset (Tops Apply (d) で swap 済み)
///       Tops 無: target 元 costume asset
///   - mesh_skin_lower の素は常に target 元 costume asset (誰も swap しない)
///   - Bottoms 単独 → Tops 後追い override で skin_upper の素が target 元 → Babydoll に切替わる。
///     これは Tops 設計に内在する挙動で、Coordinator 化で初めて起きるわけではない。
///
/// API 契約:
///   - <see cref="UnregisterTops"/> 呼出前に呼出元は mesh_skin_upper.sharedMesh を
///     target 元 costume asset (= Tops snapshot.OriginalMesh) に戻し終えていること。
///     <see cref="TopsLoader.RestoreFor"/> 末尾で呼ぶのはこの前提を満たす。
///   - <see cref="RegisterTops"/> 呼出時に mesh_skin_upper.sharedMesh が Babydoll asset
///     になっていること (Tops Apply (d) 完了済)。Apply 末尾で呼ぶのはこの前提を満たす。
///   - すべての Register/Unregister は character GameObject の component が live な状態で呼ぶ
///     (<see cref="ClearScene"/> 後は呼ばない)。
///
/// skin_lower 側に対称な API (RestoreSkinLowerToOriginal 相当) は **意図的に未提供**:
/// <see cref="TopsLoader"/>/<see cref="BottomsLoader"/> どちらも Apply で skin_lower SMR を
/// CaptureSnapshotIfFirst していないため、transient pushed Mesh が snapshot に焼き込まれる
/// 経路がない (本 Coordinator が skin_lower.sharedMesh を transient で書き換えても snapshot 化
/// されないので live tune cycle で destroyed Mesh を sharedMesh に書き戻す事故は起きない)。
/// 将来 BottomsLoader.Apply 等で skin_lower の snapshot capture を追加する場合は対称 API も
/// 追加すること。
/// </summary>
internal static class SkinShrinkCoordinator
{
    private struct Contribution
    {
        public List<SkinnedMeshRenderer> ClothSmrs;
        public float SkinPush;
        public float FalloffR;
        public float SampleR;
    }

    private struct Entry
    {
        // Live tune の handler が cache invalidate 後に全 entry を再 push し直すために必要。
        // s_entries の key (instanceId) からは GameObject を逆引きできないため Entry 側で保持する。
        // Unity-null 比較で破棄済 GameObject を検出して RefreshAllByConfig 内で skip。
        public GameObject Character;
        public Mesh OriginalSkinUpper;
        public Mesh OriginalSkinLower;
        public Contribution Tops;
        public Contribution Bottoms;
        public bool HasTops;
        public bool HasBottoms;
    }

    private static readonly Dictionary<int, Entry> s_entries = new();

    // cache key 解説:
    //   prevSkinId    = push 直前の skin SMR.sharedMesh.GetInstanceID() (1 step 入力)
    //   donorClothId  = cloth SMR.sharedMesh.GetInstanceID()
    //   yQ/fQ/srQ     = push/falloff/sample の量子化値 (param 同値で cache hit)
    //   srcTag        = 0=Tops, 1=Bottoms (params が src で異なるため必須)
    //   kindTag       = 0=mesh_skin_upper push, 1=mesh_skin_lower push
    //                   (Bottoms 経路で 1 つの skirt mesh を upper/lower 両方に push するため必須)
    // InstanceID 安全性: Unity の Mesh.GetInstanceID() は process 内で monotonic 採番のため、
    // InvalidateCache で Destroy 済み Mesh の InstanceID が新規 Mesh で再利用される確率は事実上ゼロ。
    // 元 TopsLoader/BottomsLoader.s_skinShrinkCache と同等の安全性を継承。
    // s_entries.Values の OriginalSkinUpper/Lower は addressables 共有 asset のため Destroy されず
    // InstanceID は永続。よって Original* に対する key は完全安定。
    private static readonly Dictionary<(int prevSkinId, int donorClothId, int yQ, int fQ, int srQ, int srcTag, int kindTag), Mesh> s_cache = new();

    // Anchor は非 skin 系 (face/eye) を優先する。upper/lower 互いを anchor から外すことで
    // waist 縫い目 (上下境界) で push が anchor 距離 0 → skinScale=0 にフェードする現象を解消。
    // upper/lower それぞれが同じ face/eye anchor を見るため、両 SMR で対応する境界頂点が
    // 同等の anchor 距離を得て自然に連続する。
    // face/eye SMR が不在のキャラ (Bunnygirl 等の可能性) では skin系 anchor (互いを除外) に
    // fallback して全頂点 full push 暴走を防ぐ。
    private static readonly string[] s_faceBoundarySmrNames = new[]
    {
        "mesh_face",
        "mesh_eye",
        "mesh_face_blush_high",
    };
    private static readonly HashSet<string> s_fallbackExcludeUpper = new() { "mesh_skin_upper" };
    private static readonly HashSet<string> s_fallbackExcludeLower = new() { "mesh_skin_lower" };

    public static void RegisterTops(GameObject character, IEnumerable<SkinnedMeshRenderer> topClothSmrs,
        float skinPush, float falloffR, float sampleR)
    {
        if (character == null) return;
        int id = character.GetInstanceID();
        var smrs = topClothSmrs?.Where(s => s != null).ToList() ?? new List<SkinnedMeshRenderer>();
        if (!s_entries.TryGetValue(id, out var e)) e = default;

        e.Character = character;
        e.HasTops = true;
        e.Tops = new Contribution
        {
            ClothSmrs = smrs,
            SkinPush = skinPush,
            FalloffR = falloffR,
            SampleR = sampleR,
        };

        // Originals: Tops Apply (d) で skin_upper を Babydoll に swap した直後に呼ばれる前提。
        // 既存 entry が Bottoms 単独経路で target 元 mesh を OriginalSkinUpper に格納していた可能性が
        // あるため、Tops Register は **強制的に** 現 sharedMesh (= Babydoll) で上書きする。
        // skin_lower は誰も swap しないので未捕捉なら現 sharedMesh を捕捉、既存値があれば据置。
        var renderers = character.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var su = renderers.FirstOrDefault(r => r != null && r.name == "mesh_skin_upper");
        if (su != null && su.sharedMesh != null) e.OriginalSkinUpper = su.sharedMesh;
        if (e.OriginalSkinLower == null)
        {
            var sl = renderers.FirstOrDefault(r => r != null && r.name == "mesh_skin_lower");
            if (sl != null && sl.sharedMesh != null) e.OriginalSkinLower = sl.sharedMesh;
        }

        s_entries[id] = e;
        RefreshOne(id, character, renderers);
    }

    public static void RegisterBottoms(GameObject character, IEnumerable<SkinnedMeshRenderer> bottomsClothSmrs,
        float skinPush, float falloffR, float sampleR)
    {
        if (character == null) return;
        int id = character.GetInstanceID();
        var smrs = bottomsClothSmrs?.Where(s => s != null).ToList() ?? new List<SkinnedMeshRenderer>();
        if (!s_entries.TryGetValue(id, out var e)) e = default;

        e.Character = character;
        e.HasBottoms = true;
        e.Bottoms = new Contribution
        {
            ClothSmrs = smrs,
            SkinPush = skinPush,
            FalloffR = falloffR,
            SampleR = sampleR,
        };

        // Bottoms は skin SMR を swap しない。entry に originals が既にあれば据置 (Tops 由来の Babydoll を尊重)。
        var renderers = character.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        if (e.OriginalSkinUpper == null)
        {
            var su = renderers.FirstOrDefault(r => r != null && r.name == "mesh_skin_upper");
            if (su != null && su.sharedMesh != null) e.OriginalSkinUpper = su.sharedMesh;
        }
        if (e.OriginalSkinLower == null)
        {
            var sl = renderers.FirstOrDefault(r => r != null && r.name == "mesh_skin_lower");
            if (sl != null && sl.sharedMesh != null) e.OriginalSkinLower = sl.sharedMesh;
        }

        s_entries[id] = e;
        RefreshOne(id, character, renderers);
    }

    public static void UnregisterTops(GameObject character)
    {
        if (character == null) return;
        int id = character.GetInstanceID();
        if (!s_entries.TryGetValue(id, out var e)) return;
        e.HasTops = false;
        e.Tops = default;

        // Bottoms 残存: TopsLoader.RestoreFor が直前に skin_upper.sharedMesh を target 元 costume asset に
        // 戻した前提。OriginalSkinUpper を null 化してから現 sharedMesh で再捕捉することで、
        // 「Tops 有時の素 = Babydoll」「Tops 無時の素 = target 元 asset」の遷移を吸収する。
        // 安全弁: live tune cycle で snap.OriginalMesh が destroyed transient を指していた場合、
        // RestoreFor で sharedMesh が Unity-null になっている。`su.sharedMesh != null` チェックは
        // Unity-null も False で弾くため、destroyed Mesh を OriginalSkinUpper に再捕獲してしまう
        // 事故は起きない (e.OriginalSkinUpper は null のまま温存される)。RefreshOne 内 L220 の
        // null check で rewind も skip され、後続の Apply (d) が RestoreSkinUpperToOriginal +
        // SwapSmr で正常に復旧する。
        var renderers = character.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        e.OriginalSkinUpper = null;
        var su = renderers.FirstOrDefault(r => r != null && r.name == "mesh_skin_upper");
        if (su != null && su.sharedMesh != null) e.OriginalSkinUpper = su.sharedMesh;
        s_entries[id] = e;
        // entry は s_entries に残す (Has* 両方 false でも OriginalSkin* は保持して、live tune
        // cycle 中の InvalidateCache → 再 Register で transient pushed Mesh を「素」と誤捕獲する事故を
        // 防ぐ)。両 Has=false の empty entry は RefreshOne で rewind だけ走り無害、scene unload の
        // ClearScene または character destroy 時の RefreshAllByConfig cleanup で除去される。
        RefreshOne(id, character, renderers);
    }

    public static void UnregisterBottoms(GameObject character)
    {
        if (character == null) return;
        int id = character.GetInstanceID();
        if (!s_entries.TryGetValue(id, out var e)) return;
        e.HasBottoms = false;
        e.Bottoms = default;
        s_entries[id] = e;
        // UnregisterTops と同方針で entry 保持。RefreshOne が rewind + 残存 contribution の push を行う
        // (HasTops も false なら no-op rewind のみ)。
        var renderers = character.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        RefreshOne(id, character, renderers);
    }

    /// <summary>
    /// 指定 character の skin SMR を素 mesh に rewind し、登録された contribution を順次 push し直す。
    /// 順序: Tops contribution (skin_upper のみ) → Bottoms contribution (skin_lower → skin_upper)。
    /// </summary>
    private static void RefreshOne(int charId, GameObject character, SkinnedMeshRenderer[] renderers)
    {
        if (character == null) return;
        if (!s_entries.TryGetValue(charId, out var e)) return;
        var skinUpper = renderers.FirstOrDefault(r => r != null && r.name == "mesh_skin_upper");
        var skinLower = renderers.FirstOrDefault(r => r != null && r.name == "mesh_skin_lower");

        // 1. Rewind to originals (素 asset 参照のみ。Destroy 不要、addressables 共有で永続)。
        if (skinUpper != null && e.OriginalSkinUpper != null) skinUpper.sharedMesh = e.OriginalSkinUpper;
        if (skinLower != null && e.OriginalSkinLower != null) skinLower.sharedMesh = e.OriginalSkinLower;

        // 2. Anchor 一括収集 (rewind 直後に固定化、push 中に anchor が変わらないように)。
        // face/eye 系を優先、不在なら skin系互いを除外する旧仕様に fallback (Bunnygirl 暴走防止)。
        // upper/lower 共通の anchor を使う: 互いに anchor から外すと waist 縫い目で push が連続化。
        var anchor = CollectFaceBoundaryAnchorVerts(renderers);
        Vector3[] anchorForUpper = anchor;
        Vector3[] anchorForLower = anchor;
        if (anchor.Length == 0)
        {
            // face/eye 不在 fallback: 旧 skin系 anchor (互いを除外)
            anchorForUpper = (skinUpper != null && skinUpper.sharedMesh != null)
                ? TopsLoader.CollectSkinShrinkAnchorVerts(renderers, s_fallbackExcludeUpper) : null;
            anchorForLower = (skinLower != null && skinLower.sharedMesh != null)
                ? TopsLoader.CollectSkinShrinkAnchorVerts(renderers, s_fallbackExcludeLower) : null;
            PatchLogger.LogDebug($"[SkinShrinkCoord] face/eye anchor 不在、skin系 fallback 経路 ({character.name})");
        }

        int totalSteps = 0;

        // 3. Tops contribution → skin_upper 先、続いて skin_lower push (waist / 長め Tops が
        //    腰下に達するワンピース型 donor 対策、Bottoms と対称化)。
        if (e.HasTops && e.Tops.SkinPush > 0f)
        {
            if (skinUpper != null && skinUpper.sharedMesh != null)
                totalSteps += ApplyContribution(skinUpper, "mesh_skin_upper", anchorForUpper, e.Tops, srcTag: 0, kindTag: 0);
            if (skinLower != null && skinLower.sharedMesh != null)
                totalSteps += ApplyContribution(skinLower, "mesh_skin_lower", anchorForLower, e.Tops, srcTag: 0, kindTag: 1);
        }

        // 4. Bottoms contribution → skin_lower 先、続いて skin_upper push (waist 部分)
        if (e.HasBottoms && e.Bottoms.SkinPush > 0f)
        {
            if (skinLower != null && skinLower.sharedMesh != null)
                totalSteps += ApplyContribution(skinLower, "mesh_skin_lower", anchorForLower, e.Bottoms, srcTag: 1, kindTag: 1);
            if (skinUpper != null && skinUpper.sharedMesh != null)
                totalSteps += ApplyContribution(skinUpper, "mesh_skin_upper", anchorForUpper, e.Bottoms, srcTag: 1, kindTag: 0);
        }

        if (totalSteps > 0)
            PatchLogger.LogDebug($"[SkinShrinkCoord] refresh: {totalSteps} step (hasTops={e.HasTops}, hasBottoms={e.HasBottoms}, anchorVerts={anchor.Length}, {character.name})");
    }

    /// <summary>
    /// face/eye 系 SMR の頂点を anchor として収集する。skin系 (mesh_skin_upper / lower) は意図的に
    /// 除外し、waist 縫い目で push が anchor 距離 0 → skinScale=0 にフェードする現象を解消する。
    /// 配列が空なら呼出側で skin系 anchor 経路に fallback する責務 (Bunnygirl 等 face SMR 不在向け)。
    /// </summary>
    private static Vector3[] CollectFaceBoundaryAnchorVerts(SkinnedMeshRenderer[] renderers)
    {
        var list = new List<Vector3>();
        foreach (var r in renderers)
        {
            if (r == null || r.sharedMesh == null) continue;
            bool match = false;
            for (int i = 0; i < s_faceBoundarySmrNames.Length; i++)
            {
                if (r.name == s_faceBoundarySmrNames[i]) { match = true; break; }
            }
            if (match) list.AddRange(r.sharedMesh.vertices);
        }
        return list.ToArray();
    }

    /// <summary>
    /// 1 つの contribution を 1 つの skin SMR に対して累積適用する。
    /// 各 cloth SMR ごとに <see cref="MeshPenetrationResolver.Resolve"/> を呼び、結果を skin SMR.sharedMesh に挿す。
    /// 同 cloth mesh の重複は呼出内 HashSet で 1 回に絞る。
    /// </summary>
    private static int ApplyContribution(SkinnedMeshRenderer skinSmr, string skinLabel, Vector3[] anchorVerts,
        Contribution c, int srcTag, int kindTag)
    {
        if (c.ClothSmrs == null || c.ClothSmrs.Count == 0) return 0;
        int yQ = Mathf.RoundToInt(c.SkinPush * 10_000f);
        int fQ = Mathf.RoundToInt(c.FalloffR * 10_000f);
        int srQ = Mathf.RoundToInt(c.SampleR * 1_000f);
        var seenInThisCall = new HashSet<int>();
        int stepsApplied = 0;

        // Invariant: c.ClothSmrs は Register 時に swappedTopsPairs / swappedBottomsPairs から構築されるため
        // 意図的 hide (case (c) "target のみ持つ → hide") の SMR は含まれない。よって activeInHierarchy
        // ガードは不要 (むしろ setup() Postfix 時点で character 親が SetActive(false) で hierarchy 上
        // inactive な場合に false 評価され、初回ロードで全 cloth が skip → push 0 step になる症状を踏んだ)。
        // push は Mesh data の static 計算なので cloth が今 visible でなくても pre-push しておけば
        // 後で active 化した時に正しく描画される。
        foreach (var clothSmr in c.ClothSmrs)
        {
            if (clothSmr == null || clothSmr.sharedMesh == null) continue;
            var donorMesh = clothSmr.sharedMesh;
            if (!seenInThisCall.Add(donorMesh.GetInstanceID())) continue;

            var currentSkin = skinSmr.sharedMesh;
            var key = (currentSkin.GetInstanceID(), donorMesh.GetInstanceID(), yQ, fQ, srQ, srcTag, kindTag);
            if (!s_cache.TryGetValue(key, out var pushedSkin) || pushedSkin == null)
            {
                var pair = MeshPenetrationResolver.Resolve(
                    donorMesh: donorMesh, referenceMesh: currentSkin, referenceShape: null,
                    minOffset: 0f, skinPushAmount: c.SkinPush,
                    skinAnchorVerts: anchorVerts, skinFalloffRadius: c.FalloffR,
                    logTag: $"SkinShrinkCoord({(srcTag == 0 ? "Tops" : "Bottoms")}/{skinLabel})",
                    useSkinNormalForPush: true,
                    clothSampleRadius: c.SampleR,
                    useScatterPush: true);
                pushedSkin = pair.skin;
                s_cache[key] = pushedSkin;
            }
            if (pushedSkin != null)
            {
                skinSmr.sharedMesh = pushedSkin;
                stepsApplied++;
            }
        }
        return stepsApplied;
    }

    /// <summary>
    /// 指定 character の skin_upper SMR を、Coordinator が保持する stable な原 mesh
    /// (<c>e.OriginalSkinUpper</c>) に書き戻す。<see cref="TopsLoader"/> の Apply (d)
    /// で <c>CaptureSnapshotIfFirst</c> 直前に呼ぶ用途。
    ///
    /// 動機: ApplyDirectly 経路では <see cref="TopsLoader.RestoreFor"/> → <see cref="UnregisterTops"/>
    /// の <see cref="RefreshOne"/> が HasBottoms 残存時に Bottoms contribution を skin_upper に
    /// push して sharedMesh を transient pushed Mesh で上書きする。直後の Apply (d) で
    /// <c>CaptureSnapshotIfFirst</c> がその transient Mesh を <c>OriginalMesh</c> として焼き込むと、
    /// 次の slider 変更で <c>InvalidateCache</c> が transient を Destroy → RestoreFor が destroyed
    /// Mesh を sharedMesh に戻す → skin_upper 描画破損 という連鎖が起きる。本 API で capture 直前に
    /// stable asset へ rewind することで snapshot が常に addressables 由来 asset を指すよう保つ。
    ///
    /// no-op 条件: character/smr null、entry 未登録 (初回 Apply / ClearScene 直後)、
    /// OriginalSkinUpper 未捕捉、既に一致。
    /// </summary>
    internal static void RestoreSkinUpperToOriginal(GameObject character, SkinnedMeshRenderer skinUpperSmr)
    {
        if (character == null || skinUpperSmr == null) return;
        int id = character.GetInstanceID();
        if (!s_entries.TryGetValue(id, out var e)) return;
        if (e.OriginalSkinUpper == null) return;
        // `==` は UnityEngine.Object overload。skinUpperSmr.sharedMesh が destroyed (Unity-null) の
        // 場合 e.OriginalSkinUpper (alive non-null) との比較は False を返すため、destroyed Mesh は
        // 確実に下行で alive asset に書き戻される。
        if (skinUpperSmr.sharedMesh == e.OriginalSkinUpper) return;
        skinUpperSmr.sharedMesh = e.OriginalSkinUpper;
    }

    public static void ClearScene()
    {
        s_entries.Clear();
        // s_cache は scene 跨ぎで保持 (TopsLoader.OnSceneUnloaded と同方針 — 加算読込時の Unity null 化回避)。
        // GPU memory cleanup は InvalidateCache (param 変更経由) に集約。
    }

    /// <summary>
    /// 登録済み全 entry を Refresh する (live tune handler 用)。
    /// Tops/Bottoms 一方の handler が <see cref="InvalidateCache"/> 後に自分の override store だけ
    /// 再 register するだけだと、もう一方しか override されていない target が destroyed Mesh を
    /// 参照したまま孤児化してしまう。本メソッドが s_entries 全件を refresh して整合を取る。
    /// 破棄済み GameObject (Unity-null) は entry を削除する。
    /// </summary>
    public static void RefreshAllByConfig()
    {
        if (s_entries.Count == 0) return;
        var deadIds = new List<int>();
        // 列挙中の操作で例外を避けるため key を ToList でスナップショット。
        foreach (var id in s_entries.Keys.ToList())
        {
            if (!s_entries.TryGetValue(id, out var e)) continue;
            if (e.Character == null)
            {
                deadIds.Add(id);
                continue;
            }
            var renderers = e.Character.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            RefreshOne(id, e.Character, renderers);
        }
        foreach (var id in deadIds) s_entries.Remove(id);
    }

    public static void InvalidateCache()
    {
        foreach (var m in s_cache.Values)
            if (m != null) Object.Destroy(m);
        s_cache.Clear();
    }
}
