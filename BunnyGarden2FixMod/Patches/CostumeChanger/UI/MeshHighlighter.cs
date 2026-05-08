using BunnyGarden2FixMod.Utils;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BunnyGarden2FixMod.Patches.CostumeChanger.UI;

/// <summary>
/// SkinnedMeshRenderer を MaterialPropertyBlock 経由で赤 tint する Wardrobe DEBUG タブ専用ユーティリティ。
///
/// 設計方針:
///   - sharedMaterial を一切触らない（衣装切替パッチ群との衝突回避、復元 100% 保証）
///   - URP Lit (_BaseColor) と Unlit/旧 Standard (_Color) の両プロパティを set し shader 種別非依存
///   - SetPropertyBlock(null) で完全復元
///   - Unity main thread 限定 API (Unity 規約)
///   - sceneUnloaded 購読で残骸インスタンスを保険的に掃除
/// </summary>
public static class MeshHighlighter
{
    private static readonly HashSet<int> s_highlighted = new();   // SMR.GetInstanceID()
    private static MaterialPropertyBlock s_block;
    private static bool s_sceneUnloadHooked;
    private static readonly HashSet<int> s_warnedShaderInstanceIds = new();

    private static readonly Color s_tint = new Color(1f, 0f, 0f, 1f);

    /// <summary>
    /// SMR を赤 tint する。Unity main thread 限定。
    ///
    /// 副作用注意: 復元 (Unhighlight / ClearFor) は SetPropertyBlock(null) で行うため、対象 SMR が
    /// 元々 per-renderer MaterialPropertyBlock を保持していた場合はその block ごと消える。
    /// BunnyGarden 本体は per-renderer block を使っていない想定 (デバッグ用途限定)。
    /// </summary>
    public static void Highlight(SkinnedMeshRenderer smr)
    {
        if (smr == null) return;
        EnsureSceneUnloadHook();

        s_block ??= new MaterialPropertyBlock();
        smr.GetPropertyBlock(s_block);
        s_block.SetColor("_BaseColor", s_tint);
        s_block.SetColor("_Color", s_tint);
        smr.SetPropertyBlock(s_block);

        s_highlighted.Add(smr.GetInstanceID());

        WarnIfShaderUnsupported(smr);
    }

    /// <summary>SMR の highlight を解除する。Unity main thread 限定。</summary>
    public static void Unhighlight(SkinnedMeshRenderer smr)
    {
        if (smr == null) return;
        smr.SetPropertyBlock(null);
        s_highlighted.Remove(smr.GetInstanceID());
    }

    /// <summary>
    /// 内部 highlight 集合だけをクリアする (visual 復元は行わない)。
    /// 用途: Controller.OnDestroy / sceneUnloaded など、対応する SMR が destroy 済 / 不可達な経路。
    /// 生存中の SMR を視覚的に元へ戻したいなら <see cref="ClearFor"/> を使うこと。
    /// </summary>
    public static void ClearAll()
    {
        if (s_highlighted.Count == 0) return;
        s_highlighted.Clear();
    }

    /// <summary>
    /// 呼出側が現在生存している SMR リストを保持しているとき、そこに含まれる instance を解除する。
    /// dead SMR の MaterialPropertyBlock は Unity 側で SMR destroy と同時に解放されるため放置で問題ない。
    /// </summary>
    public static void ClearFor(IReadOnlyList<SkinnedMeshRenderer> smrs)
    {
        if (smrs == null) return;
        for (int i = 0; i < smrs.Count; i++)
        {
            var smr = smrs[i];
            if (smr == null) continue;
            if (!s_highlighted.Contains(smr.GetInstanceID())) continue;
            smr.SetPropertyBlock(null);
            s_highlighted.Remove(smr.GetInstanceID());
        }
    }

    /// <summary>SMR が highlight 中か。</summary>
    public static bool IsHighlighted(SkinnedMeshRenderer smr)
    {
        if (smr == null) return false;
        return s_highlighted.Contains(smr.GetInstanceID());
    }

    /// <summary>
    /// 生存 instanceId 集合に含まれない id を highlight 集合から落とす。
    /// 衣装切替で SMR が destroy + 新規生成された場合の dead 残骸防止用。
    /// </summary>
    public static void ForgetDeadInstances(IReadOnlyCollection<int> aliveInstanceIds)
    {
        if (s_highlighted.Count == 0) return;
        if (aliveInstanceIds == null) { s_highlighted.Clear(); return; }

        // HashSet の RemoveWhere を使うため一旦コピー
        s_highlighted.RemoveWhere(id => !aliveInstanceIds.Contains(id));
    }

    private static void EnsureSceneUnloadHook()
    {
        if (s_sceneUnloadHooked) return;
        s_sceneUnloadHooked = true;
        SceneManager.sceneUnloaded += _ => ClearAll();
    }

    /// <summary>
    /// _BaseColor / _Color のいずれにも対応していない shader が混じっている SMR を検出して 1 回ログ警告。
    /// 「チェック入れたのに赤くならない」の発見可能性を確保する保険。
    /// </summary>
    private static void WarnIfShaderUnsupported(SkinnedMeshRenderer smr)
    {
        if (smr == null) return;
        int id = smr.GetInstanceID();
        if (s_warnedShaderInstanceIds.Contains(id)) return;

        var mats = smr.sharedMaterials;
        if (mats == null) return;
        foreach (var m in mats)
        {
            if (m == null) continue;
            if (!m.HasProperty("_BaseColor") && !m.HasProperty("_Color"))
            {
                s_warnedShaderInstanceIds.Add(id);
                PatchLogger.LogWarning($"[MeshInspector] {smr.name}: _BaseColor/_Color とも未対応 shader ({m.shader?.name})");
                return;
            }
        }
    }
}
