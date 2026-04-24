using HarmonyLib;
using UnityEngine.Rendering.Universal;

namespace BunnyGarden2FixMod.Patches;

[HarmonyPatch(typeof(ChromaticAberration), nameof(ChromaticAberration.IsActive))]
public static class ChromaticAberrationPatch
{
    private static bool Prefix(ChromaticAberration __instance, ref bool __result)
    {
        if (!Plugin.ConfigDisableChromaticAberration.Value)
            return true;
        __result = false;
        return false;
    }
}
