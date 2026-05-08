using GB.Game;
using System.Collections.Generic;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// キャラごとの「下衣移植」override（donor キャラ + donor コスチューム）を保持する
/// プロセス内セッションストア。永続化しない。
///
/// 制約:
///   - target == donor は許可（自身の他コスチューム由来 bottoms を素体に移植する用途）
///   - donor の costume が Bunnygirl は拒否（フルボディスーツで mesh_costume_pants/skirt 構造差大）
///   - SwimWear donor は許可（mesh_costume_skirt を持つため水着のスカート部分を移植可）
///   - target 側が SwimWear / Bunnygirl のキャラ状態のときは Apply 段階で別途スキップ
///     (SwimWearStockingPatch との競合回避)
///
/// 投入経路: Wardrobe (F7) Bottoms タブの apply ハンドラ
/// (<see cref="UI.CostumePickerController.ApplyBottomsAsync"/>) から <see cref="Set"/> /
/// <see cref="Clear"/> を呼ぶ。シーン遷移をまたいで保持される（pickerHost が DontDestroyOnLoad）。
/// </summary>
public static class BottomsOverrideStore
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
    /// SwimWear donor は許可（mesh_costume_skirt を持つため）。
    /// donor == target も許可（自身の他コスチューム移植）。
    /// 既存 target を上書きする場合も true を返す（重複検出は呼出し側の責務）。
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
}
