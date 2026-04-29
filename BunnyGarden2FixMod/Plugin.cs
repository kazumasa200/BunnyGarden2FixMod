#if BIE6
using BepInEx.Unity.Mono;
#endif

using System;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using BunnyGarden2FixMod.Utils;
using GB;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace BunnyGarden2FixMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    private static Plugin Instance;

    internal static event Action GUICallback;

    private Patches.FreeCamera.FreeCameraManager freeCamera;
    private bool isOverlayVisible = true;
    private bool isCapturingScreenshot;
    private static float suppressGameInputUntilUnscaledTime = -1f;
    private const float ControllerShortcutSuppressDuration = 0.18f;

    private static readonly string ScreenshotDirectory = Path.Combine(Paths.BepInExRootPath, "screenshots",
        MyPluginInfo.PLUGIN_GUID);

    internal new static ManualLogSource Logger;

    private void Awake()
    {
        Instance = this;
        Logger = base.Logger;
        PatchLogger.Initialize(Logger);
        ConfigMigration.Migrate(Config);

        // YAML 駆動 Config エントリ（source of truth: Configs.yaml → Generated/Configs.g.cs）。
        // HotkeyConfig (KB+Pad 統合型) も BindAll 内で初期化される。
        Configs.BindAll(Config);

        // Steam 外起動を検出した場合は Steam 経由で再起動して即終了
        if (Configs.SteamLaunchCheck.Value && SteamLaunchChecker.CheckAndRelaunchIfNeeded())
        {
            Application.Quit();
            return;
        }

        StartCoroutine(UpdateChecker.Check());
        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();
        // async ステートマシンは Harmony でパッチできないため LateUpdate 方式で補正
        Patches.CameraZoomPatch.Initialize(gameObject);
        Patches.CastOrderUI.CastOrderController.Initialize(gameObject);
        Patches.CostumeChanger.CostumeChangerPatch.Initialize(gameObject);
        Patches.Settings.SettingsController.Initialize(gameObject);
        Patches.HideUI.HideUIRuntime.Initialize(gameObject);
        Patches.CostumeChanger.StockingsDonorLoader.Initialize(gameObject);
        freeCamera = Patches.FreeCamera.FreeCameraManager.Initialize(gameObject);
        Patches.TimeController.Initialize(gameObject);
        SceneManager.sceneUnloaded += Patches.CostumeChanger.PantiesAltSlotMatchPatch.OnSceneUnloaded;
        PatchLogger.LogInfo($"プラグイン起動: {MyPluginInfo.PLUGIN_GUID} v{MyPluginInfo.PLUGIN_VERSION}");
        PatchLogger.LogInfo($"解像度パッチを適用しました: {Configs.Width.Value}x{Configs.Height.Value}");
        PatchLogger.LogInfo($"アンチエイリアシング設定: {Configs.AntiAliasing.Value}");
    }

    private void OnDestroy()
    {
        if (ReferenceEquals(Instance, this))
            Instance = null;
    }

    private void Update()
    {
        if (Keyboard.current?[Key.F4].wasPressedThisFrame == true)
            Config.Reload();

        if (Configs.OverlayToggle.IsTriggered())
            ToggleOverlay();

        if (Configs.CaptureScreenshot.IsTriggered())
            CaptureScreenshot();
    }

    private void OnGUI()
    {
        if (!isOverlayVisible || isCapturingScreenshot)
            return;

        GUILayout.BeginArea(new Rect(10, 10, Screen.width / 2, Screen.height - 10));
        GUICallback?.Invoke();
        GUILayout.EndArea();
    }

    internal static void DisableFreeCamForSystemUiIfNeeded(string reason)
    {
        Instance?.freeCamera?.Deactivate();

        PatchLogger.LogInfo($"フリーカメラを自動解除しました: {reason}");
    }

    /// <summary>
    /// 一定時間 (0.18 秒) ゲーム本体側の入力およびホットキー判定を抑止する。
    /// コントローラーショートカット発火後の連続発火防止と、KeyBinding キャプチャ確定後の
    /// 同一キー再評価防止に使用。
    /// </summary>
    public static void SuppressGameInputTemporarily()
    {
        suppressGameInputUntilUnscaledTime = Time.unscaledTime + ControllerShortcutSuppressDuration;
    }

    /// <summary>SuppressGameInputTemporarily 期間中なら true。</summary>
    internal static bool ShouldSuppressGameInput()
    {
        return Time.unscaledTime < suppressGameInputUntilUnscaledTime;
    }

    internal static Camera FindCurrentCamera()
    {
        var mainCam = Camera.main;
        if (mainCam != null)
        {
            Plugin.Logger.LogInfo($"Camera.main = {mainCam.name}");
            return mainCam;
        }

        // tag に頼らず depth 最大のカメラを代替として使用
        var cam = Camera.allCameras.OrderByDescending(c => c.depth).FirstOrDefault();
        if (cam == null)
        {
            Plugin.Logger.LogError("有効なカメラが見つかりません。");
            return null;
        }
        Plugin.Logger.LogInfo($"代替カメラを使用: {cam.name}");

        return cam;
    }

    private void ToggleOverlay()
    {
        isOverlayVisible = !isOverlayVisible;
        PatchLogger.LogInfo($"表示: {(isOverlayVisible ? "ON" : "OFF")}");
    }

    private void CaptureScreenshot()
    {
        StartCoroutine(CaptureScreenshotCoroutine());
    }

    private System.Collections.IEnumerator CaptureScreenshotCoroutine()
    {
        Camera captureCam = FindCurrentCamera();
        if (captureCam == null)
            yield break;

        isCapturingScreenshot = true;

        try
        {
            Directory.CreateDirectory(ScreenshotDirectory);
            string path = Path.Combine(ScreenshotDirectory, $"bg2_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            ScreenCapture.CaptureScreenshot(path, Configs.ScreenshotScale.Value);
            PatchLogger.LogInfo($"スクリーンショットを保存しました: {path}");
        }
        catch (Exception ex)
        {
            PatchLogger.LogError($"スクリーンショット保存失敗: {ex.Message}");
        }

        // スクリーンショットがキャプチャされる前にオーバーレイを再表示しないよう、1フレーム待機します
        yield return null;
        isCapturingScreenshot = false;
    }
}

[HarmonyPatch(typeof(GBSystem), "IsInputDisabled")]
public class FreeCamInputDisablePatch
{
    private static void Postfix(ref bool __result)
    {
        if (Patches.FreeCamera.FreeCameraManager.IsActive && !Patches.FreeCamera.FreeCameraManager.IsFixed)
            __result = true;
    }
}

[HarmonyPatch(typeof(GBSystem), "confirmQuit")]
public class FreeCamDisableOnQuitConfirmPatch
{
    private static void Prefix()
    {
        Plugin.DisableFreeCamForSystemUiIfNeeded("終了確認ダイアログ");
    }
}

[HarmonyPatch]
public class FreeCamControllerShortcutInputSuppressionPatch
{
    [HarmonyPatch(typeof(GBInput), "isTriggered")]
    [HarmonyPrefix]
    private static bool SuppressTriggered(InputAction button, ref bool __result)
    {
        return TrySuppress(button, ref __result);
    }

    [HarmonyPatch(typeof(GBInput), "isPressing")]
    [HarmonyPrefix]
    private static bool SuppressPressing(InputAction button, ref bool __result)
    {
        return TrySuppress(button, ref __result);
    }

    [HarmonyPatch(typeof(GBInput), "isReleased")]
    [HarmonyPrefix]
    private static bool SuppressReleased(InputAction button, ref bool __result)
    {
        return TrySuppress(button, ref __result);
    }

    [HarmonyPatch(typeof(GBInput), "isTriggeredR")]
    [HarmonyPrefix]
    private static bool SuppressTriggeredRepeat(ref bool __result)
    {
        // キャプチャ中はゲーム側の全リピート入力を遮断する
        if (Patches.Settings.SettingsController.IsAnyCapturing)
        {
            __result = false;
            return false;
        }
        if (!Patches.FreeCamera.FreeCameraManager.IsActive || !Plugin.ShouldSuppressGameInput())
            return true;

        __result = false;
        return false;
    }

    [HarmonyPatch(typeof(GBInput), "GetStickValue")]
    [HarmonyPrefix]
    private static bool SuppressStick(InputAction stick, ref Vector2 __result)
    {
        // キャプチャ中はゲーム側のスティック入力を遮断する
        if (Patches.Settings.SettingsController.IsAnyCapturing)
        {
            __result = Vector2.zero;
            return false;
        }
        if (!Patches.FreeCamera.FreeCameraManager.IsActive || !Plugin.ShouldSuppressGameInput())
            return true;

        if (stick?.activeControl?.device is not Gamepad)
            return true;

        __result = Vector2.zero;
        return false;
    }

    [HarmonyPatch(typeof(GBInput), "CameraControll")]
    [HarmonyPrefix]
    private static bool SuppressCameraControl(ref Vector2 __result)
    {
        // キャプチャ中はゲーム側のカメラ操作入力を遮断する
        if (Patches.Settings.SettingsController.IsAnyCapturing)
        {
            __result = Vector2.zero;
            return false;
        }
        if (!Patches.FreeCamera.FreeCameraManager.IsActive || !Plugin.ShouldSuppressGameInput())
            return true;

        __result = Vector2.zero;
        return false;
    }

    private static bool TrySuppress(InputAction button, ref bool result)
    {
        // キャプチャ中はゲーム側の全ボタン入力を遮断する
        if (Patches.Settings.SettingsController.IsAnyCapturing)
        {
            result = false;
            return false;
        }
        if (!Patches.FreeCamera.FreeCameraManager.IsActive || !Plugin.ShouldSuppressGameInput())
            return true;

        if (button?.activeControl?.device is not Gamepad)
            return true;

        result = false;
        return false;
    }
}

// 以前ここには CostumePickerInputDisablePatch があり、Wardrobe 表示中に
// IsInputDisabled を強制 true にしていたが、GBInput.LeftClick (ADV のクリック判定)
// も IsInputDisabled ゲートを通るため、Wardrobe 表示中は ADV が一切進まなくなっていた。
// Wardrobe 操作は CostumePickerController が Keyboard.current を直接ポーリングする
// 設計なので本体 IsInputDisabled に依存しない → パッチを削除しゲーム本体の入力を通す。
// ただし panel 裏のクリックが ADV に貫通するのを防ぐため、下の
// SuppressClickOverWardrobePatch でカーソル位置によって個別にマスクする。

/// <summary>
/// カーソルが Wardrobe / HideUI パネル矩形内にある間は GBInput.isMouseTriggered を false に差し替え、
/// panel 裏のクリックで ADV が進行したり背後の uGUI ボタンが反応するのを防ぐ。
/// panel 外クリックは素通しするため、ADV の進行や他操作は通常通り動作する。
/// </summary>
[HarmonyPatch(typeof(GBInput), "isMouseTriggered")]
public class SuppressClickOverWardrobePatch
{
    private static bool Prefix(ref bool __result)
    {
        // キャプチャ中はマウスクリックもゲーム側に流れないよう遮断する
        if (Patches.Settings.SettingsController.IsAnyCapturing) { __result = false; return false; }
        if (!Patches.CostumeChanger.UI.CostumePickerController.ShouldSuppressGameInput() &&
            !Patches.Settings.SettingsController.ShouldSuppressMouseInput()) return true;
        __result = false;
        return false;
    }
}

/// <summary>
/// カーソルが Wardrobe / HideUI パネル矩形内にある間は GBInput.ScrollAxis を 0 に差し替え、
/// panel 上でのマウスホイールが ADV/BackLog 呼び出し等の本体操作に流れるのを防ぐ。
/// UI Toolkit 内部の ScrollView は EventSystem 側から独立して WheelEvent を受け取るため
/// この差し替えでは影響を受けず、panel 内スクロールは従来通り動作する。
/// HarmonyX の MethodType.Getter より確実な AccessTools.PropertyGetter で target を明示する。
/// </summary>
[HarmonyPatch]
public class SuppressScrollOverWardrobePatch
{
    private static System.Reflection.MethodBase TargetMethod()
        => AccessTools.PropertyGetter(typeof(GBInput), nameof(GBInput.ScrollAxis));

    private static bool Prefix(ref float __result)
    {
        // キャプチャ中はスクロール入力もゲーム側に流れないよう遮断する
        if (Patches.Settings.SettingsController.IsAnyCapturing) { __result = 0f; return false; }
        if (!Patches.CostumeChanger.UI.CostumePickerController.ShouldSuppressGameInput() &&
            !Patches.Settings.SettingsController.ShouldSuppressMouseInput()) return true;
        __result = 0f;
        return false;
    }
}

/// <summary>
/// カーソルが Wardrobe パネル矩形内にある間、CostumePicker が使用する GBInput アクション
/// （AButton/Up/Down/Left/Right/StartButton/XButton/Auto）の一発押しを false に差し替える。
/// 対象アクションは CostumePickerController.s_pickerActions で管理。
/// </summary>
[HarmonyPatch(typeof(GBInput), "isTriggered")]
public class SuppressKeyOverWardrobePatch
{
    private static bool Prefix(UnityEngine.InputSystem.InputAction button, ref bool __result)
    {
        // キャプチャ中はキー入力もゲーム側に流れないよう遮断する
        if (Patches.Settings.SettingsController.IsAnyCapturing) { __result = false; return false; }
        if (!Patches.CostumeChanger.UI.CostumePickerController.ShouldSuppressGameInput(button?.name)) return true;
        __result = false;
        return false;
    }
}

/// <summary>
/// カーソルが Wardrobe / HideUI パネル矩形内にある間は GBInput.isTriggeredR を false に差し替え、
/// リピート入力がゲーム側に流れるのを防ぐ。
/// isTriggeredR はボタン情報を持たないため全アクションを対象とする。
/// </summary>
[HarmonyPatch(typeof(GBInput), "isTriggeredR")]
public class SuppressKeyRepeatOverWardrobePatch
{
    private static bool Prefix(ref bool __result)
    {
        // キャプチャ中はリピートキー入力もゲーム側に流れないよう遮断する
        if (Patches.Settings.SettingsController.IsAnyCapturing) { __result = false; return false; }
        if (!Patches.CostumeChanger.UI.CostumePickerController.ShouldSuppressGameInput() &&
            !Patches.Settings.SettingsController.ShouldSuppressMouseInput()) return true;
        __result = false;
        return false;
    }
}

/// <summary>
/// CurrentCast 切替時に、新 current キャラの直近 LoadArg (衣装/パンツ/ストッキング) を
/// 履歴へフラッシュする。キャスト交代では新キャラの Preload が走り直さないため、
/// CostumeChangerPatch.Postfix のタイミングでは current != 新キャラ だった分を救う。
/// </summary>
[HarmonyPatch(typeof(GB.Game.GameData), nameof(GB.Game.GameData.SetCurrentCast))]
public class SetCurrentCastFlushHistoryPatch
{
    private static void Postfix(GB.Game.CharID id)
    {
        if (!Patches.CostumeChanger.WardrobeHistoryGate.ShouldRecord(id)) return;
        if (!Patches.CostumeChanger.WardrobeLastLoadArg.TryGet(id,
                out var costume, out var pt, out var pc, out var stocking)) return;
        Patches.CostumeChanger.CostumeViewHistory.MarkViewed(id, costume);
        Patches.CostumeChanger.PantiesViewHistory.MarkViewed(id, pt, pc);
        Patches.CostumeChanger.StockingViewHistory.MarkViewed(id, stocking);
    }
}
