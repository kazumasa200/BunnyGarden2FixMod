using System.Collections.Generic;
using BepInEx.Configuration;
using BunnyGarden2FixMod.Utils;
using HarmonyLib;
using UnityEngine.InputSystem;

namespace BunnyGarden2FixMod;

public static class ConfigMigration
{
    private readonly struct MigrationEntry(ConfigDefinition oldDef, ConfigDefinition newDef)
    {
        public ConfigDefinition OldDef { get; } = oldDef;
        public ConfigDefinition NewDef { get; } = newDef;
    }

    private static readonly MigrationEntry[] Migrations =
    [
        // 第一
        new(new("AntiAliasing", "AntiAliasingType"), new("Graphics", "AntiAliasingType")),
        new(new("Camera", "ControllerToggleFixedFreeCam"), new("Camera", HotkeyConfig.GamepadKey("ToggleFixedFreeCam"))),
        new(new("Camera", "ControllerToggleFreeCam"), new("Camera", HotkeyConfig.GamepadKey("ToggleFreeCam"))),
        new(new("Camera", "ControllerToggleModifier"), new("Input", "ControllerModifier")),
        new(new("Camera", "ControllerToggleScreenshot"), new("General", HotkeyConfig.GamepadKey("CaptureScreenshot"))),
        new(new("Camera", "ScreenshotKey"), new("General", HotkeyConfig.KeyboardKey("CaptureScreenshot"))),
        new(new("Camera", "ControllerToggleTimeStop"), new("Time", HotkeyConfig.GamepadKey("ToggleTimeStop"))),
        new(new("Camera", "TimeStopToggleKey"), new("Time", HotkeyConfig.KeyboardKey("ToggleTimeStop"))),
        new(new("Camera", "ControllerTriggerDeadzone"), new("Input", "ControllerTriggerDeadzone")),
        new(new("CastOrder", "Enabled"), new("Cheat", "CastOrder")),
        new(new("Cheat", "Enabled"), new("Cheat", "Likability")),
        new(new("CostumeChanger", "Hotkey"), new("CostumeChanger", HotkeyConfig.KeyboardKey("Show"))),
        new(new("Resolution", "Width"), new("Graphics", "Width")),
        new(new("Resolution", "Height"), new("Graphics", "Height")),
        new(new("Resolution", "FrameRate"), new("Graphics", "FrameRate")),
        new(new("Resolution", "ExtraWidth"), new("Graphics", "ExtraWidth")),
        new(new("Resolution", "ExtraHeight"), new("Graphics", "ExtraHeight")),
    ];

    public static void Migrate(ConfigFile config)
    {
        PatchLogger.LogInfo($"[{nameof(ConfigMigration)}] 設定の移行を開始します");
        var previousSaveOnConfigSet = config.SaveOnConfigSet;
        config.SaveOnConfigSet = false;

        ArrayMigration(config);

        config.Save();
        config.SaveOnConfigSet = previousSaveOnConfigSet;
        PatchLogger.LogInfo($"[{nameof(ConfigMigration)}] 設定の移行が完了しました");
    }

    private static void ArrayMigration(ConfigFile config)
    {
        // これは少しハッキーですが、エントリの移行と古いエントリの実際の削除を処理する最善の方法です
        var orphanedEntries =
            (Dictionary<ConfigDefinition, string>)
            AccessTools.PropertyGetter(typeof(ConfigFile), "OrphanedEntries").Invoke(config, null);

        if (orphanedEntries == null)
        {
            PatchLogger.LogWarning(
                $"[{nameof(ConfigMigration)}] " +
                $"移行のための「OrphanedEntries」にアクセスできませんでした。移行はスキップされます");
            return;
        }

        foreach (var entry in Migrations)
        {
            // 古いエントリが存在しない場合、それはすでに移行されているか、
            // そもそも存在しなかったことを意味するため、ログなしでスキップします
            if (!orphanedEntries.TryGetValue(entry.OldDef, out var oldValue))
                continue;

            orphanedEntries.Remove(entry.OldDef);
            PatchLogger.LogInfo($"[{nameof(ConfigMigration)}] 古い設定エントリを削除しました: {entry.OldDef}");

            // 新しいエントリがすでに存在する場合、ユーザーがすでに手動で移行したか新しいエントリを変更したことを意味するため、
            // さらにログなしでスキップします。
            if (orphanedEntries.ContainsKey(entry.NewDef))
                continue;

            orphanedEntries.Add(entry.NewDef, oldValue);
            PatchLogger.LogInfo(
                $"[{nameof(ConfigMigration)}]" +
                $"新しい設定エントリに移行しました: {entry.NewDef} = {oldValue}");
        }
    }
}