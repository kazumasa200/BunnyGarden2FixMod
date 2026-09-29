using System.Collections.Generic;
using GB;
using GB.Bar;
using HarmonyLib;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.Ultrawide;

/// <summary>
/// バー HUD のうち、キャンバス拡幅後に 16:9 時代の置き場所・大きさが露呈する部品の補正。
///
/// <list type="bullet">
///   <item>会話ウィンドウ（バー用）の背景 — 横スケールを画面比に追従させる</item>
///   <item>お会計通知 (ChargeUI) の「画面外に隠す」X 座標 — 広がった分だけ外へずらす</item>
///   <item>酔い演出 (DrunkEffect) のオーバーレイ — 横スケールを画面比に追従させる</item>
/// </list>
/// </summary>
internal static class UltrawideBarHudPatch
{
    /// <summary>ゲーム側 slideNotice に埋め込まれている「隠し位置 X」のリテラル。</summary>
    private const float NoticeParkedXLiteral = 450f;

    /// <summary>横スケール補正の巻き戻し用に、対象 Transform の素のスケールを覚えておく。</summary>
    private static readonly Dictionary<Transform, Vector3> s_baseScales = new();

    /// <summary>ウルトラワイド状態に応じた「お会計通知の隠し位置 X」。</summary>
    private static float NoticeParkedX()
        => UltrawideRuntime.Active
            ? NoticeParkedXLiteral + UltrawideRuntime.EdgeExtension()
            : NoticeParkedXLiteral;

    /// <summary>
    /// 対象 Transform の横スケールを画面比へ追従させる。条件が外れていれば素の値へ戻す。
    /// </summary>
    private static void FitWidthToAspect(Transform target, bool apply)
    {
        if (target == null)
            return;

        if (!s_baseScales.TryGetValue(target, out var baseScale))
        {
            baseScale = target.localScale;
            s_baseScales[target] = baseScale;
        }

        if (!apply)
        {
            target.localScale = baseScale;
            return;
        }

        target.localScale = new Vector3(baseScale.x * UltrawideRuntime.WidthScale, baseScale.y, baseScale.z);
    }

    /// <summary>バー用会話ウィンドウの背景を拡幅する。</summary>
    [HarmonyPatch(typeof(ConversationWindowPanel), nameof(ConversationWindowPanel.Set))]
    private static class ConversationWindowWidenPatch
    {
        private static readonly AccessTools.FieldRef<ConversationWindowPanel, GameObject> s_barWindow =
            AccessTools.FieldRefAccess<ConversationWindowPanel, GameObject>("m_windowInBar");

        private static void Postfix(ConversationWindowPanel __instance)
        {
            var window = s_barWindow(__instance);
            if (window == null)
                return;

            FitWidthToAspect(window.transform, UltrawideRuntime.Active && window.activeSelf);
        }
    }

    /// <summary>起動直後から通知をウルトラワイド補正済みの隠し位置に置いておく。</summary>
    [HarmonyPatch(typeof(ChargeUI), nameof(ChargeUI.Start))]
    private static class ChargeNoticeInitPatch
    {
        private static readonly AccessTools.FieldRef<ChargeUI, GameObject> s_notice =
            AccessTools.FieldRefAccess<ChargeUI, GameObject>("m_notice");

        private static void Postfix(ChargeUI __instance)
        {
            var notice = s_notice(__instance);
            if (notice != null)
            {
                var t = notice.transform;
                var p = t.localPosition;
                t.localPosition = new Vector3(NoticeParkedX(), p.y, p.z);
            }
        }
    }

    /// <summary>スライドアニメの目標 X (450f リテラル) を動的な隠し位置へ差し替える。</summary>
    [HarmonyPatch(typeof(ChargeUI), "slideNotice")]
    private static class ChargeNoticeSlidePatch
    {
        private static System.Collections.Generic.IEnumerable<CodeInstruction> Transpiler(
            System.Collections.Generic.IEnumerable<CodeInstruction> instructions)
        {
            var provider = AccessTools.Method(typeof(UltrawideBarHudPatch), nameof(NoticeParkedX));
            var code = new List<CodeInstruction>(instructions);
            FloatConstRewriter.RewriteInPlace(code, NoticeParkedXLiteral, provider);
            return code;
        }
    }

    /// <summary>酔い演出のオーバーレイを拡幅する。</summary>
    [HarmonyPatch(typeof(DrunkEffect), nameof(DrunkEffect.Show))]
    private static class DrunkOverlayWidenPatch
    {
        private static readonly AccessTools.FieldRef<DrunkEffect, CanvasGroup> s_overlay =
            AccessTools.FieldRefAccess<DrunkEffect, CanvasGroup>("m_drunkEffect");

        private static void Postfix(DrunkEffect __instance)
        {
            var overlay = s_overlay(__instance);
            if (overlay == null)
                return;

            FitWidthToAspect(overlay.transform, UltrawideRuntime.Active);
        }
    }
}
