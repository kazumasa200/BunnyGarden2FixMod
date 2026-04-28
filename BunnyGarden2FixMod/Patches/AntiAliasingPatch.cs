using BunnyGarden2FixMod.Utils;
using GB;
using HarmonyLib;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// カメラのアンチエイリアシング設定を上書きするパッチ
/// </summary>
[HarmonyPatch(
    typeof(UniversalAdditionalCameraData),
    nameof(UniversalAdditionalCameraData.antialiasing),
    MethodType.Setter
)]
public static class AntiAliasingSetterPatch
{
    private static void Prefix(ref AntialiasingMode value)
    {
        value = Plugin.ConfigAntiAliasing.Value switch
        {
            AntiAliasingType.Off => AntialiasingMode.None,
            AntiAliasingType.FXAA => AntialiasingMode.FastApproximateAntialiasing,
            AntiAliasingType.TAA => AntialiasingMode.TemporalAntiAliasing,
            // MSAA はポストプロセスAAをオフにして別途 msaaSampleCount で設定
            _ => AntialiasingMode.None,
        };
    }
}

/// <summary>
/// MSAA のサンプル数を Config に追従させるパッチ。
/// GBSystem.Setup の Postfix で初回適用 + SettingChanged を購読し、
/// F9 / F4 reload / .cfg 直接編集どれの経路でも即時反映する。
/// MSAA → FXAA/TAA/Off 切替時に msaaSampleCount を 1 に戻すため、
/// MSAA 以外でも常に msaaSampleCount を上書きする。
/// </summary>
[HarmonyPatch(typeof(GBSystem), "Setup")]
public static class MsaaSetupPatch
{
    private static void Postfix()
        => LiveConfigBinding.BindAndApply(Plugin.ConfigAntiAliasing, Apply);

    private static void Apply()
    {
        int msaaSamples = Plugin.ConfigAntiAliasing.Value switch
        {
            AntiAliasingType.MSAA2x => 2,
            AntiAliasingType.MSAA4x => 4,
            AntiAliasingType.MSAA8x => 8,
            _ => 1,
        };

        if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urpAsset)
        {
            // MSAA 以外への切替時に sampleCount を 1 へ戻す必要があるため、
            // 値が 1 でも常に書き戻す。
            urpAsset.msaaSampleCount = msaaSamples;
            PatchLogger.LogInfo($"MSAA を {msaaSamples}x に設定しました");
        }
        else
        {
            // URP asset が取得できないと MSAA 設定が反映されない（可視的失敗）ため Warning。
            PatchLogger.LogWarning("[MSAA] URP asset を取得できませんでした。設定が反映されません");
        }
    }
}
