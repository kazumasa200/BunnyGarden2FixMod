using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace BunnyGarden2FixMod.Utils;

/// <summary>
/// 「初回適用 + ConfigEntry.SettingChanged を購読して live update」パターンを共通化するヘルパ。
///
/// <para>
/// Harmony Postfix が呼ばれる都度 <see cref="BindAndApply{T}"/> を呼び、apply ロジックを実行する。
/// 同じ <see cref="ConfigEntry{T}"/> に対する 2 回目以降の呼び出しはハンドラを再登録せず
/// <paramref name="apply"/> のみを再実行する。
/// </para>
///
/// <para>
/// dedup キーは <see cref="ConfigEntry{T}"/> のインスタンス参照。
/// <c>Plugin.ConfigXxx</c> は expression-bodied プロパティで同一の <c>Configs.Xxx</c> を返すため
/// 同 entry に対しては必ず dedup が効く。
/// </para>
///
/// <para>
/// 使い方:
/// <code>
/// [HarmonyPatch(typeof(GBSystem), "Setup")]
/// public static class FooPatch
/// {
///     private static void Postfix()
///         =&gt; LiveConfigBinding.BindAndApply(Plugin.ConfigFoo, Apply);
///
///     private static void Apply() { /* … */ }
/// }
/// </code>
/// </para>
/// </summary>
public static class LiveConfigBinding
{
    /// <summary>
    /// 同一 ConfigEntry に対するハンドラ多重登録を防ぐ dedup セット。
    /// BepInEx プラグインはホットリロードを想定していないため、解除処理は持たない。
    /// </summary>
    private static readonly HashSet<ConfigEntryBase> s_subscribed = new();

    public static void BindAndApply<T>(ConfigEntry<T> entry, Action apply)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        if (apply == null) throw new ArgumentNullException(nameof(apply));

        apply();

        if (s_subscribed.Add(entry))
        {
            entry.SettingChanged += (_, _) => apply();
        }
    }
}
