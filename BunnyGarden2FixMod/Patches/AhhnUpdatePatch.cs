using BunnyGarden2FixMod.Utils;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// あーんミニゲームのカーソル/フード移動をフレームレート非依存に修正するパッチ。
///
/// 元の実装では Update() 内の position += velocity がフレームごとに無条件加算されており、
/// 高 FPS ほどカーソルが速くなる。Prefix で移動前の座標を記録し、Postfix で
/// 実際に動いた差分に Time.deltaTime * 60f を乗算して戻すことで 60FPS 基準に正規化する。
///
/// ForPlayerMode: m_mouth (Transform.localPosition) がカーソル座標
/// ForCastMode  : m_food.transform.localPosition がフード座標
/// </summary>
[HarmonyPatch]
public static class AhhnUpdatePatch
{
    private static FieldInfo s_mouthField;
    private static FieldInfo s_foodField;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        var baseType = AccessTools.TypeByName("GB.Bar.MiniGame.AhhnModeBase");
        if (baseType != null)
        {
            s_mouthField = AccessTools.Field(baseType, "m_mouth");
            s_foodField = AccessTools.Field(baseType, "m_food");
        }

        foreach (var typeName in new[] { "GB.Bar.MiniGame.ForPlayerMode", "GB.Bar.MiniGame.ForCastMode" })
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null)
            {
                PatchLogger.LogWarning($"[AhhnUpdatePatch] {typeName} が見つかりませんでした");
                continue;
            }
            var method = AccessTools.Method(type, "Update");
            if (method == null)
            {
                PatchLogger.LogWarning($"[AhhnUpdatePatch] {typeName}.Update が見つかりませんでした");
                continue;
            }
            PatchLogger.LogInfo($"[AhhnUpdatePatch] {typeName}.Update をパッチしました");
            yield return method;
        }
    }

    private static Vector3 GetPos(object instance)
    {
        if (instance.GetType().Name == "ForPlayerMode")
            return (s_mouthField?.GetValue(instance) as Transform)?.localPosition ?? Vector3.zero;
        return (s_foodField?.GetValue(instance) as GameObject)?.transform.localPosition ?? Vector3.zero;
    }

    private static void SetPos(object instance, Vector3 pos)
    {
        if (instance.GetType().Name == "ForPlayerMode")
        {
            var mouth = s_mouthField?.GetValue(instance) as Transform;
            if (mouth != null) mouth.localPosition = pos;
        }
        else
        {
            var food = s_foodField?.GetValue(instance) as GameObject;
            if (food != null) food.transform.localPosition = pos;
        }
    }

    private static void Prefix(object __instance, out Vector3 __state)
        => __state = GetPos(__instance);

    private static void Postfix(object __instance, Vector3 __state)
    {
        Vector3 delta = GetPos(__instance) - __state;
        if (delta.sqrMagnitude < float.Epsilon) return;
        SetPos(__instance, __state + delta * (Time.deltaTime * 60f));
    }
}
