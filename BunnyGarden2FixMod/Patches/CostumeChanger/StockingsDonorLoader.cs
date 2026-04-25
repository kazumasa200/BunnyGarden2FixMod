using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BunnyGarden2FixMod.Utils;
using GB;
using GB.Game;
using GB.Scene;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.SceneManagement;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// 水着×ストッキング用に、全 6 キャラの Uniform prefab から mesh_stockings SMR を
/// 起動時にプリロードしてキャラ別キャッシュする。
///
/// Addressables.LoadAssetAsync&lt;GameObject&gt; で生 asset を取得する。
/// InstantiateAsync 経路ではランタイム処理により mesh_stockings が失われる
/// （costume が Uniform でも mesh_skin_lower_foot 構造に書き換わる）ため不可。
///
/// 用途: SwimWearStockingPatch が水着時の ApplyStocking 同期処理で
/// キャラ固有の donor SMR を取り出して bone リマップして注入する。
/// </summary>
public class StockingsDonorLoader : MonoBehaviour
{
    private static readonly Dictionary<int, SkinnedMeshRenderer> s_stockingsMesh = new();
    // Uniform の mesh_skin_lower（skin_stocking / skin_stocking_lower blendShape 入り、verts=2193/2234）
    // 水着の mesh_skin_lower（shapes=0, verts=半分）を差し替えてめり込み防止する用
    private static readonly Dictionary<int, SkinnedMeshRenderer> s_lowerMesh = new();
    // handle は保持し続ける（mesh 参照を維持するため Release しない）
    private static readonly List<AsyncOperationHandle<GameObject>> s_assetHandles = new();
    // type 1..4 のストッキングマテリアル（[0] 未使用）
    private static readonly Material[] s_materials = new Material[5];

    private static readonly string[] s_materialPaths = new string[5]
    {
        null,
        "Character/PC00_Common/Materials/stockings/m_stockings.mat",
        "Character/PC00_Common/Materials/stockings/m_stockings_white.mat",
        "Character/PC00_Common/Materials/stockings/m_fishnetstockings.mat",
        "Character/PC00_Common/Materials/stockings/m_fishnetstockings_white.mat",
    };

    public static bool IsReady { get; private set; }

    public static void Initialize(GameObject parent)
    {
        parent.AddComponent<StockingsDonorLoader>();
    }

    private void OnEnable()
    {
        SceneManager.sceneUnloaded += OnSceneUnloaded;
    }

    private void OnDisable()
    {
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
    }

    private static void OnSceneUnloaded(Scene scene)
    {
        // donor asset は永続保持（Release しない）。水着キャラ配下に注入・移植した mesh は
        // シーン破棄で fake-null 化するため SwimWearStockingPatch 側のキャッシュを一掃する。
        SwimWearStockingPatch.OnSceneUnloaded();
    }

    public static bool TryGetDonor(CharID id, out SkinnedMeshRenderer smr)
    {
        return s_stockingsMesh.TryGetValue((int)id, out smr) && smr != null;
    }

    public static bool TryGetLowerDonor(CharID id, out SkinnedMeshRenderer smr)
    {
        return s_lowerMesh.TryGetValue((int)id, out smr) && smr != null;
    }

    public static Material GetMaterial(int type)
    {
        if (type >= 1 && type <= 4) return s_materials[type];
        return null;
    }

    private IEnumerator Start()
    {
        yield return new WaitUntil(() => GBSystem.Instance != null && GBSystem.Instance.RefSaveData() != null);

        for (int i = 0; i < 6; i++)
        {
            var id = (CharID)i;
            var key = CharacterHandle.COSTUME_FILE_PATH(id, CostumeType.Uniform);
            var h = Addressables.LoadAssetAsync<GameObject>(key);
            yield return h;

            if (!h.IsValid() || h.Result == null)
            {
                PatchLogger.LogWarning($"[StockingsDonorLoader] asset ロード失敗: {key}");
                if (h.IsValid()) Addressables.Release(h);
                continue;
            }

            var smr = h.Result.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .FirstOrDefault(m => m.name == "mesh_stockings");

            if (smr == null || smr.sharedMesh == null)
            {
                PatchLogger.LogWarning($"[StockingsDonorLoader] mesh_stockings 未検出: {key}");
                Addressables.Release(h);
                continue;
            }

            s_stockingsMesh[i] = smr;

            // mesh_skin_lower も同じ asset からキャッシュ
            var lower = h.Result.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .FirstOrDefault(m => m.name == "mesh_skin_lower");
            if (lower != null && lower.sharedMesh != null)
            {
                s_lowerMesh[i] = lower;
                PatchLogger.LogInfo($"[StockingsDonorLoader] {id} Uniform mesh_skin_lower キャッシュ (verts={lower.sharedMesh.vertexCount}, shapes={lower.sharedMesh.blendShapeCount})");
            }

            s_assetHandles.Add(h);
            PatchLogger.LogInfo($"[StockingsDonorLoader] {id} Uniform mesh_stockings キャッシュ (verts={smr.sharedMesh.vertexCount}, bones={smr.bones?.Length ?? 0})");
        }

        for (int t = 1; t <= 4; t++)
        {
            var h = Addressables.LoadAssetAsync<Material>(s_materialPaths[t]);
            yield return h;
            if (h.IsValid() && h.Result != null)
            {
                s_materials[t] = h.Result;
                PatchLogger.LogInfo($"[StockingsDonorLoader] stocking material type {t} プリロード完了");
            }
            else
            {
                PatchLogger.LogWarning($"[StockingsDonorLoader] material type {t} ロード失敗: {s_materialPaths[t]}");
            }
        }

        IsReady = true;
        PatchLogger.LogInfo($"[StockingsDonorLoader] Ready (donors={s_stockingsMesh.Count}/6)");
    }
}
