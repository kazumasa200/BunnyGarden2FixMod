using BunnyGarden2FixMod.ExSave;
using BunnyGarden2FixMod.Patches.CostumeChanger.Internal;
using BunnyGarden2FixMod.Utils;
using Cysharp.Threading.Tasks;
using GB.Game;
using MessagePack;
using System.Collections.Generic;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// キャラごとの「上衣移植」override（donor キャラ + donor コスチューム）を保持する
/// プロセス内セッションストア。永続化は ExSave (CommonData) の <c>tops.override.all</c> キーに行う。
///
/// 制約:
///   - target == donor は許可（自身の他コスチューム由来 tops を素体に移植する用途）
///   - donor の costume がフルボディ衣装 (Bunnygirl / フルボディ DLC) は拒否（構造差大）
///   - SwimWear donor は許可（Bottoms と方針が異なる、tops-transplant-c2-full §2.2）
///   - target 側がフルボディ衣装のキャラ状態のときは別途 Apply 段階でスキップする
///
/// 投入経路: Wardrobe (F7) Tops タブの apply ハンドラから <see cref="Set"/> / <see cref="Clear"/>。
/// シーン遷移をまたいで保持される（pickerHost が DontDestroyOnLoad）。
/// </summary>
public static class TopsOverrideStore
{
    private const string ExSaveKey = "tops.override.all";

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
    /// 無効な組み合わせは false を返す（target/donor 範囲外、costume == Num / フルボディ衣装）。
    /// 既存 target を上書きする場合も true を返す（重複検出は呼出し側の責務）。
    /// SwimWear donor は許可する（Bottoms と異なり Tops 領域は SwimWearStockingPatch と独立想定）。
    /// donor == target も許可（自身の他コスチューム移植）。
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

    public static void ClearAll() => s_overrides.Clear();

    public static bool TryGet(CharID target, out Entry entry) => s_overrides.TryGetValue(target, out entry);

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
    /// 登録済み override を (target, entry) ペアで列挙する。再適用 (live tuning 等) で全 target を巡回するために使う。
    /// 列挙中の Add/Remove は呼出し側の責務（必要なら ToList する）。
    /// </summary>
    public static IEnumerable<KeyValuePair<CharID, Entry>> EnumerateOverrides() => s_overrides;

    /// <summary>
    /// ExSave から override 状態を読み込み、s_overrides を再構築する。
    /// ExSaveStore.LoadFromPath 後に呼ばれる。
    /// </summary>
    public static void RehydrateFromExSave()
    {
        s_overrides.Clear();
        if (!OverrideStorePersistence.TryReadFromExSave<TopsOverrideExSaveEntry>(
                ExSaveKey, "[TopsOverrideStore]", Configs.PersistCostumeOverrides.Value,
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
                PatchLogger.LogWarning($"[TopsOverrideStore] rehydrate skip: target={(CharID)kv.Key}, donor={(CharID)kv.Value.DonorChar}/{donorCostume}");
            }
        }
        PatchLogger.LogInfo($"[TopsOverrideStore] rehydrate: {restored} 個復元");

        // rehydrate された donor を先行 preload（setup() Postfix と preload 完了の race を縮める）。
        // ApplyIfOverridden 側でも donor 未ロード時の自動 preload + re-apply フォールバックがあるが、
        // 先行起動して setup と race させたほうがほぼ常に間に合う。
        // Tops は 3 系統の donor を要する（ApplyTopsAsync と同じ）:
        //   (a) main donor: (donor, costume)
        //   (b) target skin donor: (target, Babydoll)
        //   (c) donor skin donor: (donor, Babydoll) — donorCostume が既に Babydoll なら不要
        foreach (var e in EnumerateUniqueDonors())
        {
            TopsLoader.PreloadDonorAsync(e.DonorChar, e.DonorCostume).Forget();
            if (e.DonorCostume != TopsLoader.SkinDonorCostume)
                TopsLoader.PreloadDonorAsync(e.DonorChar, TopsLoader.SkinDonorCostume).Forget();
        }
        foreach (var kv in EnumerateOverrides())
            TopsLoader.PreloadDonorAsync(kv.Key, TopsLoader.SkinDonorCostume).Forget();
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
        // フルボディ衣装 (Bunnygirl / フルボディ DLC) は構造差大で donor 不適。
        // SwimWear donor は許可 (ApplySwimWearBottomsPhase で full-body 移植経路がある)。
        if (costume != CostumeType.SwimWear && costume.IsFullBodyCostume()) return false;
        s_overrides[target] = new Entry(donor, costume);
        return true;
    }

    /// <summary>s_overrides の全内容を ExSave の CommonData に書き込む。</summary>
    private static void WriteToExSave()
    {
        OverrideStorePersistence.WriteToExSave(
            ExSaveKey, "[TopsOverrideStore]", Configs.PersistCostumeOverrides.Value,
            s_rehydrateFailed, BuildSerializableDict);
    }

    /// <summary>s_overrides を Dictionary&lt;int, TopsOverrideExSaveEntry&gt; に変換する（MessagePack 直列化用）。</summary>
    private static Dictionary<int, TopsOverrideExSaveEntry> BuildSerializableDict()
    {
        var dict = new Dictionary<int, TopsOverrideExSaveEntry>(s_overrides.Count);
        foreach (var kv in s_overrides)
            dict[(int)kv.Key] = new TopsOverrideExSaveEntry { DonorChar = (byte)kv.Value.DonorChar, DonorCostume = (byte)kv.Value.DonorCostume };
        return dict;
    }
}

// MessagePack-CSharp の DynamicObjectResolver は public 型のみ解決するため public class 必須
/// <summary>上衣移植 override の ExSave 直列化用 POCO。</summary>
[MessagePackObject]
public class TopsOverrideExSaveEntry
{
    [Key(0)]
    public byte DonorChar { get; set; }

    [Key(1)]
    public byte DonorCostume { get; set; }
}
