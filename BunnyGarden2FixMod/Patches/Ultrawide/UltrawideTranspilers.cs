using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BunnyGarden2FixMod.Utils;
using GB;
using GB.Scene;
using HarmonyLib;

namespace BunnyGarden2FixMod.Patches.Ultrawide;

/// <summary>float 定数をメソッド呼び出しへ差し替える Transpiler の共通処理。</summary>
internal static class FloatConstRewriter
{
    /// <summary>
    /// 命令列中の <c>ldc.r4 constant</c> を <c>call replacement</c> に置き換え、置換数を返す。
    /// 命令オブジェクトは生かしたまま opcode / operand のみ書き換えるため、
    /// 分岐ラベルや try/catch ブロックの紐付けが壊れない。
    /// </summary>
    internal static int RewriteInPlace(List<CodeInstruction> code, float constant, MethodInfo replacement)
    {
        int hits = 0;
        for (int i = 0; i < code.Count; i++)
        {
            var inst = code[i];
            if (inst.opcode != OpCodes.Ldc_R4)
                continue;
            if (inst.operand is not float f || f != constant)
                continue;

            inst.opcode = OpCodes.Call;
            inst.operand = replacement;
            hits++;
        }
        return hits;
    }
}

/// <summary>
/// ゲーム本体に埋め込まれた 16:9 判定定数 (1.7777778f) を、ウルトラワイド状態に応じた
/// アスペクト比 (<see cref="UltrawideRuntime.AspectForEngineChecks"/>) へ差し替える。
///
/// 対象は解像度の常時監視を行う GBSystem.Update と、起動時に画面比を確認する
/// FirstScene の async ステートマシン群。後者はコンパイラ生成の入れ子型を列挙して
/// MoveNext をまとめてパッチする（定数を含まないステートマシンがあっても正常）。
/// </summary>
[HarmonyPatch]
internal static class EngineAspectCheckPatch
{
    /// <summary>ゲーム側コードに埋まっている 16:9 リテラルの値。</summary>
    private const float EmbeddedAspectLiteral = 1.7777778f;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        var systemUpdate = AccessTools.Method(typeof(GBSystem), "Update");
        yield return systemUpdate;

        var nested = typeof(FirstScene).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var type in nested)
        {
            if (!typeof(IAsyncStateMachine).IsAssignableFrom(type))
                continue;
            var stepper = AccessTools.Method(type, "MoveNext");
            if (stepper == null)
                continue;
            yield return stepper;
        }
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var provider = AccessTools.Method(
            typeof(UltrawideRuntime), nameof(UltrawideRuntime.AspectForEngineChecks));

        var code = new List<CodeInstruction>(instructions);
        int hits = FloatConstRewriter.RewriteInPlace(code, EmbeddedAspectLiteral, provider);

        // GBSystem.Update で 1 件も見つからない場合はゲーム更新でシグネチャが変わった疑い。
        // FirstScene 側は定数を含まないステートマシンが混ざるため 0 件でも問題ない。
        if (hits == 0 && original?.DeclaringType == typeof(GBSystem))
        {
            PatchLogger.LogError(
                "[Ultrawide] GBSystem.Update 内に 16:9 定数が見つからず、差し替えできませんでした。" +
                "ゲームのアップデートで前提が変わった可能性があります。");
        }
        else if (hits > 0)
        {
            PatchLogger.LogInfo(
                $"[Ultrawide] {original?.DeclaringType?.Name}.{original?.Name} の 16:9 定数を {hits} 箇所、動的アスペクトへ差し替えました");
        }

        return code;
    }
}
