using BepInEx.Configuration;
using UnityEngine.InputSystem;

#nullable enable

namespace BunnyGarden2FixMod.Utils;

public class HotkeyConfig
{
    public ConfigEntry<Key>? KeyConfig { get; }
    public ConfigEntry<ControllerButton>? ButtonConfig { get; }

    public HotkeyConfig(
        ConfigFile config,
        string section,
        string key,
        Key defaultKey,
        ControllerButton defaultButton,
        string description)
    {
        KeyConfig = config.Bind(section, KeyboardKey(key), defaultKey, $"{description} (Keyboard)");
        ButtonConfig = config.Bind(section, GamepadKey(key), defaultButton, $"{description} (Gamepad)");
    }

    public HotkeyConfig(
        ConfigFile config,
        string section,
        string key,
        Key defaultKey,
        string description)
    {
        KeyConfig = config.Bind(section, KeyboardKey(key), defaultKey, $"{description} (Keyboard)");
    }

    public HotkeyConfig(
        ConfigFile config,
        string section,
        string key,
        ControllerButton defaultButton,
        string description)
    {
        ButtonConfig = config.Bind(section, GamepadKey(key), defaultButton, $"{description} (Gamepad)");
    }

    public static string GamepadKey(string key) => $"{key}Button";

    public static string KeyboardKey(string key) => $"{key}Key";

    public override string ToString()
    {
        string? keyLabel = KeyConfig == null || KeyConfig.Value == Key.None ? null : KeyConfig.Value.ToString();
        string? buttonLabel = GetControllerBindingLabel(Plugin.ConfigControllerModifier.Value, ButtonConfig?.Value);

        if (keyLabel != null && buttonLabel != null)
            return $"{keyLabel} / {buttonLabel}";

        return keyLabel ?? buttonLabel ?? "Unbound";
    }

    public bool IsHeld()
    {
        if (KeyConfig != null && Keyboard.current?[KeyConfig.Value].isPressed == true)
            return true;

        if (ButtonConfig != null && GamepadHelper.IsHeld(Plugin.ConfigControllerModifier.Value) &&
            GamepadHelper.IsHeld(ButtonConfig.Value))
        {
            return true;
        }

        return false;
    }

    public bool IsTriggered()
    {
        return IsKeyboardTriggered() || IsControllerTriggered();
    }

    public bool IsKeyboardTriggered()
    {
        return KeyConfig != null && Keyboard.current?[KeyConfig.Value].wasPressedThisFrame == true;
    }

    public bool IsControllerTriggered()
    {
        if (ButtonConfig != null &&
            IsControllerComboTriggered(Plugin.ConfigControllerModifier.Value, ButtonConfig.Value))
        {
            Plugin.SuppressGameInputTemporarily();
            return true;
        }

        return false;
    }

    private static string? GetControllerBindingLabel(ControllerButton modifier, ControllerButton? action)
    {
        if (action == null || action == ControllerButton.None)
            return null;

        if (modifier == ControllerButton.None || modifier == action)
            return action.ToString();

        return $"{modifier}+{action}";
    }

    private static bool IsControllerComboTriggered(ControllerButton modifier, ControllerButton action)
    {
        if (action == ControllerButton.None)
            return false;

        if (modifier == ControllerButton.None || modifier == action)
            return GamepadHelper.IsTriggered(action);

        return GamepadHelper.IsHeld(modifier) && GamepadHelper.IsTriggered(action);
    }
}
