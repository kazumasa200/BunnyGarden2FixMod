using YamlDotNet.Serialization;

namespace BunnyGarden2FixMod.ConfigGen;

public class SectionDef
{
    public string Section { get; set; } = "";
    /// <summary>docs/configs.md 出力用の表示名。省略時は <see cref="Section"/> をそのまま使う。</summary>
    public string? Name { get; set; }
    /// <summary>docs/configs.md でテーブル直前に挿入する Markdown 本文（任意）。</summary>
    public string? Header { get; set; }
    /// <summary>docs/configs.md でテーブル直後に挿入する Markdown 本文（任意）。</summary>
    public string? Footer { get; set; }
    /// <summary>true ならこのセクションを docs/configs.md から除外する（C# 生成には影響しない）。</summary>
    public bool Hidden { get; set; }
    public List<ConfigEntryDef> Configs { get; set; } = new();
}

public class ConfigEntryDef
{
    public string Name { get; set; } = "";
    [YamlIgnore]
    public string Section { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Key { get; set; }
    public string Type { get; set; } = "";
    public string? EnumType { get; set; }
    public object? Default { get; set; }
    public string? DefaultKey { get; set; }
    public string? DefaultButton { get; set; }
    public List<object>? Range { get; set; }
    public string Description { get; set; } = "";
    /// <summary>
    /// type=hotkey 専用。Gamepad エントリ (XxxButton) の description にのみ追記される
    /// ゲームパッド固有の補足説明。Keyboard エントリには出力されない。
    /// </summary>
    public string? ControllerDescription { get; set; }
    public UiDef? Ui { get; set; }

    [YamlIgnore]
    public string EffectiveKey => Key ?? Name;
}

public class UiDef
{
    public string Kind { get; set; } = "";
    public double? Step { get; set; }
    public string? Format { get; set; }
}
