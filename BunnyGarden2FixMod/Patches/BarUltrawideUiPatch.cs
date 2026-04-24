using GB;
using GB.Bar;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection.Emit;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// Fixes bar UI elements whose original 16:9 staging becomes visible after the gameplay canvas is widened.
/// </summary>
internal static class BarUltrawideUiPatch
{
    private const float VanillaChargeNoticeHiddenX = 450f;

    private static readonly Dictionary<Transform, Vector3> OriginalScales = new();

    private static float GetChargeNoticeHiddenX()
    {
        if (!GameplayFullscreenUltrawideSupport.ShouldUseNativeFullscreen())
        {
            return VanillaChargeNoticeHiddenX;
        }

        return VanillaChargeNoticeHiddenX + GameplayFullscreenUltrawideSupport.GetUltrawideExtraHalfWidth();
    }

    private static void MoveChargeNoticeToHiddenPosition(GameObject notice)
    {
        if (notice == null)
        {
            return;
        }

        Vector3 position = notice.transform.localPosition;
        notice.transform.localPosition = new Vector3(GetChargeNoticeHiddenX(), position.y, position.z);
    }

    [HarmonyPatch(typeof(ConversationWindowPanel), nameof(ConversationWindowPanel.Set))]
    private static class ConversationWindowPanelSetPatch
    {
        private static readonly AccessTools.FieldRef<ConversationWindowPanel, GameObject> WindowInBarRef =
            AccessTools.FieldRefAccess<ConversationWindowPanel, GameObject>("m_windowInBar");

        private static void Postfix(ConversationWindowPanel __instance)
        {
            GameObject windowInBar = WindowInBarRef(__instance);
            if (windowInBar == null)
            {
                return;
            }

            Transform transform = windowInBar.transform;
            if (!OriginalScales.TryGetValue(transform, out Vector3 originalScale))
            {
                originalScale = transform.localScale;
                OriginalScales[transform] = originalScale;
            }

            if (!GameplayFullscreenUltrawideSupport.ShouldUseNativeFullscreen() || !windowInBar.activeSelf)
            {
                transform.localScale = originalScale;
                return;
            }

            float multiplier = GameplayFullscreenUltrawideSupport.GetAspectMultiplier();
            transform.localScale = new Vector3(originalScale.x * multiplier, originalScale.y, originalScale.z);
        }
    }

    [HarmonyPatch(typeof(ChargeUI), nameof(ChargeUI.Start))]
    private static class ChargeUIStartPatch
    {
        private static readonly AccessTools.FieldRef<ChargeUI, GameObject> NoticeRef =
            AccessTools.FieldRefAccess<ChargeUI, GameObject>("m_notice");

        private static void Postfix(ChargeUI __instance)
        {
            MoveChargeNoticeToHiddenPosition(NoticeRef(__instance));
        }
    }

    [HarmonyPatch(typeof(ChargeUI), "slideNotice")]
    private static class ChargeUISlideNoticePatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getHiddenXMethod = AccessTools.Method(typeof(ChargeUISlideNoticePatch), nameof(GetHiddenX));

            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldc_R4
                    && instruction.operand is float value
                    && value == VanillaChargeNoticeHiddenX)
                {
                    yield return new CodeInstruction(OpCodes.Call, getHiddenXMethod);
                    continue;
                }

                yield return instruction;
            }
        }

        private static float GetHiddenX()
        {
            return GetChargeNoticeHiddenX();
        }
    }

    [HarmonyPatch(typeof(DrunkEffect), nameof(DrunkEffect.Show))]
    private static class DrunkEffectShowPatch
    {
        private static readonly AccessTools.FieldRef<DrunkEffect, CanvasGroup> DrunkEffectGroupRef =
            AccessTools.FieldRefAccess<DrunkEffect, CanvasGroup>("m_drunkEffect");

        private static void Postfix(DrunkEffect __instance)
        {
            CanvasGroup group = DrunkEffectGroupRef(__instance);
            if (group == null)
            {
                return;
            }

            Transform transform = group.transform;
            if (!OriginalScales.TryGetValue(transform, out Vector3 originalScale))
            {
                originalScale = transform.localScale;
                OriginalScales[transform] = originalScale;
            }

            if (!GameplayFullscreenUltrawideSupport.ShouldUseNativeFullscreen())
            {
                transform.localScale = originalScale;
                return;
            }

            float multiplier = GameplayFullscreenUltrawideSupport.GetAspectMultiplier();
            transform.localScale = new Vector3(originalScale.x * multiplier, originalScale.y, originalScale.z);
        }
    }
}
