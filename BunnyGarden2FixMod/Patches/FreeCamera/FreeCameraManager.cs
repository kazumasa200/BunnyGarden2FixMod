using System.Collections.Generic;
using GB;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering.Universal;

namespace BunnyGarden2FixMod.Patches.FreeCamera;

public class FreeCameraManager : MonoBehaviour
{
    public static bool IsActive { get; private set; } = false;
    public static bool IsFixed { get; private set; } = false;

    private Camera originalCam;
    private GameObject freeCamObject;
    private FreeCameraController controller;
    private readonly Dictionary<EventSystem, bool> eventSystemNavigationStates = [];
    private readonly Dictionary<Canvas, bool> canvasEnabledStates = [];
    private bool isGameUiSuppressed;
    private RenderTexture pipRenderTexture;
    private GameObject pipUIObject;
    private GameObject pipCanvasObject;

    public static FreeCameraManager Initialize(GameObject parent)
        => parent.AddComponent<FreeCameraManager>();

    private void OnEnable()
    {
        Plugin.GUICallback += GUICallback;
    }

    private void OnDisable()
    {
        Plugin.GUICallback -= GUICallback;
        Deactivate();
    }

    private void Update()
    {
        if (Configs.FreeCamToggle.IsTriggered())
            ToggleFreeCam();

        if (Configs.FixedFreeCamToggle.IsTriggered())
            ToggleFixedFreeCam();

        if (Configs.FreeCamDisplayModeToggle.IsTriggered())
            ToggleFreeCamMode();
    }

    private void ToggleFreeCam()
    {
        if (IsActive)
            Deactivate();
        else
            Activate();
    }

    private void ToggleFreeCamMode()
    {
        bool isFullScreen = Screen.fullScreen || Screen.fullScreenMode == FullScreenMode.FullScreenWindow;
        bool isExclusiveFullScreen = Configs.ForceExclusiveFullScreen.Value && isFullScreen;
        if (!IsActive || isExclusiveFullScreen) // 排他フルスクリーンモードの場合は、フリーカメラの表示モードを切り替えない
            return;

        // モードを切り替え
        Configs.FreeCamDisplayMode.Value = Configs.FreeCamDisplayMode.Value switch
        {
            FreeCamDisplayMode.MainScreen => FreeCamDisplayMode.PiP,
            FreeCamDisplayMode.PiP => isFullScreen ? FreeCamDisplayMode.Display2 : FreeCamDisplayMode.MainScreen,
            _ => FreeCamDisplayMode.MainScreen
        };

        StartCoroutine(RebootFreeCamRoutine()); // Pip表示を安定させるため deactivate->activate を1フレーム遅らせる
        Plugin.Logger.LogInfo("フリーカメラ切替: " + Configs.FreeCamDisplayMode.Value);
    }

    private System.Collections.IEnumerator RebootFreeCamRoutine()
    {
        Deactivate();
        yield return null;
        Activate();
    }

    private void ToggleFixedFreeCam()
    {
        if (!IsActive)
            return;
        IsFixed = !IsFixed;
        if (controller != null)
            controller.enabled = !IsFixed;
        RefreshGameUiSuppression(force: true);
        Plugin.Logger.LogInfo($"フリーカメラ固定モード: {(IsFixed ? "ON" : "OFF")}");
    }

    private void Activate()
    {
        originalCam = Plugin.FindCurrentCamera();
        if (originalCam == null)
        {
            IsActive = false;
            return;
        }

        freeCamObject = new GameObject("BG2FreeCam");
        var freeCam = freeCamObject.AddComponent<Camera>();
        freeCam.CopyFrom(originalCam);

        freeCamObject.transform.SetPositionAndRotation(
            originalCam.transform.position,
            originalCam.transform.rotation);

        // フリーカメラの映像をどのディスプレイに出力するかのモードを判定
        // 排他的フルスクリーンモードの場合は、不安定になる可能性があるため、Display2出力モードとPiPモードを無効化する
        bool isFullScreen = Screen.fullScreen || Screen.fullScreenMode == FullScreenMode.FullScreenWindow;
        bool isExclusiveFullScreen = Configs.ForceExclusiveFullScreen.Value && isFullScreen;
        bool useDisplay2InFreeCam = Configs.FreeCamDisplayMode.Value == FreeCamDisplayMode.Display2 && Display.displays.Length > 1;
        bool usePiPInFreeCam = Configs.FreeCamDisplayMode.Value == FreeCamDisplayMode.PiP;

        if (usePiPInFreeCam && !isExclusiveFullScreen)
        {
            // PiPモードではPiPを起動して、フリーカメラをPiPに出力する
            SetupPiP();
            originalCam.enabled = true; // 元のカメラは引き続きメインディスプレイに出力
            Plugin.Logger.LogInfo("PiPでフリーカメラを有効化");
        }
        else if (useDisplay2InFreeCam && !isExclusiveFullScreen)
        {
            // Display2出力モードでは、フリーカメラをDisplay2に出力する

            // Display.Activateの副作用でフルスクリーンが解除されるので，解像度とモードを保存して復元
            // 現在の解像度とフルスクリーンモードを保存
            int currentWidth = Screen.width;
            int currentHeight = Screen.height;
            var currentMode = Screen.fullScreenMode;

            Display.displays[1].Activate();
            freeCam.targetDisplay = 1;

            // 解像度を復元することで、フルスクリーン状態も一緒に復元される
            Screen.SetResolution(currentWidth, currentHeight, currentMode, Screen.currentResolution.refreshRateRatio);

            originalCam.enabled = true; // 元のカメラは引き続きメインディスプレイに出力
            Plugin.Logger.LogInfo("Display2でフリーカメラを有効化");
        }
        else
        {
            // どちらのモードも無効な場合は、メインを停止してフリーカメラをメインディスプレイに出力する
            freeCam.targetDisplay = 0;
            originalCam.enabled = false;
            Plugin.Logger.LogInfo("MainScreenでフリーカメラを有効化");
        }

        CopyUrpCameraData(originalCam, freeCam);
        controller = freeCamObject.AddComponent<FreeCameraController>();

        IsActive = true;
        IsFixed = false;

        Plugin.Logger.LogInfo("フリーカメラを作成しました");
        RefreshGameUiSuppression(force: true);
    }

    public void Deactivate()
    {
        if (freeCamObject != null)
        {
            var cam = freeCamObject.GetComponent<Camera>();
            if (cam != null)
                cam.targetTexture = null;
            StartCoroutine(BlackoutAndDestroyRoutine(freeCamObject)); // ブラックアウトと破棄をコルーチンで実行
            freeCamObject = null;
            controller = null;
        }

        CleanupPiP();

        if (originalCam != null)
        {
            originalCam.enabled = true;
            originalCam.targetDisplay = 0;
        }

        IsActive = false;
        IsFixed = false;
        RefreshGameUiSuppression(force: true);

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        Plugin.Logger.LogInfo($"フリーカメラを解除しました");
    }
    private System.Collections.IEnumerator BlackoutAndDestroyRoutine(GameObject targetCamObject)
    {
        // カメラのクリアフラグを「Solid Color」、背景色を黒、カリングマスクを0に設定して、画面全体を黒で塗りつぶす
        var cam = targetCamObject.GetComponent<Camera>();
        if (cam != null)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.cullingMask = 0;

            yield return new WaitForEndOfFrame();
        }
        Destroy(targetCamObject);
    }

    private void SetupPiP()
    {
        CleanupPiP();

        int pipWidth = Screen.width / 4;
        int pipHeight = Screen.height / 4;
        float pipAspectRatio = (float)pipWidth / pipHeight;

        pipRenderTexture = new RenderTexture(pipWidth, pipHeight, 16);
        pipRenderTexture.antiAliasing = MsaaSetupPatch.GetMSAASamples();
        pipRenderTexture.Create();

        if (freeCamObject != null)
        {
            var cam = freeCamObject.GetComponent<Camera>();
            if (cam != null)
            {
                cam.targetTexture = pipRenderTexture;
                cam.enabled = true;
            }
        }

        pipUIObject = new GameObject("PiPUI");
        var image = pipUIObject.AddComponent<UnityEngine.UI.RawImage>();
        image.texture = pipRenderTexture;

        // レイキャスト（マウス判定）を有効にする
        image.raycastTarget = true;

        // ドラッグによる移動とリサイズ用のコンポーネントを追加
        var pipHandler = pipUIObject.AddComponent<FreeCameraPiPHandler>();
        pipHandler.TargetAspectRatio = pipAspectRatio;
        // PiPのリサイズが確定したときにテクスチャもリサイズするようにコールバックを設定
        pipHandler.OnResizeCommitted = (newWidth, newHeight) =>
        {
            RefreshTexture(newWidth, newHeight);
        };

        // PiP用にCanvasを新規作成して、そこにPiPUIを配置する
        pipCanvasObject = new GameObject("PiPCanvas");
        var canvas = pipCanvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = GetMaxUIOrder() + 1; // 他のUIより前面に表示

        // ドラッグとリサイズを有効にするためにGraphicRaycasterを追加
        pipCanvasObject.AddComponent<UnityEngine.UI.GraphicRaycaster>();
        var scaler = pipCanvasObject.AddComponent<UnityEngine.UI.CanvasScaler>();
        scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = 1.0f;

        // PiPUIをCanvasの子にして、右下に配置
        if (canvas != null)
        {
            pipUIObject.transform.SetParent(canvas.transform, false);
            var rect = pipUIObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1, 0);
            rect.anchorMax = new Vector2(1, 0);
            rect.pivot = new Vector2(1, 0);
            rect.anchoredPosition = new Vector2(0, 0);
            rect.sizeDelta = new Vector2(pipWidth, pipHeight);
        }
    }
    private void RefreshTexture(int width, int height)
    {
        // pipRenderTextureを解放し新規生成
        if (pipRenderTexture != null)
        {
            pipRenderTexture.Release();
            Destroy(pipRenderTexture);
        }
        pipRenderTexture = new RenderTexture(width, height, 16);
        pipRenderTexture.antiAliasing = MsaaSetupPatch.GetMSAASamples();
        pipRenderTexture.Create();

        // freeCamObjectを流用し，テクスチャを更新
        var cam = freeCamObject.GetComponent<Camera>();
        if (cam != null)
            cam.targetTexture = pipRenderTexture;

        // pipUIObjectを流用し，テクスチャを更新
        var image = pipUIObject.GetComponent<UnityEngine.UI.RawImage>();
        if (image != null)
            image.texture = pipRenderTexture;
    }

    // 他のUIオブジェクトの最大のソート順序を取得する
    private int GetMaxUIOrder()
    {
        int maxOrder = 0;
        Canvas[] canvases = FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var canvas in canvases)
        {
            if (canvas != null && canvas.sortingOrder > maxOrder)
                maxOrder = canvas.sortingOrder;
        }
        return maxOrder;
    }

    private void CleanupPiP()
    {
        if (pipRenderTexture != null)
        {
            pipRenderTexture.Release();
            Destroy(pipRenderTexture);
            pipRenderTexture = null;
        }
        if (pipUIObject != null)
        {
            Destroy(pipUIObject);
            pipUIObject = null;
        }
        if (pipCanvasObject != null)
        {
            Destroy(pipCanvasObject);
            pipCanvasObject = null;
        }
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

    public void RefreshGameUiSuppression(bool force = false)
    {
        bool useDisplay2InFreeCam = Configs.FreeCamDisplayMode.Value == FreeCamDisplayMode.Display2 && Display.displays.Length > 1;
        bool usePiPInFreeCam = Configs.FreeCamDisplayMode.Value == FreeCamDisplayMode.PiP;
        bool isMainScreenOccupied = !usePiPInFreeCam && !useDisplay2InFreeCam;  // メインディスプレイにフリーカメラで占有されているかどうか

        bool shouldSuppress = isMainScreenOccupied && IsActive && !IsFixed && !ShouldExposeGameUiDuringFreeCam();
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

        if (!Configs.HideGameUiInFreeCam.Value)
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

    private bool ShouldHideCanvas(Canvas canvas)
    {
        if (canvas == null)
            return false;

        if (freeCamObject != null && canvas.transform.IsChildOf(freeCamObject.transform))
            return false;

        return canvas.renderMode != RenderMode.WorldSpace;
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

    private void GUICallback()
    {
        if (!IsActive)
            return;

        GUI.color = Color.white;
        GUILayout.Label(
            "Move: Arrow/WASD or Left Stick, Up/Down: E/Q or ZR/ZL, Look: Mouse or Right Stick, Speed: Shift/Ctrl or R/L");
        GUI.color = Color.green;
        GUILayout.Label($"Free Camera: ON ({Configs.FreeCamToggle}=OFF)");
        GUI.color = Color.yellow;
        GUILayout.Label($"Fixed Mode: {(IsFixed ? "ON" : "OFF")} ({Configs.FixedFreeCamToggle}=TOGGLE)");
        GUI.color = Color.cyan;
        GUILayout.Label($"Display Mode: {Configs.FreeCamDisplayMode.Value} ({Configs.FreeCamDisplayModeToggle}=TOGGLE)");
        GUI.color = Color.white;

    }
}