using BunnyGarden2FixMod.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger.Internal;

/// <summary>
/// donor SMR の sharedMesh / bones / sharedMaterials を target SMR に swap する共通 helper。
///
/// TopsLoader / BottomsLoader が個別実装していた SwapSmr を共通化したもの。
/// donor 固有 bone (SwimWear の skirt_*_SW 等) は <see cref="BoneGrafter.ResolveAndGraft"/> で
/// 正規化マッピング + graft を行ってから bone 名解決し、bindpose × bone.localToWorld が donor
/// bind 時と整合するように仕向ける。
/// </summary>
internal static class CostumeMeshSwapper
{
    /// <summary>
    /// donor SMR を target SMR に swap する。
    /// </summary>
    /// <param name="target">swap 先 SMR (target キャラ配下)。</param>
    /// <param name="donor">swap 元 SMR (preload host 配下)。</param>
    /// <param name="character">target キャラの GameObject。bone インデックス構築の起点。</param>
    /// <param name="boneGrafterTag">
    /// <see cref="BoneGrafter.ResolveAndGraft"/> に渡す identifier。
    /// "TopsLoader" / "BottomsLoader" 等。
    /// </param>
    /// <param name="skipActivateForTransparentLayer">
    /// true で <c>target.gameObject.name</c> が "_trp" を含む場合に <c>SetActive(true)</c> /
    /// <c>enabled=true</c> の強制 ON をスキップする。
    /// _trp 系 SMR (透過レイヤ、e.g. mesh_costume_skirt_trp) は通常 hidden が原状で、
    /// FittingRoom Panties 閲覧モード等の特定 UI でのみ ENABLED される。FixMod が強制表示すると
    /// MagicaCloth_Skirt の MeshCloth 駆動対象外で rest pose 固定の透過 skirt が visible 化し
    /// VIP で bug として露呈する (BottomsLoader 元実装の知見)。
    /// </param>
    public static void SwapSmr(
        SkinnedMeshRenderer target, SkinnedMeshRenderer donor,
        GameObject character, string boneGrafterTag,
        bool skipActivateForTransparentLayer)
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
        BoneGrafter.ResolveAndGraft(donor, character, bones, boneGrafterTag);

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

        bool isTransparentLayer = skipActivateForTransparentLayer
            && target.gameObject.name.IndexOf("_trp", StringComparison.OrdinalIgnoreCase) >= 0;
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
