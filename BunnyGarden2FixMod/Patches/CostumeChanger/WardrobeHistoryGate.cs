using GB;
using GB.Game;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// 衣装/パンツ/ストッキング閲覧履歴の記録対象を絞り込むための共通ガード。
/// 以下いずれかに該当するときは記録しない:
/// <list type="bullet">
///   <item><c>IsIngame == false</c> — タイトル/セーブロード/Album 中など
///         <c>m_currentCast</c> が汚染されるタイミング。Album はタブ切替で勝手に
///         <c>SetCurrentCast</c> が走り、BarScene.Start 前のロード直後も前回値のまま。</item>
///   <item>FittingRoom 動作中 — 試着プレビュー中の一時的な衣装切替を本履歴に残さない
///         （確定前のプレビューを「見た」扱いしない方針）。</item>
///   <item>現在接客中のキャラ (<c>GameData.GetCurrentCast()</c>) 以外 — Bar 等で横並びの
///         他キャラが Preload された際に勝手に履歴が埋まるのを防ぐ。</item>
/// </list>
/// </summary>
internal static class WardrobeHistoryGate
{
    /// <summary>history 記録対象かを判定する。対象外なら呼出し側は MarkViewed を呼ばない。</summary>
    public static bool ShouldRecord(CharID id)
    {
        if (id >= CharID.NUM) return false;
        var sys = GBSystem.Instance;
        if (sys == null) return false;
        if (!sys.IsIngame) return false;
        if (CostumeChangerPatch.IsFittingRoomActiveExternal()) return false;
        var gd = sys.RefGameData();
        if (gd == null) return false;
        return gd.GetCurrentCast() == id;
    }
}
