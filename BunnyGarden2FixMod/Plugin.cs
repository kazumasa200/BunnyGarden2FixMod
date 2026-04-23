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
    public static ConfigEntry<int> ConfigWidth;
    public static ConfigEntry<int> ConfigHeight;
    public static ConfigEntry<int> ConfigFrameRate;
    public static ConfigEntry<AntiAliasingType> ConfigAntiAliasing;
    public static ConfigEntry<float> ConfigSensitivity;
    public static ConfigEntry<float> ConfigSpeed;
    public static ConfigEntry<float> ConfigFastSpeed;
    public static ConfigEntry<float> ConfigSlowSpeed;
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
    public static ConfigEntry<bool> ConfigCostumeChangerEnabled;
    public static ConfigEntry<UnityEngine.InputSystem.Key> ConfigCostumeChangerHotkey;
    public static ConfigEntry<bool> ConfigRespectGameCostumeOverride;

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
