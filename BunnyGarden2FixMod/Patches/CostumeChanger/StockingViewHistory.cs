using BunnyGarden2FixMod.ExSave;
using BunnyGarden2FixMod.Utils;
using GB.Game;
using System;
using System.Collections.Generic;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// ExSave へのストッキング表示履歴ファサード。キャラ × stocking type を bit flag で記録。
/// <see cref="ExSaveStore.CommonData"/>（スロット非依存）に保存。
/// キー: <c>stocking.viewed.{charIdInt}</c>、値: byte[4]（実際には 5 bit のみ使用）。
/// ビット i が立てば stocking type i を表示済み。type 0-4（なし/黒/白/網黒/網白）。
/// </summary>
public static class StockingViewHistory
{
    private const string KeyPrefix = "stocking.viewed.";

    private static CharID s_lastCharId = CharID.NUM;
    private static int s_lastStocking = -1;

    public static bool IsViewed(CharID id, int stocking)
    {
        if (id >= CharID.NUM) return false;
        if (stocking < 0 || stocking >= 32) return false;
        uint bits = ReadBits(id);
        return (bits & (1u << stocking)) != 0;
    }

    public static bool IsAnyViewed(CharID id)
    {
        if (id >= CharID.NUM) return false;
        return ReadBits(id) != 0;
    }

    /// <summary>
    /// dedup キャッシュをリセットする。<see cref="ExSaveStore"/> の
    /// Reset / LoadFromPath で底データが入替わる際に呼び、次回 MarkViewed で
    /// 再登録が走るようにする。
    /// </summary>
    public static void ResetDedup()
    {
        s_lastCharId = CharID.NUM;
        s_lastStocking = -1;
    }

    /// <summary>
    /// 指定キャラ × stocking を表示済みとして記録する。
    /// スロット非依存の Common バケットに書くため <c>CurrentSaveSlot</c> 未確定でも記録する。
    /// </summary>
    public static void MarkViewed(CharID id, int stocking)
    {
        if (id >= CharID.NUM) return;
        if (stocking < 0 || stocking >= 32) return;

        if (s_lastCharId == id && s_lastStocking == stocking) return;
        s_lastCharId = id;
        s_lastStocking = stocking;

        uint bits = ReadBits(id);
        uint newBits = bits | (1u << stocking);
        if (newBits == bits) return;
        WriteBits(id, newBits);
        PatchLogger.LogInfo($"[StockingViewHistory] 記録: {id} / stocking={stocking}");
    }

    /// <summary>指定キャラの履歴をすべてクリアする。dedup もリセット。</summary>
    public static void ClearAll(CharID id)
    {
        if (id >= CharID.NUM) return;
        WriteBits(id, 0u);
        ResetDedup();
        PatchLogger.LogInfo($"[StockingViewHistory] クリア: {id}");
    }

    /// <summary>
    /// 指定した stocking type 集合を一括で表示済みにする。
    /// 呼び出し側で「解放可能」な集合を渡す前提。範囲外 (&lt;0 or &gt;=32) は無視。
    /// </summary>
    public static void MarkViewedBulk(CharID id, IEnumerable<int> types)
    {
        if (id >= CharID.NUM || types == null) return;
        uint bits = ReadBits(id);
        uint newBits = bits;
        foreach (var t in types)
        {
            if (t < 0 || t >= 32) continue;
            newBits |= (1u << t);
        }
        if (newBits == bits) return;
        WriteBits(id, newBits);
        ResetDedup();
        PatchLogger.LogInfo($"[StockingViewHistory] 一括記録: {id} bits=0x{newBits:X}");
    }

    public static bool IsTypeViewed(CharID id, int stocking) => IsViewed(id, stocking);

    private static string Key(CharID id) => KeyPrefix + ((int)id);

    private static uint ReadBits(CharID id)
    {
        var data = ExSaveStore.CommonData;
        if (data == null) return 0u;
        if (!data.TryGet(Key(id), out var bytes)) return 0u;
        if (bytes == null || bytes.Length == 0) return 0u;
        uint result = 0u;
        int len = Math.Min(bytes.Length, 4);
        for (int i = 0; i < len; i++) result |= (uint)bytes[i] << (i * 8);
        return result;
    }

    private static void WriteBits(CharID id, uint bits)
    {
        var data = ExSaveStore.CommonData;
        if (data == null) return;
        byte[] buf = new byte[4];
        buf[0] = (byte)(bits & 0xFF);
        buf[1] = (byte)((bits >> 8) & 0xFF);
        buf[2] = (byte)((bits >> 16) & 0xFF);
        buf[3] = (byte)((bits >> 24) & 0xFF);
        data.Set(Key(id), buf);
    }
}
