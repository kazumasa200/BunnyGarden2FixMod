using BunnyGarden2FixMod.Utils;
using HarmonyLib;
using UnityEngine.Rendering.Universal;

namespace BunnyGarden2FixMod.Patches;

[HarmonyPatch(typeof(DepthOfField), nameof(DepthOfField.IsActive))]
public static class DepthOfFieldPatch
{
    private static bool Prepare()
    {
        PatchLogger.LogInfo(
            $"[{nameof(DepthOfFieldPatch)}] " +
            $"{nameof(DepthOfField)}.{nameof(DepthOfField.IsActive)} " +
            $"をパッチしました。");
        return true;
    }

    private static bool Prefix(DepthOfField __instance, ref bool __result)
    {
        if (!Plugin.ConfigDisableDepthOfField.Value)
            return true;
        __result = false;
        return false;
    }
}