using GB.Game;
using System.Collections.Generic;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// キャラごとに直近で適用された <see cref="GB.CharacterHandle.LoadArg"/> を記憶する
/// プロセス内セッションストア。
/// CurrentCast 切替時 (<see cref="GB.Game.GameData.SetCurrentCast"/>) に新キャラは
/// 既に Preload 済みで再 Preload は走らないため、Wardrobe 履歴の記録にはこの
/// 保存値を参照する必要がある。
///
/// Preload / ReloadPanties / ApplyStocking のフック時に更新する。
/// </summary>
internal static class WardrobeLastLoadArg
{
    private readonly struct Entry
    {
        public Entry(CostumeType costume, int pantiesType, int pantiesColor, int stocking)
        {
            Costume = costume;
            PantiesType = pantiesType;
            PantiesColor = pantiesColor;
            Stocking = stocking;
        }

        public CostumeType Costume { get; }
        public int PantiesType { get; }
        public int PantiesColor { get; }
        public int Stocking { get; }
    }

    private static readonly Dictionary<CharID, Entry> s_map = new();

    /// <summary>Preload 完了時に全フィールドを一括更新する。</summary>
    public static void Set(CharID id, CostumeType costume, int pantiesType, int pantiesColor, int stocking)
    {
        if (id >= CharID.NUM) return;
        s_map[id] = new Entry(costume, pantiesType, pantiesColor, stocking);
    }

    /// <summary>ReloadPanties フック用。Panties のみ差分更新。既存エントリが無ければ無視。</summary>
    public static void UpdatePanties(CharID id, int type, int color)
    {
        if (id >= CharID.NUM) return;
        if (!s_map.TryGetValue(id, out var v)) return;
        s_map[id] = new Entry(v.Costume, type, color, v.Stocking);
    }

    /// <summary>ApplyStocking フック用。Stocking のみ差分更新。既存エントリが無ければ無視。</summary>
    public static void UpdateStocking(CharID id, int stocking)
    {
        if (id >= CharID.NUM) return;
        if (!s_map.TryGetValue(id, out var v)) return;
        s_map[id] = new Entry(v.Costume, v.PantiesType, v.PantiesColor, stocking);
    }

    /// <summary>CurrentCast 切替時に履歴へフラッシュ。エントリ無しなら false。</summary>
    public static bool TryGet(CharID id, out CostumeType costume, out int pantiesType, out int pantiesColor, out int stocking)
    {
        if (s_map.TryGetValue(id, out var v))
        {
            costume = v.Costume;
            pantiesType = v.PantiesType;
            pantiesColor = v.PantiesColor;
            stocking = v.Stocking;
            return true;
        }
        costume = default;
        pantiesType = 0;
        pantiesColor = 0;
        stocking = 0;
        return false;
    }
}
