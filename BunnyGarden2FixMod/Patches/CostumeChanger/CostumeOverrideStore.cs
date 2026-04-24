using GB.Game;
using System.Collections.Generic;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// キャラごとの MOD override 衣装を保持するプロセス内セッションストア。
/// 永続化しない（ゲーム再起動でリセット）。設計書 § 「セッション override」参照。
/// </summary>
public static class CostumeOverrideStore
{
    private static readonly Dictionary<CharID, CostumeType> s_overrides = new();

    /// <summary>指定キャラの override 衣装を設定する。</summary>
    public static void Set(CharID id, CostumeType costume)
    {
        if (id >= CharID.NUM) return;
        if (costume >= CostumeType.Num) return;
        s_overrides[id] = costume;
    }

    /// <summary>指定キャラの override を解除する。</summary>
    public static void Clear(CharID id) => s_overrides.Remove(id);

    /// <summary>指定キャラの override を取得する。未設定なら false。</summary>
    public static bool TryGet(CharID id, out CostumeType costume) =>
        s_overrides.TryGetValue(id, out costume);
}
