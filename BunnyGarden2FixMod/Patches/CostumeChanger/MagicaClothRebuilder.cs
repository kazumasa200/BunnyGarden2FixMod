using BunnyGarden2FixMod.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// Bottoms swap 後の MagicaCloth_Skirt (MeshCloth) 物理を donor 側設定で再 build する。
///
/// 背景: MeshCloth は build 時に sharedMesh から proxy mesh を生成し、毎フレ vertex を直接書換える。
/// SwapSmr で SMR.sharedMesh を donor mesh に差し替えると proxy mesh と乖似して物理が止まる。
///
/// 解決: target 側 cloth component を Destroy → 新規 AddComponent → donor の serializeData /
/// serializeData2 を field-wise コピー → sourceRenderers を target SMR にマップ → BuildAndRun。
/// MagicaCloth2.MagicaCloth 型は AppDomain reflection で解決 (DLL 直接 reference を避ける)。
///
/// component 名は costume により "MagicaCloth_Skirt" / "Magica Cloth_Skirt" の 2 種あるため、
/// 名前ではなく clothType=MeshCloth + name contains "Skirt" でフィルタする。
///
/// Restore 経路: <see cref="RebuildSkirtCloth"/> 初回呼出時に target の元 config を
/// <see cref="s_snapshots"/> に保存し、<see cref="RestoreSkirtCloth"/> で同手順で逆 rebuild する。
/// </summary>
internal static class MagicaClothRebuilder
{
    private struct Snapshot
    {
        public object SerData;        // ClothSerializeData (deep field-wise copy)
        public object SerData2;       // ClothSerializeData2 (同上)
        public IList SrcRenderers;    // target 元の sourceRenderers (target 自身の SMR 参照)
        public bool CreatedByMod;     // true = Restore 時に GO ごと Destroy (元々 stock に存在しなかった component)
    }

    private static readonly Dictionary<(int InstanceId, string SkirtGoName), Snapshot> s_snapshots = new();
    private static Type s_magicaClothType;
    private static bool s_typeResolveAttempted;

    // MagicaCloth API reflection cache (ResolveType の一括解決で埋まる、再解決不要のため)
    private static PropertyInfo s_serProp;
    private static MethodInfo s_serData2Method;
    private static MethodInfo s_disableAutoBuildMethod;
    private static MethodInfo s_buildAndRunMethod;
    private static MethodInfo s_replaceTransformMethod;
    private static MethodInfo s_resetClothMethod;
    private static MethodInfo s_setParameterChangeMethod;

    // NullOutCachedOriginalMeshes 用 reflection cache (初回 BuildFromConfig で 1 度だけ解決)。
    // RenderSetupData.cs:387 の override (sharedMesh=referenceInitSetupData.originalMesh) を
    // 無効化するため、新 SerData2.initData.clothSetupDataList[*].originalMesh を null 化する経路。
    private static bool s_originalMeshNullifyResolved;
    private static FieldInfo s_initDataField;
    private static FieldInfo s_clothSetupDataListField;
    private static FieldInfo s_setupOriginalMeshField;

    // DisablePreBuildIfPresent 用 reflection cache。serializeData2.preBuildData.enabled を
    // false にして PreBuild path (RenderData.originalMesh = preBuildUniqueSerializeData.originalMesh)
    // を回避し non-PreBuild path (originalMesh = setupData.originalMesh = SMR.sharedMesh) に倒す。
    // 連続 donor 切替で customMesh が donor 元 baked mesh から生成され前 donor の shape が残る症状を回避。
    private static bool s_preBuildDisableResolved;
    private static FieldInfo s_preBuildDataField;
    private static FieldInfo s_preBuildEnabledField;

    // ForceCleanupOldClothRenderData 用 reflection cache。SafeDestroy 経由で MC_old を destroy する際、
    // ClothProcess.DisposeInternal が `if (isBuild) return;` で早期 return して RenderManager.renderDataDict
    // から RenderData を回収し損ねる症状の workaround。
    //   1. oldComp.Process.renderHandleList を空にして DisposeInternal の foreach で誤 RemoveRenderer
    //      (= 新 MC の RenderData を decrement-dispose する) を防ぐ
    //   2. RenderManager.renderDataDict から sourceRenderers の InstanceID エントリを直接 Dispose+Remove
    private static bool s_renderManagerResolved;
    private static PropertyInfo s_processProp;          // MagicaCloth.Process
    private static FieldInfo s_renderHandleListField;   // ClothProcess.renderHandleList
    private static Type s_renderDataType;
    private static FieldInfo s_renderDataDictField;     // RenderManager.renderDataDict
    private static MethodInfo s_renderManagerRemoveRenderer; // RenderManager.RemoveRenderer(int)
    private static Type s_magicaManagerType;
    private static FieldInfo s_magicaManagerRenderField; // MagicaManager.Render (static)

    private static Type ResolveType()
    {
        if (s_magicaClothType != null) return s_magicaClothType;
        if (s_typeResolveAttempted) return null;
        s_typeResolveAttempted = true;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType("MagicaCloth2.MagicaCloth", false);
                if (t != null)
                {
                    s_magicaClothType = t;
                    s_serProp = t.GetProperty("SerializeData", BindingFlags.Instance | BindingFlags.Public);
                    s_serData2Method = t.GetMethod("GetSerializeData2");
                    s_disableAutoBuildMethod = t.GetMethod("DisableAutoBuild");
                    s_buildAndRunMethod = t.GetMethod("BuildAndRun");
                    s_replaceTransformMethod = t.GetMethod("ReplaceTransform", new[] { typeof(Dictionary<string, Transform>) });
                    s_resetClothMethod = t.GetMethod("ResetCloth", new[] { typeof(bool) });
                    s_setParameterChangeMethod = t.GetMethod("SetParameterChange", Type.EmptyTypes);
                    return t;
                }
            }
            catch (Exception ex) { PatchLogger.LogDebug($"[MCR] assembly 走査中に例外: {asm?.FullName}: {ex.Message}"); }
        }
        PatchLogger.LogWarning("[MagicaClothRebuilder] MagicaCloth2.MagicaCloth 型未解決 (1 回限り走査)");
        return null;
    }

    /// <summary>
    /// シーン unload 時に呼ぶ。次シーンで instanceId 再採番されるためスナップショット無効化。
    /// </summary>
    public static void ClearAllSnapshots() => s_snapshots.Clear();

    /// <summary>
    /// BottomsLoader が SwapSmr / hide / CaptureSnapshotIfFirst で SMR.sharedMesh を捕獲する前に呼び、
    /// target の各 active MagicaCloth が SMR に書き込んだ customMesh を originalMesh (build 時 cache asset)
    /// に戻しておく。これで BottomsLoader が捕獲する snap.OriginalMesh が stable な Mesh asset になり、
    /// 後続の rebuild で customMesh が dispose されても dangling reference にならない (Restore 時に
    /// SMR.sharedMesh=null 状態で BuildAndRun が失敗するのを防ぐ)。
    /// </summary>
    public static void NormalizeSmrMeshBeforeSwap(GameObject character)
    {
        if (character == null) return;
        var magicaType = ResolveType();
        if (magicaType == null) return;

        var magicaRoot = character.transform.Find("MagicaCloth");
        if (magicaRoot == null || !magicaRoot.gameObject.activeSelf) return;

        var bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var serProp = s_serProp;
        var getOriginalMesh = magicaType.GetMethod("GetOriginalMesh", new[] { typeof(Renderer) });
        if (serProp == null || getOriginalMesh == null) return;

        int normalized = 0;
        var allComponents = character.GetComponentsInChildren(magicaType, true);
        foreach (var comp in allComponents)
        {
            if (!(comp is Behaviour beh) || !beh.isActiveAndEnabled) continue;
            var sdata = serProp.GetValue(comp);
            if (sdata == null) continue;
            if (!(sdata.GetType().GetField("sourceRenderers", bf)?.GetValue(sdata) is IList srcList)) continue;
            foreach (var item in srcList)
            {
                if (!(item is SkinnedMeshRenderer smr) || smr == null) continue;
                try
                {
                    var orig = getOriginalMesh.Invoke(comp, new object[] { smr }) as Mesh;
                    if (orig != null && smr.sharedMesh != orig)
                    {
                        smr.sharedMesh = orig;
                        normalized++;
                    }
                }
                catch (Exception ex) { PatchLogger.LogDebug($"[MCR] GetOriginalMesh 失敗: {smr?.name}: {ex.Message}"); }
            }
        }
        if (normalized > 0)
            PatchLogger.LogDebug($"[MagicaClothRebuilder] normalize SMR mesh (customMesh→originalMesh): {character.name} (count={normalized})");
    }

    /// <summary>
    /// character 配下の MagicaCloth_Skirt (MeshCloth) を、donor 側の同種 component の
    /// serializeData / serializeData2 で再 build する。
    /// 初回 rebuild 時に target の元 config を snapshot 保存し、後続 <see cref="RestoreSkirtCloth"/> で復元可能にする。
    /// donor 側に skirt cloth 無 (例: MIUKA/Casual) の場合は target component を Destroy (物理 OFF) する。
    /// target 側に skirt cloth 無 (現衣装が pants 系) の場合は no-op。
    /// </summary>
    public static void RebuildSkirtCloth(GameObject character, GameObject donorHost)
    {
        if (character == null) return;
        var magicaType = ResolveType();
        if (magicaType == null) return;

        var magicaRoot = character.transform.Find("MagicaCloth");
        if (magicaRoot == null || !magicaRoot.gameObject.activeSelf) return;

        var targetSkirt = FindSkirtMeshCloth(character, magicaType);
        var donorSkirt = donorHost != null ? FindSkirtMeshCloth(donorHost, magicaType) : null;

        // donor 側無 + target 側有 → snapshot を取って Destroy (Restore で復元できるように)
        if (donorSkirt == null)
        {
            if (targetSkirt != null)
            {
                SnapshotIfFirst(character, targetSkirt, magicaType);
                PatchLogger.LogDebug($"[MagicaClothRebuilder] donor に skirt cloth 無 → target component を Destroy (物理 OFF): {character.name}");
                UnityEngine.Object.DestroyImmediate(targetSkirt);
            }
            return;
        }
        if (targetSkirt == null)
        {
            // 過去の「donor 無」 apply (e.g. MIUKA/Casual 等 pants 専用 donor) で
            // target 側 component が Destroy されたまま、後続 skirt 系 donor が選ばれた場合
            // snapshot から component を復元してから donor rebuild に進む。
            // snapshot 無なら本当に target に skirt 系 GO が無い (現衣装が pants 系等) ので
            // 新規生成を試みる。
            if (TryRecoverDestroyedSkirt(character, magicaType))
            {
                targetSkirt = FindSkirtMeshCloth(character, magicaType);
            }
            if (targetSkirt == null)
            {
                // SwimWear 等 stock skirt cloth 無 の target に donor config で新規生成 (VIP のみ)。
                // caller (BottomsLoader.RebindMagicaClothIfActive) が magicaRoot.activeSelf=true をガード済みなので
                // ここに到達するのは VIP シーン相当。Bar では呼ばれない。
                var createdComp = TryCreateSkirtCloth(character, donorSkirt, magicaType);
                if (createdComp == null)
                {
                    PatchLogger.LogDebug($"[MagicaClothRebuilder] target に skirt cloth 無 + 新規生成失敗 → no-op: {character.name}");
                    return;
                }
                // 新規生成成功 → snapshot を CreatedByMod=true で登録。
                // 通常 RebuildFromComponent 経路には進まず、ここで完了 (BuildFromConfig は TryCreateSkirtCloth 内で実行済)。
                SnapshotAsCreated(character, createdComp);
                return;
            }
        }

        try
        {
            SnapshotIfFirst(character, targetSkirt, magicaType);
            RebuildFromComponent(targetSkirt, donorSkirt, character, magicaType);
        }
        catch (Exception ex)
        {
            PatchLogger.LogError($"[MagicaClothRebuilder] rebuild 失敗 {character.name}: {ex}");
        }
    }

    /// <summary>
    /// donor 無 apply で <see cref="DestroyImmediate"/> された target MagicaCloth_Skirt を snapshot から復元する
    /// throwaway build。snapshot は **消費しない** (toggle-off の <see cref="RestoreSkirtCloth"/> で再利用)。
    /// AddComponent 単独だと <see cref="FindSkirtMeshCloth"/> 探索 / serializeData 復元と整合が崩れるため
    /// 最低限 build まで通す (remapColliders=false)。直後 caller が <see cref="RebuildFromComponent"/> で再 build。
    /// </summary>
    private static bool TryRecoverDestroyedSkirt(GameObject character, Type magicaType)
    {
        int instId = character.GetInstanceID();
        var keys = s_snapshots.Keys.Where(k => k.InstanceId == instId).ToList();
        bool any = false;
        foreach (var key in keys)
        {
            if (!s_snapshots.TryGetValue(key, out var snap)) continue;
            if (snap.CreatedByMod) continue; // mod 生成 component は recover 対象外 (元々 stock に存在しない)
            var skirtGo = FindGameObjectByName(character, key.SkirtGoName);
            if (skirtGo == null) continue;
            if (skirtGo.GetComponent(magicaType) != null) continue; // 既に component 有 → recovery 不要
            // recover 経路も SafeDestroyPreservingSmrState を経由しないため、snap.SrcRenderers の SMR が
            // renderDataDict に残留しているケースに備えて事前 clear (create 経路と同方針)。
            ForceClearRenderDataForSmrs(snap.SrcRenderers);
            try
            {
                if (BuildFromConfig(skirtGo, character, magicaType, snap.SerData, snap.SerData2, snap.SrcRenderers, "recover", remapColliders: false))
                {
                    PatchLogger.LogDebug($"[MagicaClothRebuilder] destroyed skirt を snapshot から復元: {character.name}/{key.SkirtGoName}");
                    any = true;
                }
                else
                {
                    PatchLogger.LogWarning($"[MagicaClothRebuilder] recovery build 失敗: {character.name}/{key.SkirtGoName}");
                }
            }
            catch (Exception ex)
            {
                PatchLogger.LogError($"[MagicaClothRebuilder] recover 例外 ({key.SkirtGoName}): {ex}");
            }
        }
        return any;
    }

    /// <summary>
    /// 過去の <see cref="RebuildSkirtCloth"/> で取得した snapshot から target の MagicaCloth_Skirt を
    /// 元 config で rebuild する。snapshot 無 (= 一度も rebuild してない) なら no-op。
    /// 同 character への複数 skirt GO (将来対応用) も全エントリ復元する。
    /// </summary>
    public static void RestoreSkirtCloth(GameObject character)
    {
        if (character == null) return;
        var magicaType = ResolveType();
        if (magicaType == null) return;
        var instId = character.GetInstanceID();

        var keys = s_snapshots.Keys.Where(k => k.InstanceId == instId).ToList();

        // mod 注入した collider component を全削除 (marker と同 GO 上の Magica*Collider 両方)。
        // CreatedByMod の skirt 削除より前 / per-snapshot の rebuild より前に character 単位で 1 回実施する。
        // 非 CreatedByMod 経路 (target が stock skirt 持ち + donor 由来の clone collider が injected された
        // ケース) でも残留 collider を確実に掃除し、stock skirt の collision に donor の余剰 collider が
        // 残らないようにする。複数 snapshot がある場合も先頭で 1 回掃除すれば足りる。
        if (keys.Count > 0)
        {
            CleanupInjectedColliders(character);
        }

        foreach (var key in keys)
        {
            if (!s_snapshots.TryGetValue(key, out var snap)) continue;

            // CreatedByMod な snapshot は GO ごと Destroy (元々 stock に存在しなかったため元状態は「無し」)
            if (snap.CreatedByMod)
            {
                var skirtGoToDestroy = FindGameObjectByName(character, key.SkirtGoName);
                if (skirtGoToDestroy != null)
                {
                    var existingComp = skirtGoToDestroy.GetComponent(magicaType);
                    // SafeDestroyPreservingSmrState の SrcRenderers は CreatedByMod の場合 null だが
                    // 内部実装が null check しているため安全に呼べる。
                    if (existingComp != null) SafeDestroyPreservingSmrState(existingComp, null);
                    UnityEngine.Object.DestroyImmediate(skirtGoToDestroy);
                    PatchLogger.LogDebug($"[MagicaClothRebuilder] CreatedByMod skirt cloth を Destroy: {character.name}/{key.SkirtGoName}");
                }
                s_snapshots.Remove(key);
                continue;
            }

            bool buildSucceeded = false;
            try
            {
                var skirtGo = FindGameObjectByName(character, key.SkirtGoName);
                if (skirtGo == null)
                {
                    PatchLogger.LogWarning($"[MagicaClothRebuilder] restore: skirt GO '{key.SkirtGoName}' 未存在");
                    continue;
                }
                var existing = skirtGo.GetComponent(magicaType);
                if (existing != null)
                {
                    SafeDestroyPreservingSmrState(existing, snap.SrcRenderers);
                }
                buildSucceeded = BuildFromConfig(skirtGo, character, magicaType, snap.SerData, snap.SerData2, snap.SrcRenderers, "restore", remapColliders: false);
            }
            catch (Exception ex)
            {
                PatchLogger.LogError($"[MagicaClothRebuilder] restore 例外 ({key.SkirtGoName}): {ex}");
            }
            // build 成功時のみ snapshot 消費。失敗時は次回 Restore で retry できるよう保持。
            if (buildSucceeded) s_snapshots.Remove(key);
        }
    }

    /// <summary>
    /// host 配下の MagicaCloth component で clothType=MeshCloth かつ
    /// GameObject 名に "Skirt" を含む最初の component を返す。
    /// </summary>
    private static Component FindSkirtMeshCloth(GameObject host, Type magicaType)
    {
        var bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var serProp = s_serProp;
        if (serProp == null) return null;

        var allComponents = host.GetComponentsInChildren(magicaType, true);
        foreach (var comp in allComponents)
        {
            var name = (comp as UnityEngine.Object)?.name ?? "";
            if (name.IndexOf("Skirt", StringComparison.OrdinalIgnoreCase) < 0) continue;
            var sdata = serProp.GetValue(comp);
            if (sdata == null) continue;
            var ctValue = sdata.GetType().GetField("clothType", bf)?.GetValue(sdata)?.ToString();
            if (ctValue == "MeshCloth") return comp;
        }
        return null;
    }

    private static GameObject FindGameObjectByName(GameObject character, string name)
    {
        foreach (var t in character.GetComponentsInChildren<Transform>(true))
        {
            if (t != null && t.name == name) return t.gameObject;
        }
        return null;
    }

    /// <summary>
    /// target に skirt cloth component が無い (例: SwimWear) ケースで、donor の config を流用して
    /// target の MagicaCloth root 配下に新規 GameObject + MagicaCloth component を生成する。
    /// 成功時は新規 component を返し、失敗時は null。
    /// VIP シーン (magicaRoot.activeSelf=true) でのみ呼ばれる前提 (caller がガード)。
    /// </summary>
    private static Component TryCreateSkirtCloth(GameObject character, Component donorComp, Type magicaType)
    {
        var bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var serProp = s_serProp;
        var s2Method = s_serData2Method;
        if (serProp == null || s2Method == null) return null;

        // target の MagicaCloth root を取得 (無ければ防御的に no-op)
        var magicaRootTrans = character.transform.Find("MagicaCloth");
        if (magicaRootTrans == null)
        {
            PatchLogger.LogWarning($"[MagicaClothRebuilder] target に MagicaCloth root 無し → 新規生成中止: {character.name}");
            return null;
        }

        var donorSerData = serProp.GetValue(donorComp);
        var donorSerData2 = s2Method.Invoke(donorComp, null);
        if (donorSerData == null || donorSerData2 == null)
        {
            PatchLogger.LogError("[MagicaClothRebuilder] donor serializeData/2 取得失敗 (新規生成)");
            return null;
        }

        // GO 名は固定で "Magica Cloth_Skirt" (スペース有) を使う。
        // CharacterHandle.EnableMagicaCloth (CharacterHandle.cs:1322) が "Magica Cloth_Skirt" 名で lookup
        // するため、donor 側 GO 名 ("Magica Cloth_Skirt" / "MagicaCloth_Skirt" の 2 種あり) をそのまま
        // 継承すると EnableMagicaCloth 経由のパラメータ強制上書きを受けないケースが発生する。
        // また、連続 Override (donor A→B) で GO 名が異なると 1 回目の GO が孤児化する問題も回避。
        const string SkirtGoName = "Magica Cloth_Skirt";
        var donorGoName = SkirtGoName;

        // 既に同名 GO があれば衝突回避 (連続 Override で完全 destroy できてないケース)
        if (magicaRootTrans.Find(donorGoName) != null)
        {
            PatchLogger.LogWarning($"[MagicaClothRebuilder] 同名 GO '{donorGoName}' 既存 → 新規生成 skip: {character.name}");
            return null;
        }

        // target hierarchy 内 SMR の name → SMR 辞書 (sourceRenderers 解決用)。
        // activeInHierarchy=true を優先し、無ければ activeSelf=true (祖先 inactive) を採用。
        var smrByName = new Dictionary<string, SkinnedMeshRenderer>();
        foreach (var smr in character.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr == null) continue;
            if (!smr.gameObject.activeSelf) continue;
            if (!smrByName.TryGetValue(smr.name, out var existing))
            {
                smrByName[smr.name] = smr;
                continue;
            }
            if (!existing.gameObject.activeInHierarchy && smr.gameObject.activeInHierarchy)
                smrByName[smr.name] = smr;
        }

        // donor の sourceRenderers をターゲット SMR 名でリマップ
        var srcField = donorSerData.GetType().GetField("sourceRenderers", bf);
        if (srcField == null)
        {
            PatchLogger.LogError("[MagicaClothRebuilder] sourceRenderers field 未解決 (新規生成)");
            return null;
        }
        var donorSrcList = srcField.GetValue(donorSerData) as IList;
        var newSrcList = (IList)Activator.CreateInstance(srcField.FieldType);
        if (donorSrcList != null)
        {
            foreach (var item in donorSrcList)
            {
                var srcSmr = item as SkinnedMeshRenderer;
                if (srcSmr == null) continue;
                if (smrByName.TryGetValue(srcSmr.name, out var targetSmr))
                    newSrcList.Add(targetSmr);
            }
        }
        if (newSrcList.Count == 0)
        {
            PatchLogger.LogWarning($"[MagicaClothRebuilder] target に対応 SMR 無 (新規生成中止): {character.name}");
            return null;
        }

        // 新規 GameObject を MagicaCloth root 配下に作成
        var newGo = new GameObject(donorGoName);
        newGo.transform.SetParent(magicaRootTrans, false);
        newGo.transform.localPosition = Vector3.zero;
        newGo.transform.localRotation = Quaternion.identity;
        newGo.transform.localScale = Vector3.one;

        // create 経路は SafeDestroyPreservingSmrState を経由しないため、scene 跨ぎ等で newSrcList SMR の
        // InstanceID が renderDataDict に残留しているケースに備えて事前 clear する (rebuild/restore 経路と同形)。
        ForceClearRenderDataForSmrs(newSrcList);

        bool ok = false;
        try
        {
            ok = BuildFromConfig(newGo, character, magicaType, donorSerData, donorSerData2, newSrcList, "create", remapColliders: true);
        }
        catch (Exception ex)
        {
            PatchLogger.LogError($"[MagicaClothRebuilder] 新規生成 BuildFromConfig 例外: {character.name}: {ex}");
        }

        if (!ok)
        {
            UnityEngine.Object.DestroyImmediate(newGo);
            return null;
        }

        var newComp = newGo.GetComponent(magicaType);
        if (newComp == null)
        {
            PatchLogger.LogError($"[MagicaClothRebuilder] 新規 component 取得失敗: {character.name}");
            UnityEngine.Object.DestroyImmediate(newGo);
            return null;
        }

        PatchLogger.LogDebug($"[MagicaClothRebuilder] target に skirt cloth を新規生成: {character.name}/{donorGoName} (srcRenderers={newSrcList.Count})");
        return newComp;
    }

    /// <summary>
    /// 新規生成された skirt cloth component の snapshot を CreatedByMod=true で登録する。
    /// 既存 SnapshotIfFirst の ContainsKey ガードと共存し、同 key で 2 回登録されない。
    /// </summary>
    private static void SnapshotAsCreated(GameObject character, Component createdComp)
    {
        var key = (character.GetInstanceID(), createdComp.gameObject.name);
        if (s_snapshots.ContainsKey(key)) return;
        s_snapshots[key] = new Snapshot
        {
            SerData = null,
            SerData2 = null,
            SrcRenderers = null,
            CreatedByMod = true,
        };
        PatchLogger.LogDebug($"[MagicaClothRebuilder] snapshot saved (CreatedByMod): {character.name}/{createdComp.gameObject.name}");
    }

    /// <summary>
    /// 同 (instanceId, skirtGoName) で既に snapshot があれば no-op。
    /// 初回 rebuild 時のみ target の元 serializeData / serializeData2 / sourceRenderers を保存し、
    /// Restore で素 config への逆 rebuild ができるようにする。
    ///
    /// 重要: ContainsKey ガードは <see cref="SnapshotAsCreated"/> で登録した CreatedByMod=true な
    /// snapshot を上書きしないことが Restore 経路の前提。これを無条件上書きに変えると、
    /// Restore で「mod 生成 GO を Destroy」ではなく「null SerData で rebuild」してしまい挙動破綻する。
    /// </summary>
    private static void SnapshotIfFirst(GameObject character, Component targetComp, Type magicaType)
    {
        var key = (character.GetInstanceID(), targetComp.gameObject.name);
        if (s_snapshots.ContainsKey(key)) return;

        var bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var serProp = s_serProp;
        var s2Method = s_serData2Method;
        if (serProp == null || s2Method == null) return;

        var origSer = serProp.GetValue(targetComp);
        var origSer2 = s2Method.Invoke(targetComp, null);
        if (origSer == null || origSer2 == null) return;

        var snapSer = Activator.CreateInstance(origSer.GetType());
        CopyFields(origSer, snapSer);
        var snapSer2 = Activator.CreateInstance(origSer2.GetType());
        CopyFields(origSer2, snapSer2);

        // sourceRenderers のスナップショットは target 自身の SMR 参照 (target hierarchy に存続)
        var srcField = origSer.GetType().GetField("sourceRenderers", bf);
        IList snapSrcList = null;
        if (srcField != null)
        {
            var origSrc = srcField.GetValue(origSer) as IList;
            snapSrcList = (IList)Activator.CreateInstance(srcField.FieldType);
            if (origSrc != null)
            {
                foreach (var item in origSrc) snapSrcList.Add(item);
            }
        }

        s_snapshots[key] = new Snapshot
        {
            SerData = snapSer,
            SerData2 = snapSer2,
            SrcRenderers = snapSrcList
        };
        PatchLogger.LogDebug($"[MagicaClothRebuilder] snapshot saved: {character.name}/{targetComp.gameObject.name} (srcRenderers={snapSrcList?.Count ?? -1})");
    }

    private static void RebuildFromComponent(Component targetComp, Component donorComp, GameObject character, Type magicaType)
    {
        var bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var serProp = s_serProp;
        var s2Method = s_serData2Method;
        if (serProp == null || s2Method == null) return;

        var donorSerData = serProp.GetValue(donorComp);
        var donorSerData2 = s2Method.Invoke(donorComp, null);
        if (donorSerData == null || donorSerData2 == null)
        {
            PatchLogger.LogError("[MagicaClothRebuilder] donor serializeData/2 取得失敗");
            return;
        }

        var targetGo = targetComp.gameObject;

        // target hierarchy 内 SMR の name → SMR 辞書 (sourceRenderers 解決用)。
        // COSTUME 切替直後は古い衣装 orphan SMR (祖先 inactive) と新衣装 SMR が同名で重複するため、
        // 先勝ちだと orphan を採用 → SwapSmr で donor mesh が orphan に流れ生身は prefab mesh のまま物理 OFF。
        // 採用優先順位: activeInHierarchy=true > activeSelf=true。後者は ShowCharacter 前の fallback。
        var smrByName = new Dictionary<string, SkinnedMeshRenderer>();
        foreach (var smr in character.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr == null) continue;
            if (!smr.gameObject.activeSelf) continue;
            if (!smrByName.TryGetValue(smr.name, out var existing))
            {
                smrByName[smr.name] = smr;
                continue;
            }
            // 既存が activeInHierarchy=false で新しいのが true → 入替 (生身を優先)
            if (!existing.gameObject.activeInHierarchy && smr.gameObject.activeInHierarchy)
            {
                smrByName[smr.name] = smr;
            }
        }

        var srcField = donorSerData.GetType().GetField("sourceRenderers", bf);
        if (srcField == null)
        {
            PatchLogger.LogError("[MagicaClothRebuilder] sourceRenderers field 未解決");
            return;
        }
        var donorSrcList = srcField.GetValue(donorSerData) as IList;
        var newSrcList = (IList)Activator.CreateInstance(srcField.FieldType);
        if (donorSrcList != null)
        {
            foreach (var item in donorSrcList)
            {
                var srcSmr = item as SkinnedMeshRenderer;
                if (srcSmr == null) continue;
                if (smrByName.TryGetValue(srcSmr.name, out var targetSmr))
                {
                    newSrcList.Add(targetSmr);
                }
            }
        }

        if (newSrcList.Count == 0)
        {
            PatchLogger.LogWarning($"[MagicaClothRebuilder] target に対応 active SMR 無 (rebuild 中止): {character.name}/{targetGo.name}");
            return;
        }

        SafeDestroyPreservingSmrState(targetComp, newSrcList);
        BuildFromConfig(targetGo, character, magicaType, donorSerData, donorSerData2, newSrcList, "rebuild", remapColliders: true);
    }

    /// <summary>
    /// component Destroy 時の OnDisable → <c>RenderData.SwapOriginalMesh</c> が SMR.sharedMesh /
    /// bones / sharedMaterials を build 時 cache (= 古い state) に巻き戻す副作用がある。
    /// donor swap 後の現在の SMR state を保持したまま rebuild するため、Destroy 直前に
    /// 各 sourceRenderer の state を捕獲し Destroy 後に再適用する。
    /// 再適用しないと build 時 snapshot される sharedMesh が old original になり、
    /// donor mesh で proxy build されず user は「donor 適用したのに元 mesh のまま」 となる。
    /// </summary>
    private static void SafeDestroyPreservingSmrState(Component oldComp, IList sourceRenderers)
    {
        var saved = new List<(SkinnedMeshRenderer Smr, Mesh Mesh, Transform[] Bones, Material[] Mats, bool Active, bool Enabled)>();
        if (sourceRenderers != null)
        {
            foreach (var item in sourceRenderers)
            {
                if (item is SkinnedMeshRenderer smr)
                    saved.Add((smr, smr.sharedMesh, smr.bones, smr.sharedMaterials, smr.gameObject.activeSelf, smr.enabled));
            }
        }

        // DestroyImmediate 前に oldComp.Process.renderHandleList をクリアし、後段の async build
        // cancellation 経由 DisposeInternal が新 MC の RenderData を誤 RemoveRenderer/Dispose
        // するのを防ぐ。fix 確定後も残す根本対策の一部。
        ClearRenderHandleListIfPossible(oldComp);

        UnityEngine.Object.DestroyImmediate(oldComp);

        // RenderManager.renderDataDict 内に残っている oldComp 由来の RenderData を Dispose + Remove。
        // DisposeInternal が isBuild=true で早期 return した経路 (LUNA stock MC が初回ロード時 build 進行中
        // に削除されたケース等) で RenderData が dict に残留し続け、新 MC の AddRenderer が REUSE して
        // customMesh が前 mesh から固定される症状の根本対策。
        ForceClearRenderDataForSmrs(sourceRenderers);

        foreach (var (smr, mesh, bones, mats, active, enabled) in saved)
        {
            if (smr == null) continue;
            if (mesh != null) smr.sharedMesh = mesh;
            if (bones != null) smr.bones = bones;
            if (mats != null) smr.sharedMaterials = mats;
            smr.gameObject.SetActive(active);
            smr.enabled = enabled;
        }
    }

    /// <summary>
    /// MagicaManager / ClothProcess / RenderData のリフレクションを 1 度だけ解決する。
    /// 失敗時は対応する FieldInfo / MethodInfo / PropertyInfo を null のまま残し、後続呼出は no-op。
    /// </summary>
    private static void ResolveRenderManagerReflection()
    {
        if (s_renderManagerResolved) return;
        s_renderManagerResolved = true;
        var bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var sf = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        var magicaClothType = ResolveType();
        if (magicaClothType == null)
        {
            PatchLogger.LogWarning("[MagicaClothRebuilder] ResolveRenderManagerReflection: MagicaCloth type 未解決");
            return;
        }
        s_processProp = magicaClothType.GetProperty("Process", bf);
        if (s_processProp == null)
        {
            PatchLogger.LogWarning("[MagicaClothRebuilder] ResolveRenderManagerReflection: MagicaCloth.Process property 未解決");
            return;
        }
        var clothProcessType = s_processProp.PropertyType;
        s_renderHandleListField = clothProcessType.GetField("renderHandleList", bf);
        if (s_renderHandleListField == null)
            PatchLogger.LogWarning("[MagicaClothRebuilder] ResolveRenderManagerReflection: ClothProcess.renderHandleList field 未解決");

        // MagicaManager (static class) を assembly 走査で解決
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType("MagicaCloth2.MagicaManager", false);
                if (t != null) { s_magicaManagerType = t; break; }
            }
            catch { /* skip dynamic asm */ }
        }
        if (s_magicaManagerType == null)
        {
            PatchLogger.LogWarning("[MagicaClothRebuilder] ResolveRenderManagerReflection: MagicaManager type 未解決");
            return;
        }
        // Render は static field or static property を試す
        s_magicaManagerRenderField = s_magicaManagerType.GetField("Render", sf);
        var renderProp = s_magicaManagerRenderField == null ? s_magicaManagerType.GetProperty("Render", sf) : null;
        Type renderManagerType = null;
        if (s_magicaManagerRenderField != null)
            renderManagerType = s_magicaManagerRenderField.FieldType;
        else if (renderProp != null)
            renderManagerType = renderProp.PropertyType;
        if (renderManagerType == null)
        {
            PatchLogger.LogWarning("[MagicaClothRebuilder] ResolveRenderManagerReflection: MagicaManager.Render 未解決");
            return;
        }
        s_renderDataDictField = renderManagerType.GetField("renderDataDict", bf);
        if (s_renderDataDictField == null)
        {
            PatchLogger.LogWarning("[MagicaClothRebuilder] ResolveRenderManagerReflection: RenderManager.renderDataDict field 未解決");
            return;
        }
        var dictType = s_renderDataDictField.FieldType;
        if (dictType.IsGenericType && dictType.GetGenericArguments().Length == 2)
            s_renderDataType = dictType.GetGenericArguments()[1];
        if (s_renderDataType == null)
        {
            PatchLogger.LogWarning("[MagicaClothRebuilder] ResolveRenderManagerReflection: RenderData type 未解決");
            return;
        }
        // 本家 RenderManager.RemoveRenderer(int handle) を呼ぶ。refcount を decrement し 0 で Dispose+Remove。
        // 直接 Dispose+Remove だと refcount > 0 の他参照が dangling 化するため、本家 API 経由で安全に片付ける。
        s_renderManagerRemoveRenderer = renderManagerType.GetMethod("RemoveRenderer", new[] { typeof(int) });
        if (s_renderManagerRemoveRenderer == null)
            PatchLogger.LogWarning("[MagicaClothRebuilder] ResolveRenderManagerReflection: RenderManager.RemoveRenderer(int) 未解決");
    }

    /// <summary>
    /// oldComp.Process.renderHandleList を新規空 List で置換する。後段の async build cancellation 完了時の
    /// DisposeInternal が renderHandleList を foreach して RemoveRenderer する経路を無効化し、
    /// 新 MC の RenderData が誤って Dispose されないようにする。
    ///
    /// `Clear()` ではなく `SetValue(new List<int>())` を使う理由: 本家 RuntimeBuildAsync は build 中の
    /// `for (int i; i &lt; renderHandleList.Count; i++) renderHandleList[i]` (ClothProcess L579) で
    /// list 末尾をインデックスアクセスする。`Clear` 中の race で IndexOutOfRange を踏む可能性がある。
    /// 新 List への置換ならば、本家 build が以前 cache した古い list reference を foreach で安全に走破できる。
    /// </summary>
    private static void ClearRenderHandleListIfPossible(Component oldComp)
    {
        if (oldComp == null) return;
        ResolveRenderManagerReflection();
        if (s_processProp == null || s_renderHandleListField == null) return;
        try
        {
            var process = s_processProp.GetValue(oldComp);
            if (process == null) return;
            if (s_renderHandleListField.GetValue(process) is IList oldList)
            {
                int count = oldList.Count;
                if (count > 0)
                {
                    var newList = (IList)Activator.CreateInstance(s_renderHandleListField.FieldType);
                    s_renderHandleListField.SetValue(process, newList);
                    PatchLogger.LogDebug($"[MagicaClothRebuilder] replaced MC.Process.renderHandleList ({count} handles) with empty list before DestroyImmediate");
                }
            }
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[MagicaClothRebuilder] ClearRenderHandleListIfPossible 失敗: {ex.Message}");
        }
    }

    /// <summary>
    /// 各 sourceRenderer の InstanceID で本家 <c>RenderManager.RemoveRenderer(handle)</c> を呼び、
    /// renderDataDict 内に残留している RenderData の refcount を decrement する (count==0 で Dispose+Remove)。
    ///
    /// Why: ClothProcess.DisposeInternal は <c>isBuild=true</c> で早期 return するため、async build が
    /// 進行中の MC が DestroyImmediate されると renderDataDict 内の RenderData が回収されない。
    /// 後続の新 MC の async BuildAndRun が `if (!renderDataDict.ContainsKey(instanceID))` の早期 return で
    /// 旧 RenderData を REUSE し、customMesh が旧 originalMesh から Instantiate されて連続切替時に
    /// 前 donor (or stock) の mesh shape が残る症状の根本原因。
    ///
    /// 本家 API (RemoveRenderer) を経由する設計理由: 直接 Dispose+Remove だと refcount > 1 (legitimate な
    /// 他 MC からの参照が残っている) のケースで dangling 化する。本家 API は refcount==0 でのみ Dispose+Remove
    /// するため、leak case (refcount=1) で正常動作 + 多重参照ケースでも安全 (decrement のみ)。
    /// </summary>
    private static void ForceClearRenderDataForSmrs(IList sourceRenderers)
    {
        if (sourceRenderers == null) return;
        ResolveRenderManagerReflection();
        if (s_magicaManagerType == null || s_renderDataDictField == null) return;

        object renderManager = null;
        if (s_magicaManagerRenderField != null)
            renderManager = s_magicaManagerRenderField.GetValue(null);
        else
        {
            var renderProp = s_magicaManagerType.GetProperty("Render", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            renderManager = renderProp?.GetValue(null);
        }
        if (renderManager == null) return;

        if (s_renderDataDictField.GetValue(renderManager) is not IDictionary dict) return;
        if (s_renderManagerRemoveRenderer == null) return;

        int cleared = 0;
        int stillResident = 0;
        foreach (var item in sourceRenderers)
        {
            if (!(item is SkinnedMeshRenderer smr) || smr == null) continue;
            int instanceId = smr.GetInstanceID();
            // 本家 lock(renderDataDict) と同一 monitor を使い相互排他。
            lock (dict)
            {
                if (!dict.Contains(instanceId)) continue;
            }
            try { s_renderManagerRemoveRenderer.Invoke(renderManager, new object[] { instanceId }); }
            catch (Exception ex) { PatchLogger.LogWarning($"[MagicaClothRebuilder] RenderManager.RemoveRenderer 失敗 (smr#{instanceId}): {ex.Message}"); continue; }
            // 検証: refcount > 1 の多重参照だった場合 dict には残る。多重 leak 検出のため log。
            lock (dict)
            {
                if (dict.Contains(instanceId)) stillResident++;
            }
            cleared++;
        }
        if (cleared > 0)
            PatchLogger.LogDebug($"[MagicaClothRebuilder] force-cleared {cleared} RenderData entries from RenderManager.renderDataDict (still-resident={stillResident})");
    }

    /// <summary>
    /// 新 SerData2 内 <c>initData.clothSetupDataList[*].originalMesh</c> を null 化する。
    ///
    /// Why: <see cref="RenderSetupData"/> ctor (MagicaClothV2/RenderSetupData.cs:387) が BuildAndRun 時に
    ///   <c>referenceInitSetupData.originalMesh != null && skinnedMeshRenderer.sharedMesh != originalMesh</c>
    /// の条件で SMR.sharedMesh を donor の baked originalMesh に巻き戻し、bones / rootBone も上書きする。
    /// donor の <c>serializeData2</c> を deep copy する経路では donor 由来 baked originalMesh が transfer
    /// されるため、SwapSmr で設定した sharedMesh と differ する条件で override が発火 → 連続 donor 切替時に
    /// 前 donor の mesh shape が残る症状になる (memory `reference_magicacloth_originalmesh_override.md`)。
    ///
    /// FixMod の意図は「SwapSmr で設定した mesh / bones をそのまま使う」なので、この override は無効化したい。
    /// originalMesh = null にすれば override 条件が false になり、BuildAndRun は現 sharedMesh を素直に採取する。
    /// SafeDestroyPreservingSmrState 経由の rebuild / restore / recover 経路は SMR.sharedMesh を直前に
    /// 再注入しているため、null 化により target 元 mesh への巻き戻し依存が破綻することは無い。
    /// </summary>
    private static void NullOutCachedOriginalMeshes(object newSerData2)
    {
        if (newSerData2 == null) return;
        var bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // resolve は 1 回だけ。途中で field 未解決ならエラーを出して flag は true のまま放置する
        // (= fix が永続的に no-op になる) のは意図通り。MagicaCloth の仕様変更時にログ連発を避け、
        // 残りの rebuild path は SafeDestroyPreservingSmrState の SMR.sharedMesh 再注入に縮退委譲する。
        // LogError 級にしてある: MagicaCloth2 バージョンアップで fix が永続停止する重大事象のため、
        // BepInEx コンソールでも視認できるよう警告を昇格している。
        if (!s_originalMeshNullifyResolved)
        {
            s_originalMeshNullifyResolved = true;
            s_initDataField = newSerData2.GetType().GetField("initData", bf);
            if (s_initDataField == null)
            {
                PatchLogger.LogError("[MagicaClothRebuilder] NullOutCachedOriginalMeshes: initData field 未解決 (MagicaCloth 仕様変更の可能性、fix 無効化)");
                return;
            }
            s_clothSetupDataListField = s_initDataField.FieldType.GetField("clothSetupDataList", bf);
            if (s_clothSetupDataListField == null)
            {
                PatchLogger.LogError("[MagicaClothRebuilder] NullOutCachedOriginalMeshes: clothSetupDataList field 未解決");
                return;
            }
            var listType = s_clothSetupDataListField.FieldType;
            var elemType = listType.IsGenericType && listType.GetGenericArguments().Length == 1
                ? listType.GetGenericArguments()[0]
                : null;
            if (elemType == null)
            {
                PatchLogger.LogError("[MagicaClothRebuilder] NullOutCachedOriginalMeshes: clothSetupDataList の要素型が解決できず");
                return;
            }
            s_setupOriginalMeshField = elemType.GetField("originalMesh", bf);
            if (s_setupOriginalMeshField == null)
            {
                PatchLogger.LogError("[MagicaClothRebuilder] NullOutCachedOriginalMeshes: originalMesh field 未解決");
                return;
            }
        }
        if (s_initDataField == null || s_clothSetupDataListField == null || s_setupOriginalMeshField == null) return;

        var initData = s_initDataField.GetValue(newSerData2);
        if (initData == null) return;
        if (s_clothSetupDataListField.GetValue(initData) is not IList list) return;

        int nulled = 0;
        int total = 0;
        foreach (var item in list)
        {
            if (item == null) continue;
            total++;
            if (s_setupOriginalMeshField.GetValue(item) != null)
            {
                s_setupOriginalMeshField.SetValue(item, null);
                nulled++;
            }
        }
        if (nulled > 0)
            PatchLogger.LogDebug($"[MagicaClothRebuilder] nullify clothSetupDataList originalMesh: {nulled} entries (total={total})");
    }

    /// <summary>
    /// 新 SerData2 の <c>preBuildData.enabled = false</c> をセットして PreBuild path を回避する。
    ///
    /// Why: PreBuild path (<c>ClothProcess</c> L329-332) では <c>RenderData.originalMesh =
    /// preBuildUniqueSerializeData.originalMesh</c> となり、donor の <b>baked</b> mesh asset から
    /// customMesh が <c>Instantiate</c> される (<c>RenderData.cs</c> L72, L111)。FixMod が SwapSmr で
    /// SMR.sharedMesh を donor の SMR mesh に切り替えても、customMesh はこの baked mesh 由来になり、
    /// 連続 donor 切替で前 donor の shape が残る症状になる。
    ///
    /// PreBuild を無効化すれば non-PreBuild path に落ち、<c>RenderData.originalMesh =
    /// setupData.originalMesh = SMR.sharedMesh</c> (RenderSetupData ctor L484) で初期化されるため、
    /// customMesh は SwapSmr で設定した donor の現 mesh から生成される。
    ///
    /// 副作用: PreBuild 由来の最適化 (proxy mesh / 制約データ) が失われ初期化コストが増える可能性があるが、
    /// 衣装切替時の一時的な処理なので許容範囲。FixMod の SwapSmr 経路では target hierarchy の bones を使うため、
    /// donor の baked transform graph も流用できないので PreBuild path 自体が本来不適切。
    /// </summary>
    private static void DisablePreBuildIfPresent(object newSerData2)
    {
        if (newSerData2 == null) return;
        var bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // NullOutCachedOriginalMeshes と同様、resolve 失敗時の永続 no-op を視認しやすくするため
        // LogError 級に格上げ。MagicaCloth 仕様変更時の早期検知用。
        if (!s_preBuildDisableResolved)
        {
            s_preBuildDisableResolved = true;
            s_preBuildDataField = newSerData2.GetType().GetField("preBuildData", bf);
            if (s_preBuildDataField == null)
            {
                PatchLogger.LogError("[MagicaClothRebuilder] DisablePreBuildIfPresent: preBuildData field 未解決 (MagicaCloth 仕様変更の可能性、fix 無効化)");
                return;
            }
            s_preBuildEnabledField = s_preBuildDataField.FieldType.GetField("enabled", bf);
            if (s_preBuildEnabledField == null)
            {
                PatchLogger.LogError("[MagicaClothRebuilder] DisablePreBuildIfPresent: preBuildData.enabled field 未解決");
                return;
            }
        }
        if (s_preBuildDataField == null || s_preBuildEnabledField == null) return;

        var preBuildData = s_preBuildDataField.GetValue(newSerData2);
        if (preBuildData == null) return;
        var enabled = s_preBuildEnabledField.GetValue(preBuildData) as bool? ?? false;
        if (enabled)
        {
            s_preBuildEnabledField.SetValue(preBuildData, false);
            PatchLogger.LogDebug("[MagicaClothRebuilder] disable PreBuild path: preBuildData.enabled false 化");
        }
    }

    /// <summary>
    /// targetGo に新規 MagicaCloth を AddComponent し、srcSerData/srcSerData2 を field-wise コピー、
    /// sourceRenderers を sourceRenderers で上書きして BuildAndRun する。
    /// build 後に ReplaceTransform で donor 由来 Transform 参照を target hierarchy に再マップする。
    /// build 失敗時は新 component を Destroy。
    /// </summary>
    private static bool BuildFromConfig(
        GameObject targetGo, GameObject character, Type magicaType,
        object srcSerData, object srcSerData2, IList sourceRenderers, string label, bool remapColliders)
    {
        var bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var serProp = s_serProp;
        var s2Method = s_serData2Method;
        var disableAutoBuildMethod = s_disableAutoBuildMethod;
        var buildAndRunMethod = s_buildAndRunMethod;
        var replaceTransformMethod = s_replaceTransformMethod;
        var resetClothMethod = s_resetClothMethod;
        var setParameterChangeMethod = s_setParameterChangeMethod;

        if (serProp == null || s2Method == null || disableAutoBuildMethod == null || buildAndRunMethod == null)
        {
            PatchLogger.LogError("[MagicaClothRebuilder] MagicaCloth API 未解決");
            return false;
        }

        var newComp = targetGo.AddComponent(magicaType);
        try { disableAutoBuildMethod.Invoke(newComp, null); } catch { }

        var newSerData = serProp.GetValue(newComp);
        // donor → newComp の sub-object 共有を断つため deep clone。
        // shallow copy だと colliderCollisionConstraint 等が donor と共有 → RemapColliderRefs の
        // in-place mutation で donor が破壊され、target GO 死亡で donor.cc が dead ref で詰まる。
        CopyFields(srcSerData, newSerData, deep: true);

        var srcField = newSerData.GetType().GetField("sourceRenderers", bf);
        srcField?.SetValue(newSerData, sourceRenderers);

        var newSerData2 = s2Method.Invoke(newComp, null);
        CopyFields(srcSerData2, newSerData2, deep: true);

        // RenderSetupData ctor (MagicaClothV2:387) の sharedMesh 巻き戻し override を無効化。
        // donor 由来の baked originalMesh が残ると BuildAndRun が SwapSmr 後の sharedMesh を donor 元に
        // 戻してしまい、連続 donor 切替時に前 donor の mesh shape が残る (memory: originalMesh-override)。
        NullOutCachedOriginalMeshes(newSerData2);

        // PreBuild path を無効化して RenderData.originalMesh = SMR.sharedMesh (= SwapSmr 設定値) にする。
        // PreBuild path だと customMesh が donor の baked mesh asset から Instantiate されて連続切替で
        // 前 donor の shape が残る症状の根本原因 (donor 切替で SMR.sharedMesh は変わるが customMesh は
        // baked mesh 経由で生成されるため visual に反映されない)。
        DisablePreBuildIfPresent(newSerData2);

        // donor 由来の colliderList / collisionBones は donor body の ColliderComponent / Transform を
        // 参照しているため、target の同名 GameObject に attached された同型 component / Transform に
        // 差し替える。差し替えできなかったエントリは drop して衝突計算から除外する。
        // BuildAndRun 前に行う必要 (build 時 collision データが構築されるため)。
        if (remapColliders)
        {
            RemapColliderRefs(newSerData, character);
        }

        bool result = false;
        try
        {
            var ret = buildAndRunMethod.Invoke(newComp, null);
            result = ret is bool b && b;
        }
        catch (Exception ex)
        {
            PatchLogger.LogError($"[MagicaClothRebuilder] BuildAndRun 例外 ({label}): {ex}");
        }

        if (!result)
        {
            // build 失敗診断: source SMR の状態をダンプ
            for (int i = 0; sourceRenderers != null && i < sourceRenderers.Count; i++)
            {
                var smr = sourceRenderers[i] as SkinnedMeshRenderer;
                if (smr == null) { PatchLogger.LogWarning($"[MagicaClothRebuilder] [{label}-FAIL] sourceRenderer[{i}]=null"); continue; }
                var meshInfo = smr.sharedMesh != null ? $"{smr.sharedMesh.name}(vc={smr.sharedMesh.vertexCount})" : "<null>";
                PatchLogger.LogWarning($"[MagicaClothRebuilder] [{label}-FAIL] {smr.name}: enabled={smr.enabled} active={smr.gameObject.activeInHierarchy} bones={smr.bones?.Length ?? -1} mesh={meshInfo}");
            }
        }

        if (result && replaceTransformMethod != null)
        {
            var dict = new Dictionary<string, Transform>();
            foreach (var t in character.GetComponentsInChildren<Transform>(true))
            {
                if (t == null) continue;
                if (!dict.ContainsKey(t.name)) dict[t.name] = t;
            }
            try { replaceTransformMethod.Invoke(newComp, new object[] { dict }); }
            catch (Exception ex) { PatchLogger.LogWarning($"[MagicaClothRebuilder] post-build ReplaceTransform 失敗 ({label}): {ex.Message}"); }
        }

        // build 成功後、simulation が走らないケース (KANA/Babydoll on Casual 等) への防御策。
        // SetParameterChange() で内部パラメータ更新フラグを立て、ResetCloth(true) で
        // simulation 状態を強制再初期化 (T-pose に戻す + 内部 buffer リセット)。
        if (result)
        {
            try { setParameterChangeMethod?.Invoke(newComp, null); }
            catch (Exception ex) { PatchLogger.LogWarning($"[MagicaClothRebuilder] SetParameterChange 失敗 ({label}): {ex.Message}"); }
            try { resetClothMethod?.Invoke(newComp, new object[] { true }); }
            catch (Exception ex) { PatchLogger.LogWarning($"[MagicaClothRebuilder] ResetCloth 失敗 ({label}): {ex.Message}"); }
        }

        PatchLogger.LogDebug($"[MagicaClothRebuilder] {label} {(result ? "OK" : "失敗")}: {character.name}/{targetGo.name} (srcRenderers={sourceRenderers?.Count ?? -1})");

        if (!result)
        {
            UnityEngine.Object.DestroyImmediate(newComp);
        }
        return result;
    }

    /// <summary>
    /// donor 由来の serializeData.colliderCollisionConstraint.colliderList /collisionBones を
    /// target hierarchy 内同名 GameObject の同型 component / 同名 Transform に再マップ。
    /// 不一致エントリは drop (衝突対象から除外)。BuildAndRun 前に呼ぶこと。
    /// </summary>
    private static void RemapColliderRefs(object serData, GameObject character)
    {
        var bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var ccField = serData.GetType().GetField("colliderCollisionConstraint", bf);
        if (ccField == null) return;
        var ccData = ccField.GetValue(serData);
        if (ccData == null) return;

        // target hierarchy: name → Transform 辞書。
        // COSTUME 切替直後は古い衣装の orphan Transform (祖先 inactive) と新衣装の Transform が同名で
        // 重複するケースがある。素朴な先勝ちだと orphan に bind され simulate 時 no-effect になるため、
        // activeInHierarchy=true な Transform を優先採用する。
        var transformByName = new Dictionary<string, Transform>();
        foreach (var t in character.GetComponentsInChildren<Transform>(true))
        {
            if (t == null) continue;
            if (!transformByName.TryGetValue(t.name, out var existing))
            {
                transformByName[t.name] = t;
                continue;
            }
            if (!existing.gameObject.activeInHierarchy && t.gameObject.activeInHierarchy)
            {
                transformByName[t.name] = t;
            }
        }

        int colRemapped = 0, colCloned = 0, colInjected = 0, colDropped = 0;
        var colListField = ccData.GetType().GetField("colliderList", BindingFlags.Instance | BindingFlags.Public);
        if (colListField?.GetValue(ccData) is IList colList)
        {
            for (int i = colList.Count - 1; i >= 0; i--)
            {
                var donorCollider = colList[i] as Component;
                if (donorCollider == null) { colList.RemoveAt(i); colDropped++; continue; }
                var donorGoName = donorCollider.gameObject.name;
                var donorParentBoneName = donorCollider.transform.parent != null ? donorCollider.transform.parent.name : null;
                var (resolved, action) = ResolveOrInjectCollider(donorCollider, donorGoName, donorParentBoneName, transformByName);
                if (resolved == null) { colList.RemoveAt(i); colDropped++; continue; }
                colList[i] = resolved;
                switch (action)
                {
                    case "remap": colRemapped++; break;
                    case "clone": colCloned++; break;
                    case "inject": colInjected++; break;
                }
            }

        }

        int boneRemapped = 0, boneDropped = 0;
        var boneListField = ccData.GetType().GetField("collisionBones", BindingFlags.Instance | BindingFlags.Public);
        if (boneListField?.GetValue(ccData) is IList boneList)
        {
            for (int i = boneList.Count - 1; i >= 0; i--)
            {
                var donorBone = boneList[i] as Transform;
                if (donorBone == null) { boneList.RemoveAt(i); boneDropped++; continue; }
                if (!transformByName.TryGetValue(donorBone.name, out var targetBone))
                {
                    boneList.RemoveAt(i); boneDropped++;
                    continue;
                }
                boneList[i] = targetBone;
                boneRemapped++;
            }
        }

        PatchLogger.LogDebug($"[MagicaClothRebuilder] collider remap: {character.name} (colliderList: remapped={colRemapped} cloned={colCloned} injected={colInjected} dropped={colDropped}, collisionBones: remapped={boneRemapped} dropped={boneDropped})");
    }

    /// <summary>
    /// donor collider component を target hierarchy に解決する 3 段階フォールバック helper。
    /// 戻り値: (component, action) — action ∈ {"remap","clone","inject"}。失敗時 (null, null)。
    ///
    /// 1. <paramref name="goName"/> 同名 GO + 同型 component → <b>remap</b> (既存再利用)
    /// 2. 同名 GO 存在 / 同型 component 無 → <b>clone</b> (<see cref="CloneColliderTo"/> で AddComponent、marker 付与、DestroyGameObject=false)
    /// 3. 同名 GO 無 / <paramref name="boneName"/> 親 bone あり → <b>inject</b> (<see cref="InjectColliderGo"/> で 新規 GO 生成 + AddComponent、marker DestroyGameObject=true)
    /// 4. いずれも失敗 → (null, null)
    /// </summary>
    private static (Component comp, string action) ResolveOrInjectCollider(
        Component source,
        string goName,
        string boneName,
        Dictionary<string, Transform> transformByName)
    {
        if (source == null || string.IsNullOrEmpty(goName)) return (null, null);
        if (transformByName.TryGetValue(goName, out var existingGoTrans))
        {
            var sourceType = source.GetType();
            var existing = existingGoTrans.gameObject.GetComponent(sourceType);
            if (existing != null) return (existing, "remap");
            var cloned = CloneColliderTo(existingGoTrans.gameObject, source);
            return cloned != null ? (cloned, "clone") : (null, null);
        }
        if (string.IsNullOrEmpty(boneName)) return (null, null);
        if (!transformByName.TryGetValue(boneName, out var parentBoneTrans)) return (null, null);
        var injected = InjectColliderGo(parentBoneTrans.gameObject, source, goName);
        return injected != null ? (injected, "inject") : (null, null);
    }

    /// <summary>
    /// target に MCC 子 GO が無いケース (Bunnygirl 等) の救済。<paramref name="parentBone"/> 配下に新規 GO 生成し、
    /// source の local TRS / layer を継承、同型 component を AddComponent + <see cref="CopyFields"/> 移植。
    /// <see cref="MagicaClothInjectedColliderMarker"/> を <c>DestroyGameObject=true</c> で付与し Restore 時 GO ごと destroy。
    /// layer 明示コピー必須 (`feedback_inject_smr_layer.md`: 新規 GO は親 layer 非継承で grey 描画になる)。
    /// local TRS / center / size はそのまま転写するため、donor と target の同名 bone の局所空間一致を前提とする
    /// (twist / 軸 flip 非対称 rig ではズレる可能性あり)。vanilla MCC 命名 (<c>MCC (R_Upperleg_skinJT)</c>) では検証済。
    /// </summary>
    private static Component InjectColliderGo(GameObject parentBone, Component source, string goName)
    {
        if (parentBone == null || source == null || string.IsNullOrEmpty(goName)) return null;
        var sourceGo = source.gameObject;
        if (sourceGo == null) return null;
        GameObject newGo;
        try
        {
            newGo = new GameObject(goName);
            newGo.transform.SetParent(parentBone.transform, worldPositionStays: false);
            newGo.transform.localPosition = sourceGo.transform.localPosition;
            newGo.transform.localRotation = sourceGo.transform.localRotation;
            newGo.transform.localScale = sourceGo.transform.localScale;
            newGo.layer = parentBone.layer;
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[MagicaClothRebuilder] InjectColliderGo GO 生成失敗: {goName} under {parentBone.name}: {ex.Message}");
            return null;
        }
        Component injected;
        try { injected = newGo.AddComponent(source.GetType()); }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[MagicaClothRebuilder] InjectColliderGo AddComponent 失敗: {source.GetType().FullName} on {newGo.name}: {ex.Message}");
            UnityEngine.Object.DestroyImmediate(newGo);
            return null;
        }
        if (injected == null)
        {
            UnityEngine.Object.DestroyImmediate(newGo);
            return null;
        }
        try { CopyFields(source, injected, deep: false); }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[MagicaClothRebuilder] InjectColliderGo CopyFields 失敗: {source.GetType().FullName} on {newGo.name}: {ex.Message}");
            UnityEngine.Object.DestroyImmediate(newGo);
            return null;
        }
        var marker = newGo.AddComponent<MagicaClothInjectedColliderMarker>();
        marker.DestroyGameObject = true;
        return injected;
    }

    /// <summary>
    /// target bone GO に donor collider component と同型の component を AddComponent し、
    /// <see cref="CopyFields"/> (shallow) で値型/public field を移植する。同型 component が既に
    /// あれば再利用 (連続 Override の冪等性確保)。新規 add 時は <see cref="MagicaClothInjectedColliderMarker"/>
    /// を同 GO に付与 (<c>DestroyGameObject=false</c>) し、Restore で component のみ destroy できるようにする。
    /// CopyFields shallow モードでは <see cref="Type.GetFields(BindingFlags)"/> の仕様上、private 継承 field
    /// (e.g. ColliderComponent の back-ref cprocess) は転写されない。public field (center/size/radius 等) のみ
    /// 移植され、back-ref は新 component の Build 経路で再設定される。
    /// </summary>
    private static Component CloneColliderTo(GameObject target, Component donor)
    {
        if (target == null || donor == null) return null;
        var donorType = donor.GetType();
        var existing = target.GetComponent(donorType);
        if (existing != null)
        {
            // 既に同型 collider 在 (前回 Override の clone 残留 or stock 有) → 再利用。
            // marker は前回付与時のまま (重複 add 不要)。stock 由来の場合 marker 無 = Restore 対象外。
            return existing;
        }
        Component cloned;
        try { cloned = target.AddComponent(donorType); }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[MagicaClothRebuilder] CloneColliderTo AddComponent 失敗: {donorType.FullName} on {target.name}: {ex.Message}");
            return null;
        }
        if (cloned == null) return null;
        try { CopyFields(donor, cloned, deep: false); }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[MagicaClothRebuilder] CloneColliderTo CopyFields 失敗: {donorType.FullName} on {target.name}: {ex.Message}");
            UnityEngine.Object.DestroyImmediate(cloned);
            return null;
        }
        if (target.GetComponent<MagicaClothInjectedColliderMarker>() == null)
        {
            target.AddComponent<MagicaClothInjectedColliderMarker>();
        }
        return cloned;
    }

    /// <summary>
    /// inject された MagicaCloth collider を全削除。<see cref="MagicaClothInjectedColliderMarker.DestroyGameObject"/> で分岐:
    /// true=GO ごと destroy (<see cref="InjectColliderGo"/> 経路) / false=Magica*Collider と marker のみ destroy
    /// (<see cref="CloneColliderTo"/> 経路、GO は target body bone のため残置)。
    /// 前提: clone 経路の同 GO に複数 type の Magica*Collider は共存しない (vanilla は 1 GO 1 collider)。
    /// </summary>
    private static void CleanupInjectedColliders(GameObject character)
    {
        if (character == null) return;
        int destroyedColliders = 0, destroyedMarkers = 0, destroyedGos = 0;
        var markers = character.GetComponentsInChildren<MagicaClothInjectedColliderMarker>(true);
        foreach (var marker in markers)
        {
            if (marker == null) continue;
            var go = marker.gameObject;
            if (marker.DestroyGameObject)
            {
                // GO ごと destroy (新規 inject 経路)。同 GO 上の collider component は GO destroy で
                // 巻き取られるため明示 destroy 不要。
                try { UnityEngine.Object.DestroyImmediate(go); destroyedGos++; destroyedMarkers++; }
                catch (Exception ex) { PatchLogger.LogWarning($"[MagicaClothRebuilder] injected GO destroy 失敗: {go.name}: {ex.Message}"); }
                continue;
            }
            var comps = go.GetComponents<Component>();
            foreach (var comp in comps)
            {
                if (comp == null || comp == marker) continue;
                var fullName = comp.GetType().FullName;
                if (fullName == null) continue;
                if (fullName.StartsWith("MagicaCloth2.Magica", StringComparison.Ordinal)
                    && fullName.EndsWith("Collider", StringComparison.Ordinal))
                {
                    // 個別 destroy の例外で marker 削除に到達できず「marker 在 / collider 無」の
                    // 整合性崩れ状態にならないよう、collider ごとに try/catch する。
                    try { UnityEngine.Object.DestroyImmediate(comp); destroyedColliders++; }
                    catch (Exception ex) { PatchLogger.LogWarning($"[MagicaClothRebuilder] injected collider destroy 失敗: {fullName} on {go.name}: {ex.Message}"); }
                }
            }
            try { UnityEngine.Object.DestroyImmediate(marker); destroyedMarkers++; }
            catch (Exception ex) { PatchLogger.LogWarning($"[MagicaClothRebuilder] injected marker destroy 失敗: {go.name}: {ex.Message}"); }
        }
        if (destroyedMarkers > 0)
        {
            PatchLogger.LogDebug($"[MagicaClothRebuilder] injected collider 掃除: {character.name} (markers={destroyedMarkers}, colliders={destroyedColliders}, gos={destroyedGos})");
        }
    }

    /// <summary>
    /// reflection で全 instance field を src→dst へコピー。List/Array 以外は参照コピー。
    /// Transform 参照は donor 側を指すため呼出側で ReplaceTransform 必須。
    /// <paramref name="deep"/>=true で MagicaCloth2 系 custom class field を再帰 deep-clone:
    /// in-place mutation (<see cref="RemapColliderRefs"/> 等) が donor SerializeData を破壊 → 後続 apply で物理消失するのを防ぐ。
    /// </summary>
    private static void CopyFields(object src, object dst, bool deep = false, HashSet<object> visited = null)
    {
        if (src == null || dst == null) return;
        var type = src.GetType();
        if (dst.GetType() != type) return;
        if (deep)
        {
            if (visited == null) visited = new HashSet<object>(RefEqualityComparer.Instance);
            if (!visited.Add(src)) return; // 循環参照ガード (再訪問時は dst をそのままにして抜ける)
        }
        var bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var field in type.GetFields(bf))
        {
            if (field.IsInitOnly) continue;
            // delegate / event 系は skip (deep clone 不可、shallow も意味薄)
            if (typeof(MulticastDelegate).IsAssignableFrom(field.FieldType)) continue;

            object value;
            try { value = field.GetValue(src); }
            catch (Exception ex) { PatchLogger.LogDebug($"[MCR] reflection field.GetValue 失敗: {field.Name}: {ex.Message}"); continue; }

            // List 経路: 宣言型が IList でも実型が generic List<T> ならそこから要素型を取る。
            // 宣言型の IsGenericType ガードを掛けると非 generic 宣言で deep clone を素通りして
            // donor と shallow share になるため使わない。
            if (value is IList list && !(value is Array))
            {
                Type listType = field.FieldType.IsArray ? null : field.FieldType;
                Type runtimeListType = value.GetType();
                Type instantiationType = listType != null && !listType.IsAbstract && !listType.IsInterface ? listType : runtimeListType;
                Type elemType = null;
                if (instantiationType.IsGenericType && instantiationType.GetGenericArguments().Length == 1)
                    elemType = instantiationType.GetGenericArguments()[0];
                else if (runtimeListType.IsGenericType && runtimeListType.GetGenericArguments().Length == 1)
                    elemType = runtimeListType.GetGenericArguments()[0];

                IList newList = null;
                try { newList = (IList)Activator.CreateInstance(instantiationType); }
                catch (Exception ex) { PatchLogger.LogDebug($"[MCR] list CreateInstance 失敗: {instantiationType?.Name}: {ex.Message}"); newList = null; }
                if (newList != null)
                {
                    bool listFilled = true;
                    bool deepCloneElems = deep && elemType != null && IsMagicaClothCloneableType(elemType);
                    try
                    {
                        foreach (var item in list)
                        {
                            if (deepCloneElems && item != null && IsMagicaClothCloneableType(item.GetType()))
                            {
                                try
                                {
                                    var clone = Activator.CreateInstance(item.GetType());
                                    CopyFields(item, clone, deep: true, visited);
                                    newList.Add(clone);
                                    continue;
                                }
                                catch { /* fall through to shallow add */ }
                            }
                            newList.Add(item);
                        }
                    }
                    catch (Exception ex) { PatchLogger.LogDebug($"[MCR] list 要素コピー失敗: {field.Name}: {ex.Message}"); listFilled = false; }
                    if (listFilled)
                    {
                        try { field.SetValue(dst, newList); continue; }
                        catch { /* fall through to skip */ }
                    }
                }
                // deep mode で list 経路が破綻したら donor 共有を避けるため field を skip。
                // shallow mode では従来通り src の参照を流す (snapshot 経路は破壊されないため許容)。
                if (deep)
                {
                    LogDeepCloneFallback(type, field, value.GetType(), "list");
                    continue;
                }
                try { field.SetValue(dst, value); } catch (Exception ex) { PatchLogger.LogDebug($"[MCR] field.SetValue (list shallow) 失敗: {field.Name}: {ex.Message}"); }
                continue;
            }

            // Array は IList でもあるが新規 Array は Activator.CreateInstance で生成不可のため
            // Array.CreateInstance + Array.Copy で要素 shallow copy する (要素は ref 共有、value 型は値 copy)。
            // shallow share だと MagicaCloth の build/sim で mutation が donor に伝播する懸念があるため
            // deep モード時のみ実施。要素が MagicaCloth.* class なら個別に deep clone する。
            if (deep && value is Array srcArr)
            {
                var elemType = field.FieldType.GetElementType() ?? srcArr.GetType().GetElementType();
                Array newArr = null;
                if (elemType != null)
                {
                    try { newArr = Array.CreateInstance(elemType, srcArr.Length); }
                    catch (Exception ex) { PatchLogger.LogDebug($"[MCR] Array.CreateInstance 失敗: {elemType?.Name}: {ex.Message}"); newArr = null; }
                }
                if (newArr != null)
                {
                    bool arrFilled = true;
                    bool deepCloneElems = IsMagicaClothCloneableType(elemType);
                    try
                    {
                        if (deepCloneElems)
                        {
                            for (int i = 0; i < srcArr.Length; i++)
                            {
                                var item = srcArr.GetValue(i);
                                if (item != null && IsMagicaClothCloneableType(item.GetType()))
                                {
                                    try
                                    {
                                        var clone = Activator.CreateInstance(item.GetType());
                                        CopyFields(item, clone, deep: true, visited);
                                        newArr.SetValue(clone, i);
                                        continue;
                                    }
                                    catch { /* fall through to shallow set */ }
                                }
                                newArr.SetValue(item, i);
                            }
                        }
                        else
                        {
                            Array.Copy(srcArr, newArr, srcArr.Length);
                        }
                    }
                    catch (Exception ex) { PatchLogger.LogDebug($"[MCR] array 要素コピー失敗: {field.Name}: {ex.Message}"); arrFilled = false; }
                    if (arrFilled)
                    {
                        try { field.SetValue(dst, newArr); continue; }
                        catch { /* fall through to skip */ }
                    }
                }
                // deep mode で array 経路が破綻したら donor 共有を避けるため field を skip。
                LogDeepCloneFallback(type, field, value.GetType(), "array");
                continue;
            }

            // deep mode: MagicaCloth2 namespace の custom class のみ再帰 deep-clone
            // (Unity asset / System / UnityEngine 型は誤爆を避けるため shallow ref のまま)
            if (deep && value != null && IsMagicaClothCloneableType(value.GetType()))
            {
                try
                {
                    var clone = Activator.CreateInstance(value.GetType());
                    CopyFields(value, clone, deep: true, visited);
                    field.SetValue(dst, clone);
                    continue;
                }
                catch
                {
                    // donor 共有を避けるため class 経路は raw 参照を流さず field を skip。
                    LogDeepCloneFallback(type, field, value.GetType(), "class");
                    continue;
                }
            }

            try { field.SetValue(dst, value); } catch (Exception ex) { PatchLogger.LogDebug($"[MCR] field.SetValue 失敗: {field.Name}: {ex.Message}"); }
        }
    }

    // 同 (ownerType, fieldName, valueType, scope) の組合わせはプロセス内で 1 回だけログ出す。
    private static readonly HashSet<string> s_warnedDeepCloneFallbacks = new();
    private static void LogDeepCloneFallback(Type ownerType, FieldInfo field, Type valueType, string scope)
    {
        var key = $"{ownerType.FullName}.{field.Name}|{valueType.FullName}|{scope}";
        if (!s_warnedDeepCloneFallbacks.Add(key)) return;
        PatchLogger.LogWarning($"[MagicaClothRebuilder] deep clone fallback ({scope}, field skipped to avoid donor share): {ownerType.Name}.{field.Name} ({valueType.FullName})");
    }

    /// <summary>
    /// deep-clone 対象判定。MagicaCloth2 namespace の custom class のみ。
    /// 値型 / string / Unity asset (UnityEngine.Object 派生) / System / UnityEngine 名前空間は除外。
    /// </summary>
    private static bool IsMagicaClothCloneableType(Type type)
    {
        if (type == null || type.IsValueType || type.IsPrimitive || type.IsEnum) return false;
        if (type == typeof(string)) return false;
        if (type.IsArray) return false; // Array は専用パスで処理 (Activator.CreateInstance 不可)
        if (typeof(UnityEngine.Object).IsAssignableFrom(type)) return false;
        var ns = type.Namespace ?? "";
        return ns.StartsWith("MagicaCloth", StringComparison.Ordinal);
    }

    /// <summary>
    /// netstandard2.1 では <c>System.Collections.Generic.ReferenceEqualityComparer</c> が利用不可
    /// (.NET 5+ 標準) のため自前実装。
    /// </summary>
    private sealed class RefEqualityComparer : IEqualityComparer<object>
    {
        public static readonly RefEqualityComparer Instance = new RefEqualityComparer();
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    /// <summary>
    /// DIAGNOSTIC ONLY — SwimWear 物理問題解決後に削除。
    /// character 配下の MagicaCloth component を全列挙し、診断用情報 (name, clothType, sourceRenderersCount) を返す。
    /// </summary>
    internal static IEnumerable<(string Name, string ClothType, int SourceRendererCount)> EnumerateForDiag(GameObject character)
    {
        var magicaType = ResolveType();
        if (magicaType == null) yield break;
        var bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var serProp = s_serProp;
        if (serProp == null) yield break;

        foreach (var comp in character.GetComponentsInChildren(magicaType, true))
        {
            var name = (comp as UnityEngine.Object)?.name ?? "<null>";
            var sdata = serProp.GetValue(comp);
            var ct = sdata?.GetType().GetField("clothType", bf)?.GetValue(sdata)?.ToString() ?? "<unknown>";
            int srcCount = -1;
            if (sdata != null)
            {
                var srcField = sdata.GetType().GetField("sourceRenderers", bf);
                if (srcField?.GetValue(sdata) is IList list) srcCount = list.Count;
            }
            yield return (name, ct, srcCount);
        }
    }
}
