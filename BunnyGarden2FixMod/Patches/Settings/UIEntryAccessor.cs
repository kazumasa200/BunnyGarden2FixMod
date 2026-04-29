// NOTE: Configs.g.cs (codegen 出力) と同一アセンブリ前提。
// BoolAccessor / IntAccessor / FloatAccessor は internal なので、生成ファイルを
// 別アセンブリに切り出す場合は public 化または InternalsVisibleTo の追加が必要。
using System;
using BepInEx.Configuration;
using BunnyGarden2FixMod.Utils;

namespace BunnyGarden2FixMod.Patches.Settings;

/// <summary>F9 パネルでの行表示種別。</summary>
public enum UIKind
{
    Toggle,
    Slider,
    Dropdown,
    KeyBinding,
}

/// <summary>
/// `Configs.UIEntries` の 1 要素。Phase 3 の SettingsView が
/// これを foreach で行レンダする。codegen が組み立てる POCO。
/// </summary>
public class UIEntryMeta
{
    public string Category;
    public string Label;
    public string Desc;          // 任意。v2 以降で使用予定。Phase 2 では null 可。
    public UIKind Kind;

    // Slider 用（Toggle 時は未使用）
    public float SliderMin;
    public float SliderMax;
    public float SliderStep;
    public string Format;        // string.Format スタイル。例: "{0:F2}", "{0} fps"

    // Dropdown 用（他 Kind 時は null）。
    // EnumAccessor<T> 内の Enum.GetValues 順序と index を一致させること。
    // codegen は Enum.GetNames(typeof(T)) を出力するため自動一致する。
    public string[] DropdownOptions;

    // KeyBinding 用 (他 Kind 時は null)。
    // HotkeyConfig はキー (KeyConfig) と Pad ボタン (ButtonConfig) の 2 ConfigEntry を保持する。
    // 値変更は HotkeyProvider().KeyConfig.Value / .ButtonConfig.Value への直接代入で行う。
    public Func<global::BunnyGarden2FixMod.Utils.HotkeyConfig> HotkeyProvider;

    public IConfigAccessor Accessor;
}

/// <summary>
/// 型消去された ConfigEntry アクセサ。Toggle は 0/1、Slider は実値を float で扱う。
/// SettingsView は ConfigEntry の T を知らずに Get/Set できる。
/// </summary>
public interface IConfigAccessor
{
    float GetFloat();
    void  SetFloat(float v);
    /// <summary>BepInEx が定義するデフォルト値に戻す。</summary>
    void  ResetToDefault();
}

/// <summary>bool ConfigEntry を 0/1 でラップする。</summary>
internal sealed class BoolAccessor : IConfigAccessor
{
    private readonly Func<ConfigEntry<bool>> _entry;
    public BoolAccessor(Func<ConfigEntry<bool>> entry) { _entry = entry; }
    public float GetFloat() => _entry().Value ? 1f : 0f;
    public void  SetFloat(float v) => _entry().Value = v >= 0.5f;
    public void  ResetToDefault()
    {
        var e = _entry();
        e.Value = (bool)((ConfigEntryBase)e).DefaultValue;
    }
}

/// <summary>
/// int ConfigEntry を float でラップする。SetFloat 時に step で必ず snap してから格納する。
/// drag 中の浮動小数誤差で 60→59.999998 になり BepInEx 側の int round で 59 に落ちるのを防ぐため。
/// 注: snap 計算は FloatAccessor と同じく _step を float のまま使い、最後に int へ丸める。
/// `(int)_step` でキャストしてしまうと _step=2.5 のような小数 step で意味が壊れるため避ける。
/// </summary>
internal sealed class IntAccessor : IConfigAccessor
{
    private readonly Func<ConfigEntry<int>> _entry;
    private readonly float _step;
    public IntAccessor(Func<ConfigEntry<int>> entry, float step)
    {
        _entry = entry;
        // !(step > 0f) は step が 0 / 負 / NaN のとき true。NaN は通常の比較を全て false にするため
        // 否定形で書かないと NaN を素通りさせてしまう。
        _step = !(step > 0f) ? 1f : step;
    }
    public float GetFloat() => _entry().Value;
    public void SetFloat(float v)
    {
        var snapped = (int)Math.Round(Math.Round(v / _step) * _step);
        _entry().Value = snapped;
    }
    public void ResetToDefault()
    {
        var e = _entry();
        e.Value = (int)((ConfigEntryBase)e).DefaultValue;
    }
}

/// <summary>
/// float ConfigEntry を float でラップする。SetFloat 時に step で必ず snap してから格納する。
/// 表示値（format で整形）と実保存値の一致を保証するため。
/// </summary>
internal sealed class FloatAccessor : IConfigAccessor
{
    private readonly Func<ConfigEntry<float>> _entry;
    private readonly float _step;
    public FloatAccessor(Func<ConfigEntry<float>> entry, float step)
    {
        _entry = entry;
        // IntAccessor 同様 NaN を素通りさせないため否定形で書く。
        _step = !(step > 0f) ? 0.01f : step;
    }
    public float GetFloat() => _entry().Value;
    public void SetFloat(float v) => _entry().Value = (float)(Math.Round(v / _step) * _step);
    public void ResetToDefault()
    {
        var e = _entry();
        e.Value = (float)((ConfigEntryBase)e).DefaultValue;
    }
}

/// <summary>
/// enum ConfigEntry を「Enum.GetValues 上の index」として float でラップする。
/// underlying value をそのまま使うと sparse / 明示値 enum で UI index と食い違うため、
/// 必ず Array.IndexOf で正規化する。
/// </summary>
internal sealed class EnumAccessor<T> : IConfigAccessor where T : struct, Enum
{
    // Enum.GetNames と Enum.GetValues は同一順序（共に underlying value 昇順）で返す。
    // ConfigGen が DropdownOptions に渡す Enum.GetNames とこの GetValues の index が一致する根拠。
    private static readonly Array s_values = Enum.GetValues(typeof(T));
    private readonly Func<ConfigEntry<T>> _entry;
    public EnumAccessor(Func<ConfigEntry<T>> entry) { _entry = entry; }

    public float GetFloat()
    {
        var value = _entry().Value;
        var idx = Array.IndexOf(s_values, value);
        if (idx < 0)
        {
            // .cfg を手編集して未知の値を入れた場合や [Flags] 合成値の場合に発生。
            // 黙って先頭メンバへフォールバックすると勝手に書き換わったように見えるためログを残す。
            PatchLogger.LogWarning($"[EnumAccessor<{typeof(T).Name}>] 不明な値 '{value}' を検出、UI 上は先頭メンバへフォールバック");
            return 0f;
        }
        return idx;
    }

    public void SetFloat(float v)
    {
        if (s_values.Length == 0) return;
        var idx = (int)Math.Round(v);
        if (idx < 0) idx = 0;
        if (idx >= s_values.Length) idx = s_values.Length - 1;
        _entry().Value = (T)s_values.GetValue(idx);
    }

    public void ResetToDefault()
    {
        var e = _entry();
        e.Value = (T)(object)((ConfigEntryBase)e).DefaultValue;
    }
}
