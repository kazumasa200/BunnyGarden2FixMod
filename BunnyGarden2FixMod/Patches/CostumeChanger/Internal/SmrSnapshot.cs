using System.Collections.Generic;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger.Internal;

/// <summary>SmrSnapshot を所有する Loader の識別 tag。</summary>
internal enum SnapshotKind
{
    Tops,
    Bottoms,
}

/// <summary>
/// Apply 時に target SMR の元状態を保存し、Restore 時に復元するためのスナップショット。
/// 1 instanceId × kind (= SMR 名) × isInjected ごとに 1 エントリ。同 instance 上で複数回 Apply
/// しても初回 Apply 時のみ保存し、以後は上書きしない（Restore で素の状態に戻すため）。
/// </summary>
internal struct SmrSnapshot
{
    public bool WasInjected;          // true なら Restore 時に GameObject を Destroy
    public GameObject InjectedGo;     // WasInjected 用
    public bool OriginalActive;
    public bool OriginalEnabled;      // Renderer.enabled (描画 ON/OFF)
    // OriginalMesh は addressables 所有のため Destroy されず、参照保持のみで安全に Restore できる
    // （SwimWearStockingPatch / KneeSocksLoader と同方針）。
    public Mesh OriginalMesh;
    public Transform[] OriginalBones;
    public Material[] OriginalMaterials;
}

/// <summary>
/// 全 Loader 共有の SMR snapshot ストア。
///
/// memory <c>feedback_scene_unload_snapshot_clear.md</c>:
/// scene unload 時の Clear は **しない**。<c>m_holeScene</c> の PC は preserve され
/// scene 跨ぎで同 InstanceID で Apply trigger が再発火するため、Clear すると Capture が
/// 現在 (= donor 補正済み) の SMR.sharedMesh を OriginalMesh として誤記録する。
/// 本クラスは <see cref="Clear"/> 系 API を **意図的に提供しない** ことで誤呼出を unbuildable にする。
/// session 内で InstanceID は再利用されないため、stale entry は harmless に残るのみで誤検知しない。
/// </summary>
internal static class SmrSnapshotStore
{
    private readonly struct Key
    {
        public readonly SnapshotKind Kind;
        public readonly int InstanceId;
        public readonly string SmrKind;
        public readonly bool IsInjected;

        public Key(SnapshotKind kind, int instanceId, string smrKind, bool isInjected)
        {
            Kind = kind;
            InstanceId = instanceId;
            SmrKind = smrKind;
            IsInjected = isInjected;
        }
    }

    private static readonly Dictionary<Key, SmrSnapshot> s_snapshots = new();

    /// <summary>
    /// 同 (kind, instanceId, smrKind, isInjected) で既にスナップショットがあれば何もしない。
    /// 初回 Apply 時のみ target SMR の元状態を保存する。
    /// </summary>
    public static void Capture(
        SnapshotKind kind, int instanceId, string smrKind, bool isInjected,
        SkinnedMeshRenderer smr, GameObject injectedGo)
    {
        var key = new Key(kind, instanceId, smrKind, isInjected);
        if (s_snapshots.ContainsKey(key)) return;

        var snap = new SmrSnapshot
        {
            WasInjected = isInjected,
            InjectedGo = injectedGo,
        };
        if (smr != null)
        {
            // memory feedback_setup_postfix_inactive.md: setup() Postfix 時点で activeInHierarchy=false
            // のため activeSelf を使う（activeInHierarchy ガードを入れると初回ロードで全 skip）。
            snap.OriginalActive = smr.gameObject.activeSelf;
            snap.OriginalEnabled = smr.enabled;
            snap.OriginalMesh = smr.sharedMesh;
            // bones / sharedMaterials は defensive copy（target.bones = donor.bones で上書きされるため
            // 元配列インスタンスがそのまま残らないと restore で donor の値を戻すことになる）
            snap.OriginalBones = smr.bones != null ? (Transform[])smr.bones.Clone() : null;
            snap.OriginalMaterials = smr.sharedMaterials != null ? (Material[])smr.sharedMaterials.Clone() : null;
        }
        s_snapshots[key] = snap;
    }

    public static bool TryGet(
        SnapshotKind kind, int instanceId, string smrKind, bool isInjected,
        out SmrSnapshot snap) =>
        s_snapshots.TryGetValue(new Key(kind, instanceId, smrKind, isInjected), out snap);

    public static bool Remove(SnapshotKind kind, int instanceId, string smrKind, bool isInjected) =>
        s_snapshots.Remove(new Key(kind, instanceId, smrKind, isInjected));

    /// <summary>
    /// 指定 instanceId に紐づく entry を (smrKind, isInjected, snap) のタプルで列挙する。
    /// 列挙中の <see cref="Remove"/> 操作と衝突しないよう、内部で snapshot を取ってから返す。
    /// </summary>
    public static List<(string SmrKind, bool IsInjected, SmrSnapshot Snap)> EnumerateForInstance(
        SnapshotKind kind, int instanceId)
    {
        var result = new List<(string, bool, SmrSnapshot)>();
        foreach (var kv in s_snapshots)
        {
            if (kv.Key.Kind != kind) continue;
            if (kv.Key.InstanceId != instanceId) continue;
            result.Add((kv.Key.SmrKind, kv.Key.IsInjected, kv.Value));
        }
        return result;
    }
}
