using System;
using System.Collections.Generic;
using MessagePack;

namespace BunnyGarden2FixMod.ExSave;

/// <summary>
/// 単一セーブスロット単位の key/value ストア。
/// <see cref="ExSaveData"/> の各スロット要素として格納される。
///
/// <para>
/// 直列化は MessagePack 属性付き POCO として <see cref="ExSaveData"/> が一括担当する。
/// このクラス自身は entry 列の操作 API を提供する。
/// </para>
/// </summary>
[MessagePackObject]
public class ExSaveSlotData
{
    /// <summary>汎用 key/value ストア。key は <c>cheki.hires.{n}</c> 等。value は PNG/JPG バイト列。</summary>
    [Key(0)]
    public Dictionary<string, byte[]> Entries { get; set; } = new();

    /// <summary>格納されているエントリ数。</summary>
    [IgnoreMember]
    public int Count => Entries.Count;

    /// <summary>指定キーの値を取り出す。</summary>
    public bool TryGet(string key, out byte[] value) => Entries.TryGetValue(key, out value);

    /// <summary>
    /// 指定したキーにバイト列を格納する。
    /// <paramref name="value"/> が null の場合は <see cref="Array.Empty{T}"/> に正規化して格納する（往復非対称を防ぐ）。
    /// </summary>
    public void Set(string key, byte[] value) =>
        Entries[key] = value ?? Array.Empty<byte>();

    /// <summary>指定キーのエントリを削除する。</summary>
    public bool Remove(string key) => Entries.Remove(key);

    /// <summary>指定キーが存在するか確認する。</summary>
    public bool Has(string key) => Entries.ContainsKey(key);

    /// <summary>全エントリを削除する。</summary>
    public void Clear() => Entries.Clear();

    /// <summary>全エントリをディープコピーした新しいインスタンスを返す。</summary>
    public ExSaveSlotData CloneDeep()
    {
        var clone = new ExSaveSlotData();
        foreach (var kv in Entries)
        {
            // Set で null→空に正規化されているため null は来ないはずだが、
            // Deserialize 直後に外部由来の null が残る可能性に備えて防御する。
            clone.Entries[kv.Key] = kv.Value == null
                ? Array.Empty<byte>()
                : (byte[])kv.Value.Clone();
        }
        return clone;
    }
}
