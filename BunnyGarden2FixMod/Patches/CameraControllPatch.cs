using BunnyGarden2FixMod.Utils;
using GB;
using HarmonyLib;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// GBInput.CameraControll() のスティック入力をフレームレート非依存に修正するパッチ。
/// マウス入力はフレーム間差分（delta）のため元々フレームレート非依存。
/// スティック入力は定数値のため Time.deltaTime * 60f でスケーリングが必要。
/// RStick が使用中の場合のみスケーリングを適用し、マウス時は変更しない。
/// これにより Karuta / KaraokeCamera / Twister / DrinkPurchase / Cheki の
/// カメラ回転がすべて 60FPS 基準でフレームレート非依存になる。
/// </summary>
[HarmonyPatch(typeof(GBInput), "CameraControll")]
public class CameraControllPatch
{
    private static bool Prepare()
    {
        PatchLogger.LogInfo("[CameraControllPatch] GBInput.CameraControll をパッチしました（Karuta / KaraokeCamera / Twister / DrinkPurchase / Cheki カメラ回転）");
        return true;
    }

    private static void Postfix(ref Vector2 __result)
    {
        if (GBInput.RStick != Vector2.zero)
            __result *= Time.deltaTime * 60f;
    }
}
