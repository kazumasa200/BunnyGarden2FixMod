using GB.Game;
using System.Collections.Generic;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// キャラごとの「上衣移植」override（donor キャラ + donor コスチューム）を保持する
/// プロセス内セッションストア。永続化しない。
///
/// 制約:
///   - target == donor は許可（自身の他コスチューム由来 tops を素体に移植する用途）
///   - donor の costume が Bunnygirl は拒否（フルボディスーツで構造差大）
///   - SwimWear は許可（Bottoms と方針が異なる、tops-transplant-c2-full §2.2）
///   - target 側が Bunnygirl のキャラ状態のときは別途 Apply 段階でスキップする
///
/// 投入経路: Wardrobe (F7) Tops タブの apply ハンドラから <see cref="Set"/> / <see cref="Clear"/>。
/// シーン遷移をまたいで保持される（pickerHost が DontDestroyOnLoad）。
/// </summary>
public static class TopsOverrideStore
{
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
    /// 無効な組み合わせは false を返す（target/donor 範囲外、costume == Num/Bunnygirl）。
    /// 既存 target を上書きする場合も true を返す（重複検出は呼出し側の責務）。
    /// SwimWear donor は許可する（Bottoms と異なり Tops 領域は SwimWearStockingPatch と独立想定）。
    /// donor == target も許可（自身の他コスチューム移植）。
    /// </summary>
    public static bool Set(CharID target, CharID donor, CostumeType costume)
    {
        if (target >= CharID.NUM || donor >= CharID.NUM) return false;
        if (costume == CostumeType.Num) return false;
        if (costume == CostumeType.Bunnygirl) return false;
        s_overrides[target] = new Entry(donor, costume);
        return true;
    }

    public static void Clear(CharID target) => s_overrides.Remove(target);

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
}
