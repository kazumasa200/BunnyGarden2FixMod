using BunnyGarden2FixMod.ExSave;
using BunnyGarden2FixMod.Utils;
using GB.Game;
using System;
using System.Collections.Generic;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// ExSave への衣装表示履歴ファサード。キャラ × 衣装 の bit flag を
/// <see cref="ExSaveStore.CommonData"/>（スロット非依存の共通データ）に格納する。
/// 実ディスク書込は既存 ExSaveLifecyclePatch のセーブフックに便乗する（本クラスからフラッシュしない）。
/// 設計書 § 「CostumeViewHistory」参照。
/// </summary>
public static class CostumeViewHistory
{
    // 現在のキー空間。将来 CostumeType.Num > 32 になった場合は KeyPrefix を "costume.viewed.v2." に
    // 切り替え、ReadBits/WriteBits を byte[8]+ 対応に拡張する移行パスを想定している。
    private const string KeyPrefix = "costume.viewed.";

    // プロセス内 dedup キャッシュ。連続 Preload による冗長書込を抑える。
    private static CharID s_lastCharId = CharID.NUM;

    private static CostumeType s_lastCostume = CostumeType.Num;

    /// <summary>指定キャラ × 衣装 が一度以上表示済みか判定する。</summary>
    public static bool IsViewed(CharID id, CostumeType costume)
    {
        if (id >= CharID.NUM) return false;
        if (costume >= CostumeType.Num) return false;
        uint bits = ReadBits(id);
        return (bits & (1u << (int)costume)) != 0;
    }

    /// <summary>
    /// 指定キャラで表示済みの衣装を CostumeType 昇順の不変配列で返す。
    /// 履歴無し・破損時は空配列。呼び出し側でのキャスト書換えを防ぐため配列で返す。
    /// </summary>
    public static IReadOnlyList<CostumeType> GetViewedList(CharID id)
    {
        if (id >= CharID.NUM) return Array.Empty<CostumeType>();
        uint bits = ReadBits(id);
        if (bits == 0u) return Array.Empty<CostumeType>();
        var list = new List<CostumeType>();
        for (int i = 0; i < (int)CostumeType.Num; i++)
        {
            if ((bits & (1u << i)) != 0) list.Add((CostumeType)i);
        }
        return list.ToArray();
    }

    /// <summary>
    /// dedup キャッシュをリセットする。<see cref="ExSaveStore"/> の
    /// Reset / LoadFromPath で底データが入替わる際に呼び、次回 MarkViewed で
    /// 再登録が走るようにする。
    /// </summary>
    public static void ResetDedup()
    {
        s_lastCharId = CharID.NUM;
        s_lastCostume = CostumeType.Num;
    }

    /// <summary>
    /// 指定キャラ × 衣装 を表示済みとして記録する。
    /// 本履歴は <see cref="ExSaveStore.CommonData"/>（スロット非依存）に保存され、
    /// <c>Saves.Save</c> フックでそのまま永続化されるため、
    /// <c>CurrentSaveSlot</c> 未確定（タイトル直後・アルバム閲覧中等）でも記録する。
    /// 直前呼び出しと同一 (id, costume) なら no-op（dedup）。既にビットが立っていれば no-op。
    /// </summary>
    public static void MarkViewed(CharID id, CostumeType costume)
    {
        if (id >= CharID.NUM) return;
        if (costume >= CostumeType.Num) return;

        // dedup（連続 Preload 抑制）
        if (s_lastCharId == id && s_lastCostume == costume) return;
        s_lastCharId = id;
        s_lastCostume = costume;

        uint bits = ReadBits(id);
        uint newBits = bits | (1u << (int)costume);
        if (newBits == bits) return;  // 既にセット済み

        WriteBits(id, newBits);
        PatchLogger.LogInfo($"[CostumeViewHistory] 記録: {id} / {costume}");
    }

    /// <summary>指定キャラの履歴をすべてクリアする（bits=0 を書き込む）。dedup もリセット。</summary>
    public static void ClearAll(CharID id)
    {
        if (id >= CharID.NUM) return;
        WriteBits(id, 0u);
        ResetDedup();
        PatchLogger.LogInfo($"[CostumeViewHistory] クリア: {id}");
    }

    /// <summary>
    /// 指定した衣装集合を一括で表示済みにする。現在 bits と OR 合成し変化があれば 1 回だけ WriteBits。
    /// 呼び出し側で DLC 未導入等の「解放不可」を除外した集合を渡す前提。
    /// </summary>
    public static void MarkViewedBulk(CharID id, IEnumerable<CostumeType> costumes)
    {
        if (id >= CharID.NUM || costumes == null) return;
        uint bits = ReadBits(id);
        uint newBits = bits;
        foreach (var c in costumes)
        {
            if (c < CostumeType.Num) newBits |= (1u << (int)c);
        }
        if (newBits == bits) return;
        WriteBits(id, newBits);
        ResetDedup();
        PatchLogger.LogInfo($"[CostumeViewHistory] 一括記録: {id} bits=0x{newBits:X}");
    }

    private static string Key(CharID id) => KeyPrefix + ((int)id);

    private static uint ReadBits(CharID id)
    {
        var data = ExSaveStore.CommonData;
        if (data == null) return 0u;
        if (!data.TryGet(Key(id), out var bytes)) return 0u;
        if (bytes == null || bytes.Length == 0) return 0u;
        // リトルエンディアンで最大 4 バイト読む（現 CostumeType.Num=16 は 2 バイトに収まる）。
        // 過去バージョンが 1〜3 バイトで書いた場合も安全に読めるよう min(length, 4) で処理する。
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
