using BunnyGarden2FixMod.Utils;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BunnyGarden2FixMod.Patches.FreeCamera;

public class FreeCameraController : MonoBehaviour
{
    private const float ControllerLookScale = 18f;
    private const float StickDeadzoneSqr = 0.01f;
    private const float ZRDeadzone = 0.05f;
    private float rotationH;
    private float rotationV;
    private bool useMouseView = true;

    private void Start()
    {
        Vector3 eulerAngles = transform.rotation.eulerAngles;
        rotationH = eulerAngles.y;
        rotationV = eulerAngles.x;

        if (rotationV > 180f)
            rotationV -= 360f;

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void Update()
    {
        float deltaTime = Time.unscaledDeltaTime;

        if (useMouseView && Mouse.current != null)
        {
            Vector2 mouseDelta = Mouse.current.delta.ReadValue();
            float sensitivity = Configs.Sensitivity.Value;
            rotationH += mouseDelta.x * sensitivity * deltaTime;
            rotationV -= mouseDelta.y * sensitivity * deltaTime;
        }

        Vector2 rightStick = GamepadHelper.ReadRightStick();
        if (rightStick.sqrMagnitude > StickDeadzoneSqr)
        {
            float sensitivity = Configs.Sensitivity.Value * ControllerLookScale;
            rotationH += rightStick.x * sensitivity * deltaTime;
            rotationV -= rightStick.y * sensitivity * deltaTime;
        }

        rotationV = Mathf.Clamp(rotationV, -90f, 90f);
        transform.rotation = Quaternion.AngleAxis(rotationH, Vector3.up);
        transform.rotation *= Quaternion.AngleAxis(rotationV, Vector3.right);

        float speed = Configs.Speed.Value;
        if (Configs.ControllerEnabled.Value)
        {
            if (GamepadHelper.IsButtonHeld(ControllerButton.R))
                speed = Configs.FastSpeed.Value;
            else if (GamepadHelper.IsButtonHeld(ControllerButton.L))
                speed = Configs.SlowSpeed.Value;
        }

        if (Keyboard.current != null)
        {
            if (Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed)
                speed = Configs.FastSpeed.Value;
            else if (Keyboard.current.leftCtrlKey.isPressed || Keyboard.current.rightCtrlKey.isPressed)
                speed = Configs.SlowSpeed.Value;

            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed)
                transform.position += speed * deltaTime * transform.forward;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed)
                transform.position -= speed * deltaTime * transform.forward;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed)
                transform.position -= speed * deltaTime * transform.right;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed)
                transform.position += speed * deltaTime * transform.right;
            if (Keyboard.current.qKey.isPressed)
                transform.position += speed * deltaTime * transform.up;
            if (Keyboard.current.eKey.isPressed)
                transform.position += speed * deltaTime * -transform.up;
        }

        if (Configs.ControllerEnabled.Value)
        {
            Vector2 leftStick = GamepadHelper.ReadLeftStick();
            if (leftStick.sqrMagnitude > StickDeadzoneSqr)
            {
                transform.position += speed * deltaTime * transform.forward * leftStick.y;
                transform.position += speed * deltaTime * transform.right * leftStick.x;
            }


            transform.position += speed * deltaTime *
                Mathf.InverseLerp(ZRDeadzone, 1, GamepadHelper.ReadTrigger(ControllerButton.ZR)) * transform.up;
            transform.position += speed * deltaTime *
                Mathf.InverseLerp(ZRDeadzone, 1, GamepadHelper.ReadTrigger(ControllerButton.ZL)) * -transform.up;
        }

        if (Mouse.current != null)
        {
            if (Mouse.current.leftButton.wasPressedThisFrame || Mouse.current.rightButton.wasPressedThisFrame)
            {
                useMouseView = !useMouseView;
                Cursor.lockState = useMouseView ? CursorLockMode.Locked : CursorLockMode.None;
                Cursor.visible = !useMouseView;
            }
        }
    }

    private void OnDisable()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }
}
