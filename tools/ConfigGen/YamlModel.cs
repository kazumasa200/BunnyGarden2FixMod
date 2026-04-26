using YamlDotNet.Serialization;

namespace BunnyGarden2FixMod.ConfigGen;

public class SectionDef
{
    public string Section { get; set; } = "";
    public List<ConfigEntryDef> Configs { get; set; } = new();
}

public class ConfigEntryDef
{
    public string Name { get; set; } = "";
    [YamlIgnore]
    public string Section { get; set; } = "";
    public string? Key { get; set; }
    public string Type { get; set; } = "";
    public string? EnumType { get; set; }
    public object? Default { get; set; }
    public List<object>? Range { get; set; }
    public string Description { get; set; } = "";
    public UiDef? Ui { get; set; }

    [YamlIgnore]
    public string EffectiveKey => Key ?? Name;
}

public class UiDef
{
    public string Kind { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Category { get; set; }
    public int? Order { get; set; }
    public double? Step { get; set; }
    public string? Format { get; set; }
}
