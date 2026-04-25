#if BIE6
using BepInEx.Unity.Mono;
#endif

using System;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BunnyGarden2FixMod.Utils;
using GB;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

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

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    private static Plugin Instance;

    // Animation
    public static ConfigEntry<bool> ConfigMoreTalkReactions;

    // Appearance
    public static ConfigEntry<bool> ConfigDisableStockings;

    // Camera
    public static ConfigEntry<float> ConfigSensitivity;
    public static ConfigEntry<float> ConfigSpeed;
    public static ConfigEntry<float> ConfigFastSpeed;
    public static ConfigEntry<float> ConfigSlowSpeed;
    public static ConfigEntry<bool> ConfigHideGameUiInFreeCam;
    public static ConfigEntry<bool> ConfigControllerEnabled;
    public static HotkeyConfig ConfigFixedFreeCamToggle;
    public static HotkeyConfig ConfigFreeCamToggle;

    // Cheat
    public static ConfigEntry<bool> ConfigCastOrder;
    public static ConfigEntry<bool> ConfigGambleAlwaysWinEnabled;
    public static ConfigEntry<bool> ConfigCheatLikability;
    public static ConfigEntry<bool> ConfigUltimateSurvivorEnabled;

    // Cheki
    public static ConfigEntry<bool> ConfigChekiHighResEnabled;
    public static ConfigEntry<ChekiImageFormat> ConfigChekiFormat;
    public static ConfigEntry<int> ConfigChekiJpgQuality;
    public static ConfigEntry<int> ConfigChekiSize;

    // Conversation
    public static ConfigEntry<bool> ConfigContinueVoiceOnTap;

    // CostumeChanger
    public static ConfigEntry<bool> ConfigCostumeChangerEnabled;
    public static HotkeyConfig ConfigCostumeChangerShow;
    public static ConfigEntry<bool> ConfigRespectGameCostumeOverride;
    public static ConfigEntry<bool> ConfigSwimWearStocking;
    public static ConfigEntry<float> ConfigStockingOffset;
    public static ConfigEntry<float> ConfigStockingSkinShrink;
    public static ConfigEntry<float> ConfigStockingSkinFalloffRadius;
    public static ConfigEntry<float> ConfigStockingShapeFalloffRadius;

    // Ending
    public static ConfigEntry<bool> ConfigEndingChekiSlideshow;

    // General
    public static HotkeyConfig ConfigCaptureScreenshot;
    public static ConfigEntry<bool> ConfigSteamLaunchCheck;
    public static HotkeyConfig ConfigOverlayToggle;

    // Graphics
    public static ConfigEntry<int> ConfigWidth;
    public static ConfigEntry<int> ConfigHeight;
    public static ConfigEntry<int> ConfigExtraWidth;
    public static ConfigEntry<int> ConfigExtraHeight;
    public static ConfigEntry<int> ConfigFrameRate;
    public static ConfigEntry<AntiAliasingType> ConfigAntiAliasing;
    public static ConfigEntry<bool> ConfigDisableChromaticAberration;

    // HideUI
    public static ConfigEntry<bool> ConfigHideUIEnabled;
    public static ConfigEntry<bool> ConfigHideMoneyInSpecialScenes;
    public static ConfigEntry<bool> ConfigHideButtonGuide;
    public static ConfigEntry<bool> ConfigHideLikabilityGauge;

    // Input
    public static ConfigEntry<float> ConfigControllerTriggerDeadzone;
    public static ConfigEntry<ControllerButton> ConfigControllerModifier;

    // Internal
    public static ConfigEntry<bool> ConfigExtraActive;

    // Time
    public static HotkeyConfig ConfigTimeStopToggle;
    public static HotkeyConfig ConfigFrameAdvance;
    public static HotkeyConfig ConfigFastForward;
    public static ConfigEntry<float> ConfigFastForwardSpeed;

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

        ConfigWidth = Config.Bind(
            "Graphics",
            "Width",
            1920,
            "解像度の幅（横）を指定します");

        ConfigHeight = Config.Bind(
            "Graphics",
            "Height",
            1080,
            "解像度の高さ（縦）を指定します");

        ConfigExtraWidth = Config.Bind(
            "Graphics",
            "ExtraWidth",
            2560,
            "ゲーム内 OptionMenu の DISPLAY 項目に追加される拡張解像度（ウィンドウモード）の幅。\n" +
            "既定 2560（WQHD）。16:9 を推奨。");

        ConfigExtraHeight = Config.Bind(
            "Graphics",
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
            "Graphics",
            "FrameRate",
            60,
            "フレームレート上限を指定します。-1にすると上限を撤廃します。");

        ConfigAntiAliasing = Config.Bind(
            "Graphics",
            "AntiAliasingType",
            AntiAliasingType.MSAA8x,
            "アンチエイリアシングの種類を指定します。右の方ほど画質が良くなりますが、動作が重くなります。Off / FXAA / TAA / MSAA2x / MSAA4x / MSAA8x");

        ConfigDisableChromaticAberration = Config.Bind(
            "Graphics",
            "DisableChromaticAberration",
            false,
            "true にすると色収差エフェクト(画面の端のほうがにじんで見える効果)を無効化します。");

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

        ConfigMoreTalkReactions = Config.Bind(
            "Animation",
            "MoreTalkReactions",
            false,
            "true にすると、バーの背景キャスト2人の会話リアクションモーションがより多様になります。");

        ConfigControllerTriggerDeadzone = Config.Bind(
            "Input",
            "ControllerTriggerDeadzone",
            0.35f,
            "フリーカメラで ZL/ZR を押下扱いにするしきい値。トリガーの遊びやドリフトがある場合は上げてください。");

        ConfigHideGameUiInFreeCam = Config.Bind(
            "Camera",
            "HideGameUiInFreeCam",
            true,
            "true にするとフリーカメラ中にゲーム本体の UI(Canvas) を非表示にします。");

        ConfigControllerEnabled = Config.Bind(
            "Camera",
            "ControllerEnabled",
            true,
            "true にするとフリーカメラの操作にゲームパッド入力を使用できます。");

        ConfigControllerModifier = Config.Bind(
            "Input",
            "ControllerModifier",
            ControllerButton.Select,
            "コントローラ入力修飾ボタン。");

        ConfigOverlayToggle = new HotkeyConfig(Config,
            "General",
            "ToggleOverlay",
            Key.F12,
            ControllerButton.Start,
            "ガイドオーバーレイの表示/非表示を切り替えるホットキー。");

        ConfigFreeCamToggle = new HotkeyConfig(Config,
            "Camera",
            "ToggleFreeCam",
            Key.F5,
            ControllerButton.Y,
            "フリーカメラ ON/OFF に使うホットキー。");

        ConfigFixedFreeCamToggle = new HotkeyConfig(Config,
            "Camera",
            "ToggleFixedFreeCam",
            Key.F6,
            ControllerButton.X,
            "フリーカメラ固定 ON/OFF に使うホットキー。");

        ConfigTimeStopToggle = new HotkeyConfig(Config,
            "Time",
            "ToggleTimeStop",
            Key.T,
            ControllerButton.B,
            "時間停止 ON/OFF に使うホットキー。");

        ConfigFrameAdvance = new HotkeyConfig(Config,
            "Time",
            "FrameAdvance",
            Key.F,
            ControllerButton.None,
            "1フレーム進めるホットキー。これを押すと、1フレームだけ時間が進みます。");

        ConfigFastForward = new HotkeyConfig(Config,
            "Time",
            "FastForward",
            Key.G,
            ControllerButton.None,
            "時間を早送りするホットキー。これを押すと、時間が通常より速く進みます。");

        ConfigFastForwardSpeed = Config.Bind(
            "Time",
            "FastForwardSpeed",
            10f,
            "時間を早送りするホットキーを押している間の時間の進む速さの倍率。");

        ConfigCaptureScreenshot = new HotkeyConfig(Config,
            "General",
            "CaptureScreenshot",
            Key.P,
            ControllerButton.A,
            "スクリーンショット保存に使うホットキー。");

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

        ConfigCastOrder = Config.Bind(
            "Cheat",
            "CastOrder",
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

        ConfigCheatLikability = Config.Bind(
            "Cheat",
            "Likability",
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

        ConfigCostumeChangerShow = new HotkeyConfig(Config,
            "CostumeChanger",
            "Show",
            Key.F7,
            ControllerButton.None,
            "衣装変更 UI の表示トグルキー。");

        ConfigRespectGameCostumeOverride = Config.Bind(
            "CostumeChanger",
            "RespectGameCostumeOverride",
            true,
            "trueにすると、試着室などゲームが特定の衣装を強制するシーンではMOD側の衣装変更を一時的に停止します。これを有効にすることで、ゲーム内のイベントと衣装の競合を防げます");

        ConfigSteamLaunchCheck = Config.Bind(
            "General",
            "SteamLaunchCheck",
            true,
            "true にすると Steam 外から直接起動された場合に Steam 経由で自動的に再起動します。\n" +
            "デバッグ目的でゲームフォルダに steam_appid.txt（内容: 3443820）を置くと、この機能をバイパスできます。");

        ConfigHideUIEnabled = Config.Bind(
            "HideUI",
            "Enabled",
            true,
            "true にすると一部UIを非表示にできる設定パネルを有効化します。F9キーで開きます。");

        ConfigHideMoneyInSpecialScenes = Config.Bind(
            "HideUI",
            "HideInSpecialScenes",
            true,
            "true にすると旅行シーンおよび特別なシーンで雰囲気をぶち壊す\n" +
            "所持金UIを非表示にします。F9パネルまたはこのコンフィグでON/OFFできます。");

        ConfigHideButtonGuide = Config.Bind(
            "HideUI",
            "HideButtonGuide",
            false,
            "true にすると画面下のボタンガイド（操作ヒント）を常時非表示にします。F9パネルまたはこのコンフィグでON/OFFできます。");

        ConfigHideLikabilityGauge = Config.Bind(
            "HideUI",
            "HideLikabilityGauge",
            false,
            "true にするとラブカウンター（好感度ゲージ）を常時非表示にします。F9パネルまたはこのコンフィグでON/OFFできます。");

        ConfigSwimWearStocking = Config.Bind(
            "CostumeChanger",
            "SwimWearStocking",
            true,
            "true にすると水着コスチューム着用中にストッキングを適用できるようになります。\n" +
            "水着モデルには本来ストッキング用ブレンドシェイプがないため、同キャラの Uniform コスチュームからデータを移植します。");

        ConfigStockingOffset = Config.Bind(
            "CostumeChanger",
            "StockingOffset",
            0f,
            new BepInEx.Configuration.ConfigDescription(
                "水着+ストッキング適用時、stocking 頂点を肌より外側へ保つ最小距離（メートル）。\n" +
                "押し出すと水着の食い込み（タイトな演出）が再現できるが、stocking が水着を貫通する。\n" +
                "0 で無効化。デフォルト 0 (無効)。",
                new BepInEx.Configuration.AcceptableValueRange<float>(0f, 0.01f)));

        ConfigStockingSkinShrink = Config.Bind(
            "CostumeChanger",
            "StockingSkinShrink",
            0.001f,
            new BepInEx.Configuration.ConfigDescription(
                "水着+ストッキング適用時、肌（mesh_skin_lower）の頂点を「stocking 押し出し後表面より内側」に\n" +
                "保つ目標距離（メートル）。stocking と肌が z-fighting している箇所では、まず肌を\n" +
                "stocking 表面まで引っ込めてから、さらにこの距離だけ内側に押し込む。\n" +
                "押し込むと、肌の貫通はなくなるが、水着の食い込み（タイトな演出）が再現できない。\n" +
                "0 で無効化。デフォルト 0.001 (1mm)。",
                new BepInEx.Configuration.AcceptableValueRange<float>(0f, 0.01f)));

        ConfigStockingSkinFalloffRadius = Config.Bind(
            "CostumeChanger",
            "StockingSkinFalloffRadius",
            0.001f,
            new BepInEx.Configuration.ConfigDescription(
                "肌の押し込み量を、隣接 mesh（mesh_skin_upper 等）からの距離で線形フェードさせる半径（メートル）。\n" +
                "距離 0 で押し込み 0、半径以上で 100%。境界での段差を防ぐ。0 で無効化（一様押し込み）。\n" +
                "デフォルト 0.001 (1mm)。",
                new BepInEx.Configuration.AcceptableValueRange<float>(0f, 0.01f)));

        ConfigStockingShapeFalloffRadius = Config.Bind(
            "CostumeChanger",
            "StockingShapeFalloffRadius",
            0.001f,
            new BepInEx.Configuration.ConfigDescription(
                "skin_stocking 系 blendShape (skin_stocking / skin_socks / skin_stocking_lower) の delta 自体を、\n" +
                "隣接 mesh（mesh_skin_upper 等）からの距離で線形フェードさせる半径（メートル）。\n" +
                "距離 0 で blendShape 効果 0、半径以上で 100%。境界（ウエスト等）の段差を解消する。\n" +
                "0 で無効化（blendShape 効果は全頂点 100%）。デフォルト 0.001 (1mm)。",
                new BepInEx.Configuration.AcceptableValueRange<float>(0f, 0.01f)));

        // Steam 外起動を検出した場合は Steam 経由で再起動して即終了
        if (ConfigSteamLaunchCheck.Value && SteamLaunchChecker.CheckAndRelaunchIfNeeded())
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
        Patches.HideMoneyUI.HideMoneyUIController.Initialize(gameObject);
        Patches.CostumeChanger.StockingsDonorLoader.Initialize(gameObject);
        freeCamera = Patches.FreeCamera.FreeCameraManager.Initialize(gameObject);
        Patches.TimeController.Initialize(gameObject);
        PatchLogger.LogInfo($"プラグイン起動: {MyPluginInfo.PLUGIN_GUID} v{MyPluginInfo.PLUGIN_VERSION}");
        PatchLogger.LogInfo($"解像度パッチを適用しました: {Plugin.ConfigWidth.Value}x{Plugin.ConfigHeight.Value}");
        PatchLogger.LogInfo($"アンチエイリアシング設定: {Plugin.ConfigAntiAliasing.Value}");
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

        if (ConfigOverlayToggle.IsTriggered())
            ToggleOverlay();

        if (ConfigCaptureScreenshot.IsTriggered())
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

    public static void SuppressGameInputTemporarily()
    {
        suppressGameInputUntilUnscaledTime = Time.unscaledTime + ControllerShortcutSuppressDuration;
    }

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
        isCapturingScreenshot = true;
        Camera captureCam = FindCurrentCamera();
        yield return new WaitForEndOfFrame();

        if (captureCam == null)
        {
            isCapturingScreenshot = false;
            yield break;
        }

        RenderTexture rt = null;
        Texture2D tex = null;
        RenderTexture previousActive = RenderTexture.active;
        RenderTexture previousTarget = captureCam.targetTexture;

        try
        {
            int width = Mathf.Max(1, captureCam.pixelWidth);
            int height = Mathf.Max(1, captureCam.pixelHeight);

            Directory.CreateDirectory(ScreenshotDirectory);
            string path = Path.Combine(ScreenshotDirectory,
                $"bg2_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");

            rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 8
            };
            tex = new Texture2D(width, height, TextureFormat.RGBA32, false);

            captureCam.targetTexture = rt;
            captureCam.Render();

            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
            tex.Apply(false, false);

            File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
            PatchLogger.LogInfo($"スクリーンショットを保存しました: {path}");
        }
        catch (Exception ex)
        {
            PatchLogger.LogError($"スクリーンショット保存失敗: {ex.Message}");
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
        if (!Patches.FreeCamera.FreeCameraManager.IsActive || !Plugin.ShouldSuppressGameInput())
            return true;

        __result = false;
        return false;
    }

    [HarmonyPatch(typeof(GBInput), "GetStickValue")]
    [HarmonyPrefix]
    private static bool SuppressStick(InputAction stick, ref Vector2 __result)
    {
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
        if (!Patches.FreeCamera.FreeCameraManager.IsActive || !Plugin.ShouldSuppressGameInput())
            return true;

        __result = Vector2.zero;
        return false;
    }

    private static bool TrySuppress(InputAction button, ref bool result)
    {
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
        if (!Patches.CostumeChanger.UI.CostumePickerController.ShouldSuppressGameInput() &&
            !Patches.HideMoneyUI.HideMoneyUIController.ShouldSuppressMouseInput()) return true;
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
        if (!Patches.CostumeChanger.UI.CostumePickerController.ShouldSuppressGameInput() &&
            !Patches.HideMoneyUI.HideMoneyUIController.ShouldSuppressMouseInput()) return true;
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
/// カーソルが Wardrobe / HideUI パネル矩形内にある間は GBInput.isTriggeredR を false に差し替え、
/// リピート入力がゲーム側に流れるのを防ぐ。
/// isTriggeredR はボタン情報を持たないため全アクションを対象とする。
/// </summary>
[HarmonyPatch(typeof(GBInput), "isTriggeredR")]
public class SuppressKeyRepeatOverWardrobePatch
{
    private static bool Prefix(ref bool __result)
    {
        if (!Patches.CostumeChanger.UI.CostumePickerController.ShouldSuppressGameInput() &&
            !Patches.HideMoneyUI.HideMoneyUIController.ShouldSuppressMouseInput()) return true;
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
