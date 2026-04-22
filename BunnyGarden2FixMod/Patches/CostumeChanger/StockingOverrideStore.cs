using System.Collections.Generic;
using GB.Game;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// キャラごとのストッキング override（int: 0=なし, 1=黒, 2=白, 3=網黒, 4=網白）を保持する
/// プロセス内セッションストア。永続化しない。
/// </summary>
public static class StockingOverrideStore
{
    private static readonly Dictionary<CharID, int> s_overrides = new();

    public const int Min = 0;
    public const int Max = 4;

    public static void Set(CharID id, int stocking)
    {
        if (id >= CharID.NUM) return;
        if (stocking < Min || stocking > Max) return;
        s_overrides[id] = stocking;
    }

    public static void Clear(CharID id) => s_overrides.Remove(id);

    public static bool TryGet(CharID id, out int stocking) =>
        s_overrides.TryGetValue(id, out stocking);
}
