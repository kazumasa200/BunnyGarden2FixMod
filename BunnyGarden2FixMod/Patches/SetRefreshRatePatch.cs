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
    private static bool s_subscribed;

    private static void Postfix()
    {
        Apply();

        if (!s_subscribed)
        {
            // 値変更イベントを購読し、以降は Setup を経由しなくても適用される。
            // BepInEx の ConfigEntry.SettingChanged は同インスタンス上で複数回購読すると
            // ハンドラが重複登録されるため、s_subscribed フラグで一度だけ繋ぐ。
            Plugin.ConfigFrameRate.SettingChanged += (_, _) => Apply();
            s_subscribed = true;
        }
    }

    private static void Apply()
    {
        if (Plugin.ConfigFrameRate.Value <= 0)
        {
            // 0 以下なら上限撤廃
            Application.targetFrameRate = -1;
            PatchLogger.LogInfo("フレームレートの上限を撤廃しました");
            return;
        }
        // 指定したフレームレートに設定
        Application.targetFrameRate = Plugin.ConfigFrameRate.Value;
        PatchLogger.LogInfo($"フレームレートを {Plugin.ConfigFrameRate.Value} FPS に設定しました");
    }
}
