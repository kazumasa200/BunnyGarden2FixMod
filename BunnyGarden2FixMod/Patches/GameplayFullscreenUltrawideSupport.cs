using BunnyGarden2FixMod.Utils;
using GB;
using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace BunnyGarden2FixMod.Patches;

internal static class GameplayFullscreenUltrawideSupport
{
    internal const float Aspect16x9 = 16f / 9f;
    internal const float AspectTolerance = 0.05f;
    private const float LogIntervalSeconds = 2f;

    private static readonly System.Reflection.FieldInfo InitializedField =
        AccessTools.Field(typeof(GBSystem), "m_initialized");

    private static string lastStateLog;
    private static float nextStateLogTime;
    private static float nextUiLogTime;
    private static readonly Dictionary<CanvasScaler, (Vector2 referenceResolution, CanvasScaler.ScreenMatchMode mode, float match)> CanvasScalerStates = new();
    private static readonly Dictionary<RectTransform, Vector3> RectScaleStates = new();

    internal static bool ShouldUseNativeFullscreen()
    {
        if (!Plugin.ConfigFullscreenUltrawideEnabled.Value || !Screen.fullScreen)
        {
            return false;
        }

        GBSystem system = GBSystem.Instance;
        return system != null
            && IsBarGameplay(system)
            && IsWiderThan16x9(GetTargetResolution().width, GetTargetResolution().height);
    }

    internal static float GetExpectedFullscreenAspect()
    {
        if (!ShouldUseNativeFullscreen())
        {
            return Aspect16x9;
        }

        (int width, int height) target = GetTargetResolution();
        return (float)target.width / target.height;
    }

    internal static float GetAspectRatioForGameChecks()
    {
        return ShouldUseNativeFullscreen() ? GetExpectedFullscreenAspect() : Aspect16x9;
    }

    internal static float GetAspectMultiplier()
    {
        return GetExpectedFullscreenAspect() / Aspect16x9;
    }

    internal static float GetUltrawideExtraHalfWidth(float vanillaReferenceWidth = 1920f)
    {
        return (vanillaReferenceWidth * (GetAspectMultiplier() - 1f)) * 0.5f;
    }

    internal static (int width, int height) GetTargetResolution()
    {
        int configW = Plugin.ConfigWidth.Value;
        int configH = Plugin.ConfigHeight.Value;
        if (IsWiderThan16x9(configW, configH))
        {
            return (configW, configH);
        }

        Display display = Display.main;
        if (display != null && IsWiderThan16x9(display.systemWidth, display.systemHeight))
        {
            return (display.systemWidth, display.systemHeight);
        }

        Resolution current = Screen.currentResolution;
        return (current.width, current.height);
    }

    private static bool IsWiderThan16x9(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        float aspect = (float)width / height;
        return aspect > Aspect16x9 + AspectTolerance;
    }

    private static bool IsBarGameplay(GBSystem system)
    {
        return system.IsIngame
            && (IsSceneLoaded("BarScene") || IsGameDataInBar(system));
    }

    private static bool IsGameDataInBar(GBSystem system)
    {
        try
        {
            return system.RefGameData().IsInBar();
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSceneLoaded(string name)
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (scene.isLoaded && scene.name == name)
            {
                return true;
            }
        }

        return false;
    }

    private static string GetSceneList()
    {
        string result = string.Empty;
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded)
            {
                continue;
            }

            result = result.Length == 0 ? scene.name : result + "," + scene.name;
        }

        return result.Length == 0 ? "<none>" : result;
    }

    private static void LogStateIfChanged(GBSystem system, string reason)
    {
        (int targetW, int targetH) = GetTargetResolution();
        string state = $"{reason} scenes={GetSceneList()} ingame={system.IsIngame} inBar={IsGameDataInBar(system)} " +
            $"screen={Screen.width}x{Screen.height} fullscreen={Screen.fullScreen} current={Screen.currentResolution.width}x{Screen.currentResolution.height} " +
            $"target={targetW}x{targetH} enabled={Plugin.ConfigFullscreenUltrawideEnabled.Value} use={ShouldUseNativeFullscreen()}";

        if (state == lastStateLog && Time.unscaledTime < nextStateLogTime)
        {
            return;
        }

        lastStateLog = state;
        nextStateLogTime = Time.unscaledTime + LogIntervalSeconds;
        //PatchLogger.LogInfo("[Ultrawide] " + state); //あまりにもログが多すぎるので、必要なときだけ有効化する
    }

    internal static void ApplyUiAspect()
    {
        //ShouldUseNativeFullScreenの判定はコストが高いので、ConfigFullscreenUltrawideEnabledの判定を先に行う
        if (!Plugin.ConfigFullscreenUltrawideEnabled.Value) return;
        if (!ShouldUseNativeFullscreen())
        {
            ResetUiAspect();
            return;
        }

        float multiplier = GetAspectMultiplier();
        ApplyCanvasScalerAspect(multiplier);
        ApplyLetterboxRectAspect(multiplier);
    }

    private static void ApplyCanvasScalerAspect(float multiplier)
    {
        CanvasScaler[] scalers = UnityEngine.Object.FindObjectsByType<CanvasScaler>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);

        foreach (CanvasScaler scaler in scalers)
        {
            if (scaler == null || scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize)
            {
                continue;
            }

            if (!CanvasScalerStates.ContainsKey(scaler))
            {
                CanvasScalerStates[scaler] = (
                    scaler.referenceResolution,
                    scaler.screenMatchMode,
                    scaler.matchWidthOrHeight);
            }

            Vector2 reference = CanvasScalerStates[scaler].referenceResolution;
            scaler.referenceResolution = new Vector2(reference.x * multiplier, reference.y);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        }
    }

    private static void ApplyLetterboxRectAspect(float multiplier)
    {
        Graphic[] graphics = UnityEngine.Object.FindObjectsByType<Graphic>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);

        foreach (Graphic graphic in graphics)
        {
            if (graphic == null || !graphic.gameObject.activeInHierarchy)
            {
                continue;
            }

            RectTransform rect = graphic.rectTransform;
            if (!ShouldWidenRect(graphic, rect))
            {
                continue;
            }

            if (!RectScaleStates.ContainsKey(rect))
            {
                RectScaleStates[rect] = rect.localScale;
            }

            Vector3 original = RectScaleStates[rect];
            rect.localScale = new Vector3(original.x * multiplier, original.y, original.z);

            if (Time.unscaledTime >= nextUiLogTime)
            {
                PatchLogger.LogInfo($"[Ultrawide] UI widen {GetPath(rect)} size={rect.rect.size} scale={rect.localScale}");
            }
        }

        if (Time.unscaledTime >= nextUiLogTime)
        {
            nextUiLogTime = Time.unscaledTime + 5f;
        }
    }

    private static bool ShouldWidenRect(Graphic graphic, RectTransform rect)
    {
        string name = rect.name.ToLowerInvariant();
        bool likelyMaskName = name.Contains("mask")
            || name.Contains("fade")
            || name.Contains("black")
            || name.Contains("letter")
            || name.Contains("cinematic")
            || name.Contains("rawimage");

        bool likelyFullScreen = rect.rect.height >= 900f && rect.rect.width >= 1600f;
        bool likelyDark = graphic.color.a > 0.01f
            && graphic.color.r < 0.08f
            && graphic.color.g < 0.08f
            && graphic.color.b < 0.08f;

        return likelyFullScreen && (likelyMaskName || likelyDark);
    }

    private static void ResetUiAspect()
    {
        foreach (var pair in CanvasScalerStates)
        {
            CanvasScaler scaler = pair.Key;
            if (scaler == null)
            {
                continue;
            }

            scaler.referenceResolution = pair.Value.referenceResolution;
            scaler.screenMatchMode = pair.Value.mode;
            scaler.matchWidthOrHeight = pair.Value.match;
        }

        CanvasScalerStates.Clear();

        foreach (var pair in RectScaleStates)
        {
            RectTransform rect = pair.Key;
            if (rect != null)
            {
                rect.localScale = pair.Value;
            }
        }

        RectScaleStates.Clear();
    }

    private static string GetPath(Component component)
    {
        Transform transform = component.transform;
        string path = transform.name;
        while (transform.parent != null)
        {
            transform = transform.parent;
            path = transform.name + "/" + path;
        }

        return path;
    }

    private static bool IsInitialized(GBSystem system)
    {
        object initialized = InitializedField?.GetValue(system);
        if (initialized == null)
        {
            return false;
        }

        var valueProperty = initialized.GetType().GetProperty("Value");
        return valueProperty != null && valueProperty.GetValue(initialized) is bool value && value;
    }

    [HarmonyPatch(typeof(GBSystem), "Update")]
    private static class GBSystemUpdateDiagnosticsPatch
    {
        private static void Postfix(GBSystem __instance)
        {
            if (IsInitialized(__instance) && Screen.fullScreen)
            {
                LogStateIfChanged(__instance, "Update");
            }
        }
    }
}
