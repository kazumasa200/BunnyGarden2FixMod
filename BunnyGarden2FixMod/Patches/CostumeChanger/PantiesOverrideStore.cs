using BunnyGarden2FixMod.ExSave;
using BunnyGarden2FixMod.Patches.CostumeChanger.Internal;
using BunnyGarden2FixMod.Utils;
using GB.Game;
using MessagePack;
using System.Collections.Generic;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// キャラごとのパンツ override（type 0-6, color 0-4）を保持するプロセス内セッションストア。
/// 永続化は ExSave (CommonData) の <c>panties.override.all</c> キーに行う。
/// type, color 両方が一対で保存される。
/// </summary>
public static class PantiesOverrideStore
{
    private const string ExSaveKey = "panties.override.all";

    private static readonly Dictionary<CharID, (int Type, int Color)> s_overrides = new();

    /// <summary>
    /// rehydrate が例外で失敗したことを記録するフラグ。
    /// true の間は WriteToExSave を抑止し、破損データによる旧データ上書きを防ぐ。
    /// </summary>
    private static bool s_rehydrateFailed = false;

    public const int TypeCount = 7;   // A-G
    public const int ColorCount = 5;  // 0-4
    public const int TotalCount = TypeCount * ColorCount; // 35

    public static void Set(CharID id, int type, int color)
    {
        if (SetValidatedNoMirror(id, type, color))
            WriteToExSave();
    }

    public static void Clear(CharID id)
    {
        if (s_overrides.Remove(id))
            WriteToExSave();
    }

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

    /// <summary>
    /// ExSave から override 状態を読み込み、s_overrides を再構築する。
    /// ExSaveStore.LoadFromPath 後に呼ばれる。
    /// </summary>
    public static void RehydrateFromExSave()
    {
        s_overrides.Clear();
        if (!OverrideStorePersistence.TryReadFromExSave<PantiesOverrideExSaveEntry>(
                ExSaveKey, "[PantiesOverrideStore]", Configs.PersistCostumeOverrides.Value,
                out var dict, out s_rehydrateFailed))
        {
            return;
        }

        foreach (var kv in dict)
            SetValidatedNoMirror((CharID)kv.Key, kv.Value.Type, kv.Value.Color);
        PatchLogger.LogInfo($"[PantiesOverrideStore] rehydrate: {s_overrides.Count} 個復元");
    }

    /// <summary>in-memory の s_overrides をクリアする（Reset 時に呼ばれる）。</summary>
    public static void ClearMemory()
    {
        s_overrides.Clear();
        s_rehydrateFailed = false;
    }

    /// <summary>バリデーション後に dict へ投入する（ExSave mirror を行わない）。無効入力は false を返す。</summary>
    private static bool SetValidatedNoMirror(CharID id, int type, int color)
    {
        if (id >= CharID.NUM) return false;
        if (type < 0 || type >= TypeCount) return false;
        if (color < 0 || color >= ColorCount) return false;
        s_overrides[id] = (type, color);
        return true;
    }

    /// <summary>s_overrides の全内容を ExSave の CommonData に書き込む。</summary>
    private static void WriteToExSave()
    {
        OverrideStorePersistence.WriteToExSave(
            ExSaveKey, "[PantiesOverrideStore]", Configs.PersistCostumeOverrides.Value,
            s_rehydrateFailed, BuildSerializableDict);
    }

    /// <summary>s_overrides を Dictionary&lt;int, PantiesOverrideExSaveEntry&gt; に変換する（MessagePack 直列化用）。</summary>
    private static Dictionary<int, PantiesOverrideExSaveEntry> BuildSerializableDict()
    {
        var dict = new Dictionary<int, PantiesOverrideExSaveEntry>(s_overrides.Count);
        foreach (var kv in s_overrides)
            dict[(int)kv.Key] = new PantiesOverrideExSaveEntry { Type = (byte)kv.Value.Type, Color = (byte)kv.Value.Color };
        return dict;
    }
}

// MessagePack-CSharp の DynamicObjectResolver は public 型のみ解決するため public class 必須
/// <summary>パンツ override の ExSave 直列化用 POCO。</summary>
[MessagePackObject]
public class PantiesOverrideExSaveEntry
{
    [Key(0)]
    public byte Type { get; set; }

    [Key(1)]
    public byte Color { get; set; }
}
