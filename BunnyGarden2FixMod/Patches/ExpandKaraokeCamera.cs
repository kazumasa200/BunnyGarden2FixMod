using AsmResolver.PE.Code;
using BunnyGarden2FixMod.Utils;
using GB.Bar.MiniGame;
using HarmonyLib;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection.Emit;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// ドリンク購入時のズーム比率を上げる
/// DrinkPurchase.GBUpdate がコルーチンで呼び出されているため
/// TargetMethodを指定する形でTrispilerを当てている
/// </summary>

[HarmonyPatch(typeof(KaraokeCamera), nameof(KaraokeCamera.FreeCamera))]
public static class ExpandKaraokeCamera
{
    // ズームする画角（デフォルト 15f 度： 60度基準だと1.33倍）
    // 22.9はズーム比率1.618倍（黄金比）に相当
    private const float maxZoom = 22.9f;

    // ズーム量（デフォルト0.05f/フレーム）
    // 画角増に合わせてスピードを変更。0.0372は1/1.618倍（黄金比）に相当
    private const float zoomSpeed = 0.05f; //0.0327f; フルズームまでの時間は変えない

    // 首振りの角度
    private const float rotY = 75f; // 上下大きく首振り可能にする
    private const float rotX = 175f; // 左右大きく首振り可能にする

    private static bool Prepare()
    {
        PatchLogger.LogInfo(
            $"[{nameof(ExpandKaraokeCamera)}] " +
            $"{nameof(KaraokeCamera)}.{nameof(KaraokeCamera.FreeCamera)} " +
            $"をパッチしました。");
        return true;
    }
    
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        var camFOVField = AccessTools.Field(typeof(KaraokeCamera), nameof(KaraokeCamera.m_fov));

        var Clamp01Method = AccessTools.Method(typeof(Mathf), nameof(Mathf.Clamp01));
        var zoomField = AccessTools.Field(typeof(KaraokeCamera), nameof(KaraokeCamera.m_zoom));
        var deltaTimeMethod = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
        
        var rotxField = AccessTools.Field(typeof(KaraokeCamera), nameof(KaraokeCamera.m_angleX));
        var rotyField = AccessTools.Field(typeof(KaraokeCamera), nameof(KaraokeCamera.m_angleY));
        var ClampAngleMethod = AccessTools.Method(typeof(KaraokeCamera), nameof(KaraokeCamera.clampAngle));
       
        var customMaxMethod = AccessTools.Method(typeof(GetMaxForNum2), nameof(GetMaxForNum2.Max));
        var MaxMethod = AccessTools.Method(typeof(Mathf), nameof(Mathf.Max), new[] { typeof(float), typeof(float) });

        // パラメータ取り込みがいるとき
        // var smoothYField = AccessTools.Field(typeof(KaraokeCamera), nameof(KaraokeCamera.m_smoothY));


        int patched = 0;
        for (int i = 0; i < codes.Count; i++)
        {
            // this.m_camera.fieldOfView = Mathf.Lerp(this.m_fov, 15f, this.m_zoom); を書き換え，
            // this.m_camera.fieldOfView = Mathf.Lerp(this.m_fov, maxZoom, this.m_zoom); を書き換え，
            if (i+1 < codes.Count &&
                codes[i].LoadsField(camFOVField) &&
                codes[i+1].LoadsConstant(15f))
            {
                codes[i+1].operand = maxZoom;
                patched++;
                continue; 
            }

            // this.m_zoom = Mathf.Clamp01(this.m_zoom +/- 0.05f); を書き換え
            // this.m_zoom = Mathf.Clamp01(this.m_zoom +/- zoomSpeed * Time.deltaTime * 60f); にする
            if (i+3 < codes.Count &&
                codes[i].LoadsField(zoomField) &&
                codes[i+1].LoadsConstant(0.05f) &&
                (codes[i+2].opcode == OpCodes.Sub || codes[i+2].opcode == OpCodes.Add) &&
                codes[i+3].Calls(Clamp01Method))
            {
                codes[i+1].operand = zoomSpeed;

                codes.Insert(i+2, new CodeInstruction(OpCodes.Call, deltaTimeMethod));
                codes.Insert(i+3, new CodeInstruction(OpCodes.Mul));

                codes.Insert(i+4, new CodeInstruction(OpCodes.Ldc_R4, 60f));
                codes.Insert(i+5, new CodeInstruction(OpCodes.Mul));
                i += 5;
                patched++; // ここは2回patch++される
                continue;
            }

            // 縦の振れ幅を拡大
            //this.m_angleX = this.clampAngle(this.m_angleX + GBInput.CameraControll().y * 5f, -25f, 20f); を書き換え
            //this.m_angleX = this.clampAngle(this.m_angleX + GBInput.CameraControll().y * 5f, -rotY, rotY);にする
            if (i+1 < codes.Count &&
                codes[i].LoadsField(rotxField))
            {
                for (int j = i+1; j < codes.Count; j++)
                {
                    if (codes[j].Calls(ClampAngleMethod))
                        break;

                    if (codes[j].LoadsConstant(-25f))
                    {
                        codes[j].operand = -rotY;
                        patched++;
                        i = j;
                        break;
                    }
                }
                continue;
            }

            //横の振れ幅を拡大
            //this.m_angleY = this.clampAngle(this.m_angleY - GBInput.CameraControll().x * 5f, -85f, 20f); を書き換え
            //this.m_angleY = this.clampAngle(this.m_angleY - GBInput.CameraControll().x * 5f, -85f, 20f); を書き換え
            if (i+1 < codes.Count &&
                codes[i].LoadsField(rotyField))
            {
                int idxMax = -1;
                int idxMin = -1;
                for (int j = i+1; j < codes.Count; j++)
                {
                    if (codes[j].Calls(ClampAngleMethod))
                        break;

                    if (codes[j].LoadsConstant(-85f))
                        idxMin = j;
                    
                    if (codes[j].LoadsConstant(20f))
                        idxMax = j;

                    if (idxMin != -1 && idxMax != -1)
                    {
                        codes[idxMax].operand = rotX;
                        codes[idxMin].operand = -rotX;
                        patched++;
                        i = j;
                        break;
                    }
                }
                continue;
            }

            // 見上げた時の高さ補正を無効化
            // float num2 = Mathf.Max(this.m_smoothY + vector.y, 0.5f); を書き換え
            // float num2 = Mathf.Max(this.m_smoothY + vector.y, 0.05f); にする
            if (codes[i].Calls(MaxMethod))
            {
                codes[i].operand = customMaxMethod;
                
                // パラメータが追加で必要になれば inject する
                // codes.Insert(i, new CodeInstruction(OpCodes.Ldarg_0));
                // codes.Insert(i+1, new CodeInstruction(OpCodes.Ldfld, smoothYField));
                // i += 2;

                patched++;
                continue;
            }
        }
        if(patched != 6)
        {
            PatchLogger.LogError($"[{nameof(ExpandKaraokeCamera)}] {patched} 修正対象のパターンが見つかりませんでした。ゲームのアップデートでパッチが機能していない可能性があります。");
        }
        return codes;
    }


    private static class GetMaxForNum2
    {
        // パラメータ取り込みがいるとき
        // public static float Max(float a, float b, float smoothY)
        public static float Max(float a, float b)
        {
            return Mathf.Max(a, 0.05f);
        }
    }
}
