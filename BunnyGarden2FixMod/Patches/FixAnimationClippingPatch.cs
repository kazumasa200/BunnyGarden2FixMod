using System.Collections.Generic;
using BunnyGarden2FixMod.Utils;
using GB.Scene;
using HarmonyLib;
using UnityEngine;
using VLB;
using static GB.Scene.CharacterHandle;

namespace BunnyGarden2FixMod.Patches;

[HarmonyPatch(typeof(CharacterHandle), nameof(CharacterHandle.PlayMotion))]
public static class FixAnimationClippingPatch
{
    private static bool Prepare()
    {
        PatchLogger.LogInfo(
            $"[{nameof(FixAnimationClippingPatch)}] " +
            $"{nameof(CharacterHandle)}.{nameof(CharacterHandle.PlayMotion)} " +
            $"をパッチしました。");
        return true;
    }

    private class Fixer : MonoBehaviour
    {
        private static readonly Vector3 Offset = new(-0.01f, 0.02f, 0);

        public bool IsActive { get; set; }

        private Animator charAnimator;
        private Transform skirtFixBoneLeft;
        private Transform skirtFixBoneRight;

        private Vector3 offsetValue;

        public void SetUp(Animator animator)
        {
            charAnimator = animator;

            if (skirtFixBoneLeft == null)
                skirtFixBoneLeft = transform.Find("Root_skinJT/Pelvis_skinJT/Hip_skinJT/L_skirtD1_skinJT/L_skirtD2_skinJT");
            if (skirtFixBoneRight == null)
                skirtFixBoneRight = transform.Find("Root_skinJT/Pelvis_skinJT/Hip_skinJT/R_skirtD1_skinJT/R_skirtD2_skinJT");
        }

        private static Vector3 ExpDecay(Vector3 a, Vector3 b, float decay, float deltaTime)
            => b + (a - b) * Mathf.Exp(-decay * deltaTime);

        private void LateUpdate()
        {
            if (charAnimator == null || !charAnimator.isActiveAndEnabled)
                return;

            var goal = IsActive ? Offset : Vector3.zero;

            // アニメーションの開始と終了時にオフセットが滑らかに変化するように指数関数的減衰を使用しています。
            offsetValue = ExpDecay(offsetValue, goal, 3f, Time.deltaTime);

            // アニメーションの特定のフレームでスカートがクリッピングするのを防ぐために、スカートのボーンに小さなオフセットを適用します。
            // オフセットはLateUpdateで適用されるため、アニメーションが適用された後に位置を変更します。
            if (skirtFixBoneLeft != null)
                skirtFixBoneLeft.localPosition += offsetValue;
            if (skirtFixBoneRight != null)
                skirtFixBoneRight.localPosition += offsetValue;
        }
    }

    private static readonly HashSet<MOTION> AffectedMotions =
    [
        MOTION.KNEEL_DOWN_START,
        MOTION.KNEEL_DOWN_LP,
        MOTION.CHECK_SHELVES,
    ];

    private static void Postfix(CharacterHandle __instance, MOTION __0, float __1)
    {
        if (!Configs.FixAnimationClipping.Value || __instance.m_chara == null)
            return;

        var fixer = __instance.m_chara.GetOrAddComponent<Fixer>();
        fixer.SetUp(__instance.m_animator);
        fixer.IsActive = AffectedMotions.Contains(__0);
    }
}