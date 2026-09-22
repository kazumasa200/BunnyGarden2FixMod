using System.Collections.Generic;
using BunnyGarden2FixMod.Utils;
using GB.Save;
using HarmonyLib;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// ウィンドウの解像度が変わるとき、ウィンドウの中心位置を保つ。
///
/// <para>
/// Unity はウィンドウをリサイズしても左上を動かさないため、ウルトラワイドの出入りで
/// 横幅が変わるとウィンドウが片側へ寄っていく。サイズ変更の前に中心を覚えておき、
/// 反映された後に同じ中心へ戻す。位置は全ディスプレイを合わせたデスクトップの範囲へ収めるが、
/// デスクトップより大きいウィンドウはその軸を動かさず中心を優先する。
/// </para>
/// <para>
/// <c>Screen.SetResolution</c> は次のフレーム以降に反映されるため、要求を一旦保留し、
/// 実際にサイズが変わったフレームで移動する。
/// </para>
/// </summary>
internal static class WindowCenterKeeper
{
    /// <summary>サイズ変更が反映されるのを待つ上限フレーム数。超えたら要求を捨てる。</summary>
    private const int MaxWaitFrames = 30;

    private static bool s_pending;
    private static Vector2Int s_centerBefore;
    private static Vector2Int s_sizeBefore;
    private static int s_waitedFrames;

    /// <summary>サイズ変更の直前に呼び、現在の中心を覚える。</summary>
    internal static void Request()
    {
        if (Screen.fullScreen)
            return;

        s_sizeBefore = new Vector2Int(Screen.width, Screen.height);
        s_centerBefore = Screen.mainWindowPosition + new Vector2Int(s_sizeBefore.x / 2, s_sizeBefore.y / 2);
        s_waitedFrames = 0;
        s_pending = true;
    }

    /// <summary>毎フレーム呼ぶ。サイズが変わっていたらウィンドウを中心へ戻す。</summary>
    internal static void Tick()
    {
        if (!s_pending)
            return;

        // 途中でフルスクリーンへ移った場合は用済み
        if (Screen.fullScreen)
        {
            s_pending = false;
            return;
        }

        var size = new Vector2Int(Screen.width, Screen.height);
        if (size == s_sizeBefore)
        {
            if (++s_waitedFrames > MaxWaitFrames)
                s_pending = false;
            return;
        }
        s_pending = false;

        var target = s_centerBefore - new Vector2Int(size.x / 2, size.y / 2);
        ClampIntoDesktop(ref target, size);

        Screen.MoveMainWindowTo(Screen.mainWindowDisplayInfo, target);
        PatchLogger.LogInfo($"[Window] リサイズ後も中心を保つよう移動しました: {s_sizeBefore.x}x{s_sizeBefore.y} → {size.x}x{size.y} 位置 {target.x},{target.y}");
    }

    /// <summary>
    /// 全ディスプレイを合わせたデスクトップの範囲へ収める。
    /// 幅（高さ）がデスクトップより大きいときは収めようがないので、その軸は動かさず
    /// 中心を保ったままにする。複数モニターにまたがる横長ウィンドウを片側へ寄せないため。
    /// </summary>
    private static void ClampIntoDesktop(ref Vector2Int position, Vector2Int size)
    {
        var displays = new List<DisplayInfo>();
        Screen.GetDisplayLayout(displays);
        if (displays.Count == 0)
            return;

        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var display in displays)
        {
            var area = display.workArea;
            minX = Mathf.Min(minX, area.xMin);
            minY = Mathf.Min(minY, area.yMin);
            maxX = Mathf.Max(maxX, area.xMax);
            maxY = Mathf.Max(maxY, area.yMax);
        }

        if (size.x <= maxX - minX)
            position.x = Mathf.Clamp(position.x, minX, maxX - size.x);
        if (size.y <= maxY - minY)
            position.y = Mathf.Clamp(position.y, minY, maxY - size.y);
    }
}

/// <summary>表示サイズの変更要求を捉えて、ウィンドウの中心保持を予約する。</summary>
[HarmonyPatch(typeof(SaveData), nameof(SaveData.SetDisplaySize))]
internal static class WindowCenterKeeperPatch
{
    // ExtraResolutionPatch の Prefix が本体をスキップする経路でも Postfix は走るため、
    // 拡張解像度・1080p・720p のどの経路でも予約できる。
    private static void Postfix(DisplaySize size)
    {
        if (size == DisplaySize.FULL_SCREEN)
            return;

        WindowCenterKeeper.Request();
    }
}
