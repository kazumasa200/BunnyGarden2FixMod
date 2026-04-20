using System;
using BunnyGarden2FixMod.ExSave;
using BunnyGarden2FixMod.Utils;
using GB;
using GB.Extra;
using GB.Game;
using GB.Save;
using HarmonyLib;

namespace BunnyGarden2FixMod.Patches;

// =====================================================
// アルバム閲覧の一方向 CopyFrom を識別するフラグ
// =====================================================
/// <summary>
/// <see cref="ExtraChekiSelector.updateConfirm"/> の Prefix で true にセットされ、
/// 直後の <see cref="GameData.CopyFrom"/> Postfix で消費して false に戻す極短命フラグ。
/// Unity メインスレッド専用（競合リスクなし）。
/// </summary>
internal static class AlbumViewGate
{
    public static bool InProgress;
}

// =====================================================
// ExtraChekiSelector.updateConfirm Prefix / Finalizer
// =====================================================
/// <summary>
/// <c>ExtraChekiSelector.updateConfirm</c> の Prefix で <see cref="AlbumViewGate.InProgress"/> を
/// true にセットする。このフラグにより直後の <see cref="GameData.CopyFrom"/> がアルバム閲覧起点か
/// 通常ロード起点かを区別できる。
///
/// <para>
/// Finalizer は <c>updateConfirm</c> が例外や途中 return で終了した場合でも
/// <see cref="AlbumViewGate.InProgress"/> を強制クリアする二重防御。
/// Finalizer は例外時も必ず実行されることが Harmony で保証されている。
/// </para>
/// </summary>
[HarmonyPatch(typeof(ExtraChekiSelector), "updateConfirm")]
public static class ExtraChekiSelectorViewPatch
{
    static bool Prepare()
    {
        PatchLogger.LogInfo("[ExtraChekiSelectorViewPatch] 適用");
        return true;
    }

    static void Prefix() => AlbumViewGate.InProgress = true;

    // updateConfirm の実行が例外や途中 return で終わっても、AlbumViewGate が
    // 残留すると次回の通常 CopyFrom が誤って閲覧モード扱いになる。
    // Finalizer は例外時も必ず実行されるため、二重防御として強制クリアする。
    static void Finalizer()
    {
        if (AlbumViewGate.InProgress)
        {
            PatchLogger.LogWarning("[ExtraChekiSelectorViewPatch] updateConfirm 終了時に AlbumViewGate 残留、強制クリア");
            AlbumViewGate.InProgress = false;
        }
    }
}

// =====================================================
// GameData.CopyFrom(GameData) Postfix
// =====================================================
/// <summary>
/// <see cref="GameData.CopyFrom"/> の Postfix でセーブスロット追跡を行う。
///
/// <list type="bullet">
///   <item>
///     <term>セーブ方向スキップ</term>
///     <description><c>__instance is SavedGameData</c> の場合はスキップ（UpdateSaveSlot 経由の逆方向）</description>
///   </item>
///   <item>
///     <term>アルバム閲覧</term>
///     <description>
///     <see cref="AlbumViewGate.InProgress"/> が true の場合は閲覧モード。
///     <see cref="ExSaveStore.LoadSession"/> で M のデータを CurrentSession にコピーし、
///     <see cref="ExSaveStore.CurrentSaveSlot"/> = -1 をセットしてセーブ汚染を防ぐ。
///     </description>
///   </item>
///   <item>
///     <term>通常ロード</term>
///     <description>
///     <see cref="ExSaveStore.LoadSession"/> を呼び出して CurrentSession を更新し、
///     CurrentSaveSlot = N をセットする。
///     </description>
///   </item>
/// </list>
/// </summary>
[HarmonyPatch(typeof(GameData), nameof(GameData.CopyFrom), new[] { typeof(GameData) })]
public static class GameDataCopyFromPatch
{
    private static AccessTools.FieldRef<SaveData, SavedGameData[]> s_savedSlotsRef;

    static bool Prepare()
    {
        try
        {
            s_savedSlotsRef = AccessTools.FieldRefAccess<SaveData, SavedGameData[]>("m_savedGameData");
            PatchLogger.LogInfo("[GameDataCopyFromPatch] 適用");
            return true;
        }
        catch (Exception ex)
        {
            PatchLogger.LogError($"[GameDataCopyFromPatch] FieldRef 初期化失敗、パッチ無効化: {ex.Message}");
            return false;
        }
    }

    static void Postfix(GameData __instance, GameData __0)
    {
        // フラグは必ず消費する（Prefix が走ったが Postfix が途中で例外になっても残留しないよう）
        bool albumView = AlbumViewGate.InProgress;
        AlbumViewGate.InProgress = false;

        // セーブ方向（SavedGameData.UpdateData → CopyFrom）はスキップ
        if (__instance is SavedGameData) return;

        // rhs が SavedGameData でない場合はゲーム内の別 CopyFrom なのでスキップ
        if (!(__0 is SavedGameData target))
        {
            // アルバム閲覧フラグが立っていたが rhs が SavedGameData でない場合のフォールバック
            if (albumView)
            {
                PatchLogger.LogInfo("[GameDataCopyFromPatch] アルバム閲覧フラグあり、rhs が SavedGameData でないためスキップ");
            }
            return;
        }

        // m_savedGameData[] から target の index を逆引きする
        int slot = FindIndex(target);
        if (slot < 0)
        {
            PatchLogger.LogWarning("[GameDataCopyFromPatch] SavedGameData[] 逆引き失敗、セッション不変");
            return;
        }

        if (albumView)
        {
            // アルバム閲覧モード: CurrentSession は M の内容を持つが、保存先スロットは不明マーカー
            ExSaveStore.LoadSession(slot);
            ExSaveStore.CurrentSaveSlot = -1;
            PatchLogger.LogInfo($"[GameDataCopyFromPatch] アルバム閲覧 slot={slot}（CurrentSaveSlot=-1）");
        }
        else
        {
            // 通常ロード
            ExSaveStore.LoadSession(slot);
            PatchLogger.LogInfo($"[GameDataCopyFromPatch] 通常ロード slot={slot}");
        }
    }

    private static int FindIndex(SavedGameData target)
    {
        var sd = GBSystem.Instance?.RefSaveData();
        if (sd == null) return -1;
        var slots = s_savedSlotsRef(sd);
        if (slots == null) return -1;
        for (int i = 0; i < slots.Length; i++)
        {
            if (ReferenceEquals(slots[i], target)) return i;
        }
        return -1;
    }
}

// =====================================================
// SaveData.UpdateSaveSlot(int slot) Postfix
// =====================================================
/// <summary>
/// <c>SaveData.UpdateSaveSlot(int slot)</c> の Postfix で <see cref="ExSaveStore.CurrentSaveSlot"/> を更新する。
/// <c>CommitSession</c> 自体は <c>Saves.Save</c> Postfix が担当する（全経路共通）。
/// Postfix 採用により、<c>UpdateSaveSlot</c> が成功した後にのみ CurrentSaveSlot を更新する意味論となる。
/// </summary>
[HarmonyPatch(typeof(SaveData), nameof(SaveData.UpdateSaveSlot))]
public static class SaveDataUpdateSlotPatch
{
    static bool Prepare()
    {
        PatchLogger.LogInfo("[SaveDataUpdateSlotPatch] 適用");
        return true;
    }

    static void Postfix(int slot)
    {
        ExSaveStore.CurrentSaveSlot = slot;
        PatchLogger.LogInfo($"[SaveDataUpdateSlotPatch] CurrentSaveSlot={slot}");
    }
}
