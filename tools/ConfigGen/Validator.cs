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

        var dupKeys = entries
            .GroupBy(e => (e.Section, e.EffectiveKey))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Section}:{g.Key.EffectiveKey}");
        foreach (var k in dupKeys) errors.Add($"Duplicate (section, key): {k}");

        foreach (var e in entries)
        {
            var validTypes = new[] { "bool", "int", "float", "enum", "key" };
            if (!validTypes.Contains(e.Type))
                errors.Add($"[{e.Name}] Invalid type: {e.Type} (allowed: {string.Join(", ", validTypes)})");

            if ((e.Type == "enum" || e.Type == "key") && string.IsNullOrEmpty(e.EnumType))
                errors.Add($"[{e.Name}] type={e.Type} requires enumType");

            if ((e.Type == "enum" || e.Type == "key") && e.Default != null)
            {
                var defStr = e.Default.ToString() ?? "";
                if (!IdentifierPattern.IsMatch(defStr))
                    errors.Add($"[{e.Name}] enum/key default must be a valid identifier name (got: '{defStr}')");
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
