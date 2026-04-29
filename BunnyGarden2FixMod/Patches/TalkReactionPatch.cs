using BunnyGarden2FixMod.Utils;
using GB;
using GB.Scene;
using HarmonyLib;
using System.Linq;
using UnityEngine;
using VLB;
using static GB.Scene.CharacterHandle;

namespace BunnyGarden2FixMod.Patches;

[HarmonyPatch(typeof(CharacterHandle), nameof(CharacterHandle.updateTalkReactionMotion))]
public static class TalkReactionPatch
{
    private static bool Prepare()
    {
        PatchLogger.LogInfo(
            $"[{nameof(TalkReactionPatch)}] " +
            $"{nameof(CharacterHandle)}.{nameof(CharacterHandle.updateTalkReactionMotion)} " +
            $"をパッチしました。");
        return true;
    }

    private class Data : MonoBehaviour
    {
        private static readonly MOTION[] TalkReactionMotions =
        [
            MOTION.TALK_REACTION,
            MOTION.KNEEL_DOWN_START,
            MOTION.BOW,
        ];

        private MOTION lastMotion = MOTION._DUMMY;

        public MOTION GetNextMotion()
        {
            lastMotion = lastMotion switch
            {
                MOTION.KNEEL_DOWN_START => MOTION.KNEEL_DOWN_END,
                _ => TalkReactionMotions[Random.RandomRangeInt(0, TalkReactionMotions.Length)]
            };
            return lastMotion;
        }
    }

    private static bool Prefix(CharacterHandle __instance)
    {
        if (!Configs.MoreTalkReactions.Value)
            return true;

        // まずは本来のメソッドの条件にマッチさせる
        if (!__instance.m_chara.activeSelf
            || GBSystem.Instance.IsConversateChar(__instance.m_id)
            || new[] { MOTION.WALK, MOTION.SERVING_FOOD, MOTION.MOPPING_FLOOR, MOTION.CHECK_SHELVES }
                .Any(motion => __instance.isSameAnimation(Layer.LAYER_BASE, MOTION_NAME[(int)motion]))
            || !__instance.m_enableTalkReactionMotion)
        {
            return false;
        }

        __instance.m_talkReactionMotionTimer += Time.deltaTime;
        if (__instance.m_talkReactionMotionTimer >= __instance.m_talkReactionMotionResetTime)
        {
            var data = __instance.m_chara.GetOrAddComponent<Data>();
            __instance.PlayMotion(data.GetNextMotion(), 0.4f);
            __instance.m_talkReactionMotionTimer = 0f;
            __instance.m_talkReactionMotionResetTime = Random.Range(7f, 15f);
        }

        return false;
    }
}
