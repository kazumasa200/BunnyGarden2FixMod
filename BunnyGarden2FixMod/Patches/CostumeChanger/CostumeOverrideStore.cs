using BunnyGarden2FixMod.ExSave;
using BunnyGarden2FixMod.Patches.CostumeChanger.Internal;
using BunnyGarden2FixMod.Utils;
using GB.Game;
using System.Collections.Generic;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// キャラごとの MOD override 衣装を保持するプロセス内セッションストア。
/// 永続化は ExSave (CommonData) の <c>costume.override.all</c> キーに行う。
/// </summary>
public static class CostumeOverrideStore
{
    private const string ExSaveKey = "costume.override.all";

    private static readonly Dictionary<CharID, CostumeType> s_overrides = new();

    /// <summary>
    /// rehydrate が例外で失敗したことを記録するフラグ。
    /// true の間は WriteToExSave を抑止し、破損データによる旧データ上書きを防ぐ。
    /// </summary>
    private static bool s_rehydrateFailed = false;

    /// <summary>指定キャラの override 衣装を設定する。</summary>
    public static void Set(CharID id, CostumeType costume)
    {
        if (SetValidatedNoMirror(id, costume))
            WriteToExSave();
    }

    /// <summary>指定キャラの override を解除する。</summary>
    public static void Clear(CharID id)
    {
        if (s_overrides.Remove(id))
            WriteToExSave();
    }

    /// <summary>指定キャラの override を取得する。未設定なら false。</summary>
    public static bool TryGet(CharID id, out CostumeType costume) =>
        s_overrides.TryGetValue(id, out costume);

    /// <summary>
    /// ExSave から override 状態を読み込み、s_overrides を再構築する。
    /// ExSaveStore.LoadFromPath 後に呼ばれる。
    /// </summary>
    public static void RehydrateFromExSave()
    {
        s_overrides.Clear();
        if (!OverrideStorePersistence.TryReadFromExSave<byte>(
                ExSaveKey, "[CostumeOverrideStore]", Configs.PersistCostumeOverrides.Value,
                out var dict, out s_rehydrateFailed))
        {
            return;
        }

        foreach (var kv in dict)
            SetValidatedNoMirror((CharID)kv.Key, (CostumeType)kv.Value);
        PatchLogger.LogInfo($"[CostumeOverrideStore] rehydrate: {s_overrides.Count} 個復元");
    }

    /// <summary>in-memory の s_overrides をクリアする（Reset 時に呼ばれる）。</summary>
    public static void ClearMemory()
    {
        s_overrides.Clear();
        s_rehydrateFailed = false;
    }

    /// <summary>バリデーション後に dict へ投入する（ExSave mirror を行わない）。無効入力は false を返す。</summary>
    private static bool SetValidatedNoMirror(CharID id, CostumeType costume)
    {
        if (id >= CharID.NUM) return false;
        if (costume >= CostumeType.Num) return false;
        s_overrides[id] = costume;
        return true;
    }

    /// <summary>s_overrides の全内容を ExSave の CommonData に書き込む。</summary>
    private static void WriteToExSave()
    {
        OverrideStorePersistence.WriteToExSave(
            ExSaveKey, "[CostumeOverrideStore]", Configs.PersistCostumeOverrides.Value,
            s_rehydrateFailed, BuildSerializableDict);
    }

    /// <summary>s_overrides を Dictionary&lt;int, byte&gt; に変換する（MessagePack 直列化用）。</summary>
    private static Dictionary<int, byte> BuildSerializableDict()
    {
        var dict = new Dictionary<int, byte>(s_overrides.Count);
        foreach (var kv in s_overrides)
            dict[(int)kv.Key] = (byte)kv.Value;
        return dict;
    }
}
