using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.DualShock;

#nullable enable

namespace BunnyGarden2FixMod.Utils;

public enum ControllerButton
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
        return Gamepad.all
            .Select(selector)
            .FirstOrDefault(value => value.sqrMagnitude > 0f);
    }

    private static float ReadRawTrigger(ControllerButton button)
    {
        return Gamepad.all
            .Select(gamepad => button switch
            {
                ControllerButton.ZL => gamepad.leftTrigger.ReadValue(),
                ControllerButton.ZR => gamepad.rightTrigger.ReadValue(),
                _ => 0f,
            })
            .FirstOrDefault(value => value > 0f);
    }

    private static bool IsRawTriggered(ControllerButton button)
    {
        return Gamepad.all
            .Select(gamepad => GetRawGamepadButton(gamepad, button))
            .Any(control => control?.wasPressedThisFrame == true);
    }

    private static bool IsRawHeld(ControllerButton button)
    {
        return Gamepad.all
            .Select(gamepad => GetRawGamepadButton(gamepad, button))
            .Any(control => control?.isPressed == true);
    }

    private static ButtonControl? GetRawGamepadButton(Gamepad? gamepad, ControllerButton button)
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