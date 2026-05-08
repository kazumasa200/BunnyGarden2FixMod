// DIAGNOSTIC ONLY — remove after SwimWear physics issue resolved.
// target SwimWear での skirt 物理駆動骨を実機ログで確定するための診断パッチ。
// 1. CharacterHandle.setup Postfix で SwimWear 初回 setup 時に hierarchy/Animator/MagicaCloth ダンプ
// 2. 2 秒待機後、60 frame LateUpdate で動いた骨を判定してログ
// 重複ガード: (charId, costume, sceneName) ごとに 1 回。donor host 配下は除外。

using BunnyGarden2FixMod.Utils;
using Cysharp.Threading.Tasks;
using GB.Game;
using GB.Scene;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// DIAGNOSTIC ONLY — SwimWear 物理問題解決後に削除。
/// CharacterHandle.setup Postfix で SwimWear 初回 setup 時に hierarchy/Animator/MagicaCloth をダンプし、
/// 2 秒後 60 フレームにわたり駆動骨を特定する診断 MonoBehaviour。
/// </summary>
internal class SwimWearPhysicsDiag : MonoBehaviour
{
    // ─── probe エントリ ────────────────────────────
    private class ProbeEntry
    {
        public GameObject Character;
        public string SceneName;
        public Transform[] Bones;
        public Vector3[] InitialPositions;
        public Quaternion[] InitialRotations;
        public Vector3[] MaxDeltaPos;
        public float[] MinDot;
        public int FrameCount;
        public bool Started;
        public float RegisteredTime;
    }

    // ─── 静的メンバ ──────────────────────────────
    private static SwimWearPhysicsDiag s_instance;
    private static readonly HashSet<(CharID, CostumeType, string)> s_diagDone = new();
    private static readonly List<ProbeEntry> s_pendingProbes = new();
    private static bool s_bulkDone = false;

    private const float ProbeDelaySeconds = 2f;
    private const int ProbeTotalFrames = 60;

    // ─── Initialize (Plugin.Awake から呼ぶ) ────────

    /// <summary>
    /// DIAGNOSTIC ONLY。
    /// host GameObject に <see cref="SwimWearPhysicsDiag"/> を AddComponent する。
    /// 既に追加済みの場合は警告のみ。
    /// </summary>
    public static void Initialize(GameObject host)
    {
        if (s_instance != null)
        {
            PatchLogger.LogWarning("[SwimWearDiag] 既に Initialize 済みです");
            return;
        }
        s_instance = host.AddComponent<SwimWearPhysicsDiag>();
        SceneManager.sceneUnloaded += OnSceneUnloaded;
        PatchLogger.LogInfo("[SwimWearDiag] Initialized (DIAGNOSTIC ONLY)");
    }

    // 同一シーン再 enter (バー出入り 等) でも再 dump できるよう、シーン unload 時に
    // 該当 sceneName のキーを掃除する。
    private static void OnSceneUnloaded(Scene scene)
    {
        s_diagDone.RemoveWhere(k => k.Item3 == scene.name);
    }

    // ─── Bulk preload (起動時一括 preload) ──────────

    // MonoBehaviour Start() から bulk preload coroutine を起動する。
    private void Start()
    {
        if (!(Configs.SwimWearPhysicsDiag?.Value ?? false)) return;
        StartCoroutine(BulkPreloadDumpCoroutine());
    }

    private IEnumerator BulkPreloadDumpCoroutine()
    {
        if (s_bulkDone) yield break;
        s_bulkDone = true;

        // GBSystem 等のゲームシステムが起動するまで待機する。
        // BottomsLoader.PreloadDonorAsync 内の WaitUntil と重複するが、
        // ここでは coroutine 側の依存を明示するためにも待機する。
        yield return new WaitUntil(() => GB.GBSystem.Instance != null && GB.GBSystem.Instance.RefSaveData() != null);

        PatchLogger.LogInfo("[SwimWearDiag] === Bulk preload start (all chars × SwimWear) ===");
        foreach (CharID id in Enum.GetValues(typeof(CharID)))
        {
            if (id >= CharID.NUM) continue;
            yield return BulkPreloadOne(id);
        }
        PatchLogger.LogInfo("[SwimWearDiag] === Bulk preload done ===");
    }

    private IEnumerator BulkPreloadOne(CharID id)
    {
        // UniTask<bool> を coroutine にブリッジする。
        // KneeSocksLoader 等の既存パターンに従い ToCoroutine() を使用する。
        var task = BottomsLoader.PreloadDonorAsync(id, CostumeType.SwimWear);
        yield return task.ToCoroutine();

        if (BottomsLoader.TryGetDonorParent(id, CostumeType.SwimWear, out var donorParent))
        {
            // bulk preload 経路専用の sentinel を sceneName として使い、
            // gameplay OnSetup からの dump とキーが衝突しないようにする。
            var key = (id, CostumeType.SwimWear, "<bulk-preload>");
            if (!s_diagDone.Add(key)) yield break; // 念のため重複ガード
            DumpHierarchy(donorParent, id, "<bulk-preload>");
        }
        else
        {
            PatchLogger.LogInfo($"[SwimWearDiag] {id}/SwimWear: donor parent 取得失敗（preload 未完了 or 例外）");
        }
    }

    // ─── OnSetup (HarmonyPatch から呼ぶ) ────────────

    /// <summary>
    /// DIAGNOSTIC ONLY。
    /// CharacterHandle.setup Postfix から呼び出す。
    /// SwimWear かつ初回 (charId, costume, scene) のみ診断を実行する。
    /// 診断中の例外を本番経路に漏らさないため全体を try-catch でガードする。
    /// </summary>
    internal static void OnSetup(CharacterHandle handle)
    {
        if (!(Configs.SwimWearPhysicsDiag?.Value ?? false)) return;
        if (handle?.Chara == null) return;

        try
        {
            var arg = handle.m_lastLoadArg;
            if (arg == null || arg.Costume != CostumeType.SwimWear) return;

            // donor host 配下チェック（BottomsLoader / TopsLoader preload 経路を除外）
            if (BottomsLoader.IsDonorPreloadParent(handle.Chara)) return;
            if (TopsLoader.IsDonorPreloadParent(handle.Chara)) return;

            var charId = handle.GetCharID();
            var sceneName = SceneManager.GetActiveScene().name;
            var key = (charId, arg.Costume, sceneName);
            if (!s_diagDone.Add(key)) return; // 同 (charId, costume, scene) で 2 回目以降はスキップ

            DumpHierarchy(handle.Chara, charId, sceneName);
            EnqueueProbe(handle.Chara, sceneName);
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[SwimWearDiag] OnSetup 例外（診断のみ、本番影響なし）: {ex}");
        }
    }

    // ─── 階層ダンプ ──────────────────────────────

    private static void DumpHierarchy(GameObject character, CharID charId, string sceneName)
    {
        PatchLogger.LogInfo($"[SwimWearDiag] === {charId}/SwimWear setup (scene={sceneName}, includes inactive) ===");

        // skirt/frill/_SW_/thighstrap 骨を列挙
        var allTransforms = character.GetComponentsInChildren<Transform>(true);
        var matchedBones = new List<(string Path, Transform T)>();
        foreach (var t in allTransforms)
        {
            var n = t.name;
            if (ContainsBoneKeyword(n))
            {
                matchedBones.Add((GetRelativePath(character.transform, t), t));
            }
        }

        PatchLogger.LogInfo($"[SwimWearDiag]   bones (skirt/SW/frill/thighStrap): {matchedBones.Count} 件");
        foreach (var (path, _) in matchedBones)
        {
            PatchLogger.LogInfo($"[SwimWearDiag]     {path}");
        }

        // MagicaCloth ルート
        var magicaRootTrans = character.transform.Find("MagicaCloth");
        if (magicaRootTrans != null)
        {
            var magicaRoot = magicaRootTrans.gameObject;
            PatchLogger.LogInfo($"[SwimWearDiag]   MagicaCloth root: activeSelf={magicaRoot.activeSelf}");
            var diagList = new List<(string Name, string ClothType, int SourceRendererCount)>(
                MagicaClothRebuilder.EnumerateForDiag(character));
            PatchLogger.LogInfo($"[SwimWearDiag]   MagicaCloth components ({diagList.Count}):");
            foreach (var (name, ct, srcCount) in diagList)
            {
                PatchLogger.LogInfo($"[SwimWearDiag]     '{name}' clothType={ct}, sources={srcCount}");
            }
            if (diagList.Count == 0)
            {
                PatchLogger.LogInfo("[SwimWearDiag]   MagicaCloth components: 0 件");
            }
        }
        else
        {
            PatchLogger.LogInfo("[SwimWearDiag]   MagicaCloth root: 未検出");
        }

        // Animator 情報
        var animator = character.GetComponentInChildren<Animator>(true);
        if (animator == null)
        {
            PatchLogger.LogInfo("[SwimWearDiag]   Animator: null");
            return;
        }

        var controller = animator.runtimeAnimatorController;
        var controllerDesc = controller == null
            ? "null"
            : $"{controller.GetType().Name}({controller.name})";
        PatchLogger.LogInfo($"[SwimWearDiag]   Animator: layers={animator.layerCount}, controller={controllerDesc}");

        if (animator.layerCount > 3)
        {
            float w3 = animator.GetLayerWeight(3);
            var stateInfo3 = animator.GetCurrentAnimatorStateInfo(3);
            PatchLogger.LogInfo($"[SwimWearDiag]     layer3.weight={w3:F2}, currentState={stateInfo3.shortNameHash}");
        }

        for (int i = 0; i < animator.layerCount; i++)
        {
            var si = animator.GetCurrentAnimatorStateInfo(i);
            PatchLogger.LogInfo($"[SwimWearDiag]     layer[{i}] stateHash={si.shortNameHash} normalizedTime={si.normalizedTime:F3}");
        }

        // animation clips（先頭 20 件）
        if (controller != null)
        {
            var clips = controller.animationClips;
            int limit = Mathf.Min(clips.Length, 20);
            var clipNames = new System.Text.StringBuilder();
            for (int i = 0; i < limit; i++)
            {
                if (i > 0) clipNames.Append(", ");
                clipNames.Append(clips[i]?.name ?? "<null>");
            }
            if (clips.Length > 20) clipNames.Append($", ...({clips.Length - 20} more)");
            PatchLogger.LogInfo($"[SwimWearDiag]     clips=[{clipNames}]");
        }
    }

    // ─── Probe 登録 ──────────────────────────────

    private static void EnqueueProbe(GameObject character, string sceneName)
    {
        var allTransforms = character.GetComponentsInChildren<Transform>(true);
        var matchedBones = new List<Transform>();
        foreach (var t in allTransforms)
        {
            if (ContainsBoneKeyword(t.name)) matchedBones.Add(t);
        }

        var entry = new ProbeEntry
        {
            Character = character,
            SceneName = sceneName,
            Bones = matchedBones.ToArray(),
            InitialPositions = new Vector3[matchedBones.Count],
            InitialRotations = new Quaternion[matchedBones.Count],
            MaxDeltaPos = new Vector3[matchedBones.Count],
            MinDot = new float[matchedBones.Count],
            FrameCount = 0,
            Started = false,
            RegisteredTime = Time.unscaledTime,
        };
        for (int i = 0; i < matchedBones.Count; i++) entry.MinDot[i] = 1f;

        s_pendingProbes.Add(entry);
    }

    // ─── MonoBehaviour ───────────────────────────

    // Update は使わず、LateUpdate 内で「2 秒経過チェック → initial 記録 → sample」を一本化する。
    // Animator は通常 Update と LateUpdate の間で評価されるため、Update で initial を取って
    // 同フレ LateUpdate で diff を取ると 1 frame 目に不当な delta が出る。LateUpdate 内で
    // 「初回フレは initial 記録のみ、次フレから diff 計測」とすることで起点を揃える。
    private void LateUpdate()
    {
        for (int i = s_pendingProbes.Count - 1; i >= 0; i--)
        {
            var entry = s_pendingProbes[i];
            if (entry.Character == null)
            {
                PatchLogger.LogWarning($"[SwimWearDiag] character が Destroy されました (scene={entry.SceneName})。probe 中止。");
                s_pendingProbes.RemoveAt(i);
                continue;
            }

            if (!entry.Started)
            {
                // 2 秒未満は待機
                if (Time.unscaledTime - entry.RegisteredTime < ProbeDelaySeconds) continue;
                // 初回 LateUpdate: initial を記録 + Animator/MagicaCloth 状態ログ。diff 計測は次フレから。
                StartProbe(entry);
                continue;
            }

            // 各骨の変位を記録
            for (int b = 0; b < entry.Bones.Length; b++)
            {
                var bone = entry.Bones[b];
                if (bone == null) continue;
                var pos = bone.localPosition;
                var rot = bone.localRotation;
                var delta = pos - entry.InitialPositions[b];
                if (delta.sqrMagnitude > entry.MaxDeltaPos[b].sqrMagnitude)
                    entry.MaxDeltaPos[b] = delta;
                float dot = Quaternion.Dot(rot, entry.InitialRotations[b]);
                if (dot < entry.MinDot[b]) entry.MinDot[b] = dot;
            }

            entry.FrameCount++;
            if (entry.FrameCount >= ProbeTotalFrames)
            {
                FlushProbeResult(entry);
                s_pendingProbes.RemoveAt(i);
            }
        }
    }

    // ─── Probe 開始 ──────────────────────────────

    private static void StartProbe(ProbeEntry entry)
    {
        if (entry.Character == null)
        {
            s_pendingProbes.Remove(entry);
            return;
        }

        entry.Started = true;

        // initial 位置・回転を記録
        for (int b = 0; b < entry.Bones.Length; b++)
        {
            var bone = entry.Bones[b];
            if (bone == null) continue;
            entry.InitialPositions[b] = bone.localPosition;
            entry.InitialRotations[b] = bone.localRotation;
        }

        // Animator / MagicaCloth 状態を再取得してログ
        var charId = TryGetCharIdFromGameObject(entry.Character);
        PatchLogger.LogInfo($"[SwimWearDiag] === {charId}/SwimWear probe start (scene={entry.SceneName}, after 2s wait) ===");

        var animator = entry.Character.GetComponentInChildren<Animator>(true);
        float layer3Weight = -1f;
        if (animator != null && animator.layerCount > 3)
            layer3Weight = animator.GetLayerWeight(3);

        var magicaRootTrans = entry.Character.transform.Find("MagicaCloth");
        bool magicaActive = magicaRootTrans != null && magicaRootTrans.gameObject.activeSelf;

        PatchLogger.LogInfo($"[SwimWearDiag]   layer3.weight={(layer3Weight >= 0 ? layer3Weight.ToString("F2") : "N/A")}, magicaRoot.activeSelf={magicaActive}");
    }

    // ─── Probe 結果ログ ───────────────────────────

    private static void FlushProbeResult(ProbeEntry entry)
    {
        if (entry.Character == null) return;
        var charId = TryGetCharIdFromGameObject(entry.Character);
        PatchLogger.LogInfo($"[SwimWearDiag] === {charId}/SwimWear probe result ({ProbeTotalFrames} frames) ===");

        const float PosThr = 1e-4f;
        const float DotThr = 0.9999f;

        var driven = new List<(string Name, float MaxDeltaPosMag, float MinDot)>();
        for (int b = 0; b < entry.Bones.Length; b++)
        {
            var bone = entry.Bones[b];
            if (bone == null) continue;
            float mag = entry.MaxDeltaPos[b].magnitude;
            float dot = entry.MinDot[b];
            if (mag > PosThr || dot < DotThr)
            {
                driven.Add((bone.name, mag, dot));
            }
        }

        PatchLogger.LogInfo($"[SwimWearDiag]   driven bones: {driven.Count} 件");
        foreach (var (name, mag, dot) in driven)
        {
            PatchLogger.LogInfo($"[SwimWearDiag]     {name} (maxDeltaPos={mag:F4}, minDot={dot:F4})");
        }
    }

    // ─── ユーティリティ ──────────────────────────

    private static bool ContainsBoneKeyword(string name)
    {
        return name.IndexOf("skirt", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("_SW_", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("frill", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("thighstrap", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string GetRelativePath(Transform root, Transform target)
    {
        if (target == root) return root.name;
        var parts = new List<string>();
        var cur = target;
        while (cur != null && cur != root)
        {
            parts.Add(cur.name);
            cur = cur.parent;
        }
        parts.Add(root.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    /// <summary>
    /// GameObject から CharID を逆引きする（ログ用）。
    /// 見つからなければ CharID.NUM を返す。
    /// </summary>
    private static CharID TryGetCharIdFromGameObject(GameObject character)
    {
        try
        {
            var env = GB.GBSystem.Instance?.GetActiveEnvScene();
            if (env?.m_characters == null) return CharID.NUM;
            foreach (var h in env.m_characters)
            {
                if (h != null && h.Chara == character) return h.GetCharID();
            }
        }
        catch { }
        return CharID.NUM;
    }
}

// ─── Harmony パッチ ──────────────────────────────────────────────────────────

[HarmonyPatch(typeof(CharacterHandle), "setup")]
internal static class SwimWearPhysicsDiagPatch
{
    private static bool Prepare()
    {
        bool enabled = Configs.SwimWearPhysicsDiag?.Value ?? false;
        if (enabled) PatchLogger.LogInfo("[SwimWearDiag] 適用 (DIAGNOSTIC ONLY)");
        return enabled;
    }

    private static void Postfix(CharacterHandle __instance)
        => SwimWearPhysicsDiag.OnSetup(__instance);
}
