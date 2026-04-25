using BunnyGarden2FixMod.Utils;
using GB;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using TMPro;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// 会話選択肢の正解（好感度上昇選択肢）を視覚的に示すパッチ。
///
/// ConversationChoice.Enter() および Enter2() の Postfix で、
/// m_currentUI.GetChoiceItems() でアイテムを順番通り取得し、
/// m_choiceIndex を一時的に差し替えてゲーム本来の
/// IsLikabilityUpChoice() / IsLikabilityDownChoice() で正誤を判定する。
///
/// ★ : IsLikabilityUpChoice() == true
/// ▼ : IsLikabilityDownChoice() == true（酔い選択肢が現状 DOWN になる場合）
///
/// ★ テキスト書き換えを 1 フレーム遅延させる理由 ★
/// ConversationChoiceItem が初めてアクティブになるフレームでは、
/// TextLabel.Start() が翌フレームに実行され updateText() でテキストを上書きする。
/// この上書きにより同フレームで付けた記号が消えてしまう（初回のみ発生）。
/// 判定だけ同フレームで行い、記号の付加を StartCoroutine で 1 フレーム後に
/// 遅らせることで Start() の上書きを回避する。
/// </summary>
[HarmonyPatch]
public static class ConversationChoiceCheatPatch
{
    private static readonly FieldInfo s_shuffleTableField =
        AccessTools.Field(typeof(ConversationChoice), "m_shuffleTable");

    private static readonly FieldInfo s_currentUIField =
        AccessTools.Field(typeof(ConversationChoice), "m_currentUI");

    private static readonly FieldInfo s_choiceIndexField =
        AccessTools.Field(typeof(ConversationChoice), "m_choiceIndex");

    private static readonly MethodInfo s_isLikUpMethod =
        AccessTools.Method(typeof(ConversationChoice), "IsLikabilityUpChoice");

    private static readonly MethodInfo s_isLikDownMethod =
        AccessTools.Method(typeof(ConversationChoice), "IsLikabilityDownChoice");

    private static IEnumerable<MethodBase> TargetMethods()
    {
        // Enter と Enter2 の両方をパッチ。
        // Enter2 は内部で Enter を呼んだあと各アイテムの Setup を再実行して
        // テキストを上書きするため、Enter2 の Postfix も必要。
        yield return AccessTools.Method(typeof(ConversationChoice), "Enter",
            new[] { typeof(MSGID), typeof(MSGID) });
        yield return AccessTools.Method(typeof(ConversationChoice), "Enter2",
            new[] { typeof(MSGID), typeof(MSGID), typeof(List<MSGID>), typeof(List<long>) });
        PatchLogger.LogInfo("[ConversationChoiceCheatPatch] ConversationChoice.Enter / Enter2 をパッチしました（正解表示チート）");
    }

    private static void Postfix(ConversationChoice __instance)
    {
        if (!Plugin.ConfigCheatLikability.Value) return;
        try
        {
            var shuffleTable = s_shuffleTableField.GetValue(__instance) as List<int>;
            var currentUI = s_currentUIField.GetValue(__instance);
            if (shuffleTable == null || currentUI == null) return;

            var getChoiceItemsMethod = currentUI.GetType().GetMethod(
                "GetChoiceItems", BindingFlags.Public | BindingFlags.Instance);
            if (getChoiceItemsMethod == null) return;
            var choiceItems = getChoiceItemsMethod.Invoke(currentUI, null) as List<ConversationChoiceItem>;
            if (choiceItems == null) return;

            // 判定を同フレームで確定させる（m_choiceIndex を一時差し替え）
            int count = Math.Min(choiceItems.Count, shuffleTable.Count);
            var marks = new string[count]; // null = 記号なし
            int savedIdx = (int)s_choiceIndexField.GetValue(__instance);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    s_choiceIndexField.SetValue(__instance, shuffleTable[i]);
                    bool isCorrect = (bool)s_isLikUpMethod.Invoke(__instance, null);
                    bool isDown = (bool)s_isLikDownMethod.Invoke(__instance, null);
                    marks[i] = isCorrect ? "★" : (isDown ? "▼" : null);
                }
            }
            finally
            {
                s_choiceIndexField.SetValue(__instance, savedIdx);
            }

            // テキスト書き換えを 1 フレーム遅延させる。
            // TextLabel.Start() は初回アクティブ化の翌フレームに実行されるため、
            // 同フレームで記号を付けても Start() → updateText() で上書きされてしまう。
            // yield return null で 1 フレーム待つことで Start() 完了後に書き換えられる。
            __instance.StartCoroutine(ApplyMarksNextFrame(choiceItems, marks));
        }
        catch (Exception e)
        {
            PatchLogger.LogError($"[ConversationChoiceCheatPatch] エラー: {e.Message}");
        }
    }

    private static IEnumerator ApplyMarksNextFrame(List<ConversationChoiceItem> items, string[] marks)
    {
        yield return null; // TextLabel.Start() の完了を待つ

        for (int i = 0; i < marks.Length && i < items.Count; i++)
        {
            if (marks[i] == null) continue;
            foreach (var tmp in items[i].GetComponentsInChildren<TextMeshProUGUI>())
            {
                // 二重追記防止（Enter → Enter2 の両 Postfix からコルーチンが走る場合）
                if (!tmp.text.StartsWith("★") && !tmp.text.StartsWith("▼"))
                {
                    PatchLogger.LogInfo($"[ConversationChoiceCheatPatch] [{marks[i]}] UI[{i}]: \"{tmp.text}\"");
                    tmp.text = $"{marks[i]}{tmp.text}";
                }
            }
        }
    }
}
