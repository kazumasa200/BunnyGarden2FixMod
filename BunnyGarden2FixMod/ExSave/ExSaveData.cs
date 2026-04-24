using BunnyGarden2FixMod.Utils;
using MessagePack;
using System;
using System.Collections.Generic;

namespace BunnyGarden2FixMod.ExSave;

/// <summary>
/// .exmod サイドカーのルート構造。saveSlotId 毎に <see cref="ExSaveSlotData"/> を持つ。
/// 直列化は MessagePack (LZ4BlockArray 圧縮)。
///
/// <para>
/// 破損時は <see cref="Deserialize"/> で空データを返し、主セーブには一切触らないフェイルセーフ。
/// </para>
/// </summary>
[MessagePackObject]
public class ExSaveData
{
    /// <summary>saveSlotId → ExSaveSlotData。キーは 0..8 を想定。</summary>
    [Key(0)]
    public Dictionary<int, ExSaveSlotData> Slots { get; set; } = new();

    /// <summary>
    /// スロットに紐づかない共通データ（衣装閲覧履歴等）。
    /// MessagePack の [Key] 追記は後方互換なので、旧 .exmod (Key 0 のみ) は Common が空で Deserialize される。
    /// </summary>
    [Key(1)]
    public ExSaveSlotData Common { get; set; } = new();

    /// <summary>格納されているスロット数。</summary>
    [IgnoreMember]
    public int Count => Slots.Count;

    /// <summary>ゲーム本体と同じ LZ4BlockArray 圧縮オプション。</summary>
    private static readonly MessagePackSerializerOptions s_options =
        MessagePackSerializerOptions.Standard
            .WithCompression(MessagePackCompression.Lz4BlockArray);

    /// <summary>指定スロットが存在すれば返す。存在しなければ新規作成して登録する。</summary>
    public ExSaveSlotData GetOrCreateSlot(int saveSlot)
    {
        if (!Slots.TryGetValue(saveSlot, out var slot))
        {
            slot = new ExSaveSlotData();
            Slots[saveSlot] = slot;
        }
        return slot;
    }

    /// <summary>指定スロットが存在すれば out に返す。</summary>
    public bool TryGetSlot(int saveSlot, out ExSaveSlotData slot) =>
        Slots.TryGetValue(saveSlot, out slot);

    /// <summary>指定スロットを削除する。</summary>
    public bool RemoveSlot(int saveSlot) => Slots.Remove(saveSlot);

    /// <summary>指定スロットが存在するか確認する。</summary>
    public bool HasSlot(int saveSlot) => Slots.ContainsKey(saveSlot);

    /// <summary>
    /// MessagePack 直列化。LZ4BlockArray 圧縮を適用する。
    /// 例外は呼び出し側（ExSaveStore.SaveToPathAsync）の try/catch に委ねる。
    /// </summary>
    /// <exception cref="MessagePackSerializationException">
    /// スキーマ不整合や LZ4 圧縮失敗時に送出される可能性。
    /// </exception>
    public byte[] Serialize() => MessagePackSerializer.Serialize(this, s_options);

    /// <summary>
    /// MessagePack で逆直列化する。失敗時・null 要素検出時は空ないしデフォルトに差し替えてフェイルセーフ。
    /// </summary>
    public static ExSaveData Deserialize(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return new ExSaveData();
        try
        {
            var data = MessagePackSerializer.Deserialize<ExSaveData>(bytes, s_options);
            if (data == null) return new ExSaveData();
            data.Slots ??= new Dictionary<int, ExSaveSlotData>();
            data.Common ??= new ExSaveSlotData();
            data.Common.Entries ??= new Dictionary<string, byte[]>();
            List<string> commonNullKeys = null;
            foreach (var ek in data.Common.Entries)
            {
                if (ek.Value == null)
                {
                    commonNullKeys ??= new List<string>();
                    commonNullKeys.Add(ek.Key);
                }
            }
            if (commonNullKeys != null)
                foreach (var ek in commonNullKeys)
                    data.Common.Entries[ek] = Array.Empty<byte>();

            // 欠損キー / 他言語ツール由来の null 要素を正規化
            var nullKeys = new List<int>();
            foreach (var kv in data.Slots)
            {
                if (kv.Value == null) { nullKeys.Add(kv.Key); continue; }
                kv.Value.Entries ??= new Dictionary<string, byte[]>();
                // Entries 内の null バイト列を空配列に正規化（他言語ツール由来の .exmod 想定）
                List<string> entryNullKeys = null;
                foreach (var ek in kv.Value.Entries)
                {
                    if (ek.Value == null)
                    {
                        entryNullKeys ??= new List<string>();
                        entryNullKeys.Add(ek.Key);
                    }
                }
                if (entryNullKeys != null)
                    foreach (var ek in entryNullKeys)
                        kv.Value.Entries[ek] = Array.Empty<byte>();
            }
            foreach (var k in nullKeys)
                data.Slots[k] = new ExSaveSlotData();

            return data;
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[ExSave] Deserialize 失敗、空データで続行: {ex}");
            return new ExSaveData();
        }
    }
}
