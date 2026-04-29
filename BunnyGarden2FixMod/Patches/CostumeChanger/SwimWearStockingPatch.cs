using System.Collections.Generic;
using System.Linq;
using BunnyGarden2FixMod.Utils;
using GB.Game;
using GB.Scene;
using HarmonyLib;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// 水着コスチューム着用中、CharacterHandle.ApplyStocking は IsDisableStocking で早期 return し
/// ストッキングが一切適用されない。本パッチは Prefix で水着を検出し、async をスキップして
/// 同期的にストッキングを適用／解除する。
///
/// 実装:
///   1. StockingsDonorLoader が Uniform prefab からキャラ別に mesh_stockings / mesh_skin_lower をキャッシュ
///   2. 水着キャラ配下に新 GameObject "mesh_stockings" を注入（bone リマップ）
///   3. 水着の mesh_skin_lower / mesh_skin_lower_foot に blendShape の delta を nearest-neighbor で移植
///      - Uniform donor mesh_skin_lower の skin_stocking / skin_socks / skin_stocking_lower blendShape デルタを
///        swim 各頂点に対して「Uniform 側の最近傍頂点のデルタ」として転写する
///      - 頂点数が違っても適用できる（Uniform 2193 → swim 1238〜1304 や 892〜946）
///      - mesh 構造は swim のまま保持。material/UV/外形シルエットが崩れない
///   4. blendShape weight を 100 に設定 → 脚が少し細くなり stocking にフィット
///
/// 順序: HarmonyPriority.First で他の ApplyStocking Prefix より先に走らせ、水着時は return false で打ち切る。
/// </summary>
[HarmonyPatch(typeof(CharacterHandle), nameof(CharacterHandle.ApplyStocking))]
[HarmonyPriority(Priority.First)]
public static class SwimWearStockingPatch
{
    private const string InjectedName = "mesh_stockings";

    private static readonly string[] s_transplantShapeNames = new[]
    {
        "blendShape_skin_lower.skin_stocking",
        "blendShape_skin_lower.skin_socks",
        "blendShape_skin_lower.skin_stocking_lower",
    };

    /// <summary>水着キャラの lower 系メッシュを差し替え前に保存。</summary>
    private class SwimwearBackup
    {
        public SkinnedMeshRenderer LowerSmr;
        public Mesh LowerOriginalMesh;
        public SkinnedMeshRenderer LowerFootSmr;
        public Mesh LowerFootOriginalMesh;
    }

    private static readonly Dictionary<int, SwimwearBackup> s_backups = new();

    // (swimMeshInstanceId, charId, isKneeSocks, shapeFalloffQ) → nearest-neighbor 移植済みメッシュ
    // isKneeSocks=true の場合は skin_kneehigh ドナーも含めて移植する。
    // shapeFalloffQ は ConfigStockingShapeFalloffRadius を 0.1mm 量子化した値で、
    // skin_stocking 系 blendShape の per-vertex フェード量に応じて別キャッシュを保持する。
    private static readonly Dictionary<(int, int, bool, int), Mesh> s_transplantedCache = new();

    // (donorMeshInstanceId, swimSkinMeshInstanceId, isKneeSocks, x_q, y_q, f_q)
    // → 食い込み解消済みの (donor stocking, swim skin) ペア。MeshPenetrationResolver の出力をキャッシュ。
    private static readonly Dictionary<(int, int, bool, int, int, int), (Mesh donor, Mesh skin)> s_resolvedCache = new();

    // resolve 適用済み Mesh の InstanceID。SMR が既に補正済 mesh を持っている場合の二重補正防止。
    private static readonly System.Collections.Generic.HashSet<int> s_resolvedAppliedIds = new();

    static bool Prepare()
    {
        bool enabled = Configs.CostumeChangerEnabled?.Value ?? true;
        if (enabled) PatchLogger.LogInfo("[SwimWearStockingPatch] 適用");
        return enabled;
    }

    /// <summary>
    /// シーンアンロード時にキャッシュを一掃する。StockingsDonorLoader MonoBehaviour 側から呼ばれる。
    /// 破棄済み SMR 参照を持ち越すと次シーンでの復元時に fake-null 例外 / InstanceID 再利用による
    /// 誤 hit が発生するため、毎シーンでリセットする。
    /// </summary>
    internal static void OnSceneUnloaded()
    {
        // Mesh オブジェクト自体は Destroy しない:
        //   水着キャラ GameObject が DontDestroyOnLoad 等でシーンを跨いで生存するケースがあり、
        //   その SMR が transplanted/resolved mesh を参照している。ここで Destroy すると
        //   次シーンで SMR が fake-null mesh を抱えて肌が消える。
        //   キャッシュ辞書だけクリアし、Mesh は SMR が手放した時点で Unity GC に回収させる。
        int transplantedCount = s_transplantedCache.Count;
        s_transplantedCache.Clear();

        int resolvedCount = s_resolvedCache.Count;
        s_resolvedCache.Clear();
        s_resolvedAppliedIds.Clear();

        // s_backups.LowerOriginalMesh / LowerFootOriginalMesh はゲーム本体所有の sharedMesh のため
        // Destroy せず参照のみクリアする（Destroy するとゲーム側のメッシュが消えて破綻する）。
        s_backups.Clear();
        PatchLogger.LogInfo($"[SwimWearStockingPatch] シーンアンロード: cache クリア (transplanted {transplantedCount} 件、resolved {resolvedCount} 件)");
    }

    /// <summary>
    /// チューニングスライダー等から ConfigStockingOffset/SkinShrink/FalloffRadius を変更した直後に呼び、
    /// 次の env.ApplyStockings() で食い込み解消が新パラメータで再構築されるよう状態を整える。
    ///
    /// 復元手順:
    ///   1. swim skin の sharedMesh を backup originalMesh に戻す（次の TransplantInto が
    ///      vanilla skin から transplanted を構築できるようにする）
    ///   2. s_resolvedAppliedIds をクリア（ApplyPenetrationResolve の二重補正防止 early-return を解除）
    ///
    /// s_resolvedCache / s_transplantedCache は破棄しない:
    ///   - resolvedCache キーには量子化パラメータが含まれるので、別パラメータでは別エントリが作られる。
    ///     同一パラメータに戻したときの再ヒットを許容するためそのまま保持する（OnSceneUnloaded で一括破棄）。
    ///   - 注入 stockings smr の sharedMesh は ApplyStockingSync 冒頭で donor.sharedMesh に
    ///     必ず差し戻されるため、ここで触らなくて良い。
    /// </summary>
    internal static void InvalidateForReapply(CharID id)
    {
        int key = (int)id;
        if (s_backups.TryGetValue(key, out var backup))
        {
            if (backup.LowerSmr != null && backup.LowerOriginalMesh != null)
                backup.LowerSmr.sharedMesh = backup.LowerOriginalMesh;
            if (backup.LowerFootSmr != null && backup.LowerFootOriginalMesh != null)
                backup.LowerFootSmr.sharedMesh = backup.LowerFootOriginalMesh;
        }
        s_resolvedAppliedIds.Clear();
    }

    private static bool Prefix(CharacterHandle __instance, int __0)
    {
        if (__instance == null || __instance.Chara == null || __instance.m_lastLoadArg == null) return true;
        if (__instance.m_lastLoadArg.Costume != CostumeType.SwimWear) return true;
        if (KneeSocksLoader.IsPreloading) return true;

        var charId = __instance.GetCharID();

        // 水着×ストッキングは CostumeChanger の StockingOverrideStore 経由でのみ適用する。
        // override が無いと vanilla の IsDisableStocking 相当（= ストッキング無し）。
        // （m_lastLoadArg.Stocking に他コスチューム由来の値が残っていても無視する）
        // override 未設定 or override=0（デフォルト／ストッキング無し）は本パッチでは何もせず
        // 注入済みメッシュだけ掃除する。vanilla の IsDisableStocking 相当の挙動に委ねる。
        bool hasOverride = StockingOverrideStore.TryGet(charId, out var overrideType)
                           && overrideType != 0;
        bool isKneeSocks = hasOverride && StockingOverrideStore.IsKneeSocksType(overrideType);

        if (Configs.DisableStockings.Value) { hasOverride = false; isKneeSocks = false; }

        if (!hasOverride)
        {
            ClearStockingSync(__instance);
            __instance.m_lastLoadArg.Stocking = 0;
            return false;
        }

        if (!StockingsDonorLoader.IsReady)
        {
            PatchLogger.LogWarning($"[SwimWearStockingPatch] donor 未 Ready（{charId}）、通常 async に委譲");
            return true;
        }

        if (!StockingsDonorLoader.TryGetDonor(charId, out var donor))
        {
            PatchLogger.LogWarning($"[SwimWearStockingPatch] donor 未キャッシュ: {charId}");
            return true;
        }

        // KneeSocks 系は KneeSocksLoader のプリロードが必要
        if (isKneeSocks && !KneeSocksLoader.IsLoaded)
        {
            PatchLogger.LogWarning($"[SwimWearStockingPatch] KneeSocks 未ロード（{charId}）、通常 async に委譲");
            return true;
        }

        if (!ApplyStockingSync(__instance, overrideType, donor, isKneeSocks))
        {
            PatchLogger.LogWarning($"[SwimWearStockingPatch] ApplyStockingSync 失敗: {charId} override={overrideType} knee={isKneeSocks}");
            return true;
        }

        // CostumeChangerPatch に合わせ、KneeSocks 系は m_lastLoadArg.Stocking=0 として記録
        __instance.m_lastLoadArg.Stocking = isKneeSocks ? 0 : overrideType;
        return false;
    }

    private static void ClearStockingSync(CharacterHandle handle)
    {
        var chara = handle.Chara;
        if (chara == null) return;

        int key = (int)handle.GetCharID();

        // 注入 mesh_stockings を削除
        var injected = chara.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .FirstOrDefault(m => m.name == InjectedName);
        if (injected != null)
        {
            Object.Destroy(injected.gameObject);
            PatchLogger.LogInfo($"[SwimWearStockingPatch] 注入 mesh_stockings 削除: {handle.GetCharID()}");
        }

        // sharedMesh を元に戻す（SMR が破棄済みなら Unity == が true で skip される）
        if (s_backups.TryGetValue(key, out var backup))
        {
            if (backup.LowerSmr != null && backup.LowerOriginalMesh != null)
                backup.LowerSmr.sharedMesh = backup.LowerOriginalMesh;
            if (backup.LowerFootSmr != null && backup.LowerFootOriginalMesh != null)
                backup.LowerFootSmr.sharedMesh = backup.LowerFootOriginalMesh;
            s_backups.Remove(key);
            PatchLogger.LogInfo($"[SwimWearStockingPatch] sharedMesh 復元: {handle.GetCharID()}");
        }
    }

    private static bool ApplyStockingSync(CharacterHandle handle, int overrideType, SkinnedMeshRenderer donor, bool isKneeSocks)
    {
        var chara = handle.Chara;
        if (chara == null) return false;

        var renderers = chara.GetComponentsInChildren<SkinnedMeshRenderer>(true);

        // 目標メッシュ/ボーン/マテリアルを overrideType から決定
        Mesh targetMesh;
        Transform[] targetBonesSrc;
        Transform targetRootBoneSrc;
        Material targetMat;

        if (isKneeSocks)
        {
            var kneeSocks = KneeSocksLoader.KneeSocksSmr;
            if (kneeSocks == null || kneeSocks.sharedMesh == null)
            {
                PatchLogger.LogWarning("[SwimWearStockingPatch] KneeSocks SMR 未ロード");
                return false;
            }
            targetMesh = kneeSocks.sharedMesh;
            targetBonesSrc = kneeSocks.bones;
            targetRootBoneSrc = kneeSocks.rootBone;
            targetMat = KneeSocksLoader.GetMaterialForOverride(overrideType);
        }
        else
        {
            targetMesh = donor.sharedMesh;
            targetBonesSrc = donor.bones;
            targetRootBoneSrc = donor.rootBone;
            targetMat = StockingsDonorLoader.GetMaterial(overrideType);
        }

        // 1. 注入 SMR を取得 or 生成（生成時は空の GO+SMR。mesh/bones は下で統一的に設定）
        var existing = renderers.FirstOrDefault(m => m.name == InjectedName);
        SkinnedMeshRenderer smr;
        bool created = existing == null;
        if (!created)
        {
            smr = existing;
        }
        else
        {
            smr = CreateInjected(chara);
            if (smr == null) return false;
        }

        // 2. mesh + bones を常にセット（新規は初回、既存は type 変更時のみ差替え）
        if (smr.sharedMesh != targetMesh)
        {
            smr.sharedMesh = targetMesh;
            var boneDict = BuildBoneDict(chara);
            RemapBonesInto(smr, targetBonesSrc, targetRootBoneSrc, boneDict, chara.transform);
        }

        if (targetMat != null) smr.sharedMaterial = targetMat;
        else PatchLogger.LogWarning($"[SwimWearStockingPatch] material 未ロード: override={overrideType}");

        smr.gameObject.SetActive(true);

        ApplyBlendShapeTransplant(handle, chara, renderers, 100f, isKneeSocks);

        // donor stocking と swim skin の食い込みを 1 パスで検出し、両側に push して z-fighting を解消
        var swimLowerForRef = renderers.FirstOrDefault(m => m.name == "mesh_skin_lower");
        ApplyPenetrationResolve(smr, swimLowerForRef, renderers, isKneeSocks);

        PatchLogger.LogInfo($"[SwimWearStockingPatch] 適用: {handle.GetCharID()} override={overrideType} knee={isKneeSocks} created={created} meshVerts={targetMesh.vertexCount} bones={smr.bones?.Length ?? 0}");
        return true;
    }

    /// <summary>
    /// donor stocking と swim skin の食い込みを 1 パスで検出し、両側へ押し出して解消する。
    /// 食い込み点でのみ skin 頂点を内側へ push するため、一様 shrink と違い境界で段差が出ない。
    /// 結果ペアは s_resolvedCache にキャッシュ。
    /// </summary>
    private static void ApplyPenetrationResolve(SkinnedMeshRenderer stockingsSmr, SkinnedMeshRenderer swimLower, SkinnedMeshRenderer[] renderers, bool isKneeSocks)
    {
        if (stockingsSmr == null || stockingsSmr.sharedMesh == null) return;
        if (swimLower == null || swimLower.sharedMesh == null) return;

        // ニーハイは形状・カバー範囲が異なり本補正の前提（尻まわりの z-fighting）と合わないため対象外
        if (isKneeSocks) return;

        float minOffset = Configs.StockingOffset?.Value ?? 0f;
        float skinPushAmount = Configs.StockingSkinShrink?.Value ?? 0f;
        float falloffRadius = Configs.StockingSkinFalloffRadius?.Value ?? 0f;
        if (minOffset <= 0f && skinPushAmount <= 0f) return;

        var currentDonor = stockingsSmr.sharedMesh;
        var currentSkin = swimLower.sharedMesh;

        // 二重補正防止
        if (s_resolvedAppliedIds.Contains(currentDonor.GetInstanceID())) return;

        // 量子化キー: 0.1mm precision (max 0.01m → 100、int で十分収まる)
        int xQ = Mathf.RoundToInt(minOffset * 10_000f);
        int yQ = Mathf.RoundToInt(skinPushAmount * 10_000f);
        int fQ = Mathf.RoundToInt(falloffRadius * 10_000f);
        var cacheKey = (currentDonor.GetInstanceID(), currentSkin.GetInstanceID(), isKneeSocks, xQ, yQ, fQ);

        if (!s_resolvedCache.TryGetValue(cacheKey, out var pair))
        {
            var skinAnchorVerts = CollectShrinkAnchorVerts(renderers);

            pair = MeshPenetrationResolver.Resolve(
                currentDonor, currentSkin,
                "blendShape_skin_lower.skin_stocking",
                minOffset, skinPushAmount,
                skinAnchorVerts, falloffRadius,
                "SwimWearStockingPatch");

            s_resolvedCache[cacheKey] = pair;
            if (pair.donor != null) s_resolvedAppliedIds.Add(pair.donor.GetInstanceID());
            if (pair.skin != null) s_resolvedAppliedIds.Add(pair.skin.GetInstanceID());
        }

        if (pair.donor != null) stockingsSmr.sharedMesh = pair.donor;
        if (pair.skin != null) swimLower.sharedMesh = pair.skin;
    }

    private static void ApplyBlendShapeTransplant(CharacterHandle handle, GameObject chara, SkinnedMeshRenderer[] renderers, float shrinkWeight, bool isKneeSocks)
    {
        var charId = handle.GetCharID();
        int key = (int)charId;

        if (!StockingsDonorLoader.TryGetLowerDonor(charId, out var lowerDonor) || lowerDonor.sharedMesh == null)
        {
            PatchLogger.LogWarning($"[SwimWearStockingPatch] lower donor 未キャッシュ: {charId}");
            return;
        }

        var swimLower = renderers.FirstOrDefault(m => m.name == "mesh_skin_lower");
        var swimLowerFoot = renderers.FirstOrDefault(m => m.name == "mesh_skin_lower_foot");

        if (!s_backups.TryGetValue(key, out var backup))
        {
            backup = new SwimwearBackup();
            s_backups[key] = backup;
        }

        // skin_stocking 系 blendShape の per-vertex フェード設定（KneeSocks では skin_kneehigh が
        // 主体になるためスキップ）
        Vector3[] shapeAnchorVerts = null;
        float shapeFalloffRadius = 0f;
        if (!isKneeSocks)
        {
            shapeFalloffRadius = Configs.StockingShapeFalloffRadius?.Value ?? 0f;
            if (shapeFalloffRadius > 0f) shapeAnchorVerts = CollectShrinkAnchorVerts(renderers);
        }

        TransplantInto(swimLower, lowerDonor.sharedMesh, key, shrinkWeight, isKneeSocks, shapeAnchorVerts, shapeFalloffRadius, ref backup.LowerSmr, ref backup.LowerOriginalMesh);
        TransplantInto(swimLowerFoot, lowerDonor.sharedMesh, key, shrinkWeight, isKneeSocks, shapeAnchorVerts, shapeFalloffRadius, ref backup.LowerFootSmr, ref backup.LowerFootOriginalMesh);
    }

    private static readonly System.Collections.Generic.HashSet<string> s_shrinkTargetNames = new()
    {
        "mesh_skin_lower",
        "mesh_skin_lower_foot",
    };

    /// <summary>
    /// shrink 対象（mesh_skin_lower / mesh_skin_lower_foot）以外の skin 系メッシュの頂点を集める。
    /// 同一キャラ配下で sibling SMR は共通の mesh-local 座標系を持つ前提で、頂点を直接連結する。
    /// </summary>
    private static Vector3[] CollectShrinkAnchorVerts(SkinnedMeshRenderer[] renderers)
    {
        var list = new System.Collections.Generic.List<Vector3>();
        foreach (var r in renderers)
        {
            if (r == null || r.sharedMesh == null) continue;
            if (!r.name.StartsWith("mesh_skin_")) continue;
            if (s_shrinkTargetNames.Contains(r.name)) continue;
            list.AddRange(r.sharedMesh.vertices);
        }
        return list.ToArray();
    }

    private static void TransplantInto(SkinnedMeshRenderer target, Mesh donorMesh, int charKey, float shrinkWeight, bool isKneeSocks,
        Vector3[] shapeAnchorVerts, float shapeFalloffRadius,
        ref SkinnedMeshRenderer backupSmr, ref Mesh backupOriginal)
    {
        if (target == null || target.sharedMesh == null) return;

        // 過去の override から残っている transplanted/resolved mesh を vanilla に戻す。
        // 戻さないと cache key が「以前の transplanted ID」になり、その mesh を base に
        // 二重 transplant してしまう（例: ニーハイ→パンスト時に skin_kneehigh delta が
        // 残った状態の上に skin_stocking 系を載せてしまい、Resolve の reference 表面が狂う）。
        if (backupOriginal != null && target.sharedMesh != backupOriginal)
        {
            target.sharedMesh = backupOriginal;
        }

        // shape falloff 量子化キー (0.1mm 精度, max 0.01m → 100)
        int shapeFalloffQ = Mathf.RoundToInt(shapeFalloffRadius * 10_000f);
        var cacheKey = (target.sharedMesh.GetInstanceID(), charKey, isKneeSocks, shapeFalloffQ);

        if (!s_transplantedCache.TryGetValue(cacheKey, out var transplanted) || transplanted == null)
        {
            // 元メッシュが既に transplanted だった場合（reload で cache miss）はバックアップ済みと想定しスキップ
            // ここでは target.sharedMesh が元の mesh（swim 生）であることを期待
            transplanted = BuildTransplantedMesh(target.sharedMesh, donorMesh, isKneeSocks);
            if (transplanted == null) return;

            // skin_stocking 系 blendShape を per-vertex でフェード（mesh_skin_upper 等の境界で段差を解消）
            if (shapeFalloffRadius > 0f && shapeAnchorVerts != null && !isKneeSocks)
            {
                BlendShapeFalloffApplier.Apply(transplanted, s_transplantShapeNames, shapeAnchorVerts, shapeFalloffRadius, "SwimWearStockingPatch");
            }

            s_transplantedCache[cacheKey] = transplanted;
        }

        if (target.sharedMesh != transplanted)
        {
            if (backupSmr == null || backupOriginal == null)
            {
                backupSmr = target;
                backupOriginal = target.sharedMesh;
            }
            target.sharedMesh = transplanted;
        }

        // weight 設定: isKneeSocks=true かつ Luna Casual ドナー取得済みなら skin_kneehigh=100 方式
        // ドナー未取得（フォールバック）時は skin_kneehigh blendShape が移植されないので、
        // 従来の skin_stocking=100 方式を維持して z-fighting 再発を防ぐ
        bool useKneehighWeights = isKneeSocks && KneeSocksLoader.DonorSkinLower != null;
        if (useKneehighWeights)
        {
            SetBlendShape(target, "blendShape_skin_lower.skin_stocking", 0f);
            SetBlendShape(target, "blendShape_skin_lower.skin_socks", 0f);
            SetBlendShape(target, "blendShape_skin_lower.skin_stocking_lower", 0f);
            SetBlendShape(target, "blendShape_skin_lower.skin_kneehigh", 100f);
        }
        else
        {
            SetBlendShape(target, "blendShape_skin_lower.skin_stocking", shrinkWeight);
            SetBlendShape(target, "blendShape_skin_lower.skin_socks", shrinkWeight);
            SetBlendShape(target, "blendShape_skin_lower.skin_stocking_lower", shrinkWeight);
            SetBlendShape(target, "blendShape_skin_lower.skin_kneehigh", 0f);
        }
    }

    /// <summary>
    /// targetMesh の頂点構造を保ったまま、blendShape delta を nearest-neighbor で転写した新 Mesh を生成する。
    /// isKneeSocks=true の場合は Uniform donor（skin_stocking 系）に加えて
    /// KneeSocksLoader の Luna Casual ドナー（skin_kneehigh）も移植する。
    /// 移植ロジックは MeshBlendShapeTransplanter に委譲する。
    /// </summary>
    private static Mesh BuildTransplantedMesh(Mesh targetMesh, Mesh donorMesh, bool isKneeSocks)
    {
        if (!isKneeSocks)
        {
            // isKneeSocks=false: 単一ドナー（Uniform）から skin_stocking 系のみ移植
            return MeshBlendShapeTransplanter.Transplant(targetMesh, donorMesh, s_transplantShapeNames, "SwimWearStockingPatch");
        }

        // isKneeSocks=true: Uniform ドナー + Luna Casual ドナーの複数ドナー移植
        var kneehighDonor = KneeSocksLoader.DonorSkinLower;
        if (kneehighDonor == null)
        {
            // DonorSkinLower 未取得: フォールバックとして従来方式（skin_stocking=100）を維持
            PatchLogger.LogWarning("[SwimWearStockingPatch] DonorSkinLower 未取得のためフォールバック (skin_stocking=100)");
            return MeshBlendShapeTransplanter.Transplant(targetMesh, donorMesh, s_transplantShapeNames, "SwimWearStockingPatch");
        }

        // Uniform ドナー: skin_stocking 系 3 shape + Luna Casual ドナー: skin_kneehigh
        var donors = new (Mesh donor, System.Collections.Generic.IReadOnlyList<string> shapeNames)[]
        {
            (donorMesh, s_transplantShapeNames),
            (kneehighDonor, new[] { "blendShape_skin_lower.skin_kneehigh" }),
        };
        return MeshBlendShapeTransplanter.Transplant(targetMesh, donors, "SwimWearStockingPatch");
    }

    private static void SetBlendShape(SkinnedMeshRenderer smr, string name, float weight)
    {
        if (smr == null || smr.sharedMesh == null) return;
        int idx = smr.sharedMesh.GetBlendShapeIndex(name);
        if (idx >= 0) smr.SetBlendShapeWeight(idx, weight);
    }

    /// <summary>
    /// 水着キャラ配下に空の "mesh_stockings" GO + SkinnedMeshRenderer を作る。
    /// mesh/bones/material は呼び出し側が設定する。親は既存 mesh_skin_lower と揃える。
    /// </summary>
    private static SkinnedMeshRenderer CreateInjected(GameObject chara)
    {
        var referenceSmr = chara.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .FirstOrDefault(m => m.name == "mesh_skin_lower");
        var parentTransform = referenceSmr != null ? referenceSmr.transform.parent : chara.transform;

        var go = new GameObject(InjectedName);
        go.transform.SetParent(parentTransform, false);

        return go.AddComponent<SkinnedMeshRenderer>();
    }

    /// <summary>
    /// donor の bones[] を chara 階層の同名 Transform にリマップして target SMR に適用する。
    /// </summary>
    private static void RemapBonesInto(SkinnedMeshRenderer target, Transform[] donorBones, Transform donorRootBone,
        Dictionary<string, Transform> boneDict, Transform fallback)
    {
        var src = donorBones ?? System.Array.Empty<Transform>();
        var newBones = new Transform[src.Length];
        int missing = 0;
        for (int i = 0; i < src.Length; i++)
        {
            var db = src[i];
            if (db != null && boneDict.TryGetValue(db.name, out var mapped))
                newBones[i] = mapped;
            else
            {
                missing++;
                newBones[i] = fallback;
            }
        }
        target.bones = newBones;

        if (donorRootBone != null && boneDict.TryGetValue(donorRootBone.name, out var root))
            target.rootBone = root;
        else
            target.rootBone = fallback;

        if (missing > 0)
            PatchLogger.LogInfo($"[SwimWearStockingPatch] bone 未対応 {missing}/{src.Length}");
    }

    /// <summary>
    /// キャラ配下の Transform を名前 → Transform の辞書にする。名前衝突時は先勝ち（最初に見つけた方を採用）し、
    /// 衝突件数をまとめてログに出す（L_hand / R_hand は名前が違うので衝突しないが、LOD 複製やミラーノードで同名が出ると後勝ち上書きで誤マッピングする恐れがあるため）。
    /// </summary>
    private static Dictionary<string, Transform> BuildBoneDict(GameObject chara)
    {
        var dict = new Dictionary<string, Transform>(System.StringComparer.OrdinalIgnoreCase);
        int collisions = 0;
        foreach (var t in chara.GetComponentsInChildren<Transform>(true))
        {
            if (!dict.ContainsKey(t.name))
                dict[t.name] = t;
            else
                collisions++;
        }
        if (collisions > 0)
            PatchLogger.LogInfo($"[SwimWearStockingPatch] bone 名衝突 {collisions} 件を先勝ちで無視 (chara={chara.name})");
        return dict;
    }
}
