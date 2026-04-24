using BunnyGarden2FixMod.Utils;
using GB;
using GB.Save;
using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// ゲーム内 OptionMenu の DISPLAY 項目に拡張解像度（既定 2560×1440）を追加するパッチ。
///
/// <para>設計方針（MOD 外しても壊れない版）:</para>
/// バニラでは <see cref="DisplaySize"/> に <c>DISPLAY_1080P=0</c> / <c>DISPLAY_720P=1</c> /
/// <c>FULL_SCREEN=2</c> の 3 値しかなく、<c>OptionMenu</c> のラベルリストも 3 要素固定。
/// もし MOD 独自の 4 番目をセーブに <c>(DisplaySize)3</c> として永続化すると、
/// MOD を外したバニラが <c>m_labels[3]</c> で <c>IndexOutOfRangeException</c> を起こして
/// オプション画面が開けなくなる。
///
/// 本実装はこれを避けるため、**拡張解像度の選択状態を 2 層に分離して保存**する:
///
/// <list type="number">
/// <item>バニラ <see cref="SaveData.m_displaySize"/> には常に <c>0/1/2</c> のどれかを書く（バニラ互換）</item>
/// <item>「拡張が選ばれているか」の真偽は <c>Plugin.ConfigExtraActive</c>（BepInEx config）に書く</item>
/// </list>
///
/// 拡張選択時はバニラ側には <c>DisplaySize=0</c>（=1080P と同値）を書き、
/// ConfigExtraActive=true で上書きを示す。SetDisplaySize が size=0 で呼ばれたら、
/// ConfigExtraActive を見て 2560×1440 か 1080P のどちらを適用するかを決める。
///
/// <para>OptionMenu でのインデックスマッピング:</para>
/// ラベルリスト先頭に拡張を挿入するので UI 内のインデックスは:
/// <c>0=Extra / 1=1080P / 2=720P / 3=FULL_SCREEN</c>。
/// バニラの <see cref="OptionItemUI.GetSelection"/> は UI インデックスをそのまま返して
/// <c>SetDisplaySize((DisplaySize)selection)</c> に渡すので、
/// <see cref="GetSelection_Postfix"/> で UI→enum の逆マッピング（Extra→0 / 1080P→0 / 720P→1 / FullScreen→2）と
/// <c>ConfigExtraActive</c> の更新を同時に行う。
///
/// <para>MOD を外した場合の挙動:</para>
/// バニラは <c>DisplaySize=0/1/2</c> のどれかを読むので落ちない。
/// ただし拡張選択中 (ExtraActive=true, DisplaySize=0) で外した場合は 1080P として扱われる。
/// 再度 MOD を入れると ConfigExtraActive が残っていれば拡張に戻る。
/// コーナーケース: MOD 外し中にバニラで 1080P を再選択 → DisplaySize=0 のまま、
/// ExtraActive は古いフラグ（true）が残る。次回 MOD 起動時に拡張へ戻ってしまう。
/// </summary>
[HarmonyPatch]
public static class ExtraResolutionPatch
{
    /// <summary>DISPLAY 行の判定に使う title MSGID の ID 値。</summary>
    internal static readonly int DisplayTitleId = (int)MSGID_SPLIT_2.OPTION_DISPLAY;

    /// <summary>
    /// ラベルリスト先頭に追加する拡張のプレースホルダ MSGID。
    /// 実描画は updateDisplay の Postfix で上書きするので中身は何でもよい。
    /// </summary>
    private static readonly MSGID ExtraLabelPlaceholder = MSGID_SPLIT_2.OPTION_DISPLAY_1080P;

    /// <summary>UI 上の拡張解像度のインデックス（先頭に挿入するので 0）。</summary>
    internal const int ExtraUiIndex = 0;

    /// <summary>バニラの DISPLAY 項目のラベル数（1080P / 720P / FullScreen の 3 つ）。</summary>
    private const int VanillaLabelCount = 3;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(OptionItemUI), nameof(OptionItemUI.SetupText))]
    private static void SetupText_Prefix(MSGID title, ref int sel, List<MSGID> list)
    {
        if (list == null || title == null) return;
        if (title.ID != DisplayTitleId) return;
        if (list.Count != VanillaLabelCount) return;

        list.Insert(ExtraUiIndex, ExtraLabelPlaceholder);
        sel = Plugin.ConfigExtraActive.Value
            ? ExtraUiIndex
            : sel + 1;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(OptionItemUI), "updateDisplay")]
    private static void UpdateDisplay_Postfix(OptionItemUI __instance)
    {
        var title = __instance.m_title;
        if (title == null || title.m_label == null) return;
        if (title.m_label.ID != DisplayTitleId) return;
        if (__instance.m_select != ExtraUiIndex) return;
        if (__instance.m_text == null) return;

        var (w, h) = NormalizeAspect(Plugin.ConfigExtraWidth.Value, Plugin.ConfigExtraHeight.Value);
        __instance.m_text.SetWithoutMSGID($"{w}×{h}");
    }

    /// <summary>
    /// UI インデックス (0..3) を vanilla DisplaySize enum (0..2) に逆マップし、
    /// 同時に ConfigExtraActive を現在の UI 選択状態に同期する。
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(OptionItemUI), nameof(OptionItemUI.GetSelection))]
    private static void GetSelection_Postfix(OptionItemUI __instance, ref int __result)
    {
        var title = __instance.m_title;
        if (title == null || title.m_label == null) return;
        if (title.m_label.ID != DisplayTitleId) return;

        int uiSelect = __result;
        bool isExtra = uiSelect == ExtraUiIndex;
        if (Plugin.ConfigExtraActive.Value != isExtra)
            Plugin.ConfigExtraActive.Value = isExtra;

        __result = isExtra ? 0 : uiSelect - 1;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SaveData), nameof(SaveData.SetDisplaySize))]
    private static bool SetDisplaySize_Prefix(SaveData __instance, DisplaySize size)
    {
        if ((int)size != 0)
        {
            if (Plugin.ConfigExtraActive.Value)
                Plugin.ConfigExtraActive.Value = false;
            return true;
        }

        if (!Plugin.ConfigExtraActive.Value)
            return true;

        int rawW = Plugin.ConfigExtraWidth.Value;
        int rawH = Plugin.ConfigExtraHeight.Value;
        if (rawW <= 0 || rawH <= 0)
        {
            PatchLogger.LogWarning(
                $"ExtraWidth/ExtraHeight が不正です ({rawW}x{rawH})。1080P にフォールバックします。");
            Plugin.ConfigExtraActive.Value = false;
            return true;
        }

        var (w, h) = NormalizeAspect(rawW, rawH);
        Screen.SetResolution(w, h, fullscreen: false);
        __instance.m_displaySize = size;
        PatchLogger.LogInfo($"拡張解像度を適用: {w}x{h} (window)");
        return false;
    }

    /// <summary>
    /// アスペクト比 16:9 に正規化する。config 値がユーザー由来で明示指定なので
    /// vanilla <see cref="GBSystem.CalcWindowResolution"/> の画面サイズクランプは行わない。
    /// </summary>
    private static (int width, int height) NormalizeAspect(int rawWidth, int rawHeight)
    {
        if (rawWidth <= 0 || rawHeight <= 0) return (rawWidth, rawHeight);
        const float targetAspect = 16f / 9f;
        float actual = (float)rawWidth / rawHeight;
        int width = rawWidth;
        int height = rawHeight;
        if (actual > targetAspect)
            width = (int)(rawHeight * targetAspect);
        else if (actual < targetAspect)
            height = (int)(rawWidth / targetAspect);
        return (width, height);
    }
}
