using System;
using System.Collections.Generic;
using BunnyGarden2FixMod.ExSave;
using BunnyGarden2FixMod.Utils;
using GB.Game;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// ExSave へのパンツ表示履歴ファサード。キャラ × (type, color) ペアを bit flag で記録。
/// <see cref="ExSaveStore.CommonData"/>（スロット非依存）に保存。
/// キー: <c>panties.viewed.{charIdInt}</c>、値: byte[8]（実際には 35 bit 使用）。
/// インデックス: type * ColorCount + color。type 0-6（A-G）× color 0-4。
/// </summary>
public static class PantiesViewHistory
{
    private const string KeyPrefix = "panties.viewed.";

    private static CharID s_lastCharId = CharID.NUM;
    private static int s_lastIndex = -1;

    public static bool IsViewed(CharID id, int type, int color)
    {
        if (id >= CharID.NUM) return false;
        int index = ToIndex(type, color);
        if (index < 0) return false;
        ulong bits = ReadBits(id);
        return (bits & (1UL << index)) != 0;
    }

    /// <summary>表示済み (type, color) ペアを昇順で列挙する。</summary>
    public static IReadOnlyList<(int Type, int Color)> GetViewedList(CharID id)
    {
        if (id >= CharID.NUM) return Array.Empty<(int, int)>();
        ulong bits = ReadBits(id);
        var list = new List<(int, int)>();
        for (int t = 0; t < PantiesOverrideStore.TypeCount; t++)
        {
            for (int c = 0; c < PantiesOverrideStore.ColorCount; c++)
            {
                int index = t * PantiesOverrideStore.ColorCount + c;
                if ((bits & (1UL << index)) != 0) list.Add((t, c));
            }
        }
        return list;
    }

    public static void MarkViewed(CharID id, int type, int color)
    {
        if (id >= CharID.NUM) return;
        int index = ToIndex(type, color);
        if (index < 0) return;
        if (ExSaveStore.CurrentSaveSlot < 0) return;

        if (s_lastCharId == id && s_lastIndex == index) return;
        s_lastCharId = id;
        s_lastIndex = index;

        ulong bits = ReadBits(id);
        ulong newBits = bits | (1UL << index);
        if (newBits == bits) return;
        WriteBits(id, newBits);
        PatchLogger.LogInfo($"[PantiesViewHistory] 記録: {id} / type={type} color={color}");
    }

    private static int ToIndex(int type, int color)
    {
        if (type < 0 || type >= PantiesOverrideStore.TypeCount) return -1;
        if (color < 0 || color >= PantiesOverrideStore.ColorCount) return -1;
        return type * PantiesOverrideStore.ColorCount + color;
    }

    private static string Key(CharID id) => KeyPrefix + ((int)id);

    private static ulong ReadBits(CharID id)
    {
        var data = ExSaveStore.CommonData;
        if (data == null) return 0UL;
        if (!data.TryGet(Key(id), out var bytes)) return 0UL;
        if (bytes == null || bytes.Length == 0) return 0UL;
        ulong result = 0UL;
        int len = Math.Min(bytes.Length, 8);
        for (int i = 0; i < len; i++) result |= (ulong)bytes[i] << (i * 8);
        return result;
    }

    private static void WriteBits(CharID id, ulong bits)
    {
        var data = ExSaveStore.CommonData;
        if (data == null) return;
        byte[] buf = new byte[8];
        for (int i = 0; i < 8; i++) buf[i] = (byte)((bits >> (i * 8)) & 0xFF);
        data.Set(Key(id), buf);
    }
}
