using System;
using System.Collections.Generic;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger.Internal;

/// <summary>
/// 全 Loader の donor preload host を集約判定する registry。
///
/// 各 Loader (Tops/Bottoms 等) は <see cref="Initialize"/> で自身の <see cref="DonorPreloadCache{T}.IsHostParent"/>
/// 関数を <see cref="Register"/> する。<see cref="IsAnyHostParent"/> はすべての登録済みチェックを
/// or で結合して、いずれかの Loader の preload host 配下なら true を返す。
///
/// memory <c>feedback_loader_preload_host_mutex.md</c>: 各 Loader 自身の host だけでなく
/// 全 Loader の host を排他網に含める必要がある。registry 集約により呼出側で or を並べる
/// 重複が消え、新 Loader 追加時も登録するだけで網に参加する（cross-Loader 契約の構造的強制）。
/// </summary>
internal static class DonorPreloadRegistry
{
    private static readonly List<Func<GameObject, bool>> s_isHostParentChecks = new();

    /// <summary>Loader の Initialize で自 cache の IsHostParent を登録する。</summary>
    public static void Register(Func<GameObject, bool> isHostParent)
    {
        if (isHostParent == null) return;
        s_isHostParentChecks.Add(isHostParent);
    }

    /// <summary>
    /// 登録済みのいずれかの cache が parent を host 配下と判定すれば true。
    /// CostumeChangerPatch / KneeSocksLoader / 各 Loader の ApplyIfOverridden 等から呼ぶ。
    /// </summary>
    public static bool IsAnyHostParent(GameObject parent)
    {
        if (parent == null) return false;
        // ホットパスではないが Linq Any() は GC 圧があるため for で回す。
        for (int i = 0; i < s_isHostParentChecks.Count; i++)
        {
            if (s_isHostParentChecks[i](parent)) return true;
        }
        return false;
    }
}
