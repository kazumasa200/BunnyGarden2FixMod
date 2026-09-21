using System.Collections.Generic;
using System.Reflection;
using GB;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// ゲーム本体 (GBInput) へ流れる入力を MOD の都合で一時的に堰き止めるゲート。
///
/// <para>堰き止める状況は 2 つだけ:</para>
/// <list type="number">
///   <item>F9 パネルがキー割り当てをキャプチャしている間（全デバイス・全入力）</item>
///   <item>フリーカメラ操作中、パッドのショートカット同時押しが成立した直後の猶予時間
///         （パッド由来の入力のみ）。修飾ボタン+アクションの押下が同じフレームで
///         ゲーム本体のボタンとしても解釈される誤爆を防ぐ。</item>
/// </list>
/// </summary>
public static class InputGate
{
    /// <summary>パッド猶予の期限 (Time.unscaledTime 基準)。初期値は「期限切れ」。</summary>
    private static float s_padGraceDeadline = float.NegativeInfinity;

    /// <summary>パッドショートカット成立後にゲーム入力を堰き止める秒数。</summary>
    private const float PadGraceSeconds = 0.18f;

    /// <summary>パッド猶予タイマーを開始する。パッドショートカット成立時に呼ぶ。</summary>
    public static void ArmPadGrace()
    {
        s_padGraceDeadline = Time.unscaledTime + PadGraceSeconds;
    }

    /// <summary>パッド猶予時間の内側かどうか。</summary>
    public static bool PadGraceActive => Time.unscaledTime < s_padGraceDeadline;

    /// <summary>
    /// MOD 側ホットキーの判定を止めるべきか。
    /// キャプチャ中（確定したキーが同フレームでホットキーとして発火するのを防ぐ）と、
    /// パッド猶予中（同時押しの余韻での連続発火を防ぐ）が対象。
    /// </summary>
    public static bool HotkeysLocked =>
        Settings.SettingsController.IsAnyCapturing || PadGraceActive;

    /// <summary>
    /// GBInput の入力 1 件を堰き止めるべきかを判定する。
    /// <paramref name="source"/> が null の入力（リピート判定・カメラ操作など
    /// 発生源を特定できないもの）は、猶予中はデバイスを問わず堰き止める。
    /// </summary>
    public static bool ShouldBlock(InputAction source)
    {
        // キャプチャ中はゲーム側へ一切流さない
        if (Settings.SettingsController.IsAnyCapturing)
            return true;

        // パッド猶予はフリーカメラ操作の文脈でのみ働く
        if (!FreeCamera.FreeCameraManager.IsActive || !PadGraceActive)
            return false;

        // 猶予中でもキーボード・マウス由来の入力は素通し
        return source == null || source.activeControl?.device is Gamepad;
    }
}

/// <summary>
/// ボタン系 GBInput メソッド（InputAction を受け取り bool を返す一発押し・押下・離し判定）のゲート。
/// 対象メソッドは TargetMethods で一括列挙する。
/// </summary>
[HarmonyPatch]
internal static class GBInputButtonGatePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var name in new[] { "isTriggered", "isPressing", "isReleased" })
            yield return AccessTools.Method(typeof(GBInput), name, new[] { typeof(InputAction) });
    }

    private static bool Prefix(InputAction button, ref bool __result)
    {
        if (!InputGate.ShouldBlock(button))
            return true;
        __result = false;
        return false;
    }
}

/// <summary>リピート入力 (isTriggeredR) のゲート。発生源が取れないため猶予中は全て堰き止める。</summary>
[HarmonyPatch(typeof(GBInput), "isTriggeredR")]
internal static class GBInputRepeatGatePatch
{
    private static bool Prefix(ref bool __result)
    {
        if (!InputGate.ShouldBlock(null))
            return true;
        __result = false;
        return false;
    }
}

/// <summary>スティック値 (GetStickValue) のゲート。</summary>
[HarmonyPatch(typeof(GBInput), "GetStickValue")]
internal static class GBInputStickGatePatch
{
    private static bool Prefix(InputAction stick, ref Vector2 __result)
    {
        if (!InputGate.ShouldBlock(stick))
            return true;
        __result = Vector2.zero;
        return false;
    }
}

/// <summary>カメラ操作入力 (CameraControll) のゲート。発生源が取れないため猶予中は全て堰き止める。</summary>
[HarmonyPatch(typeof(GBInput), "CameraControll")]
internal static class GBInputCameraGatePatch
{
    private static bool Prefix(ref Vector2 __result)
    {
        if (!InputGate.ShouldBlock(null))
            return true;
        __result = Vector2.zero;
        return false;
    }
}
