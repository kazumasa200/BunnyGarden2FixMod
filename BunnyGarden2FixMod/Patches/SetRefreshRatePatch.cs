using BunnyGarden2FixMod.Utils;
using GB;
using HarmonyLib;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// フレームレートを Config に追従させるパッチ。
/// GBSystem.Setup の Postfix で初回適用 + SettingChanged を購読し、
/// F9 / F4 reload / .cfg 直接編集のいずれの経路でも即時反映する。
/// </summary>
[HarmonyPatch(typeof(GBSystem), "Setup")]
public class SetRefreshRatePatch
{
    private static void Postfix()
        => LiveConfigBinding.BindAndApply(Configs.FrameRate, Apply);

    private static void Apply()
    {
        if (Configs.FrameRate.Value <= 0)
        {
            // 0 以下なら上限撤廃
            Application.targetFrameRate = -1;
            PatchLogger.LogInfo("フレームレートの上限を撤廃しました");
            return;
        }
        // 指定したフレームレートに設定
        Application.targetFrameRate = Configs.FrameRate.Value;
        PatchLogger.LogInfo($"フレームレートを {Configs.FrameRate.Value} FPS に設定しました");
    }
}
