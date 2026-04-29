using BunnyGarden2FixMod.Utils;
using GB;
using GB.Scene;
using HarmonyLib;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// 会話送り（タップ／オート／スキップ）時に、現在再生中のボイスが途中で切れないようにする
/// パッチ群。
///
/// ConversationWindow は UpdateRoutine / ToNextText の中で GBSystem.StopVoice() と
/// EnvSceneBase.FinishLipSync() を呼び出しており、次の台詞に進むたびにボイスと口パクが
/// 即停止する。これらのメソッドの実行範囲だけ StopVoice / FinishLipSync を no-op にすることで、
///   - 次の台詞にボイスがある場合: PlayVoice / StartLipSync により自然に切り替わる
///   - 次の台詞にボイスがない場合: 現在のボイスが最後まで再生され、口パクも
///     LipSyncCalculator の自己終了判定で自動的に停止する
/// という挙動を得る。
///
/// ConversationWindow.ForceFinish（シーン遷移）や ASMR / HolidayAfterScene などの
/// 会話ウィンドウ外経路の StopVoice 呼び出しは抑制対象外。
/// ConversationWindow.Setup 経由の初回 textRoutine も抑制対象外だが、Setup 自体は
/// StopVoice を直接呼ばないため実害はない。
///
/// 設計上の前提:
/// - textRoutine は async void。await を跨いで後続処理が走るが、現行実装では await 後に
///   StopVoice を呼ぶのは ToNextText 経由のみのため、ToNextText をラップすれば十分。
///   将来 textRoutine の await 以降で直接 StopVoice を呼ぶ改変がゲーム本体に入った場合は
///   抑制が届かなくなる点に注意。
/// </summary>
/// <remarks>
/// スコープは ContinueVoiceOnTap_*Patch 専用の state ホルダ。
/// </remarks>
internal static class ContinueVoiceState
{
    // Unity メインスレッド専用。Harmony のパッチは呼び出し元と同じスレッドで走るため
    // 単純な static int で十分であり、[ThreadStatic] は不要。
    public static int SuppressDepth;
}

[HarmonyPatch(typeof(ConversationWindow), nameof(ConversationWindow.UpdateRoutine))]
public static class ContinueVoiceOnTap_UpdateRoutinePatch
{
    private static bool Prepare()
    {
        PatchLogger.LogInfo("[ContinueVoiceOnTapPatch] ConversationWindow.UpdateRoutine をラップ");
        return true;
    }

    private static void Prefix(out bool __state)
    {
        __state = false;
        if (!Configs.ContinueVoiceOnTap.Value) return;
        ContinueVoiceState.SuppressDepth++;
        __state = true;
    }

    private static void Postfix(bool __state)
    {
        if (!__state) return;
        if (ContinueVoiceState.SuppressDepth > 0)
            ContinueVoiceState.SuppressDepth--;
    }
}

[HarmonyPatch(typeof(ConversationWindow), nameof(ConversationWindow.ToNextText))]
public static class ContinueVoiceOnTap_ToNextTextPatch
{
    private static bool Prepare()
    {
        PatchLogger.LogInfo("[ContinueVoiceOnTapPatch] ConversationWindow.ToNextText をラップ");
        return true;
    }

    private static void Prefix(out bool __state)
    {
        __state = false;
        if (!Configs.ContinueVoiceOnTap.Value) return;
        ContinueVoiceState.SuppressDepth++;
        __state = true;
    }

    private static void Postfix(bool __state)
    {
        if (!__state) return;
        if (ContinueVoiceState.SuppressDepth > 0)
            ContinueVoiceState.SuppressDepth--;
    }
}

[HarmonyPatch(typeof(GBSystem), nameof(GBSystem.StopVoice))]
public static class ContinueVoiceOnTap_StopVoicePatch
{
    private static bool Prepare()
    {
        PatchLogger.LogInfo("[ContinueVoiceOnTapPatch] GBSystem.StopVoice の抑制ゲートを登録");
        return true;
    }

    private static bool Prefix()
    {
        if (!Configs.ContinueVoiceOnTap.Value) return true; // 設定オフ時は早期リターン
        return ContinueVoiceState.SuppressDepth <= 0; // 抑制中なら StopVoice を実行しない
    }
}

[HarmonyPatch(typeof(EnvSceneBase), nameof(EnvSceneBase.FinishLipSync))]
public static class ContinueVoiceOnTap_FinishLipSyncPatch
{
    private static bool Prepare()
    {
        PatchLogger.LogInfo("[ContinueVoiceOnTapPatch] EnvSceneBase.FinishLipSync の抑制ゲートを登録");
        return true;
    }

    private static bool Prefix()
    {
        if (!Configs.ContinueVoiceOnTap.Value) return true; // 設定オフ時は早期リターン
        return ContinueVoiceState.SuppressDepth <= 0; // 抑制中なら FinishLipSync を実行しない（LipSyncCalculator が音声終了時に自動停止）
    }
}
