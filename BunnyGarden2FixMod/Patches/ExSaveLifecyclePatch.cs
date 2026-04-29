using BunnyGarden2FixMod.ExSave;
using BunnyGarden2FixMod.Utils;
using Cysharp.Threading.Tasks;
using GB.Save;
using HarmonyLib;
using System;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// MOD 独自サイドカー (<c>.exmod</c>) のライフサイクルを <see cref="Saves"/> のロード／保存に同期させるパッチ群。
///
/// <list type="bullet">
///   <item>
///     <term>Load</term>
///     <description>
///     <c>Saves.Load()</c> は sync なので Postfix で主セーブパスを解決して
///     <see cref="ExSaveStore.LoadFromPath"/> を呼ぶ。その後 <see cref="ExSaveStore.ResetSession"/>
///     でセッション状態を初期化する（AllSlots は保持）。
///     </description>
///   </item>
///   <item>
///     <term>Save</term>
///     <description>
///     <c>Saves.Save()</c> は <c>async UniTask</c>。Harmony は async メソッドの外側 stub にのみ当てられ、
///     ステートマシン本体（MoveNext）は触れない。Postfix で <c>ref UniTask __result</c> を
///     wrapper task に差し替え、元 task を await した<b>後</b>に ExSave を保存する逐次化パターンを使う。
///     <para>
///     <see cref="ExSaveStore.CurrentSaveSlot"/> が 0 以上の場合、await 前（= 主セーブ書き出し前）に
///     同期で <see cref="ExSaveStore.CommitSession"/> を実行して CurrentSession の内容を AllSlots に反映させる
///     （await 中に CurrentSession が変更されてもコピー済みのため破損しない。
///     <c>UpdateSaveSlot</c> を経由しない <c>Save()</c> 直接呼び出し経路の救済も兼ねる）。
///     </para>
///     </description>
///   </item>
///   <item>
///     <term>CreateNewData</term>
///     <description>
///     <c>Saves.CreateNewData()</c> Postfix で全状態リセット（AllSlots・CurrentSession・CurrentSaveSlot）。
///     </description>
///   </item>
/// </list>
///
/// <para>
/// 本パッチは <see cref="Configs.ChekiHighResEnabled"/> の値に関係なく常に適用される
/// （ExSave 基盤は汎用であり、将来の機能追加でも使える共通配線）。
/// </para>
/// </summary>
internal static class ExSaveLifecyclePatch
{
}

/// <summary>
/// <c>Saves.Load()</c> Postfix: 主セーブ読み込み完了後にサイドカーを読み込む。
/// セッション状態は <see cref="ExSaveStore.ResetSession"/> でリセットする（AllSlots は保持）。
/// </summary>
[HarmonyPatch(typeof(Saves), nameof(Saves.Load))]
public static class ExSaveOnLoadPatch
{
    private static bool Prepare()
    {
        PatchLogger.LogInfo("[ExSaveOnLoadPatch] Saves.Load をパッチしました（サイドカー読込）");
        return true;
    }

    private static void Postfix(Saves __instance)
    {
        string path = ExSaveStore.TryResolveMainPath(__instance);
        if (string.IsNullOrEmpty(path))
        {
            // Paths 未解決（新規ゲームなど）。empty state で続行。
            ExSaveStore.Reset();
            return;
        }
        // AllSlots をファイルから読み込み、セッション状態は初期化する
        ExSaveStore.LoadFromPath(path);
        ExSaveStore.ResetSession();
    }
}

/// <summary>
/// <c>Saves.Save()</c> Postfix: 本体保存 UniTask を wrap して完了後にサイドカーを保存する。
/// <see cref="ExSaveStore.CurrentSaveSlot"/> が 0 以上の場合は、await 前（同期フェーズ）に
/// <see cref="ExSaveStore.CommitSession"/> を実行してから wrap に入る
/// （await 中の CurrentSession 変更による破損防止。<c>UpdateSaveSlot</c> 非経由パスの救済も兼ねる）。
/// </summary>
[HarmonyPatch(typeof(Saves), nameof(Saves.Save))]
public static class ExSaveOnSavePatch
{
    private static bool Prepare()
    {
        PatchLogger.LogInfo("[ExSaveOnSavePatch] Saves.Save をパッチしました（サイドカー保存 wrap）");
        return true;
    }

    private static void Postfix(Saves __instance, ref UniTask __result)
    {
        // await 前（= 主セーブ書き出し開始前）に同期でコミットしておく。
        // await 中に他スレッド / 別 UI 操作で CurrentSession が更新されても
        // AllSlots へのコピーは既に済んでいるため破損しない。
        if (ExSaveStore.CurrentSaveSlot >= 0)
        {
            ExSaveStore.CommitSession(ExSaveStore.CurrentSaveSlot);
            PatchLogger.LogInfo($"[ExSave] CommitSession(slot={ExSaveStore.CurrentSaveSlot}) 実行（await 前に同期実行）");
        }
        else
        {
            PatchLogger.LogInfo("[ExSave] CurrentSaveSlot=-1 のため CommitSession スキップ（既存 AllSlots をそのまま書き出し）");
        }

        // async メソッドの Postfix で __result を差し替えるケースは、
        // 元タスク(orig)を WrapAndPersist の中で 1 度だけ await する逐次パターンに限定する。
        // orig を外部で別に await すると二重消費になるため、この Postfix の外では orig を参照しないこと。
        // Preserve() は UniTask 内部を AsyncLazy 的に保持して完了状態の再参照を許容する防御措置。
        var orig = __result.Preserve();
        __result = WrapAndPersist(orig, __instance);
    }

    private static async UniTask WrapAndPersist(UniTask originalSave, Saves saves)
    {
        // 本体保存を先に完遂させる。本体側で例外が出た場合はそのまま伝播させる（本体の挙動を変えない）。
        await originalSave;

        // 本体保存に成功した後だけサイドカーを書き出す。
        // ここでの例外は本体保存結果に影響させず、ログにとどめる。
        try
        {
            string path = ExSaveStore.CurrentMainPath ?? ExSaveStore.TryResolveMainPath(saves);
            if (string.IsNullOrEmpty(path))
            {
                PatchLogger.LogWarning("[ExSave] 主セーブパス未解決のためサイドカー保存をスキップ");
                return;
            }

            await ExSaveStore.SaveToPathAsync(path);
        }
        catch (Exception ex)
        {
            PatchLogger.LogError($"[ExSave] サイドカー保存中に例外: {ex.Message}");
        }
    }
}

/// <summary>
/// <c>Saves.CreateNewData()</c> Postfix: 新規ゲーム作成時に全状態をリセットする。
/// </summary>
[HarmonyPatch(typeof(Saves), nameof(Saves.CreateNewData))]
public static class ExSaveOnCreateNewDataPatch
{
    private static bool Prepare()
    {
        PatchLogger.LogInfo("[ExSaveOnCreateNewDataPatch] Saves.CreateNewData をパッチしました（全状態リセット）");
        return true;
    }

    private static void Postfix()
    {
        ExSaveStore.Reset();
        PatchLogger.LogInfo("[ExSaveOnCreateNewDataPatch] 新規ゲーム: ExSave 全状態をリセット");
    }
}
