using GB;
using GB.Game;
using GB.Scene;
using System.Linq;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger.Internal;

/// <summary>
/// env と HoleScene の <c>m_characters</c> から GameObject に対応する <see cref="CharacterHandle"/> を
/// 逆引きする共通 helper。
///
/// 同伴イベント等で env != HoleScene のとき、HoleScene 配下の preserved PC に対しても
/// CharacterHandle を解決する必要がある。env のみ走査だと additive モード判定が誤り、
/// Bunnygirl ガード等が効かなくなる。
/// </summary>
internal static class CharacterResolver
{
    /// <summary>
    /// env と HoleScene の m_characters を順に走査して character の <see cref="CharacterHandle"/> を逆引き解決する。
    /// 失敗時は null を返す。
    /// </summary>
    public static CharacterHandle ResolveHandle(GameObject character)
    {
        var sys = GBSystem.Instance;
        if (sys == null) return null;
        var env = sys.GetActiveEnvScene();
        var handle = env?.m_characters?.FirstOrDefault(x => x != null && x.Chara == character);
        if (handle != null) return handle;
        var holeScene = sys.GetHoleScene();
        if (ReferenceEquals(holeScene, env)) return null;
        return holeScene?.m_characters?.FirstOrDefault(x => x != null && x.Chara == character);
    }
}
