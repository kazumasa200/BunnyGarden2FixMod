using BunnyGarden2FixMod.Patches.Ultrawide;
using GB;
using HarmonyLib;
using System;
using UnityEngine; // Screen.currentResolution

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// フルスクリーンの解像度計算を上書きするパッチ。
///
/// 基本は従来どおり 16:9 の解像度を返す。ウルトラワイド表示が有効なフレーム
/// (UltrawideRuntime.Active) に限り、ネイティブ比率の解像度へ切り替える。
///
/// ■ 入力が正確に 16:9 の場合（例: 1920×1080, 3840×2160, 2560×1440）
///   → そのまま返す。モニター解像度を超える値（スーパーサンプリング）も許可。
///
/// ■ 入力が 16:9 でない場合（例: ウルトラワイド 3440×1440 をそのまま入力）
///   → 幅・高さをそれぞれ基準に 16:9 に換算し、モニターに収まる範囲で
///     より高解像度になる候補を採用する。
///
/// 例: ウルトラワイド 3440×1440 モニター、設定 3440×1440（非16:9）
///   Option A: 幅 3440 → 16:9 高さ = 1935 → 上限 1440 超過 → 高さ 1440 で再計算 → (2560, 1440)
///   Option B: 高さ 1440 → 16:9 幅 = 2560 ≤ 3440 OK → (2560, 1440)
///   → (2560, 1440) 採用。ゲームが 2560×1440 フルスクリーンで安定する。
/// </summary>
[HarmonyPatch(typeof(GBSystem), "CalcFullScreenResolution")]
public class CalcFullScreenResolutionPatch
{
    private static bool Prefix(ref ValueTuple<int, int, bool> __result)
    {
        int configW = Configs.Width.Value;
        int configH = Configs.Height.Value;
        Resolution mon = Screen.currentResolution;

        if (UltrawideRuntime.Active)
        {
            __result = ValueTuple.Create(UltrawideRuntime.WideWidth, UltrawideRuntime.WideHeight, true);
            return false;
        }

        // 入力が正確に 16:9 かどうかを整数演算で判定（浮動小数点誤差を排除）
        bool isExact16x9 = configW * 9 == configH * 16;

        int resW, resH;
        bool flag;

        if (isExact16x9)
        {
            // ── 16:9 入力: クランプなしでそのまま使用 ──────────────────────
            // モニター解像度を超える値も許可（スーパーサンプリング用途）。
            // GBSystem.Update() の幅チェック（Item3=true → Item1 == Screen.width）を使用。
            resW = configW;
            resH = configH;
            flag = true;
        }
        else
        {
            // ── 非 16:9 入力: 16:9 に変換してモニターに収める ──────────────
            // 幅・高さをそれぞれ基準に 16:9 換算し、より高解像度な候補を採用する。

            // Option A: 入力の幅を基準に算出
            int wA = Math.Min(configW, mon.width);
            int hA = (int)(wA / UltrawideRuntime.BaseAspect);
            if (hA > mon.height)
            {
                // 高さがモニターを超える場合は高さ上限で再計算
                hA = mon.height;
                wA = (int)(hA * UltrawideRuntime.BaseAspect);
            }

            // Option B: 入力の高さを基準に算出
            int hB = Math.Min(configH, mon.height);
            int wB = (int)(hB * UltrawideRuntime.BaseAspect);
            if (wB > mon.width)
            {
                // 幅がモニターを超える場合は幅上限で再計算
                wB = mon.width;
                hB = (int)(wB / UltrawideRuntime.BaseAspect);
            }

            // ピクセル数が多い（より高解像度な）候補を採用
            if (wA * hA >= wB * hB)
            {
                resW = wA;
                resH = hA;
            }
            else
            {
                resW = wB;
                resH = hB;
            }

            // adjustByWidth フラグ:
            //   true  → GBSystem.Update() が Screen.width  == resW を確認（幅が制約）
            //   false → GBSystem.Update() が Screen.height == resH を確認（高さが制約）
            flag = resH < mon.height;
        }

        __result = new ValueTuple<int, int, bool>(resW, resH, flag);

        return false;
    }
}
