using System.Text.RegularExpressions;

namespace BunnyGarden2FixMod.ConfigGen;

public static class Validator
{
    private static readonly Regex IdentifierPattern = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public static List<string> Validate(List<ConfigEntryDef> entries)
    {
        var errors = new List<string>();

        var dupNames = entries.GroupBy(e => e.Name).Where(g => g.Count() > 1).Select(g => g.Key);
        foreach (var n in dupNames) errors.Add($"Duplicate name: {n}");

        // hotkey は cfg 上で {key}Key / {key}Button の 2 つに展開されるため、衝突検出も展開後の名前で行う。
        var keyTuples = new List<(string Section, string Key, string SourceName)>();
        foreach (var e in entries)
        {
            if (e.Type == "hotkey")
            {
                keyTuples.Add((e.Section, e.EffectiveKey + "Key", e.Name));
                keyTuples.Add((e.Section, e.EffectiveKey + "Button", e.Name));
            }
            else
            {
                keyTuples.Add((e.Section, e.EffectiveKey, e.Name));
            }
        }
        var dupKeys = keyTuples
            .GroupBy(t => (t.Section, t.Key))
            .Where(g => g.Count() > 1);
        foreach (var g in dupKeys)
            errors.Add($"Duplicate (section, key): {g.Key.Section}:{g.Key.Key} (from: {string.Join(", ", g.Select(t => t.SourceName).Distinct().OrderBy(s => s, StringComparer.Ordinal))})");

        foreach (var e in entries)
        {
            if (string.IsNullOrEmpty(e.Label))
                errors.Add($"[{e.Name}] label is required (top-level field, placed under name)");
            else if (e.Label.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                errors.Add($"[{e.Name}] label must be a single line (no CR/LF; CodeEmitter writes it as a one-line // comment)");

            var validTypes = new[] { "bool", "int", "float", "enum", "key", "hotkey" };
            if (!validTypes.Contains(e.Type))
                errors.Add($"[{e.Name}] Invalid type: {e.Type} (allowed: {string.Join(", ", validTypes)}). " +
                           $"Note: 'key' = single ConfigEntry<Key>, 'hotkey' = HotkeyConfig (KB+Pad pair)");

            if ((e.Type == "enum" || e.Type == "key") && string.IsNullOrEmpty(e.EnumType))
                errors.Add($"[{e.Name}] type={e.Type} requires enumType");

            if ((e.Type == "enum" || e.Type == "key") && e.Default != null)
            {
                var defStr = e.Default.ToString() ?? "";
                if (!IdentifierPattern.IsMatch(defStr))
                    errors.Add($"[{e.Name}] enum/key default must be a valid identifier name (got: '{defStr}')");
            }

            if (e.Type == "hotkey")
            {
                if (e.Default != null)
                    errors.Add($"[{e.Name}] type=hotkey must not specify 'default' (use defaultKey/defaultButton)");
                if (!string.IsNullOrEmpty(e.EnumType))
                    errors.Add($"[{e.Name}] type=hotkey must not specify 'enumType' (Key + ControllerButton are fixed)");
                if (e.Range != null)
                    errors.Add($"[{e.Name}] type=hotkey must not specify 'range'");
                if (e.Ui != null)
                    errors.Add($"[{e.Name}] type=hotkey must not specify 'ui' (HotkeyConfig is not on the F9 panel)");
                if (string.IsNullOrEmpty(e.DefaultKey))
                    errors.Add($"[{e.Name}] type=hotkey requires defaultKey (e.g. F12, P, T)");
                else if (!IdentifierPattern.IsMatch(e.DefaultKey))
                    errors.Add($"[{e.Name}] defaultKey must be a valid identifier (got: '{e.DefaultKey}')");
                if (!string.IsNullOrEmpty(e.DefaultButton) && !IdentifierPattern.IsMatch(e.DefaultButton))
                    errors.Add($"[{e.Name}] defaultButton must be a valid identifier (got: '{e.DefaultButton}')");
            }
            else
            {
                if (!string.IsNullOrEmpty(e.DefaultKey))
                    errors.Add($"[{e.Name}] defaultKey is only valid for type=hotkey");
                if (!string.IsNullOrEmpty(e.DefaultButton))
                    errors.Add($"[{e.Name}] defaultButton is only valid for type=hotkey");
                if (!string.IsNullOrEmpty(e.ControllerDescription))
                    errors.Add($"[{e.Name}] controllerDescription is only valid for type=hotkey");
            }

            if (e.Range != null && e.Type != "int" && e.Type != "float")
                errors.Add($"[{e.Name}] range is only valid for int/float (got: {e.Type})");

            if (e.Range != null && e.Range.Count == 2 && e.Default != null)
            {
                if (TryParseDouble(e.Range[0], out var min) &&
                    TryParseDouble(e.Range[1], out var max) &&
                    TryParseDouble(e.Default, out var def))
                {
                    if (def < min || def > max)
                        errors.Add($"[{e.Name}] default={def} is out of range [{min}, {max}]");
                }
            }

            if (e.Ui != null)
            {
                var validKinds = new[] { "toggle", "slider" };
                if (!validKinds.Contains(e.Ui.Kind))
                    errors.Add($"[{e.Name}] ui.kind must be toggle/slider (got: {e.Ui.Kind})");

                if (e.Ui.Kind == "toggle" && e.Type != "bool")
                    errors.Add($"[{e.Name}] ui.kind=toggle requires type=bool (got: {e.Type})");

                if (e.Ui.Kind == "slider")
                {
                    if (e.Type != "int" && e.Type != "float")
                        errors.Add($"[{e.Name}] ui.kind=slider requires type=int/float (got: {e.Type})");
                    if (e.Range == null)
                        errors.Add($"[{e.Name}] ui.kind=slider requires range");
                    if (e.Ui.Step == null)
                        errors.Add($"[{e.Name}] ui.kind=slider requires ui.step");
                    if (string.IsNullOrEmpty(e.Ui.Format))
                        errors.Add($"[{e.Name}] ui.kind=slider requires ui.format");
                }
            }
        }

        return errors;
    }

    private static bool TryParseDouble(object? v, out double result)
    {
        result = 0;
        if (v == null) return false;
        return double.TryParse(v.ToString(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out result);
    }
}
