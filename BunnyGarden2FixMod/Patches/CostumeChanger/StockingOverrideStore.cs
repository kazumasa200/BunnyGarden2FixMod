using BunnyGarden2FixMod.ExSave;
using BunnyGarden2FixMod.Utils;
using GB.Game;
using MessagePack;
using System;
using System.Collections.Generic;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// キャラごとのストッキング override（int: 0=なし, 1=黒, 2=白, 3=網黒, 4=網白,
/// 5=ニーハイ, 6=ニーハイ(黒), 7=ニーハイ(白)）を保持するプロセス内セッションストア。
/// 永続化は ExSave (CommonData) の <c>stocking.override.all</c> キーに行う。
/// </summary>
public static class StockingOverrideStore
{
    private const string ExSaveKey = "stocking.override.all";

    private static readonly Dictionary<CharID, int> s_overrides = new();

    /// <summary>
    /// rehydrate が例外で失敗したことを記録するフラグ。
    /// true の間は WriteToExSave を抑止し、破損データによる旧データ上書きを防ぐ。
    /// </summary>
    private static bool s_rehydrateFailed = false;

    public const int Min = 0;
    public const int Max = 7;

    /// <summary>ゲーム本体のストッキングスロットに kneehigh メッシュを差し込む MOD 独自タイプ。</summary>
    public const int KneeSocks = 5;

    /// <summary>kneehigh メッシュ + 黒ストッキングマテリアル。</summary>
    public const int KneeSocksBlack = 6;

    /// <summary>kneehigh メッシュ + 白ストッキングマテリアル。</summary>
    public const int KneeSocksWhite = 7;

    /// <summary>type がニーハイ系（5–7）かどうかを返す。</summary>
    public static bool IsKneeSocksType(int type) => type >= KneeSocks && type <= Max;

    /// <summary>
    /// ニーハイ系 type に対応するマテリアルインデックスを返す。
    /// 0=デフォルト(kneehigh 素材), 1=黒ストッキング, 2=白ストッキング
    /// </summary>
    public static int KneeSocksStockingType(int type) => type switch
    {
        KneeSocks => 0,
        KneeSocksBlack => 1,
        KneeSocksWhite => 2,
        _ => 0, // 呼び出し元で IsKneeSocksType 確認済み前提。5–7 以外は到達しない。
    };

    public static void Set(CharID id, int stocking)
    {
        if (SetValidatedNoMirror(id, stocking))
            WriteToExSave();
    }

    public static void Clear(CharID id)
    {
        if (s_overrides.Remove(id))
            WriteToExSave();
    }

    public static bool TryGet(CharID id, out int stocking) =>
        s_overrides.TryGetValue(id, out stocking);

    /// <summary>
    /// ExSave から override 状態を読み込み、s_overrides を再構築する。
    /// ExSaveStore.LoadFromPath 後に呼ばれる。
    /// </summary>
    public static void RehydrateFromExSave()
    {
        s_overrides.Clear();
        s_rehydrateFailed = false;
        if (!Configs.PersistCostumeOverrides.Value)
        {
            PatchLogger.LogInfo("[StockingOverrideStore] rehydrate skip: PersistCostumeOverrides=false");
            return;
        }
        if (!ExSaveStore.CommonData.TryGet(ExSaveKey, out byte[] bytes) || bytes == null || bytes.Length == 0)
        {
            PatchLogger.LogInfo("[StockingOverrideStore] rehydrate skip: ExSave entry なし");
            return;
        }
        try
        {
            var dict = MessagePackSerializer.Deserialize<Dictionary<int, byte>>(bytes, ExSaveData.s_options);
            foreach (var kv in dict)
                SetValidatedNoMirror((CharID)kv.Key, (int)kv.Value);
            int restored = s_overrides.Count;
            PatchLogger.LogInfo($"[StockingOverrideStore] rehydrate: {bytes.Length} bytes → {restored} 個復元");
        }
        catch (Exception ex)
        {
            s_rehydrateFailed = true;
            PatchLogger.LogWarning($"[StockingOverrideStore] ExSave rehydrate 失敗、空で続行 + 次回保存もスキップして元データ保護: {ex.Message} (bytes={bytes?.Length ?? 0})");
        }
    }

    /// <summary>in-memory の s_overrides をクリアする（Reset 時に呼ばれる）。</summary>
    public static void ClearMemory()
    {
        s_overrides.Clear();
        s_rehydrateFailed = false;
    }

    /// <summary>バリデーション後に dict へ投入する（ExSave mirror を行わない）。無効入力は false を返す。</summary>
    private static bool SetValidatedNoMirror(CharID id, int stocking)
    {
        if (id >= CharID.NUM) return false;
        if (stocking < Min || stocking > Max) return false;
        s_overrides[id] = stocking;
        return true;
    }

    /// <summary>s_overrides の全内容を ExSave の CommonData に書き込む。</summary>
    private static void WriteToExSave()
    {
        if (!Configs.PersistCostumeOverrides.Value) return;
        if (s_rehydrateFailed)
        {
            PatchLogger.LogWarning("[StockingOverrideStore] ExSave 書込スキップ: 直前の rehydrate 失敗データを保護中（再起動 or 修復まで dict のみ更新）");
            return;
        }
        try
        {
            var dict = BuildSerializableDict();
            byte[] bytes = MessagePackSerializer.Serialize(dict, ExSaveData.s_options);
            ExSaveStore.CommonData.Set(ExSaveKey, bytes);
            PatchLogger.LogDebug($"[StockingOverrideStore] write: {dict.Count} 個 → {bytes.Length} bytes");
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[StockingOverrideStore] ExSave 書込失敗、in-memory 維持: {ex.Message}");
        }
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
