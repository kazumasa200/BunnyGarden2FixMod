using BunnyGarden2FixMod.Utils;
using GB.Scene;
using HarmonyLib;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// <see cref="CharacterHandle.setup"/> の Postfix で <see cref="TopsLoader.ApplyIfOverridden"/>
/// を呼び、<see cref="TopsOverrideStore"/> に登録された上衣移植を適用する。
///
/// Bottoms と独立した patch class（HarmonyX は同一 method への複数 patch を許容）。
/// </summary>
[HarmonyPatch(typeof(CharacterHandle), nameof(CharacterHandle.setup))]
internal static class TopsSetupPatch
{
    private static bool Prepare()
    {
        bool enabled = Configs.CostumeChangerEnabled.Value;
        if (enabled) PatchLogger.LogInfo("[TopsSetupPatch] 適用");
        return enabled;
    }

    private static void Postfix(CharacterHandle __instance) =>
        TopsLoader.ApplyIfOverridden(__instance);
}
