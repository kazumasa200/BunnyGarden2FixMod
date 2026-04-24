using GB.Game;
using System.Collections.Generic;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// キャラごとのパンツ override（type 0-6, color 0-4）を保持するプロセス内セッションストア。
/// 永続化しない。
/// type, color 両方が一対で保存される。
/// </summary>
public static class PantiesOverrideStore
{
    private static readonly Dictionary<CharID, (int Type, int Color)> s_overrides = new();

    public const int TypeCount = 7;   // A-G
    public const int ColorCount = 5;  // 0-4
    public const int TotalCount = TypeCount * ColorCount; // 35

    public static void Set(CharID id, int type, int color)
    {
        if (id >= CharID.NUM) return;
        if (type < 0 || type >= TypeCount) return;
        if (color < 0 || color >= ColorCount) return;
        s_overrides[id] = (type, color);
    }

    public static void Clear(CharID id) => s_overrides.Remove(id);

    public static bool TryGet(CharID id, out int type, out int color)
    {
        if (s_overrides.TryGetValue(id, out var v))
        {
            type = v.Type;
            color = v.Color;
            return true;
        }
        type = 0;
        color = 0;
        return false;
    }
}
