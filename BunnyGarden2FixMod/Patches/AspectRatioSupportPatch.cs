using GB;
using GB.Scene;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// ゲーム本体の 16:9 固定チェックを、バー入店中だけウルトラワイド比率へ差し替える。
/// </summary>
[HarmonyPatch]
public static class AspectRatioSupportPatch
{
    private const float VanillaAspect = 1.7777778f;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(GBSystem), "Update");

        foreach (var nestedType in typeof(FirstScene).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (!typeof(IAsyncStateMachine).IsAssignableFrom(nestedType))
            {
                continue;
            }

            var moveNext = AccessTools.Method(nestedType, "MoveNext");
            if (moveNext != null)
            {
                yield return moveNext;
            }
        }
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getAspectMethod = AccessTools.Method(
            typeof(GameplayFullscreenUltrawideSupport),
            nameof(GameplayFullscreenUltrawideSupport.GetAspectRatioForGameChecks));

        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Ldc_R4
                && instruction.operand is float value
                && value == VanillaAspect)
            {
                yield return new CodeInstruction(OpCodes.Call, getAspectMethod);
                continue;
            }

            yield return instruction;
        }
    }
}
