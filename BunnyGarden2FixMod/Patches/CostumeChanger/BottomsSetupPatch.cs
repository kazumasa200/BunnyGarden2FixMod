using BunnyGarden2FixMod.Utils;
using GB.Scene;
using HarmonyLib;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// <see cref="CharacterHandle.setup"/> の Postfix で <see cref="BottomsLoader.ApplyIfOverridden"/>
/// を呼び、<see cref="BottomsOverrideStore"/> に登録された下衣移植を適用する。
///
/// CharacterHandle.Preload は L538 で m_lastLoadArg = arg してから return し、setup() は
/// modelLoader/animLoader の OnComplete callback 経由で後から呼ばれるため、
/// Postfix 時点で arg は新値を指している（KneeSocksLoader と同じ前提）。
///
/// setupPantiesOnly には張らない（panties 経路で bottoms は再ロードされない）。
/// </summary>
[HarmonyPatch(typeof(CharacterHandle), nameof(CharacterHandle.setup))]
internal static class BottomsSetupPatch
{
    private static bool Prepare()
    {
        bool enabled = Configs.CostumeChangerEnabled?.Value ?? true;
        if (enabled) PatchLogger.LogInfo("[BottomsSetupPatch] 適用");
        return enabled;
    }

    private static void Postfix(CharacterHandle __instance) =>
        BottomsLoader.ApplyIfOverridden(__instance);
}
