using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.DualShock;

#nullable enable

namespace BunnyGarden2FixMod.Utils;

public static class GamepadHelper
{
    internal static bool IsButtonHeld(ControllerButton button)
    {
        if (button == ControllerButton.ZL || button == ControllerButton.ZR)
            return ReadTrigger(button) >= Plugin.ConfigControllerTriggerDeadzone.Value;

        return IsHeld(button);
    }

    internal static Vector2 ReadLeftStick()
    {
        return ReadRawStick(gamepad => gamepad.leftStick.ReadValue());
    }

    internal static Vector2 ReadRightStick()
    {
        return ReadRawStick(gamepad => gamepad.rightStick.ReadValue());
    }

    internal static float ReadTrigger(ControllerButton button)
    {
        return ReadRawTrigger(button);
    }

    internal static bool IsTriggered(ControllerButton button)
    {
        return IsRawTriggered(button);
    }

    internal static bool IsHeld(ControllerButton button)
    {
        return IsRawHeld(button);
    }

    private static Vector2 ReadRawStick(System.Func<Gamepad, Vector2> selector)
    {
        foreach (var gamepad in Gamepad.all)
        {
            Vector2 value = selector(gamepad);
            if (value.sqrMagnitude > 0f)
                return value;
        }

        return Vector2.zero;
    }

    private static float ReadRawTrigger(ControllerButton button)
    {
        foreach (var gamepad in Gamepad.all)
        {
            float value = button switch
            {
                ControllerButton.ZL => gamepad.leftTrigger.ReadValue(),
                ControllerButton.ZR => gamepad.rightTrigger.ReadValue(),
                _ => 0f,
            };

            if (value > 0f)
                return value;
        }

        return 0f;
    }

    private static bool IsRawTriggered(ControllerButton button)
    {
        foreach (var gamepad in Gamepad.all)
        {
            var control = GetRawGamepadButton(gamepad, button);
            if (control?.wasPressedThisFrame == true)
                return true;
        }

        return false;
    }

    private static bool IsRawHeld(ControllerButton button)
    {
        foreach (var gamepad in Gamepad.all)
        {
            var control = GetRawGamepadButton(gamepad, button);
            if (control?.isPressed == true)
                return true;
        }

        return false;
    }

    private static ButtonControl? GetRawGamepadButton(Gamepad gamepad, ControllerButton button)
    {
        if (gamepad == null)
            return null;

        return button switch
        {
            ControllerButton.A => gamepad.buttonSouth,
            ControllerButton.B => gamepad.buttonEast,
            ControllerButton.X => gamepad.buttonWest,
            ControllerButton.Y => gamepad.buttonNorth,
            ControllerButton.L => gamepad.leftShoulder,
            ControllerButton.R => gamepad.rightShoulder,
            ControllerButton.ZL => gamepad.leftTrigger,
            ControllerButton.ZR => gamepad.rightTrigger,
            ControllerButton.Start => gamepad.startButton,
            ControllerButton.Select => GetRawSelectButton(gamepad),
            _ => null,
        };
    }

    private static ButtonControl GetRawSelectButton(Gamepad gamepad)
    {
        if (gamepad is DualShockGamepad dualShockGamepad)
            return dualShockGamepad.touchpadButton;

        return gamepad.selectButton;
    }
}