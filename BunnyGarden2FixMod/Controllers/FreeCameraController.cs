using BunnyGarden2FixMod;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BunnyGarden2FixMod.Controllers;

public class FreeCameraController : MonoBehaviour
{
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
        if (Plugin.isFixedFreeCam)
            return;

        if (useMouseView && Mouse.current != null)
        {
            Vector2 mouseDelta = Mouse.current.delta.ReadValue();
            float sensitivity = Plugin.ConfigSensitivity.Value;
            rotationH += mouseDelta.x * sensitivity * Time.unscaledDeltaTime;
            rotationV -= mouseDelta.y * sensitivity * Time.unscaledDeltaTime;
        }

        rotationV = Mathf.Clamp(rotationV, -90f, 90f);
        transform.rotation = Quaternion.AngleAxis(rotationH, Vector3.up);
        transform.rotation *= Quaternion.AngleAxis(rotationV, Vector3.right);

        float speed = Plugin.ConfigSpeed.Value;

        if (Keyboard.current != null)
        {
            if (Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed)
                speed = Plugin.ConfigFastSpeed.Value;
            else if (Keyboard.current.leftCtrlKey.isPressed || Keyboard.current.rightCtrlKey.isPressed)
                speed = Plugin.ConfigSlowSpeed.Value;

            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed)
                transform.position += speed * Time.unscaledDeltaTime * transform.forward;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed)
                transform.position -= speed * Time.unscaledDeltaTime * transform.forward;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed)
                transform.position -= speed * Time.unscaledDeltaTime * transform.right;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed)
                transform.position += speed * Time.unscaledDeltaTime * transform.right;
            if (Keyboard.current.qKey.isPressed)
                transform.position += speed * Time.unscaledDeltaTime * Vector3.up;
            if (Keyboard.current.eKey.isPressed)
                transform.position += speed * Time.unscaledDeltaTime * Vector3.down;
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
