using System.Linq;
using BunnyGarden2FixMod.Utils;
using GB;
using GB.Scene;
using HarmonyLib;
using UnityEngine;
using VLB;
using static GB.Scene.CharacterHandle;

namespace BunnyGarden2FixMod.Patches;

[HarmonyPatch(typeof(CharacterHandle), nameof(CharacterHandle.updateTalkReactionMotion))]
public static class TalkReactionPatch
{
    static bool Prepare()
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

        private MOTION _lastMotion = MOTION._DUMMY;

        public MOTION GetNextMotion()
        {
            _lastMotion = _lastMotion switch
            {
                MOTION.KNEEL_DOWN_START => MOTION.KNEEL_DOWN_END,
                _ => TalkReactionMotions[Random.RandomRangeInt(0, TalkReactionMotions.Length)]
            };
            return _lastMotion;
        }
    }

    private static bool Prefix(CharacterHandle __instance)
    {
        if (!Plugin.ConfigMoreTalkReactions.Value)
            return true;

        // まずは本来のメソッドの条件にマッチさせる
        if (!__instance.m_chara.activeSelf
            || GBSystem.Instance.IsConversateChar(__instance.m_id)
            || new[] { MOTION.WALK, MOTION.SERVING_FOOD, MOTION.MOPPING_FLOOR, MOTION.CHECK_SHELVES }
                .Any(motion => __instance.m_animator.GetCurrentAnimatorStateInfo(2).IsName(MOTION_NAME[(int)motion]))
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
