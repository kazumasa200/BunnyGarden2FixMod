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
            MOTION.KNEEL_DOWN_START,
            MOTION.BOW,
            MOTION.TAKE_CHEAP_BOTTLE,
            MOTION.TAKE_EXPENSIVE_BOTTLE,
            MOTION.TAKE_VERY_HIGH_BOTTLE,
            MOTION.SHAKER,
            MOTION.SHAKER_HARD,
        ];

        private MOTION lastMotion = MOTION._DUMMY;

        public MOTION GetNextMotion()
        {
            lastMotion = lastMotion switch
            {
                MOTION.KNEEL_DOWN_START => MOTION.KNEEL_DOWN_END,
                MOTION.TAKE_CHEAP_BOTTLE => MOTION.DRINK,
                MOTION.TAKE_EXPENSIVE_BOTTLE => MOTION.DRINK,
                MOTION.TAKE_VERY_HIGH_BOTTLE => MOTION.DRINK,
                MOTION.DRINK => MOTION.DRINK_END,
                MOTION.SHAKER => MOTION.DRINK_COCKTAIL,
                MOTION.SHAKER_HARD => MOTION.DRINK_COCKTAIL,
                MOTION.IDLE => MOTION.TALK_REACTION,
                MOTION.TALK_REACTION => TalkReactionMotions[Random.RandomRangeInt(0, TalkReactionMotions.Length)],
                _ => MOTION.IDLE,
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
            MOTION nextMotion = data.GetNextMotion();
            __instance.PlayFacial(FACIAL.SMILE, 0f); // 表情は常に笑顔にする
            __instance.PlayMotion(nextMotion, 0.8f);
            __instance.m_talkReactionMotionTimer = 0f;
            __instance.m_talkReactionMotionResetTime = nextMotion switch
            {
                MOTION.DRINK => 5.5f,
                MOTION.TAKE_CHEAP_BOTTLE => 6.5f,
                MOTION.TAKE_EXPENSIVE_BOTTLE => 6.5f,
                MOTION.TAKE_VERY_HIGH_BOTTLE => 6.5f,
                MOTION.SHAKER => 9f,
                MOTION.SHAKER_HARD => 9f,
                MOTION.DRINK_COCKTAIL => 9f,
                MOTION.DRINK_END => 5f,
                MOTION.IDLE => Random.Range(1f, 3f),
                _ => Random.Range(8f,15f)
            };
        }

        return false;
    }
}
