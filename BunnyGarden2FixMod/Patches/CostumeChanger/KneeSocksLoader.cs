using BunnyGarden2FixMod.Utils;
using Cysharp.Threading.Tasks;
using GB;
using GB.Game;
using GB.Scene;
using HarmonyLib;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// ストッキングタブで選択できる「ニーソックス」(type 5) の実装。
///
/// Luna の Casual コスチュームから mesh_kneehigh をプリロードし、
/// ニーソックス override が設定されたキャラの mesh_stockings に差し替える。
///
/// ゲーム本体は stocking type を 0–4 しか扱わないため、type 5 は
/// CostumeChangerPatch.Prefix で 0 (no stocking) に変換してから Preload に渡す。
/// その後 setup/setupPantiesOnly Postfix でメッシュ差し替えを適用する。
///
/// mesh 差し替えの副作用:
///   - mesh_stockings: active + kneehigh mesh に置換
///   - mesh_kneehigh (ネイティブ): inactive（二重描画防止）
///   - mesh_socks: inactive（kneehigh との重複防止）
///   - blendShape_skin_lower.skin_stocking = 100 (z-fighting 対策)
///   - blendShape_skin_lower.skin_kneehigh = 0 (ネイティブが非表示のためリセット)
///
/// ニーソックス override を解除するときは Restore() で副作用を復元してから
/// env.ApplyStockings を呼ぶことでリロード不要。
/// CostumePickerController.DecideActiveTab / ApplyStocking が担う。
/// </summary>
public class KneeSocksLoader : MonoBehaviour
{
    private struct CharacterSnapshot
    {
        public bool KneehighActive;
        public bool SocksActive;
        public float SkinStockingBlend;
        public float SkinKneehighBlend;
        public Mesh OriginalStockingsMesh;
        public Transform[] OriginalStockingsBones;
        // blendShape 移植により sharedMesh を差し替えた場合のみセット（それ以外は null）
        public Mesh LowerOriginalMesh;
    }

    private static readonly Dictionary<int, CharacterSnapshot> s_snapshots = new();
    private static CharacterHandle s_handle; // GC 防止: CharacterHandle が破棄されると SMR も破棄される
    private static SkinnedMeshRenderer s_kneeSocks;
    // Luna Casual の mesh_skin_lower（skin_kneehigh blendShape のドナー）
    private static Mesh s_donorSkinLower;
    // 元 sharedMesh の InstanceID → blendShape 移植済みメッシュのキャッシュ。
    // ドナーは固定（Luna Casual）で移植結果は元メッシュごとに決定的なため、charId なしのキーで安全。
    // 異なるキャラが同一 sharedMesh を共有する場合は意図的に同じ結果を再利用する。
    // ライフサイクル: シーン継続中は再利用（Restore 後も保持）、シーンアンロード時に Object.Destroy で破棄。
    private static readonly Dictionary<int, Mesh> s_transplantedLowerCache = new();

    // インデックスは KneeSocksStockingType(type)。[0]=null（デフォルトは s_kneeSocks.material 直接使用）
    private static readonly Material[] s_stockingMaterials = new Material[3];

    /// <summary>マテリアルプリロード中かどうかを返す。DisableStockingPatch などのガードに使用する。</summary>
    public static bool IsPreloading { get; private set; }

    /// <summary>
    /// 水着×ニーソックス用: kneehigh SMR を借用してマテリアルを取得するためのアクセサ。
    /// </summary>
    internal static SkinnedMeshRenderer KneeSocksSmr => s_kneeSocks;

    /// <summary>
    /// 水着×ニーソックス用: Luna Casual の mesh_skin_lower ドナーを SwimWearStockingPatch に公開する。
    /// プリロード未完了時は null を返す。
    /// </summary>
    internal static Mesh DonorSkinLower => s_donorSkinLower;

    /// <summary>
    /// 指定 override type（5-7）に対応する kneehigh マテリアルを返す。未ロード時は s_kneeSocks の既定マテリアルを返す。
    /// </summary>
    internal static Material GetMaterialForOverride(int overrideType)
    {
        int idx = StockingOverrideStore.KneeSocksStockingType(overrideType);
        if (idx > 0 && idx < s_stockingMaterials.Length && s_stockingMaterials[idx] != null)
            return s_stockingMaterials[idx];
        return s_kneeSocks != null ? s_kneeSocks.sharedMaterial : null;
    }

    public static void Initialize(GameObject parent)
    {
        parent.AddComponent<KneeSocksLoader>();
    }

    public static bool IsLoaded => s_kneeSocks != null;

    private IEnumerator Start()
    {
        var parent = new GameObject(nameof(KneeSocksLoader));
        parent.transform.SetParent(transform, false);
        parent.SetActive(false);
        s_handle = new CharacterHandle(parent);

        SceneManager.sceneUnloaded += OnSceneUnloaded;

        yield return new WaitUntil(() => GBSystem.Instance != null && GBSystem.Instance.RefSaveData() != null);

        s_handle.Preload(CharID.LUNA, new CharacterHandle.LoadArg { Costume = CostumeType.Casual });
        yield return new WaitUntil(() => s_handle.IsPreloadDone());

        s_kneeSocks = parent.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .FirstOrDefault(m => m.name == "mesh_kneehigh");

        if (s_kneeSocks == null)
            PatchLogger.LogWarning($"[{nameof(KneeSocksLoader)}] mesh_kneehigh が見つかりませんでした。ニーソックスは使用できません。");
        else
            PatchLogger.LogInfo($"[{nameof(KneeSocksLoader)}] mesh_kneehigh をプリロードしました。");

        // Luna Casual の mesh_skin_lower から skin_kneehigh blendShape をドナーとしてキャッシュ
        var donorLowerSmr = parent.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .FirstOrDefault(m => m.name == "mesh_skin_lower");
        if (donorLowerSmr != null && donorLowerSmr.sharedMesh != null)
        {
            s_donorSkinLower = donorLowerSmr.sharedMesh;
            PatchLogger.LogInfo($"[{nameof(KneeSocksLoader)}] mesh_skin_lower ドナーをキャッシュしました (verts={s_donorSkinLower.vertexCount} shapes={s_donorSkinLower.blendShapeCount})。");
        }
        else
        {
            PatchLogger.LogWarning($"[{nameof(KneeSocksLoader)}] mesh_skin_lower が見つかりませんでした。skin_kneehigh 移植はフォールバック動作になります。");
        }

        // ダミーキャラの mesh_stockings に ApplyStocking を呼んでマテリアルをキャッシュする
        var stockingsMesh = parent.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .FirstOrDefault(m => m.name == "mesh_stockings");

        if (stockingsMesh != null)
        {
            for (int t = 1; t <= 2; t++)
            {
                IsPreloading = true;
                try
                {
                    yield return s_handle.ApplyStocking(t).ToCoroutine();
                    s_stockingMaterials[t] = stockingsMesh.sharedMaterial;
                    PatchLogger.LogInfo($"[KneeSocksLoader] ストッキングマテリアル type {t} をプリロードしました。");
                }
                finally
                {
                    IsPreloading = false; // 例外発生時も確実にリセット
                }
            }
        }
    }

    private void OnDestroy()
    {
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
    }

    private static void OnSceneUnloaded(Scene scene)
    {
        s_snapshots.Clear();

        // 移植済みメッシュは Destroy しない:
        //   キャラ GameObject が DontDestroyOnLoad 等でシーンを跨いで生存するケースがあり、
        //   その lower SMR が transplanted mesh を参照したまま次シーンに入ると、ここで Destroy
        //   していると fake-null mesh を抱えて肌が消える。
        //   キャッシュ辞書だけクリアし、Mesh は SMR が手放した時点で Unity GC に回収させる。
        int transplantedCount = s_transplantedLowerCache.Count;
        s_transplantedLowerCache.Clear();

        PatchLogger.LogInfo($"[{nameof(KneeSocksLoader)}] シーンアンロード: snapshot クリア、transplanted cache クリア (mesh {transplantedCount} 件)。");
    }

    /// <summary>
    /// <paramref name="character"/> の mesh_stockings に kneehigh メッシュを差し込む。
    /// setup/setupPantiesOnly Postfix および CostumePickerController の live 切替から呼ぶ。
    /// </summary>
    public static void Apply(GameObject character, int overrideType = StockingOverrideStore.KneeSocks)
    {
        if (s_kneeSocks == null || s_kneeSocks.sharedMesh == null || character == null) return;

        var renderers = character.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var stockings = renderers.FirstOrDefault(m => m.name == "mesh_stockings");
        var nativeKneehigh = renderers.FirstOrDefault(m => m.name == "mesh_kneehigh");
        var socks = renderers.FirstOrDefault(m => m.name == "mesh_socks");
        var lower = renderers.FirstOrDefault(m => m.name == "mesh_skin_lower");

        if (stockings == null)
        {
            PatchLogger.LogWarning($"[{nameof(KneeSocksLoader)}] mesh_stockings が見つかりません（キャラ: {character.name}）");
            return;
        }

        // ストッキングスロットにニーソックスメッシュを表示
        stockings.gameObject.SetActive(true);

        // ネイティブの kneehigh と通常ソックスを非表示（重複描画防止）
        // 変更前の状態をスナップショット保存（初回のみ; 多重呼び出しでも元状態を保持）
        var instanceId = character.GetInstanceID();
        if (!s_snapshots.ContainsKey(instanceId))
        {
            s_snapshots[instanceId] = new CharacterSnapshot
            {
                KneehighActive = nativeKneehigh?.gameObject.activeSelf ?? true,
                SocksActive = socks?.gameObject.activeSelf ?? true,
                SkinStockingBlend = lower != null ? GetBlendShapeWeight(lower, "blendShape_skin_lower.skin_stocking") : 0f,
                SkinKneehighBlend = lower != null ? GetBlendShapeWeight(lower, "blendShape_skin_lower.skin_kneehigh") : 0f,
                OriginalStockingsMesh = stockings.sharedMesh,
                OriginalStockingsBones = stockings.bones,
                LowerOriginalMesh = null,
            };
        }

        if (nativeKneehigh != null) nativeKneehigh.gameObject.SetActive(false);
        if (socks != null) socks.gameObject.SetActive(false);

        // ボーン対応付け（キャラのボーン名 → Transform）
        var bones = new Dictionary<string, Transform>();
        foreach (var b in character.GetComponentsInChildren<Transform>(true))
            bones[b.name.ToLowerInvariant()] = b;

        var fallback = stockings.rootBone ?? character.transform;
        int missingBones = 0;
        var mappedBones = s_kneeSocks.bones
            .Select(b =>
            {
                if (b != null && bones.TryGetValue(b.name.ToLowerInvariant(), out var t)) return t;
                missingBones++;
                return fallback;
            })
            .ToArray();
        if (missingBones > 0)
            PatchLogger.LogInfo($"[{nameof(KneeSocksLoader)}] ボーン未対応 {missingBones}/{mappedBones.Length}: {character.name}（フォールバックボーン使用）");
        stockings.sharedMesh = s_kneeSocks.sharedMesh;
        int matIdx = StockingOverrideStore.KneeSocksStockingType(overrideType);
        if (matIdx > 0 && s_stockingMaterials[matIdx] == null)
            PatchLogger.LogWarning($"[{nameof(KneeSocksLoader)}] マテリアル index {matIdx} 未ロード（プリロード失敗？）。デフォルト素材で代替します。");
        stockings.material = matIdx > 0 && s_stockingMaterials[matIdx] != null
            ? s_stockingMaterials[matIdx]
            : s_kneeSocks.material;
        stockings.bones = mappedBones;

        // z-fighting 対策: skin_kneehigh=100 がニーハイ専用シェイプのため理想的。
        // skin_kneehigh は Luna Casual の mesh_skin_lower にしか存在しないため、他キャラ／コスチュームには移植が必要。
        // タイトル戻りなどで sharedMesh が null 化することがあるため lower.sharedMesh も確認する。
        if (lower != null && lower.sharedMesh != null)
        {
            int kneehighIdx = lower.sharedMesh.GetBlendShapeIndex("blendShape_skin_lower.skin_kneehigh");
            if (kneehighIdx >= 0)
            {
                // 既存に skin_kneehigh がある（Luna Casual 等）: そのまま weight を設定
                SetBlendShape(lower, "blendShape_skin_lower.skin_stocking", 0f);
                SetBlendShape(lower, "blendShape_skin_lower.skin_kneehigh", 100f);
            }
            else if (s_donorSkinLower != null)
            {
                // skin_kneehigh がない場合: Luna Casual ドナーから nearest-neighbor 移植
                int origId = lower.sharedMesh.GetInstanceID();
                if (!s_transplantedLowerCache.TryGetValue(origId, out var transplanted) || transplanted == null)
                {
                    transplanted = MeshBlendShapeTransplanter.Transplant(
                        lower.sharedMesh,
                        s_donorSkinLower,
                        new[] { "blendShape_skin_lower.skin_kneehigh" },
                        nameof(KneeSocksLoader));
                    if (transplanted != null)
                        s_transplantedLowerCache[origId] = transplanted;
                }

                if (transplanted != null)
                {
                    // snapshot に元 sharedMesh を記録してから差し替え
                    var snap = s_snapshots[instanceId];
                    snap.LowerOriginalMesh = lower.sharedMesh;
                    s_snapshots[instanceId] = snap;

                    lower.sharedMesh = transplanted;
                    SetBlendShape(lower, "blendShape_skin_lower.skin_stocking", 0f);
                    SetBlendShape(lower, "blendShape_skin_lower.skin_kneehigh", 100f);
                }
                else
                {
                    // 移植結果が null（shape なし等）: フォールバック
                    PatchLogger.LogWarning($"[{nameof(KneeSocksLoader)}] 移植結果が null のためフォールバック (skin_stocking=100)");
                    SetBlendShape(lower, "blendShape_skin_lower.skin_stocking", 100f);
                    SetBlendShape(lower, "blendShape_skin_lower.skin_kneehigh", 0f);
                }
            }
            else
            {
                // ドナー未取得: フォールバック（skin_stocking=100 で z-fighting を最低限抑制）
                PatchLogger.LogWarning($"[{nameof(KneeSocksLoader)}] ドナー mesh_skin_lower 未取得のためフォールバック (skin_stocking=100)");
                SetBlendShape(lower, "blendShape_skin_lower.skin_stocking", 100f);
                SetBlendShape(lower, "blendShape_skin_lower.skin_kneehigh", 0f);
            }
        }

        PatchLogger.LogInfo($"[{nameof(KneeSocksLoader)}] ニーソックスを適用しました: {character.name}");
    }

    /// <summary>
    /// setup/setupPantiesOnly Postfix から呼ばれる。ニーソックス override が設定されていれば Apply する。
    /// </summary>
    public static void ApplyIfOverridden(CharacterHandle handle)
    {
        if (IsPreloading) return; // プリロード中の dummy handle への誤適用を防ぐ
        if (handle?.Chara == null) return;
        // 水着は SwimWearStockingPatch が専用ロジックで処理するためスキップ
        if (handle.m_lastLoadArg != null && handle.m_lastLoadArg.Costume == CostumeType.SwimWear) return;
        var id = handle.GetCharID();
        if (!StockingOverrideStore.TryGet(id, out var stk) || !StockingOverrideStore.IsKneeSocksType(stk)) return;
        Apply(handle.Chara, stk);
    }

    private static void SetBlendShape(SkinnedMeshRenderer renderer, string name, float weight)
    {
        if (renderer == null || renderer.sharedMesh == null) return;
        int idx = renderer.sharedMesh.GetBlendShapeIndex(name);
        if (idx >= 0) renderer.SetBlendShapeWeight(idx, weight);
    }

    private static float GetBlendShapeWeight(SkinnedMeshRenderer renderer, string name)
    {
        if (renderer == null || renderer.sharedMesh == null) return 0f;
        int idx = renderer.sharedMesh.GetBlendShapeIndex(name);
        return idx >= 0 ? renderer.GetBlendShapeWeight(idx) : 0f;
    }

    /// <summary>
    /// Apply() による副作用（mesh_kneehigh / mesh_socks の可視状態・blendShape 変更）を元に戻す。
    /// ニーソックス override 解除時に env.ApplyStockings の前に呼ぶ。
    /// Apply() が呼ばれていない場合は何もしない。
    /// </summary>
    public static void Restore(GameObject character)
    {
        if (character == null) return;
        var instanceId = character.GetInstanceID();

        CharacterSnapshot snap;
        if (!s_snapshots.TryGetValue(instanceId, out snap)) return;
        s_snapshots.Remove(instanceId);

        var renderers = character.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var nativeKneehigh = renderers.FirstOrDefault(m => m.name == "mesh_kneehigh");
        var socks = renderers.FirstOrDefault(m => m.name == "mesh_socks");
        var lower = renderers.FirstOrDefault(m => m.name == "mesh_skin_lower");

        if (nativeKneehigh != null) nativeKneehigh.gameObject.SetActive(snap.KneehighActive);
        if (socks != null) socks.gameObject.SetActive(snap.SocksActive);
        if (lower != null)
        {
            // 移植により sharedMesh を差し替えていた場合は先に元に戻す（blendShape index が変わるため）
            if (snap.LowerOriginalMesh != null)
                lower.sharedMesh = snap.LowerOriginalMesh;

            SetBlendShape(lower, "blendShape_skin_lower.skin_stocking", snap.SkinStockingBlend);
            SetBlendShape(lower, "blendShape_skin_lower.skin_kneehigh", snap.SkinKneehighBlend);
        }

        // ニーソックス適用で置き換えた mesh_stockings の sharedMesh/bones を元に戻す
        var stockings = renderers.FirstOrDefault(m => m.name == "mesh_stockings");
        if (stockings != null)
        {
            stockings.sharedMesh = snap.OriginalStockingsMesh;
            stockings.bones = snap.OriginalStockingsBones;
        }

        PatchLogger.LogInfo($"[{nameof(KneeSocksLoader)}] ニーソックス副作用を復元しました: {character.name}");
    }
}

/// <summary>
/// 衣装が最初にロードされるとき（setup）にニーソックスを適用する。
/// </summary>
[HarmonyPatch(typeof(CharacterHandle), nameof(CharacterHandle.setup))]
internal static class KneeSocksSetupPatch
{
    private static bool Prepare()
    {
        bool enabled = Plugin.ConfigCostumeChangerEnabled?.Value ?? true;
        if (enabled) PatchLogger.LogInfo("[KneeSocksSetupPatch] 適用");
        return enabled;
    }

    private static void Postfix(CharacterHandle __instance) => KneeSocksLoader.ApplyIfOverridden(__instance);
}

/// <summary>
/// パンツのみ再ロードされたとき（setupPantiesOnly）にニーソックスを再適用する。
/// </summary>
[HarmonyPatch(typeof(CharacterHandle), nameof(CharacterHandle.setupPantiesOnly))]
internal static class KneeSocksSetupPantiesOnlyPatch
{
    private static bool Prepare()
    {
        bool enabled = Plugin.ConfigCostumeChangerEnabled?.Value ?? true;
        if (enabled) PatchLogger.LogInfo("[KneeSocksSetupPantiesOnlyPatch] 適用");
        return enabled;
    }

    private static void Postfix(CharacterHandle __instance) => KneeSocksLoader.ApplyIfOverridden(__instance);
}

/// <summary>
/// setup/setupPantiesOnly 末尾で .Forget() 発火する ApplyStocking(0) の async 処理が
/// KneeSocksSetupPatch で適用済みのメッシュを上書きする race を防ぐ。
/// KneeSocks override 中かつ type=0 のときのみ介入し、元の async をスキップする。
/// </summary>
[HarmonyPatch(typeof(CharacterHandle), nameof(CharacterHandle.ApplyStocking))]
internal static class KneeSocksApplyStockingPatch
{
    private static bool Prepare()
    {
        bool enabled = Plugin.ConfigCostumeChangerEnabled?.Value ?? true;
        if (enabled) PatchLogger.LogInfo("[KneeSocksApplyStockingPatch] 適用");
        return enabled;
    }

    private static bool Prefix(CharacterHandle __instance, int __0)
    {
        if (__0 != 0 || __instance?.Chara == null) return true;
        var id = __instance.GetCharID();
        if (!StockingOverrideStore.TryGet(id, out var stk) || !StockingOverrideStore.IsKneeSocksType(stk)) return true;
        KneeSocksLoader.Apply(__instance.Chara, stk);
        return false; // async 処理をスキップ
    }
}
