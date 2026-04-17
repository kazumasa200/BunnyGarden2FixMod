using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
#if BIE6
using BepInEx.Unity.Mono;
#endif
using BunnyGarden2FixMod.Controllers;
using BunnyGarden2FixMod.Utils;
using GB;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace BunnyGarden2FixMod;

public enum AntiAliasingType
{
    Off,
    FXAA,
    TAA,
    MSAA2x,
    MSAA4x,
    MSAA8x,
}

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    public static ConfigEntry<int> ConfigWidth;
    public static ConfigEntry<int> ConfigHeight;
    public static ConfigEntry<int> ConfigFrameRate;
    public static ConfigEntry<AntiAliasingType> ConfigAntiAliasing;
    public static ConfigEntry<float> ConfigSensitivity;
    public static ConfigEntry<float> ConfigSpeed;
    public static ConfigEntry<float> ConfigFastSpeed;
    public static ConfigEntry<float> ConfigSlowSpeed;

    private GameObject freeCamObject;
    private Camera freeCam;
    private Camera originalCam;
    private FreeCameraController controller;
    public static bool isFreeCamActive = false;
    public static bool isFixedFreeCam = false;

    internal new static ManualLogSource Logger;

    private void Awake()
    {
        ConfigWidth = Config.Bind(
            "Resolution",
            "Width",
            1920,
            "解像度の幅（横）を指定します");

        ConfigHeight = Config.Bind(
            "Resolution",
            "Height",
            1080,
            "解像度の高さ（縦）を指定します");

        ConfigFrameRate = Config.Bind(
            "Resolution",
            "FrameRate",
            60,
            "フレームレート上限を指定します。-1にすると上限を撤廃します。");

        ConfigAntiAliasing = Config.Bind(
            "AntiAliasing",
            "AntiAliasingType",
            AntiAliasingType.MSAA8x,
            "アンチエイリアシングの種類を指定します。右の方ほど画質が良くなりますが、動作が重くなります。Off / FXAA / TAA / MSAA2x / MSAA4x / MSAA8x");

        ConfigSensitivity = Config.Bind(
            "Camera",
            "Sensitivity",
            2f,
            "フリーカメラのマウス感度");

        ConfigSpeed = Config.Bind(
            "Camera",
            "Speed",
            10f,
            "フリーカメラの移動速度");

        ConfigFastSpeed = Config.Bind(
            "Camera",
            "FastSpeed",
            30f,
            "フリーカメラの高速移動速度（Shift）");

        ConfigSlowSpeed = Config.Bind(
            "Camera",
            "SlowSpeed",
            2.5f,
            "フリーカメラの低速移動速度（Ctrl）");

        Logger = base.Logger;
        PatchLogger.Initialize(Logger);
        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();
        PatchLogger.LogInfo($"プラグイン起動: {MyPluginInfo.PLUGIN_GUID} v{MyPluginInfo.PLUGIN_VERSION}");
        PatchLogger.LogInfo($"解像度パッチを適用しました: {Plugin.ConfigWidth.Value}x{Plugin.ConfigHeight.Value}");
        PatchLogger.LogInfo($"アンチエイリアシング設定: {Plugin.ConfigAntiAliasing.Value}");
    }

    private void OnGUI()
    {
        if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.F5)
            ToggleFreeCam();

        if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.F6)
            ToggleFixedFreeCam();

        if (isFreeCamActive)
        {
            if (isFixedFreeCam)
            {
                GUI.color = Color.yellow;
                GUI.Label(new Rect(10, 40, 500, 30), "Fixed Free Camera Mode: ON (F6=TOGGLE)");
                GUI.color = Color.white;
            }
            GUI.color = Color.green;
            GUI.Label(new Rect(10, 10, 500, 30), "Free Camera: ON (F5=OFF, Arrow/WASD=Move, E/Q=UpDown)");
            GUI.color = Color.white;
        }
    }

    private void ToggleFreeCam()
    {
        isFreeCamActive = !isFreeCamActive;

        if (isFreeCamActive)
            CreateFreeCam();
        else
        {
            DestroyFreeCam();
            isFixedFreeCam = false;
        }

        PatchLogger.LogInfo($"フリーカメラ: {(isFreeCamActive ? "ON" : "OFF")}");
    }

    private void ToggleFixedFreeCam()
    {
        if (isFreeCamActive)
        {
            isFixedFreeCam = !isFixedFreeCam;
            PatchLogger.LogInfo($"フリーカメラ固定モード: {(isFixedFreeCam ? "ON" : "OFF")}");
        }
    }

    private void CreateFreeCam()
    {
        // シーン内の全カメラを診断ログ出力
        var allCameras = Camera.allCameras;
        PatchLogger.LogInfo($"[FreeCam診断] シーン内カメラ数: {allCameras.Length}");
        foreach (var cam in allCameras)
        {
            var brain = cam.GetComponent("CinemachineBrain");
            PatchLogger.LogInfo($"  - {cam.name} | tag={cam.tag} | depth={cam.depth} | enabled={cam.enabled} | CinemachineBrain={brain != null}");
        }

        originalCam = Camera.main;
        if (originalCam == null)
        {
            // tag に頼らず depth 最大のカメラを代替として使用
            foreach (var cam in allCameras)
            {
                if (originalCam == null || cam.depth > originalCam.depth)
                    originalCam = cam;
            }

            if (originalCam == null)
            {
                PatchLogger.LogError("[FreeCam診断] 有効なカメラが見つかりません。フリーカメラを起動できません");
                isFreeCamActive = false;
                return;
            }
            PatchLogger.LogInfo($"[FreeCam診断] 代替カメラを使用: {originalCam.name}");
        }
        else
        {
            PatchLogger.LogInfo($"[FreeCam診断] Camera.main = {originalCam.name}");
        }

        freeCamObject = new GameObject("BG2FreeCam");
        freeCam = freeCamObject.AddComponent<Camera>();
        freeCam.CopyFrom(originalCam);
        freeCamObject.transform.SetPositionAndRotation(
            originalCam.transform.position,
            originalCam.transform.rotation);

        // URP ポストプロセス設定をコピー（CinemachineBrain には触らない）
        CopyUrpCameraData(originalCam, freeCam);

        controller = freeCamObject.AddComponent<FreeCameraController>();
        freeCamObject.AddComponent<AudioListener>();

        originalCam.enabled = false;
        var originalListener = originalCam.GetComponent<AudioListener>();
        if (originalListener != null)
            originalListener.enabled = false;

        PatchLogger.LogInfo("フリーカメラを作成しました");
    }

    private static void CopyUrpCameraData(Camera src, Camera dst)
    {
        var srcData = src.GetUniversalAdditionalCameraData();
        var dstData = dst.GetUniversalAdditionalCameraData();
        if (srcData == null || dstData == null)
            return;

        dstData.renderPostProcessing  = srcData.renderPostProcessing;
        dstData.antialiasing          = srcData.antialiasing;
        dstData.antialiasingQuality   = srcData.antialiasingQuality;
        dstData.stopNaN               = srcData.stopNaN;
        dstData.dithering             = srcData.dithering;
        dstData.renderShadows         = srcData.renderShadows;
        dstData.volumeLayerMask       = srcData.volumeLayerMask;
        dstData.volumeTrigger         = srcData.volumeTrigger;
    }

    private void DestroyFreeCam()
    {
        if (freeCamObject != null)
        {
            Destroy(freeCamObject);
            freeCamObject = null;
            freeCam = null;
            controller = null;
        }

        if (originalCam != null)
        {
            originalCam.enabled = true;

            var originalListener = originalCam.GetComponent<AudioListener>();
            if (originalListener != null)
                originalListener.enabled = true;
        }
    }
}

[HarmonyPatch(typeof(GBSystem), "IsInputDisabled")]
public class FreeCamInputDisablePatch
{
    private static void Postfix(ref bool __result)
    {
        if (Plugin.isFreeCamActive && !Plugin.isFixedFreeCam)
            __result = true;
    }
}
