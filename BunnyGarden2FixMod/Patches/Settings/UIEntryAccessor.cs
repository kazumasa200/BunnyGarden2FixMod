// NOTE: Configs.g.cs (codegen 出力) と同一アセンブリ前提。
// BoolAccessor / IntAccessor / FloatAccessor は internal なので、生成ファイルを
// 別アセンブリに切り出す場合は public 化または InternalsVisibleTo の追加が必要。
using System;
using BepInEx.Configuration;

namespace BunnyGarden2FixMod.Patches.Settings;

/// <summary>F9 パネルでの行表示種別。enum 設定の dropdown 等は v1 範囲外。</summary>
public enum UIKind
{
    Toggle,
    Slider,
}

/// <summary>
/// `Configs.UIEntries` の 1 要素。Phase 3 の SettingsView が
/// これを foreach で行レンダする。codegen が組み立てる POCO。
/// </summary>
public class UIEntryMeta
{
    public string Category;
    public int Order;
    public string Label;
    public string Desc;          // 任意。v2 以降で使用予定。Phase 2 では null 可。
    public UIKind Kind;

    // Slider 用（Toggle 時は未使用）
    public float SliderMin;
    public float SliderMax;
    public float SliderStep;
    public string Format;        // string.Format スタイル。例: "{0:F2}", "{0} fps"

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
