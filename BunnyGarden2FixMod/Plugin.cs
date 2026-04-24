using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using System.IO;

#if BIE6
using BepInEx.Unity.Mono;
#endif

using BunnyGarden2FixMod.Controllers;
using BunnyGarden2FixMod.Utils;
using GB;
using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.DualShock;
using UnityEngine.Rendering.Universal;
using UnityEngine.EventSystems;

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

/// <summary>チェキ高解像度版を ExSave に保存する際の画像フォーマット。</summary>
public enum ChekiImageFormat
{
    /// <summary>PNG 無劣化圧縮。サイズ 1/5〜1/20・エンコード 50〜200ms/枚</summary>
    PNG,
    /// <summary>JPG 劣化圧縮。サイズ 1/20〜1/50・エンコード 30〜100ms/枚</summary>
    JPG,
}

public enum ControllerHotkeyButton
{
    None,
    A,
    B,
    X,
    Y,
    L,
    R,
    ZL,
    ZR,
    Start,
    Select,
}

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    private static Plugin Instance;
    public static ConfigEntry<int> ConfigWidth;
    public static ConfigEntry<int> ConfigHeight;
    public static ConfigEntry<int> ConfigExtraWidth;
    public static ConfigEntry<int> ConfigExtraHeight;
    public static ConfigEntry<bool> ConfigExtraActive;
    public static ConfigEntry<int> ConfigFrameRate;
    public static ConfigEntry<AntiAliasingType> ConfigAntiAliasing;
    public static ConfigEntry<float> ConfigSensitivity;
    public static ConfigEntry<float> ConfigSpeed;
    public static ConfigEntry<float> ConfigFastSpeed;
    public static ConfigEntry<float> ConfigSlowSpeed;
    public static ConfigEntry<float> ConfigControllerTriggerDeadzone;
    public static ConfigEntry<bool> ConfigHideGameUiInFreeCam;
    public static ConfigEntry<Key> ConfigTimeStopToggleKey;
    public static ConfigEntry<Key> ConfigScreenshotKey;
    public static ConfigEntry<bool> ConfigCheatEnabled;
    public static ConfigEntry<bool> ConfigUltimateSurvivorEnabled;
    public static ConfigEntry<bool> ConfigGambleAlwaysWinEnabled;
    public static ConfigEntry<bool> ConfigDisableStockings;
    public static ConfigEntry<bool> ConfigContinueVoiceOnTap;
    public static ConfigEntry<bool> ConfigChekiHighResEnabled;
    public static ConfigEntry<int> ConfigChekiSize;
    public static ConfigEntry<ChekiImageFormat> ConfigChekiFormat;
    public static ConfigEntry<int> ConfigChekiJpgQuality;
    public static ConfigEntry<bool> ConfigEndingChekiSlideshow;
    public static ConfigEntry<bool> ConfigCastOrderEnabled;
    public static ConfigEntry<bool> ConfigControllerEnabled;
    public static ConfigEntry<ControllerHotkeyButton> ConfigControllerModifier;
    public static ConfigEntry<ControllerHotkeyButton> ConfigControllerFreeCamToggle;
    public static ConfigEntry<ControllerHotkeyButton> ConfigControllerFixedFreeCamToggle;
    public static ConfigEntry<ControllerHotkeyButton> ConfigControllerTimeStopToggle;
    public static ConfigEntry<ControllerHotkeyButton> ConfigControllerScreenshotToggle;
    public static ConfigEntry<bool> ConfigCostumeChangerEnabled;
    public static ConfigEntry<UnityEngine.InputSystem.Key> ConfigCostumeChangerHotkey;
    public static ConfigEntry<bool> ConfigRespectGameCostumeOverride;

    private GameObject freeCamObject;
    private Camera freeCam;
    private Camera originalCam;
    private FreeCameraController controller;
    private readonly Dictionary<EventSystem, bool> eventSystemNavigationStates = new();
    private readonly Dictionary<Canvas, bool> canvasEnabledStates = new();
    private bool isGameUiSuppressed;
    private float previousTimeScale = 1f;
    private bool isFreeCamOverlayVisible = true;
    private bool isCapturingScreenshot;
    private static float suppressGameInputUntilUnscaledTime = -1f;
    private const float ControllerShortcutSuppressDuration = 0.18f;
    private static readonly string ScreenshotDirectory = Path.Combine(Paths.BepInExRootPath, "screenshots",
        MyPluginInfo.PLUGIN_GUID);
    public static bool isFreeCamActive = false;
    public static bool isFixedFreeCam = false;
    public static bool isTimeStopped = false;

    internal new static ManualLogSource Logger;

    private void Awake()
    {
        Instance = this;
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

        ConfigExtraWidth = Config.Bind(
            "Resolution",
            "ExtraWidth",
            2560,
            "ゲーム内 OptionMenu の DISPLAY 項目に追加される拡張解像度（ウィンドウモード）の幅。\n" +
            "既定 2560（WQHD）。16:9 を推奨。");

        ConfigExtraHeight = Config.Bind(
            "Resolution",
            "ExtraHeight",
            1440,
            "ゲーム内 OptionMenu の DISPLAY 項目に追加される拡張解像度（ウィンドウモード）の高さ。\n" +
            "既定 1440（WQHD）。16:9 を推奨。");

        ConfigExtraActive = Config.Bind(
            "Internal",
            "ExtraActive",
            false,
            "【内部状態】ユーザーが OptionMenu で拡張解像度 (ExtraWidth×ExtraHeight) を\n" +
            "選択中かどうかを記録します。ゲーム内オプション操作時に自動更新されます。\n" +
            "手動変更しないでください。");

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
            10f,
            "フリーカメラのマウス感度");

        ConfigSpeed = Config.Bind(
            "Camera",
            "Speed",
            2.5f,
            "フリーカメラの移動速度");

        ConfigFastSpeed = Config.Bind(
            "Camera",
            "FastSpeed",
            20f,
            "フリーカメラの高速移動速度（Shift）");

        ConfigSlowSpeed = Config.Bind(
            "Camera",
            "SlowSpeed",
            0.5f,
            "フリーカメラの低速移動速度（Ctrl）");

        ConfigControllerTriggerDeadzone = Config.Bind(
            "Camera",
            "ControllerTriggerDeadzone",
            0.35f,
            "フリーカメラで ZL/ZR を押下扱いにするしきい値。トリガーの遊びやドリフトがある場合は上げてください。");

        ConfigHideGameUiInFreeCam = Config.Bind(
            "Camera",
            "HideGameUiInFreeCam",
            true,
            "true にするとフリーカメラ中にゲーム本体の UI(Canvas) を非表示にします。");

        ConfigTimeStopToggleKey = Config.Bind(
            "Camera",
            "TimeStopToggleKey",
            Key.T,
            "フリーカメラ中の時間停止 ON/OFF に使うキーボードキー。既定 T。");

        ConfigScreenshotKey = Config.Bind(
            "Camera",
            "ScreenshotKey",
            Key.P,
            "フリーカメラ中のスクリーンショット保存に使うキーボードキー。既定 P。");

        ConfigControllerEnabled = Config.Bind(
            "Camera",
            "ControllerEnabled",
            true,
            "true にするとフリーカメラの切り替えと操作にゲームパッド入力を使用できます。");

        ConfigControllerModifier = Config.Bind(
            "Camera",
            "ControllerToggleModifier",
            ControllerHotkeyButton.Select,
            "フリーカメラ切り替え用コントローラ修飾ボタン。既定 Select。");

        ConfigControllerFreeCamToggle = Config.Bind(
            "Camera",
            "ControllerToggleFreeCam",
            ControllerHotkeyButton.Y,
            "フリーカメラ ON/OFF に使うコントローラボタン。既定 Y。");

        ConfigControllerFixedFreeCamToggle = Config.Bind(
            "Camera",
            "ControllerToggleFixedFreeCam",
            ControllerHotkeyButton.X,
            "フリーカメラ固定 ON/OFF に使うコントローラボタン。既定 X。");

        ConfigControllerTimeStopToggle = Config.Bind(
            "Camera",
            "ControllerToggleTimeStop",
            ControllerHotkeyButton.B,
            "フリーカメラ中の時間停止 ON/OFF に使うコントローラボタン。既定 B。");

        ConfigControllerScreenshotToggle = Config.Bind(
            "Camera",
            "ControllerToggleScreenshot",
            ControllerHotkeyButton.A,
            "フリーカメラ中のスクリーンショット保存に使うコントローラボタン。既定 A。修飾ボタンと同時押しです。");

        ConfigDisableStockings = Config.Bind(
            "Appearance",
            "DisableStockings",
            false,
            "true にするとキャストのストッキングを非表示にします。");

        ConfigContinueVoiceOnTap = Config.Bind(
            "Conversation",
            "ContinueVoiceOnTap",
            false,
            "true にすると会話送り（タップ／オート／スキップ）時にボイスが途中停止せず、\n" +
            "次の台詞のボイス再生で自然に上書きされるか、ボイスが最後まで再生されるようになります。");

        ConfigChekiHighResEnabled = Config.Bind(
            "Cheki",
            "HighResEnabled",
            false,
            "true にするとチェキ（撮影写真）の保存解像度を Size で指定した値に変更します。\n" +
            "false の場合は本体既定（320x320）のままです。\n" +
            "互換性: 本体セーブには常に 320x320 版も保存されるため、MOD を外しても主セーブは破損しません。\n" +
            "高解像度版は MOD 独自のサイドカーファイル（主セーブ + .exmod）に格納されます。\n" +
            "スロット対応: セーブスロット単位で高解像度データを分離管理します。\n" +
            "  別スロットに切り替えた際も各スロットの高解像度チェキが正しく表示されます。\n" +
            "副作用: 高解像度化でメモリ／セーブサイズが増加します（1024 時: 約 48MB/12 枚）。");

        ConfigChekiSize = Config.Bind(
            "Cheki",
            "Size",
            1024,
            "チェキ画像の正方形サイズ（ピクセル）。64〜2048 にクランプされます。既定 1024。\n" +
            "HighResEnabled が false の場合は無視されます（本体既定の 320 が使用されます）。\n" +
            "PNG で実測 1〜5MB/枚 程度に収まります。");

        ConfigChekiFormat = Config.Bind(
            "Cheki",
            "ImageFormat",
            ChekiImageFormat.PNG,
            "ExSave に格納する画像フォーマット。PNG / JPG。既定 PNG。\n" +
            "  PNG : 無劣化圧縮。サイズ 1/5〜1/20・エンコード 50〜200ms/枚。既定\n" +
            "  JPG : 劣化圧縮。サイズ 1/20〜1/50・エンコード 30〜100ms/枚\n" +
            "エンコードはシャッター時に 1 度のみ走ります。\n" +
            "読み込みは magic byte による自動判別です。");

        ConfigChekiJpgQuality = Config.Bind(
            "Cheki",
            "JpgQuality",
            90,
            "ImageFormat=JPG のときの品質（1〜100）。既定 90。値が小さいほどサイズは小さく画質は粗くなります。");

        ConfigEndingChekiSlideshow = Config.Bind(
            "Ending",
            "ChekiSlideshow",
            true,
            "true にするとエンディング中に撮影済みのチェキをスライドショーで表示します。");

        ConfigCastOrderEnabled = Config.Bind(
            "CastOrder",
            "Enabled",
            false,
            "true にするとバーに入る前にキャストの出勤順序を変更できます。\n" +
            "F1 キーで編集モードを開始し、数字キー（1〜5）でキャストを選択・入れ替えます。");

        ConfigUltimateSurvivorEnabled = Config.Bind(
            "Cheat",
            "UltimateSurvivor",
            false,
            "true にすると鉄骨渡りミニゲームで落下しなくなります。");

        ConfigGambleAlwaysWinEnabled = Config.Bind(
            "Cheat",
            "GambleAlwaysWin",
            false,
            "true にするとギャンブルで負けなくなります。");

        ConfigCheatEnabled = Config.Bind(
            "Cheat",
            "Enabled",
            false,
            "true にすると会話選択肢・ドリンク・フードの正解をゲーム内に表示します。\n" +
            "【会話選択肢】選択肢テキストの先頭に記号が追加されます。\n" +
            "  ★ : 好感度UP（正解）\n" +
            "  ▼ : 好感度DOWN（酔い選択肢だが現在の状況では効果なし）\n" +
            "【ドリンク・フード】アイテムの背景色が変化します。\n" +
            "  緑 : キャストのお気に入り（AddFavoriteLikability > 0）\n" +
            "  黄 : 今日の旬アイテム（ボーナスあり）\n" +
            "  赤 : キャストが嫌いなもの（AddFavoriteLikability < 0）");

        ConfigCostumeChangerEnabled = Config.Bind(
            "CostumeChanger",
            "Enabled",
            true,
            "true にすると衣装変更 UI とパッチを有効化します。");

        ConfigCostumeChangerHotkey = Config.Bind(
            "CostumeChanger",
            "Hotkey",
            UnityEngine.InputSystem.Key.F7,
            "衣装変更 UI の表示トグルキー（UnityEngine.InputSystem.Key enum 名で指定）。");

        ConfigRespectGameCostumeOverride = Config.Bind(
            "CostumeChanger",
            "RespectGameCostumeOverride",
            true,
            "true のとき、ゲーム本体が CostumeOverride を ForceXxx に設定している間は MOD 側の override を一時停止します。");

        Logger = base.Logger;
        PatchLogger.Initialize(Logger);
        StartCoroutine(UpdateChecker.Check());
        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();
        // async ステートマシンは Harmony でパッチできないため LateUpdate 方式で補正
        Patches.CameraZoomPatch.Initialize(gameObject);
        Patches.CastOrderPatch.Initialize(gameObject);
        Patches.CostumeChanger.CostumeChangerPatch.Initialize(gameObject);
        PatchLogger.LogInfo($"プラグイン起動: {MyPluginInfo.PLUGIN_GUID} v{MyPluginInfo.PLUGIN_VERSION}");
        PatchLogger.LogInfo($"解像度パッチを適用しました: {Plugin.ConfigWidth.Value}x{Plugin.ConfigHeight.Value}");
        PatchLogger.LogInfo($"アンチエイリアシング設定: {Plugin.ConfigAntiAliasing.Value}");
    }

    private void OnDestroy()
    {
        DisableTimeStopIfNeeded();

        if (ReferenceEquals(Instance, this))
            Instance = null;
    }

    private void Update()
    {
        if (Keyboard.current?[Key.F4].wasPressedThisFrame == true)
            Config.Reload();

        bool ctrlPressed = Keyboard.current?.leftCtrlKey.isPressed == true || Keyboard.current?.rightCtrlKey.isPressed == true;

        if (ctrlPressed && Keyboard.current?[Key.F5].wasPressedThisFrame == true)
            ToggleFreeCamOverlay();
        else if (Keyboard.current?[Key.F5].wasPressedThisFrame == true)
            ToggleFreeCam();

        if (Keyboard.current?[Key.F6].wasPressedThisFrame == true)
            ToggleFixedFreeCam();

        if (isFreeCamActive && Keyboard.current?[ConfigTimeStopToggleKey.Value].wasPressedThisFrame == true)
            ToggleTimeStop();

        if (isFreeCamActive && Keyboard.current?[ConfigScreenshotKey.Value].wasPressedThisFrame == true)
            CaptureFreeCamScreenshot();

        RefreshGameUiSuppression();

        if (!ConfigControllerEnabled.Value)
            return;

        if (IsControllerComboTriggered(ConfigControllerModifier.Value, ConfigControllerFreeCamToggle.Value))
        {
            SuppressGameInputTemporarily();
            ToggleFreeCam();
        }

        if (isFreeCamActive &&
            IsControllerComboTriggered(ConfigControllerModifier.Value, ConfigControllerFixedFreeCamToggle.Value))
        {
            SuppressGameInputTemporarily();
            ToggleFixedFreeCam();
        }

        if (isFreeCamActive && IsControllerComboTriggered(ConfigControllerModifier.Value, ControllerHotkeyButton.Start))
        {
            SuppressGameInputTemporarily();
            ToggleFreeCamOverlay();
        }

        if (isFreeCamActive && !isFixedFreeCam &&
            IsControllerComboTriggered(ConfigControllerModifier.Value, ConfigControllerTimeStopToggle.Value))
        {
            SuppressGameInputTemporarily();
            ToggleTimeStop();
        }

        if (isFreeCamActive &&
            IsControllerComboTriggered(ConfigControllerModifier.Value, ConfigControllerScreenshotToggle.Value))
        {
            SuppressGameInputTemporarily();
            CaptureFreeCamScreenshot();
        }
    }

    private void OnGUI()
    {
        if (!isFreeCamActive || !isFreeCamOverlayVisible)
            return;

        string controllerFreeCamLabel = GetControllerBindingLabel(ConfigControllerModifier.Value,
            ConfigControllerFreeCamToggle.Value);
        string controllerOverlayLabel = GetControllerBindingLabel(ConfigControllerModifier.Value,
            ControllerHotkeyButton.Start);
        string controllerFixedLabel = GetControllerBindingLabel(ConfigControllerModifier.Value,
            ConfigControllerFixedFreeCamToggle.Value);
        string controllerTimeStopLabel = GetControllerBindingLabel(ConfigControllerModifier.Value,
            ConfigControllerTimeStopToggle.Value);
        string controllerScreenshotLabel = GetControllerBindingLabel(ConfigControllerModifier.Value,
            ConfigControllerScreenshotToggle.Value);

        GUI.color = Color.green;
        GUI.Label(new Rect(10, 10, 1200, 30),
            $"Free Camera: ON (F5 / {controllerFreeCamLabel}=OFF, Ctrl+F5 / {controllerOverlayLabel}=HIDE)");
        GUI.color = Color.yellow;
        GUI.Label(new Rect(10, 40, 900, 30),
            $"Fixed Mode: {(isFixedFreeCam ? "ON" : "OFF")} (F6 / {controllerFixedLabel}=TOGGLE)");
        GUI.color = Color.cyan;
        GUI.Label(new Rect(10, 70, 900, 30),
            $"Time Stop: {(isTimeStopped ? "ON" : "OFF")} (T / {controllerTimeStopLabel}=TOGGLE)");
        GUI.color = Color.magenta;
        GUI.Label(new Rect(10, 100, 900, 30),
            $"Screenshot: {ConfigScreenshotKey.Value} / {controllerScreenshotLabel}=SAVE PNG");
        GUI.color = Color.white;
        GUI.Label(new Rect(10, 130, 1300, 30),
            "Move: Arrow/WASD or Left Stick, Up/Down: E/Q or ZR/ZL, Look: Mouse or Right Stick, Speed: Shift/Ctrl or R/L");
        GUI.color = Color.white;
    }

    private void ToggleFreeCam()
    {
        isFreeCamActive = !isFreeCamActive;

        if (isFreeCamActive)
            CreateFreeCam();
        else
        {
            DisableTimeStopIfNeeded();
            DestroyFreeCam();
            isFixedFreeCam = false;
        }

        RefreshGameUiSuppression(force: true);

        PatchLogger.LogInfo($"フリーカメラ: {(isFreeCamActive ? "ON" : "OFF")}");
    }

    private void DisableFreeCamForSystemUi(string reason)
    {
        if (!isFreeCamActive)
            return;

        DisableTimeStopIfNeeded();
        DestroyFreeCam();
        isFreeCamActive = false;
        isFixedFreeCam = false;
        RefreshGameUiSuppression(force: true);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        PatchLogger.LogInfo($"フリーカメラを自動解除しました: {reason}");
    }

    internal static void DisableFreeCamForSystemUiIfNeeded(string reason)
    {
        Instance?.DisableFreeCamForSystemUi(reason);
    }

    private static void SuppressGameInputTemporarily()
    {
        suppressGameInputUntilUnscaledTime = Time.unscaledTime + ControllerShortcutSuppressDuration;
    }

    internal static bool ShouldSuppressGameInput()
    {
        return Time.unscaledTime < suppressGameInputUntilUnscaledTime;
    }

    private void ToggleFixedFreeCam()
    {
        if (isFreeCamActive)
        {
            isFixedFreeCam = !isFixedFreeCam;
            if (isFixedFreeCam)
                DisableTimeStopIfNeeded();
            RefreshGameUiSuppression(force: true);
            PatchLogger.LogInfo($"フリーカメラ固定モード: {(isFixedFreeCam ? "ON" : "OFF")}");
        }
    }

    private void ToggleFreeCamOverlay()
    {
        if (!isFreeCamActive)
            return;

        isFreeCamOverlayVisible = !isFreeCamOverlayVisible;
        PatchLogger.LogInfo($"フリーカメラ表示: {(isFreeCamOverlayVisible ? "ON" : "OFF")}");
    }

    private void ToggleTimeStop()
    {
        if (!isFreeCamActive || isFixedFreeCam)
            return;

        if (isTimeStopped)
        {
            DisableTimeStopIfNeeded();
            PatchLogger.LogInfo("時間停止: OFF");
            return;
        }

        previousTimeScale = Time.timeScale;
        Time.timeScale = 0f;
        isTimeStopped = true;
        PatchLogger.LogInfo("時間停止: ON");
    }

    private void DisableTimeStopIfNeeded()
    {
        if (!isTimeStopped)
            return;

        Time.timeScale = previousTimeScale;
        isTimeStopped = false;
    }

    private void CaptureFreeCamScreenshot()
    {
        if (!isFreeCamActive || freeCam == null || isCapturingScreenshot)
            return;

        StartCoroutine(CaptureFreeCamScreenshotCoroutine());
    }

    private System.Collections.IEnumerator CaptureFreeCamScreenshotCoroutine()
    {
        isCapturingScreenshot = true;
        Camera captureCam = freeCam;
        yield return new WaitForEndOfFrame();

        if (!isFreeCamActive || captureCam == null)
        {
            isCapturingScreenshot = false;
            yield break;
        }

        string path = null;
        RenderTexture rt = null;
        Texture2D tex = null;
        RenderTexture previousActive = RenderTexture.active;
        RenderTexture previousTarget = captureCam.targetTexture;

        try
        {
            int width = Mathf.Max(1, captureCam.pixelWidth);
            int height = Mathf.Max(1, captureCam.pixelHeight);

            Directory.CreateDirectory(ScreenshotDirectory);
            path = Path.Combine(ScreenshotDirectory,
                $"freecam_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");

            rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            tex = new Texture2D(width, height, TextureFormat.RGBA32, false);

            captureCam.targetTexture = rt;
            captureCam.Render();

            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
            tex.Apply(false, false);

            File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
            PatchLogger.LogInfo($"フリーカメラスクリーンショットを保存しました: {path}");
        }
        catch (Exception ex)
        {
            PatchLogger.LogError($"フリーカメラスクリーンショット保存失敗: {ex.Message}");
        }
        finally
        {
            if (captureCam != null)
                captureCam.targetTexture = previousTarget;

            RenderTexture.active = previousActive;

            if (rt != null)
                Destroy(rt);

            if (tex != null)
                Destroy(tex);

            isCapturingScreenshot = false;
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

        dstData.renderPostProcessing = srcData.renderPostProcessing;
        dstData.antialiasing = srcData.antialiasing;
        dstData.antialiasingQuality = srcData.antialiasingQuality;
        dstData.stopNaN = srcData.stopNaN;
        dstData.dithering = srcData.dithering;
        dstData.renderShadows = srcData.renderShadows;
        dstData.volumeLayerMask = srcData.volumeLayerMask;
        dstData.volumeTrigger = srcData.volumeTrigger;
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

    private void RefreshGameUiSuppression(bool force = false)
    {
        bool shouldSuppress = isFreeCamActive && !isFixedFreeCam && !ShouldExposeGameUiDuringFreeCam();
        if (!force && shouldSuppress == isGameUiSuppressed)
            return;

        isGameUiSuppressed = shouldSuppress;

        EventSystem[] eventSystems = FindObjectsByType<EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        Canvas[] canvases = FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        if (!shouldSuppress)
        {
            foreach (var pair in eventSystemNavigationStates)
            {
                if (pair.Key != null)
                    pair.Key.sendNavigationEvents = pair.Value;
            }

            eventSystemNavigationStates.Clear();

            foreach (var pair in canvasEnabledStates)
            {
                if (pair.Key != null)
                    pair.Key.enabled = pair.Value;
            }

            canvasEnabledStates.Clear();
            return;
        }

        foreach (var eventSystem in eventSystems)
        {
            if (eventSystem == null)
                continue;

            if (!eventSystemNavigationStates.ContainsKey(eventSystem))
                eventSystemNavigationStates[eventSystem] = eventSystem.sendNavigationEvents;

            eventSystem.sendNavigationEvents = false;
            eventSystem.SetSelectedGameObject(null);
        }

        if (!ConfigHideGameUiInFreeCam.Value)
            return;

        foreach (var canvas in canvases)
        {
            if (!ShouldHideCanvas(canvas))
                continue;

            if (!canvasEnabledStates.ContainsKey(canvas))
                canvasEnabledStates[canvas] = canvas.enabled;

            canvas.enabled = false;
        }
    }

    private static bool ShouldExposeGameUiDuringFreeCam()
    {
        var gbSystem = GBSystem.Instance;
        if (gbSystem == null)
            return false;

        if (gbSystem.IsInConfirmQuit || gbSystem.IsPauseMenuActive())
            return true;

        var confirmDialog = gbSystem.GetConfirmDialog();
        return confirmDialog != null && confirmDialog.IsActive();
    }

    private bool ShouldHideCanvas(Canvas canvas)
    {
        if (canvas == null)
            return false;

        if (freeCamObject != null && canvas.transform.IsChildOf(freeCamObject.transform))
            return false;

        return canvas.renderMode != RenderMode.WorldSpace;
    }

    private static bool IsControllerComboTriggered(ControllerHotkeyButton modifier, ControllerHotkeyButton action)
    {
        if (action == ControllerHotkeyButton.None)
            return false;

        if (modifier == ControllerHotkeyButton.None || modifier == action)
            return IsControllerButtonTriggered(action);

        return IsControllerButtonPressing(modifier) && IsControllerButtonTriggered(action);
    }

    private static bool IsControllerButtonTriggered(ControllerHotkeyButton button)
    {
        return IsRawGamepadButtonTriggered(button);
    }

    private static bool IsControllerButtonPressing(ControllerHotkeyButton button)
    {
        return IsRawGamepadButtonPressing(button);
    }

    internal static bool IsControllerButtonHeld(ControllerHotkeyButton button)
    {
        if (!ConfigControllerEnabled.Value)
            return false;

        if (button == ControllerHotkeyButton.ZL || button == ControllerHotkeyButton.ZR)
            return ReadControllerTriggerValue(button) >= ConfigControllerTriggerDeadzone.Value;

        return IsControllerButtonPressing(button);
    }

    internal static Vector2 ReadControllerLeftStick()
    {
        return ReadRawGamepadStick(gamepad => gamepad.leftStick.ReadValue());
    }

    internal static Vector2 ReadControllerRightStick()
    {
        return ReadRawGamepadStick(gamepad => gamepad.rightStick.ReadValue());
    }

    internal static float ReadControllerTriggerValue(ControllerHotkeyButton button)
    {
        return ReadRawGamepadTrigger(button);
    }

    private static bool IsRawGamepadButtonTriggered(ControllerHotkeyButton button)
    {
        foreach (var gamepad in Gamepad.all)
        {
            var control = GetRawGamepadButton(gamepad, button);
            if (control?.wasPressedThisFrame == true)
                return true;
        }

        return false;
    }

    private static bool IsRawGamepadButtonPressing(ControllerHotkeyButton button)
    {
        foreach (var gamepad in Gamepad.all)
        {
            var control = GetRawGamepadButton(gamepad, button);
            if (control?.isPressed == true)
                return true;
        }

        return false;
    }

    private static Vector2 ReadRawGamepadStick(System.Func<Gamepad, Vector2> selector)
    {
        foreach (var gamepad in Gamepad.all)
        {
            Vector2 value = selector(gamepad);
            if (value.sqrMagnitude > 0f)
                return value;
        }

        return Vector2.zero;
    }

    private static float ReadRawGamepadTrigger(ControllerHotkeyButton button)
    {
        foreach (var gamepad in Gamepad.all)
        {
            float value = button switch
            {
                ControllerHotkeyButton.ZL => gamepad.leftTrigger.ReadValue(),
                ControllerHotkeyButton.ZR => gamepad.rightTrigger.ReadValue(),
                _ => 0f,
            };

            if (value > 0f)
                return value;
        }

        return 0f;
    }

    private static ButtonControl GetRawGamepadButton(Gamepad gamepad, ControllerHotkeyButton button)
    {
        if (gamepad == null)
            return null;

        return button switch
        {
            ControllerHotkeyButton.A => gamepad.buttonSouth,
            ControllerHotkeyButton.B => gamepad.buttonEast,
            ControllerHotkeyButton.X => gamepad.buttonWest,
            ControllerHotkeyButton.Y => gamepad.buttonNorth,
            ControllerHotkeyButton.L => gamepad.leftShoulder,
            ControllerHotkeyButton.R => gamepad.rightShoulder,
            ControllerHotkeyButton.ZL => gamepad.leftTrigger,
            ControllerHotkeyButton.ZR => gamepad.rightTrigger,
            ControllerHotkeyButton.Start => gamepad.startButton,
            ControllerHotkeyButton.Select => GetRawSelectButton(gamepad),
            _ => null,
        };
    }

    private static ButtonControl GetRawSelectButton(Gamepad gamepad)
    {
        var dualShockGamepad = gamepad as DualShockGamepad;
        if (dualShockGamepad != null)
            return dualShockGamepad.touchpadButton;

        return gamepad.selectButton;
    }

    private static string GetControllerBindingLabel(ControllerHotkeyButton modifier, ControllerHotkeyButton action)
    {
        if (action == ControllerHotkeyButton.None)
            return "Disabled";

        if (modifier == ControllerHotkeyButton.None || modifier == action)
            return action.ToString();

        return $"{modifier}+{action}";
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
        if (!Plugin.isFreeCamActive || !Plugin.ShouldSuppressGameInput())
            return true;

        __result = false;
        return false;
    }

    [HarmonyPatch(typeof(GBInput), "GetStickValue")]
    [HarmonyPrefix]
    private static bool SuppressStick(InputAction stick, ref Vector2 __result)
    {
        if (!Plugin.isFreeCamActive || !Plugin.ShouldSuppressGameInput())
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
        if (!Plugin.isFreeCamActive || !Plugin.ShouldSuppressGameInput())
            return true;

        __result = Vector2.zero;
        return false;
    }

    private static bool TrySuppress(InputAction button, ref bool result)
    {
        if (!Plugin.isFreeCamActive || !Plugin.ShouldSuppressGameInput())
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
/// カーソルが Wardrobe パネル矩形内にある間は GBInput.isMouseTriggered を false に差し替え、
/// panel 裏のクリックで ADV が進行したり背後の uGUI ボタンが反応するのを防ぐ。
/// panel 外クリックは素通しするため、ADV の進行や他操作は通常通り動作する。
/// </summary>
[HarmonyPatch(typeof(GBInput), "isMouseTriggered")]
public class SuppressClickOverWardrobePatch
{
    private static bool Prefix(ref bool __result)
    {
        if (!Patches.CostumeChanger.UI.CostumePickerController.ShouldSuppressGameInput()) return true;
        __result = false;
        return false; // 元実装 (Mouse.current.leftButton.wasPressedThisFrame) をスキップ
    }
}

/// <summary>
/// カーソルが Wardrobe パネル矩形内にある間は GBInput.ScrollAxis を 0 に差し替え、
/// panel 上でのマウスホイールが ADV/BackLog 呼び出し等の本体操作に流れるのを防ぐ。
/// UI Toolkit 内部の ScrollView は EventSystem 側から独立して WheelEvent を受け取るため
/// この差し替えでは影響を受けず、panel 内スクロールは従来通り動作する。
/// HarmonyX の MethodType.Getter より確実な AccessTools.PropertyGetter で target を明示する。
/// </summary>
[HarmonyPatch]
public class SuppressScrollOverWardrobePatch
{
    static System.Reflection.MethodBase TargetMethod()
        => AccessTools.PropertyGetter(typeof(GBInput), nameof(GBInput.ScrollAxis));

    private static bool Prefix(ref float __result)
    {
        if (!Patches.CostumeChanger.UI.CostumePickerController.ShouldSuppressGameInput()) return true;
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
        if (!Patches.CostumeChanger.UI.CostumePickerController.ShouldSuppressGameInput(button?.name)) return true;
        __result = false;
        return false;
    }
}

/// <summary>
/// カーソルが Wardrobe パネル矩形内にある間は GBInput.isTriggeredR を false に差し替え、
/// リピート入力がゲーム側に流れるのを防ぐ。
/// isTriggeredR はボタン情報を持たないため全アクションを対象とする。
/// </summary>
[HarmonyPatch(typeof(GBInput), "isTriggeredR")]
public class SuppressKeyRepeatOverWardrobePatch
{
    private static bool Prefix(ref bool __result)
    {
        if (!Patches.CostumeChanger.UI.CostumePickerController.ShouldSuppressGameInput()) return true;
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
