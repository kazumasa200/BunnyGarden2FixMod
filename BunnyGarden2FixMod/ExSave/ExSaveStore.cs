using System;
using System.IO;
using System.Threading.Tasks;
using BepInEx;
using BunnyGarden2FixMod.Patches.CostumeChanger;
using BunnyGarden2FixMod.Utils;
using GB.Save;
using GB.Save.Pc;
using GB.Save.Steam;

namespace BunnyGarden2FixMod.ExSave;

/// <summary>
/// <see cref="ExSaveData"/> の静的アクセサ兼パスリゾルバ。セーブスロット追跡もここで管理する。
/// ゲーム側の <c>Saves.Load</c> / <c>Saves.Save</c> ライフサイクルに同期して読み書きされる。
///
/// <para>
/// ゲーム本体の「GameData (現在) / SavedGameData[9] (スロット別スナップショット)」の
/// 関係を MOD 側でも模倣する:
/// <list type="bullet">
///   <item><term><see cref="AllSlots"/></term><description>永続化全体（ファイル内容）</description></item>
///   <item><term><see cref="CurrentSession"/></term><description>現在プレイ中の作業セット（GameData 対応）</description></item>
///   <item><term><see cref="CurrentSaveSlot"/></term><description>ロード/セーブ先スロット。-1 = セーブ不可（新規ゲーム直後・アルバム閲覧中等）</description></item>
/// </list>
/// </para>
///
/// <para>
/// サイドカーファイルは Steam Cloud Save の対象外にするため、ゲーム本体のセーブとは
/// 別の <c>BepInEx/data/{PLUGIN_GUID}/</c> フォルダに置く。
/// ファイル名は主セーブファイル名（パスなし）に <c>.exmod</c> を付与したもの。
/// たとえば主セーブが <c>save_00.dat</c> なら
/// <c>BepInEx/data/net.noeleve.BunnyGarden2FixMod/save_00.dat.exmod</c>。
/// </para>
/// </summary>
public static class ExSaveStore
{
    private const string SidecarExtension = ".exmod";

    /// <summary>サイドカーを格納する BepInEx データフォルダ。</summary>
    private static string DataDirectory
        => Path.Combine(Paths.BepInExRootPath, "data", MyPluginInfo.PLUGIN_GUID);

    /// <summary>永続化全体のデータ（全スロット分）。ファイルに読み書きされる。</summary>
    public static ExSaveData AllSlots { get; private set; } = new ExSaveData();

    /// <summary>
    /// 現在プレイ中のセッション（GameData に対応するスロット相当）。
    /// チェキ撮影や閲覧時はここに読み書きする。
    /// </summary>
    public static ExSaveSlotData CurrentSession { get; private set; } = new ExSaveSlotData();

    /// <summary>
    /// スロットに紐づかない共通データ（衣装/パンツ/ストッキング閲覧履歴等）。
    /// <see cref="AllSlots"/>.Common への直接参照なので、ここへの書き込みはそのまま次回
    /// <c>Saves.Save</c> フックで永続化される。LoadFromPath / Reset で AllSlots が再代入されても
    /// プロパティとして毎回解決するため追従する。
    /// </summary>
    public static ExSaveSlotData CommonData => AllSlots.Common;

    /// <summary>
    /// 現在のロード/セーブ先スロット番号。
    /// -1 の場合は「セーブ不可」マーカー（新規ゲーム直後・アルバム閲覧中等）。
    /// <c>Saves.Save</c> Postfix wrap ではこの値が 0 以上のときのみ <see cref="CommitSession"/> を実行する。
    /// </summary>
    public static int CurrentSaveSlot { get; set; } = -1;

    /// <summary>現在のセーブに対応する主セーブのパス（Load/Save 後に確定）。null の間は I/O スキップ。</summary>
    public static string CurrentMainPath { get; private set; }

    /// <summary>
    /// 主セーブパスからサイドカーパスを返す。
    /// ファイルは <c>BepInEx/data/{PLUGIN_GUID}/</c> に置き、Steam Cloud Save の対象外にする。
    /// </summary>
    public static string GetSidecarPath(string mainSavePath)
    {
        if (string.IsNullOrEmpty(mainSavePath)) return null;
        string fileName = Path.GetFileName(mainSavePath) + SidecarExtension;
        return Path.Combine(DataDirectory, fileName);
    }

    /// <summary>
    /// 旧形式（主セーブと同じフォルダの <c>.exmod</c>）が残っていれば新しい場所へ移動する。
    /// 移動成功後は旧ファイルを削除する。
    /// </summary>
    private static void MigrateIfNeeded(string mainSavePath, string newPath)
    {
        string oldPath = mainSavePath + SidecarExtension;
        if (!File.Exists(oldPath) || File.Exists(newPath)) return;
        try
        {
            string dir = Path.GetDirectoryName(newPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.Move(oldPath, newPath);
            PatchLogger.LogInfo($"[ExSave] 旧サイドカーを移行しました: {oldPath} → {newPath}");
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[ExSave] 旧サイドカーの移行失敗（手動でコピーしてください）: {ex.Message}");
        }
    }

    /// <summary>
    /// <see cref="Saves"/> の具象インスタンスから主セーブファイルパスを取り出す。
    /// <c>SteamSave</c> / <c>PcSave</c> は <c>Paths</c> public プロパティを持つため
    /// リフレクションなしで取得可能。未知の具象は null を返す。
    /// </summary>
    public static string TryResolveMainPath(Saves saves)
    {
        if (saves == null) return null;
        try
        {
            if (saves is SteamSave steam && steam.Paths != null && steam.Paths.Count > 0)
                return steam.Paths[0];
            if (saves is PcSave pc && pc.Paths != null && pc.Paths.Count > 0)
                return pc.Paths[0];
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[ExSave] 主セーブパス解決失敗: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// 全状態をリセットする（新規ゲーム作成時に使用）。
    /// AllSlots / CurrentSession / CurrentSaveSlot / CurrentMainPath を全て初期化する。
    /// </summary>
    public static void Reset()
    {
        AllSlots = new ExSaveData();
        CurrentSession = new ExSaveSlotData();
        CurrentSaveSlot = -1;
        CurrentMainPath = null;
        // 底データを入替えたので ViewHistory 側の dedup キャッシュも無効化する
        // （次回 MarkViewed で新データに対して再登録が走るように）
        InvalidateCommonCaches();
    }

    /// <summary>
    /// セッション状態のみリセットする（ロード完了後に使用）。
    /// AllSlots / CurrentMainPath は変更しない。
    /// CurrentSession を空にし、CurrentSaveSlot = -1 にする。
    /// </summary>
    public static void ResetSession()
    {
        CurrentSession = new ExSaveSlotData();
        CurrentSaveSlot = -1;
    }

    /// <summary>
    /// <see cref="AllSlots"/> の saveSlot 番目の内容を <see cref="CurrentSession"/> に
    /// ディープコピーし、<see cref="CurrentSaveSlot"/> = saveSlot をセットする。
    /// 該当スロットが存在しない場合は <see cref="CurrentSession"/> が空になる。
    /// </summary>
    public static void LoadSession(int saveSlot)
    {
        if (AllSlots.TryGetSlot(saveSlot, out var slotData))
        {
            CurrentSession = slotData.CloneDeep();
        }
        else
        {
            CurrentSession = new ExSaveSlotData();
        }
        CurrentSaveSlot = saveSlot;
    }

    /// <summary>
    /// <see cref="CurrentSession"/> を <see cref="AllSlots"/> の saveSlot 番目に
    /// ディープコピーで書き戻す。<see cref="CurrentSaveSlot"/> は変更しない。
    /// </summary>
    public static void CommitSession(int saveSlot)
    {
        var slot = AllSlots.GetOrCreateSlot(saveSlot);
        // 既存スロットを CurrentSession の内容で上書きする
        slot.Clear();
        foreach (var kv in CurrentSession.Entries)
        {
            // Set / Deserialize で正規化されるため到達想定なし。防御のみ。
            byte[] src = kv.Value ?? Array.Empty<byte>();
            byte[] copy = new byte[src.Length];
            Buffer.BlockCopy(src, 0, copy, 0, src.Length);
            slot.Set(kv.Key, copy);
        }
    }

    public static void LoadFromPath(string mainSavePath)
    {
        CurrentMainPath = mainSavePath;
        string path = GetSidecarPath(mainSavePath);
        // 旧形式（セーブと同じフォルダ）があれば新しい場所へ移行する
        MigrateIfNeeded(mainSavePath, path);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            AllSlots = new ExSaveData();
            PatchLogger.LogInfo($"[ExSave] サイドカー無し、空データで開始: {path ?? "(null)"}");
            InvalidateCommonCaches();
            return;
        }
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            AllSlots = ExSaveData.Deserialize(bytes);
            int totalEntries = 0;
            foreach (var kv in AllSlots.Slots)
                totalEntries += kv.Value?.Count ?? 0;
            PatchLogger.LogInfo($"[ExSave] ロード: {path} ({bytes.Length} bytes, {AllSlots.Slots.Count} slots, {totalEntries} entries)");
        }
        catch (Exception ex)
        {
            AllSlots = new ExSaveData();
            PatchLogger.LogWarning($"[ExSave] ロード失敗、空データで続行: {path} ({ex})");
        }
        // ロード（成功/失敗問わず）で AllSlots が差替わったので dedup キャッシュ無効化。
        InvalidateCommonCaches();
    }

    /// <summary>
    /// Common バケットに書き込む <see cref="ExSaveSlotData"/> 派生データの dedup キャッシュを
    /// 無効化する。<see cref="AllSlots"/> 入替タイミング（Reset / LoadFromPath）で呼ぶ。
    /// </summary>
    private static void InvalidateCommonCaches()
    {
        CostumeViewHistory.ResetDedup();
        PantiesViewHistory.ResetDedup();
        StockingViewHistory.ResetDedup();
    }

    public static async Task SaveToPathAsync(string mainSavePath)
    {
        string path = GetSidecarPath(mainSavePath);
        if (string.IsNullOrEmpty(path))
        {
            PatchLogger.LogWarning("[ExSave] 主セーブパス未確定のため保存をスキップ");
            return;
        }
        try
        {
            // 先に同期でスナップショット化してから await に入る。
            // これにより WriteAllBytesAsync 中に AllSlots に書き込みが入っても既にコピー済みで列挙例外を避けられる。
            byte[] bytes = AllSlots.Serialize();
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
            int totalEntries = 0;
            foreach (var kv in AllSlots.Slots)
                totalEntries += kv.Value?.Count ?? 0;
            PatchLogger.LogInfo($"[ExSave] 保存: {path} ({bytes.Length} bytes, {AllSlots.Slots.Count} slots, {totalEntries} entries)");
        }
        catch (Exception ex)
        {
            PatchLogger.LogError($"[ExSave] 保存失敗: {path} ({ex})");
        }
    }
}
