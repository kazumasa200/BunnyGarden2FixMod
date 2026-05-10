using BunnyGarden2FixMod.Patches.CostumeChanger;
using BunnyGarden2FixMod.Utils;
using GB.Scene;
using HarmonyLib;
using UnityEngine;
using static GB.Scene.CharacterHandle;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// ミニゲームのときにそよかぜを吹かせるパッチ
///
/// <para>
/// <b>使い方</b><br/>
/// ミニゲーム中にHotKeyを押すとそよかぜが吹く
/// </para>
/// </summary>

[HarmonyPatch(typeof(CharacterHandle), nameof(CharacterHandle.setup))]
public static class SwaySkirtPatch
{
    private static bool Prepare()
    {
        PatchLogger.LogInfo(
            $"[{nameof(SwaySkirtPatch)}] " +
            $"{nameof(CharacterHandle)}.{nameof(CharacterHandle.setup)} " +
            $"をパッチしました。");
        return true;
    }
    private static void Postfix(CharacterHandle __instance)
    {
        if (!IsValid(__instance))
            return;

        if (__instance.m_animator.gameObject.GetComponent<ForceSwayingLoop>() == null)
        {
            var loop = __instance.m_animator.gameObject.AddComponent<ForceSwayingLoop>();
            loop.Initialize(__instance);
        }
    }

    // ミニゲームのみで有効。フィッティングルームでは無効
    // ミニゲーム以外ではlayer 3がなくスカートが揺れない
    private static bool IsValid(CharacterHandle instance)
    {
        return  Configs.SwaySkirtEnabled.Value &&
                instance?.m_lastLoadArg != null &&
                instance.m_animator?.gameObject != null &&
                instance.m_lastLoadArg.AnimatorType == AnimatorType.Minigame &&
                !CostumeChangerPatch.IsFittingRoomActiveExternal();
    }

    // SkirtWeightを変更するクラス
    // HotKeyを押す間，SkirtWeight を 1.0f に，離すと 0.0f にする
    private class ForceSwayingLoop : MonoBehaviour
    {
        private CharacterHandle target;
        private float currentWeight = 0f;
        private float transitionSpeed = 5f;

        private void Awake()
        {
            Plugin.Logger.LogInfo($"[{nameof(SwaySkirtPatch)}] {nameof(ForceSwayingLoop)}をセットしました");            
        }
        private void OnDestroy()
        {
            Plugin.Logger.LogInfo($"[{nameof(SwaySkirtPatch)}] {nameof(ForceSwayingLoop)}は破棄されました");            
        }

        public void Initialize(CharacterHandle handle)
        {
            target = handle;
        }

        private void Update()
        {
            if (!IsValid(target))
            {
                Destroy(this);
                return;
            }

            if (target.m_animator.layerCount <= 3) // layerCountが3になるまで待つ
                return;

            float targetWeight = Configs.SwaySkirt.IsHeld() ? 1.0f : 0.0f;
            if (currentWeight != targetWeight)
            {
                currentWeight = Mathf.MoveTowards(currentWeight, targetWeight, transitionSpeed * Time.deltaTime);
                target.SetSkirtWeight(currentWeight);
            }
        }
    }
}
