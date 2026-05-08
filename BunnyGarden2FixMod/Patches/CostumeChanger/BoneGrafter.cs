using BunnyGarden2FixMod.Utils;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// donor SMR の bones[] のうち target キャラに名前一致しない bone について、
/// 段階的フォールバックで target 側の bone を解決する。
///
/// 解決順:
///   1. 直接名前一致
///   2. SwimWear などコスチューム固有の中間語 (例: <c>_SW</c>) を除去した正規化名で再検索
///      → KANA SwimWear の <c>L_skirtA1_SW_skinJT</c> を target の <c>L_skirtA1_skinJT</c> へマップ。
///        target 側 standard 骨は target 自身の Animator (<c>skirt_swaying_lp</c>) で物理駆動されるので
///        skirt sway 物理を自動で再現できる。
///   3. donor 側の missing 祖先 subtree を Transform-only で clone して target に植え替え
///      （bindpose 互換目的、physics は無し）
///   4. rootBone fallback
///
/// 仕組み:
///   - graft された subtree の root に <see cref="GraftedBoneMarker"/> を付与（owner = <c>logTag</c>）し、
///     RestoreFor から該 owner の clone のみ Destroy できるようにする。
///   - 再 Apply 時は target hierarchy 走査で **同 owner の** 既存 clone を検出し再 graft しない (冪等)。
///   - **per-owner isolation**: ResolveAndGraft は別 owner (Tops vs Bottoms) の grafted subtree 配下の
///     transform を targetBones から除外してから処理。これにより Tops/Bottoms それぞれが独立した clone を
///     持ち、片側の Restore で他方の donor.bones が dangle するのを防ぐ。
/// </summary>
internal static class BoneGrafter
{
    /// <summary>
    /// donor bones を target bone へマッピングして書き換える。
    /// targetBones は in/out: 新規 clone bone を追加する。
    /// 別 owner (= logTag 不一致) の既存 grafted subtree は targetBones から除外してから処理する
    /// （Tops/Bottoms 間で clone を共有しないことで、片側の cleanup で他方が dangle するのを防ぐ）。
    /// 戻り値は (donor 固有名で正規化解決した bone 数, graft で clone した root subtree 数, clone bone 合計数, root fallback 数)。
    /// </summary>
    public static (int NormalizedResolved, int RootCount, int BoneCount, int RootFallback) ResolveAndGraft(
        SkinnedMeshRenderer donor,
        GameObject character,
        Dictionary<string, Transform> targetBones,
        string logTag)
    {
        if (donor == null) return (0, 0, 0, 0);
        var donorBones = donor.bones;
        if (donorBones == null || donorBones.Length == 0) return (0, 0, 0, 0);

        // 別 owner の grafted subtree 配下の transform を targetBones から除外。
        // 別 owner の clone を共有してしまうと、その owner が後で Restore したとき donor.bones[] が
        // dangle する (Tops 適用後 Bottoms 適用 → Bottoms が Tops clone を借用 → Tops Restore で崩壊、等)。
        var allMarkers = character.GetComponentsInChildren<GraftedBoneMarker>(true);
        if (allMarkers != null && allMarkers.Length > 0)
        {
            var otherOwned = new HashSet<Transform>();
            foreach (var m in allMarkers)
            {
                if (m == null || m.OwnerTag == logTag) continue;
                foreach (var t in m.GetComponentsInChildren<Transform>(true))
                    otherOwned.Add(t);
            }
            if (otherOwned.Count > 0)
            {
                var keysToRemove = targetBones.Where(kv => otherOwned.Contains(kv.Value)).Select(kv => kv.Key).ToList();
                foreach (var k in keysToRemove) targetBones.Remove(k);
                if (keysToRemove.Count > 0)
                    PatchLogger.LogInfo($"[{logTag}/BoneGraft] 他 owner clone {keysToRemove.Count} 件を targetBones から除外");
            }
        }

        int normalizedResolved = 0;

        // Step 1: 直接一致しない & 正規化で一致する donor bone は targetBones に正規化名 alias を追加
        // （以降の bone 解決で名前検索だけで済むようにする）
        foreach (var b in donorBones)
        {
            if (b == null) continue;
            var k = b.name.ToLowerInvariant();
            if (targetBones.ContainsKey(k)) continue;
            var norm = NormalizeBoneName(k);
            if (norm == k) continue;
            if (targetBones.TryGetValue(norm, out var t))
            {
                targetBones[k] = t;
                normalizedResolved++;
            }
        }

        // Step 2: 残った missing donor bone について最上位の missing 祖先 (親が target に存在) を抽出
        var rootsToClone = new HashSet<Transform>();
        foreach (var b in donorBones)
        {
            if (b == null) continue;
            if (targetBones.ContainsKey(b.name.ToLowerInvariant())) continue;

            var top = b;
            while (top.parent != null && !targetBones.ContainsKey(top.parent.name.ToLowerInvariant()))
                top = top.parent;
            rootsToClone.Add(top);
        }

        int totalCloned = 0;
        foreach (var root in rootsToClone)
        {
            Transform targetParent;
            if (root.parent != null && targetBones.TryGetValue(root.parent.name.ToLowerInvariant(), out var p))
                targetParent = p;
            else
                targetParent = character.transform; // 祖先まで遡っても無ければ character 直下

            var clone = CloneTransformOnly(root, targetParent);
            clone.gameObject.AddComponent<GraftedBoneMarker>().OwnerTag = logTag;

            foreach (var t in clone.GetComponentsInChildren<Transform>(true))
            {
                var key = t.name.ToLowerInvariant();
                if (!targetBones.ContainsKey(key))
                    targetBones[key] = t;
                totalCloned++;
            }
        }

        int rootFallback = 0;
        foreach (var b in donorBones)
        {
            if (b == null) { rootFallback++; continue; }
            if (!targetBones.ContainsKey(b.name.ToLowerInvariant())) rootFallback++;
        }

        if (normalizedResolved > 0 || rootsToClone.Count > 0 || rootFallback > 0)
        {
            PatchLogger.LogInfo($"[{logTag}/BoneGraft] {character.name}: 正規化解決={normalizedResolved}, graft root={rootsToClone.Count} (clone bone={totalCloned}), root_fallback={rootFallback}");
        }
        return (normalizedResolved, rootsToClone.Count, totalCloned, rootFallback);
    }

    /// <summary>
    /// donor 固有の中間語を除去した正規化 bone 名を返す。
    /// 例: <c>l_skirta1_sw_skinjt</c> → <c>l_skirta1_skinjt</c> (KANA SwimWear)
    /// 既知の固有 token (<c>_sw_</c>) を空文字に置換する。
    /// 大文字小文字は呼出側で小文字化済み前提。
    /// </summary>
    private static string NormalizeBoneName(string lowerName)
    {
        if (string.IsNullOrEmpty(lowerName)) return lowerName;
        // SwimWear 中間 token: _sw_ を取り除く ("_sw" や "sw_" 単独語境界は誤一致しやすいので避ける)
        if (lowerName.Contains("_sw_"))
            return lowerName.Replace("_sw_", "_");
        return lowerName;
    }

    /// <summary>
    /// 指定 character 配下の grafted bone 群を Destroy する。RestoreFor / ApplyDirectly cleanup から呼ぶ。
    /// <paramref name="ownerTag"/> 指定時は <see cref="GraftedBoneMarker.OwnerTag"/> が **完全一致** する
    /// marker のみ destroy（per-loader isolation）。OwnerTag が null/別 owner の marker は touch しない
    /// （ResolveAndGraft の除外ロジックと対称: tag 指定モードでは「自分の clone のみ管理」を一貫させる）。
    /// <paramref name="ownerTag"/> = null は **全削除モード** (orphan / 旧バージョン残骸の emergency 回収用)。
    /// 通常は loader 名 ("TopsLoader" / "BottomsLoader") を渡すこと。
    /// Destroy 直前に親から detach する: Unity の <see cref="Object.Destroy"/> はフレーム終端まで
    /// 遅延されるため、同フレーム内で <see cref="ResolveAndGraft"/> が再走査すると doomed clone を
    /// 拾い「既に存在する」と誤判定して新規 graft をスキップ → donor SMR.bones が destroy 予定の
    /// Transform を参照したまま frame end で dangling refs となり表示崩壊する
    /// （TopsLoader.OnDistancePreserveParamChanged の同フレーム Restore→Apply 経路で発火）。
    /// </summary>
    public static void DestroyGrafted(GameObject character, string ownerTag = null)
    {
        if (character == null) return;
        var markers = character.GetComponentsInChildren<GraftedBoneMarker>(true);
        if (markers == null || markers.Length == 0) return;
        foreach (var m in markers)
        {
            if (m == null) continue;
            // tag 指定モード: 完全一致のみ destroy。null OwnerTag (旧版残骸) は emergency mode (ownerTag=null) で回収。
            if (ownerTag != null && m.OwnerTag != ownerTag) continue;
            m.transform.SetParent(null, false);
            Object.Destroy(m.gameObject);
        }
    }

    private static Transform CloneTransformOnly(Transform src, Transform parent)
    {
        var go = new GameObject(src.name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = src.localPosition;
        go.transform.localRotation = src.localRotation;
        go.transform.localScale = src.localScale;
        // Transform 以外のコンポーネント (DynamicBone / Collider 等) はあえて clone しない。
        // physics は target 側 standard 骨 (正規化解決) で取得する想定。
        foreach (Transform child in src)
            CloneTransformOnly(child, go.transform);
        return go.transform;
    }
}

/// <summary>
/// <see cref="BoneGrafter"/> が clone した root GameObject に付与するマーカー。
/// RestoreFor から GetComponentsInChildren で発見して Destroy するため。
/// <see cref="OwnerTag"/> で graft 元の loader (TopsLoader / BottomsLoader 等) を識別し、
/// per-owner で destroy / 共有抑止できるようにする。
/// </summary>
internal class GraftedBoneMarker : MonoBehaviour
{
    public string OwnerTag;
}
