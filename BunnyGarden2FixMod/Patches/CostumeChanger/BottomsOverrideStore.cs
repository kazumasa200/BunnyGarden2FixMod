using BunnyGarden2FixMod.ExSave;
using BunnyGarden2FixMod.Patches.CostumeChanger.Internal;
using BunnyGarden2FixMod.Utils;
using Cysharp.Threading.Tasks;
using GB.Game;
using MessagePack;
using System.Collections.Generic;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// キャラごとの「下衣移植」override（donor キャラ + donor コスチューム）を保持する
/// プロセス内セッションストア。永続化は ExSave (CommonData) の <c>bottoms.override.all</c> キーに行う。
///
/// 制約:
///   - target == donor は許可（自身の他コスチューム由来 bottoms を素体に移植する用途）
///   - donor の costume がフルボディ衣装 (Bunnygirl / フルボディ DLC) は拒否
///     （mesh_costume_pants/skirt 構造差大）
///   - SwimWear donor は許可（mesh_costume_skirt を持つため水着のスカート部分を移植可）
///   - target 側がフルボディ衣装のキャラ状態のときは Apply 段階で別途スキップ
///     (SwimWearStockingPatch との競合回避)
///
/// 投入経路: Wardrobe (F7) Bottoms タブの apply ハンドラ
/// (<see cref="UI.CostumePickerController.ApplyBottomsAsync"/>) から <see cref="Set"/> /
/// <see cref="Clear"/> を呼ぶ。シーン遷移をまたいで保持される（pickerHost が DontDestroyOnLoad）。
/// </summary>
public static class BottomsOverrideStore
{
    private const string ExSaveKey = "bottoms.override.all";

    public readonly struct Entry
    {
        public Entry(CharID donorChar, CostumeType donorCostume)
        {
            DonorChar = donorChar;
            DonorCostume = donorCostume;
        }

        public CharID DonorChar { get; }
        public CostumeType DonorCostume { get; }
    }

    private static readonly Dictionary<CharID, Entry> s_overrides = new();

    /// <summary>
    /// rehydrate が例外で失敗したことを記録するフラグ。
    /// true の間は WriteToExSave を抑止し、破損データによる旧データ上書きを防ぐ。
    /// </summary>
    private static bool s_rehydrateFailed = false;

    /// <summary>
    /// 無効な組み合わせは false を返す（target/donor 範囲外、costume == Num / フルボディ衣装 (SwimWear 除く)）。
    /// SwimWear donor は許可（mesh_costume_skirt を持つため）。
    /// donor == target も許可（自身の他コスチューム移植）。
    /// 既存 target を上書きする場合も true を返す（重複検出は呼出し側の責務）。
    /// </summary>
    public static bool Set(CharID target, CharID donor, CostumeType costume)
    {
        bool ok = SetValidatedNoMirror(target, donor, costume);
        if (ok) WriteToExSave();
        return ok;
    }

    public static void Clear(CharID target)
    {
        if (s_overrides.Remove(target))
            WriteToExSave();
    }

    public static bool TryGet(CharID target, out Entry entry) => s_overrides.TryGetValue(target, out entry);

    /// <summary>登録済み全 override を target ごとに列挙する (TopsOverrideStore.EnumerateOverrides と対称)。</summary>
    public static IEnumerable<KeyValuePair<CharID, Entry>> EnumerateOverrides() => s_overrides;

    /// <summary>
    /// 登録済み override から (donor, costume) のユニーク列を返す。
    /// preload は (donor, costume) 単位でキャッシュするため、複数 target が同じ donor を共有しても
    /// preload は 1 回で済む。
    /// </summary>
    public static IEnumerable<Entry> EnumerateUniqueDonors()
    {
        var seen = new HashSet<(CharID, CostumeType)>();
        foreach (var e in s_overrides.Values)
        {
            var key = (e.DonorChar, e.DonorCostume);
            if (seen.Add(key)) yield return e;
        }
    }

    /// <summary>
    /// ExSave から override 状態を読み込み、s_overrides を再構築する。
    /// ExSaveStore.LoadFromPath 後に呼ばれる。
    /// </summary>
    public static void RehydrateFromExSave()
    {
        s_overrides.Clear();
        if (!OverrideStorePersistence.TryReadFromExSave<BottomsOverrideExSaveEntry>(
                ExSaveKey, "[BottomsOverrideStore]", Configs.PersistCostumeOverrides.Value,
                out var dict, out s_rehydrateFailed))
        {
            return;
        }

        int restored = 0;
        foreach (var kv in dict)
        {
            var donorCostume = (CostumeType)kv.Value.DonorCostume;
            if (SetValidatedNoMirror((CharID)kv.Key, (CharID)kv.Value.DonorChar, donorCostume))
            {
                restored++;
            }
            else
            {
                // reject 理由: full-body 扱い拡張 / 不正 CharID / costume == Num のいずれか。
                // ExSave データの破損や旧 enum 値の検出にも使えるよう全 reject を 1 行ログ。
                PatchLogger.LogWarning($"[BottomsOverrideStore] rehydrate skip: target={(CharID)kv.Key}, donor={(CharID)kv.Value.DonorChar}/{donorCostume}");
            }
        }
        PatchLogger.LogInfo($"[BottomsOverrideStore] rehydrate: {restored} 個復元");

        // rehydrate された donor を先行 preload（setup() Postfix と preload 完了の race を縮める）。
        // ApplyIfOverridden 側でも donor 未ロード時の自動 preload + re-apply フォールバックがあるが、
        // 先行起動して setup と race させたほうがほぼ常に間に合う。
        foreach (var e in EnumerateUniqueDonors())
            BottomsLoader.PreloadDonorAsync(e.DonorChar, e.DonorCostume).Forget();
    }

    /// <summary>in-memory の s_overrides をクリアする（Reset 時に呼ばれる）。</summary>
    public static void ClearMemory()
    {
        s_overrides.Clear();
        s_rehydrateFailed = false;
    }

    /// <summary>バリデーション後に dict へ投入する（ExSave mirror を行わない）。</summary>
    private static bool SetValidatedNoMirror(CharID target, CharID donor, CostumeType costume)
    {
        if (target >= CharID.NUM || donor >= CharID.NUM) return false;
        if (costume == CostumeType.Num) return false;
        // フルボディ衣装 (Bunnygirl / フルボディ DLC) は mesh_costume_pants/skirt 構造差大で donor 不適。
        // SwimWear donor は許可 (mesh_costume_skirt を持つため)。
        if (costume != CostumeType.SwimWear && costume.IsFullBodyCostume()) return false;
        s_overrides[target] = new Entry(donor, costume);
        return true;
    }

    /// <summary>s_overrides の全内容を ExSave の CommonData に書き込む。</summary>
    private static void WriteToExSave()
    {
        OverrideStorePersistence.WriteToExSave(
            ExSaveKey, "[BottomsOverrideStore]", Configs.PersistCostumeOverrides.Value,
            s_rehydrateFailed, BuildSerializableDict);
    }

    /// <summary>s_overrides を Dictionary&lt;int, BottomsOverrideExSaveEntry&gt; に変換する（MessagePack 直列化用）。</summary>
    private static Dictionary<int, BottomsOverrideExSaveEntry> BuildSerializableDict()
    {
        var dict = new Dictionary<int, BottomsOverrideExSaveEntry>(s_overrides.Count);
        foreach (var kv in s_overrides)
            dict[(int)kv.Key] = new BottomsOverrideExSaveEntry { DonorChar = (byte)kv.Value.DonorChar, DonorCostume = (byte)kv.Value.DonorCostume };
        return dict;
    }
}

// MessagePack-CSharp の DynamicObjectResolver は public 型のみ解決するため public class 必須
/// <summary>下衣移植 override の ExSave 直列化用 POCO。</summary>
[MessagePackObject]
public class BottomsOverrideExSaveEntry
{
    [Key(0)]
    public byte DonorChar { get; set; }

    [Key(1)]
    public byte DonorCostume { get; set; }
}
