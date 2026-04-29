using BunnyGarden2FixMod.Utils;
using GB;
using GB.Save;
using HarmonyLib;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// VSync 強制 ON と排他的フルスクリーン強制のパッチ。
///
/// <para>
/// <b>VSync</b><br/>
/// <see cref="Configs.ForceVSync"/> が true のとき、
/// <see cref="QualitySettings.vSyncCount"/> を 1 に固定します。
/// VSync が有効な間はフレームレートがモニターのリフレッシュレートに同期され、
/// <see cref="Configs.FrameRate"/> の設定より優先されます。
/// </para>
///
/// <para>
/// <b>排他的フルスクリーン</b><br/>
/// <see cref="Configs.ForceExclusiveFullScreen"/> が true のとき、
/// ゲームがフルスクリーンモードに切り替える都度
/// <see cref="Screen.fullScreenMode"/> を
/// <see cref="FullScreenMode.ExclusiveFullScreen"/> に強制します。<br/>
/// Unity 2022 以降ではデフォルトが FullScreenWindow（ボーダーレス）になるため、
/// Windows DWM（デスクトップウィンドウマネージャー）が複数モニター分の
/// フレームバッファを合成し続けます。排他モードにすると DWM をバイパスするため、
/// サブモニターが接続されているときの FPS 低下が改善される場合があります。<br/>
/// ウィンドウモード（1080p / 720p）では何もしません。
/// </para>
/// </summary>

// ── VSync ──────────────────────────────────────────────────────────────────
[HarmonyPatch(typeof(GBSystem), "Setup")]
public class VSyncPatch
{
    private static void Postfix()
        => LiveConfigBinding.BindAndApply(Configs.ForceVSync, Apply);

    private static void Apply()
    {
        if (Configs.ForceVSync.Value)
        {
            QualitySettings.vSyncCount = 1;
            PatchLogger.LogInfo("[DisplaySettings] VSync 強制 ON を適用しました (vSyncCount=1)");
        }
        else
        {
            // ON → OFF 切替時に vSyncCount を Unity 既定の 0 へ戻し、
            // Application.targetFrameRate に主導権を返す。
            QualitySettings.vSyncCount = 0;
            PatchLogger.LogInfo("[DisplaySettings] VSync 強制を解除しました (vSyncCount=0)");
        }
    }
}

// ── 排他的フルスクリーン ────────────────────────────────────────────────────
[HarmonyPatch(typeof(SaveData), "SetDisplaySize")]
public class ForceExclusiveFullScreenPatch
{
    private static void Postfix()
    {
        if (!Configs.ForceExclusiveFullScreen.Value) return;
        if (!Screen.fullScreen) return;

        // 既に ExclusiveFullScreen なら再設定しない（GBSystem.Update のループ防止）
        if (Screen.fullScreenMode == FullScreenMode.ExclusiveFullScreen) return;

        Screen.fullScreenMode = FullScreenMode.ExclusiveFullScreen;
        PatchLogger.LogInfo("[DisplaySettings] 排他的フルスクリーンモードを強制適用しました");
    }
}
