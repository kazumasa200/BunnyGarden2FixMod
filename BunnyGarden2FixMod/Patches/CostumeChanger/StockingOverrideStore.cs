using System.Collections.Generic;
using GB.Game;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// キャラごとのストッキング override（int: 0=なし, 1=黒, 2=白, 3=網黒, 4=網白,
/// 5=ニーハイ, 6=ニーハイ(黒), 7=ニーハイ(白)）を保持するプロセス内セッションストア。永続化しない。
/// </summary>
public static class StockingOverrideStore
{
    private static readonly Dictionary<CharID, int> s_overrides = new();

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
        if (id >= CharID.NUM) return;
        if (stocking < Min || stocking > Max) return;
        s_overrides[id] = stocking;
    }

    public static void Clear(CharID id) => s_overrides.Remove(id);

    public static bool TryGet(CharID id, out int stocking) =>
        s_overrides.TryGetValue(id, out stocking);
}
