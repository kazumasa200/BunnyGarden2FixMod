using BunnyGarden2FixMod.Utils;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BunnyGarden2FixMod.Patches.FreeCamera;

/// <summary>
/// フリーカメラの視点・移動操作。
///
/// <para>
/// 毎フレーム「入力を軸値へ正規化 → ローカル空間の移動ベクトルへ合成 → 1 回だけ適用」
/// の順に処理する。キーボード・スティック・トリガーをいったん同じ軸値
/// (right / up / forward, 各 -1〜1) に落としてから合成するため、複数の入力手段を
/// 同時に操作しても移動量が二重に足し込まれない。斜め入力は大きさ 1 に丸めるので
/// 前進と横移動の同時押しでも速度が上がらない。
/// </para>
/// </summary>
public class FreeCameraController : MonoBehaviour
{
    /// <summary>
    /// マウス視点の回転量（度/ピクセル、感度 1 あたり）。
    /// マウスの delta は「前フレームからの移動量」という変位入力なので、
    /// deltaTime を掛けてはいけない（掛けると FPS が高いほど感度が下がる）。
    /// </summary>
    private const float MouseLookScale = 0.017f;

    /// <summary>右スティック最大倒し込み時の回転速度（度/秒、感度 1 あたり）。</summary>
    private const float StickLookRate = 18f;

    /// <summary>スティックの遊びを無視するしきい値（ベクトルの長さ）。</summary>
    private const float StickDeadzone = 0.1f;

    /// <summary>トリガーを入力とみなし始めるしきい値。</summary>
    private const float TriggerDeadzone = 0.05f;

    /// <summary>見上げ／見下ろしの可動範囲（度）。</summary>
    private const float PitchLimit = 90f;

    private float yaw;
    private float pitch;
    private bool mouseLookEnabled;

    private void Start()
    {
        // 乗っ取り時点のカメラ姿勢を yaw / pitch に分解して引き継ぐ。
        Vector3 angles = transform.rotation.eulerAngles;
        yaw = angles.y;
        pitch = angles.x > 180f ? angles.x - 360f : angles.x;
    }

    // 固定モードの ON/OFF はこのコンポーネントの enabled 切り替えで行われるため、
    // カーソルの拘束もそれに追従させる（固定中はゲーム UI を操作できる）。
    private void OnEnable() => SetMouseLook(true);

    private void OnDisable() => SetMouseLook(false);

    private void Update()
    {
        float dt = Time.unscaledDeltaTime;

        UpdateRotation(dt);
        UpdatePosition(dt);
        UpdateMouseLookToggle();
    }

    // ── 視点 ──────────────────────────────────────────────────────────

    private void UpdateRotation(float dt)
    {
        float sensitivity = Configs.Sensitivity.Value;

        // マウス: 変位入力。フレームごとの移動量をそのまま回転へ変換する（dt を掛けない）。
        // これによりフレームレートに関係なく「同じ距離マウスを動かせば同じだけ回る」。
        if (mouseLookEnabled && Mouse.current != null)
        {
            Vector2 mouse = Mouse.current.delta.ReadValue() * (sensitivity * MouseLookScale);
            yaw += mouse.x;
            pitch -= mouse.y;
        }

        // スティック: 倒し込み量は「回転し続ける速さ」を表す速度入力なので dt を掛ける。
        if (Configs.ControllerEnabled.Value)
        {
            Vector2 stick = ApplyDeadzone(GamepadHelper.ReadRightStick()) * (sensitivity * StickLookRate * dt);
            yaw += stick.x;
            pitch -= stick.y;
        }

        pitch = Mathf.Clamp(pitch, -PitchLimit, PitchLimit);
        transform.rotation = Quaternion.AngleAxis(yaw, Vector3.up)
                           * Quaternion.AngleAxis(pitch, Vector3.right);
    }

    // ── 移動 ──────────────────────────────────────────────────────────

    private void UpdatePosition(float dt)
    {
        Vector3 axes = ReadMoveAxes();
        if (axes == Vector3.zero)
            return;

        // 斜め入力やキーとスティックの同時操作で速度が増えないよう、長さ 1 に収める。
        if (axes.sqrMagnitude > 1f)
            axes.Normalize();

        // 「速度(単位/秒) × dt」の積分なので、移動はフレームレートに依存しない。
        transform.Translate(axes * (ResolveSpeed() * dt), Space.Self);
    }

    /// <summary>
    /// キーボード・左スティック・ZL/ZR を right / up / forward の軸値へ統合する。
    /// </summary>
    private static Vector3 ReadMoveAxes()
    {
        Vector3 axes = Vector3.zero;

        Keyboard kb = Keyboard.current;
        if (kb != null)
        {
            axes.x = ToAxis(kb.dKey.isPressed || kb.rightArrowKey.isPressed,
                            kb.aKey.isPressed || kb.leftArrowKey.isPressed);
            axes.y = ToAxis(kb.qKey.isPressed, kb.eKey.isPressed);
            axes.z = ToAxis(kb.wKey.isPressed || kb.upArrowKey.isPressed,
                            kb.sKey.isPressed || kb.downArrowKey.isPressed);
        }

        if (Configs.ControllerEnabled.Value)
        {
            Vector2 stick = ApplyDeadzone(GamepadHelper.ReadLeftStick());
            axes.x += stick.x;
            axes.z += stick.y;
            axes.y += ReadTriggerAxis(ControllerButton.ZR) - ReadTriggerAxis(ControllerButton.ZL);
        }

        return axes;
    }

    /// <summary>Shift / R で高速、Ctrl / L で低速。どちらでもなければ基本速度。</summary>
    private static float ResolveSpeed()
    {
        Keyboard kb = Keyboard.current;
        bool fast = kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);
        bool slow = kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed);

        if (Configs.ControllerEnabled.Value)
        {
            fast |= GamepadHelper.IsButtonHeld(ControllerButton.R);
            slow |= GamepadHelper.IsButtonHeld(ControllerButton.L);
        }

        if (fast) return Configs.FastSpeed.Value;
        if (slow) return Configs.SlowSpeed.Value;
        return Configs.Speed.Value;
    }

    // ── マウス視点の切り替え ──────────────────────────────────────────

    private void UpdateMouseLookToggle()
    {
        Mouse mouse = Mouse.current;
        if (mouse == null)
            return;

        // 左右どちらのクリックでも、マウス視点とカーソル表示を入れ替える。
        if (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame)
            SetMouseLook(!mouseLookEnabled);
    }

    private void SetMouseLook(bool enabled)
    {
        mouseLookEnabled = enabled;
        Cursor.lockState = enabled ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !enabled;
    }

    // ── 入力ヘルパー ──────────────────────────────────────────────────

    /// <summary>2 つの押下状態を -1 / 0 / +1 の軸値へ変換する。</summary>
    private static float ToAxis(bool positive, bool negative)
        => (positive ? 1f : 0f) - (negative ? 1f : 0f);

    /// <summary>デッドゾーン未満のスティック入力を切り捨てる。</summary>
    private static Vector2 ApplyDeadzone(Vector2 stick)
        => stick.sqrMagnitude > StickDeadzone * StickDeadzone ? stick : Vector2.zero;

    /// <summary>トリガーの踏み込み量を、デッドゾーンを 0 とする 0〜1 の軸値へ変換する。</summary>
    private static float ReadTriggerAxis(ControllerButton trigger)
        => Mathf.InverseLerp(TriggerDeadzone, 1f, GamepadHelper.ReadTrigger(trigger));
}
