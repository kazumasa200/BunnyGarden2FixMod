using BunnyGarden2FixMod.Utils;
using GB;
using GB.Home;
using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// ギャンブルで負けなくなるチートパッチ。
///
/// ■ 仕組み
///   Gamble.executeGamble() は async メソッドだが、コンパイル後は
///   ステートマシン（&lt;executeGamble&gt;d__N）の MoveNext() にロジックが収まる。
///   ここにトランスパイラで再抽選コードを注入する。
///
///   注入箇所:
///     int winAmount = baseAmount + Random.Range(...) * 100;
///       ↓ 直後に挿入
///     winAmount = GambleAlwaysWinPatch.RerollIfNeeded(winAmount);
///
///   これにより ShowResult() / AddMoney() どちらも再抽選済みの値を参照する。
///
/// ■ pekari ボーナス（ジャックポット）は常に正の値なので対象外。
/// </summary>
[HarmonyPatch]
public static class GambleAlwaysWinPatch
{
    private const int MaxRerollAttempts = 1000;
    private const int RandUnit = 100; // executeGamble 内の num2 と同値

    private static bool Prepare()
    {
        PatchLogger.LogInfo("[GambleAlwaysWin] ステートマシントランスパイラを準備中");
        return true;
    }

    // executeGamble のステートマシンの MoveNext() を対象にする
    private static MethodBase TargetMethod()
    {
        var smType = typeof(Gamble)
            .GetNestedTypes(AccessTools.all)
            .FirstOrDefault(t => t.Name.Contains("executeGamble"));

        if (smType == null)
        {
            PatchLogger.LogError("[GambleAlwaysWin] executeGamble ステートマシン型が見つかりません");
            return null;
        }

        var method = smType.GetMethod("MoveNext", AccessTools.all);
        if (method == null)
        {
            PatchLogger.LogError("[GambleAlwaysWin] MoveNext メソッドが見つかりません");
            return null;
        }

        PatchLogger.LogInfo($"[GambleAlwaysWin] ターゲット: {smType.Name}.MoveNext");
        return method;
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);

        // ステートマシン型を再取得
        var smType = typeof(Gamble)
            .GetNestedTypes(AccessTools.all)
            .FirstOrDefault(t => t.Name.Contains("executeGamble"));
        if (smType == null) return codes;

        // winAmount フィールドを探す（名前に "winAmount" を含む int フィールド）
        var winAmountField = smType.GetFields(AccessTools.all)
            .FirstOrDefault(f => f.Name.Contains("winAmount") && f.FieldType == typeof(int));
        if (winAmountField == null)
        {
            PatchLogger.LogError("[GambleAlwaysWin] winAmount フィールドが見つかりません");
            return codes;
        }

        var randomRangeInt = AccessTools.Method(typeof(Random), "Range",
            new[] { typeof(int), typeof(int) });
        var rerollMethod = AccessTools.Method(
            typeof(GambleAlwaysWinPatch), nameof(RerollIfNeeded));

        // Random.Range(int,int) が初めて呼ばれた後、最初に現れる stfld winAmountField を探す
        // （pekari ボーナス時の 2 回目以降の代入には触れない）
        bool foundRange = false;
        for (int i = 0; i < codes.Count; i++)
        {
            if (codes[i].Calls(randomRangeInt))
                foundRange = true;

            if (foundRange && codes[i].StoresField(winAmountField))
            {
                // stfld winAmountField の直後に再抽選コードを挿入:
                //   ldarg.0              ← ステートマシン this（stfld の object ref 用）
                //   ldarg.0              ← ステートマシン this（ldfld の object ref 用）
                //   ldfld winAmountField ← 格納直後の winAmount を読み出す
                //   call RerollIfNeeded  ← 負なら再抽選、非負ならそのまま返す
                //   stfld winAmountField ← 結果を書き戻す
                codes.InsertRange(i + 1, new[]
                {
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld, winAmountField),
                    new CodeInstruction(OpCodes.Call,  rerollMethod),
                    new CodeInstruction(OpCodes.Stfld, winAmountField),
                });

                PatchLogger.LogInfo("[GambleAlwaysWin] winAmount 再抽選トランスパイラを適用しました");
                return codes;
            }
        }

        PatchLogger.LogError("[GambleAlwaysWin] winAmount への代入箇所が見つかりませんでした");
        return codes;
    }

    /// <summary>
    /// winAmount が負（損失）の場合、非負になるまで元のゲームと同じ式で再抽選する。
    /// ステートマシン内から直接呼ばれるため public static が必須。
    /// </summary>
    public static int RerollIfNeeded(int winAmount)
    {
        if (!Configs.GambleAlwaysWinEnabled.Value) return winAmount;
        if (winAmount >= 0) return winAmount;

        var gamble = Object.FindFirstObjectByType<Gamble>();
        if (gamble == null) return 0;

        var gambleParam = gamble.m_gambleParams.Get(gamble.m_rate);
        // AddGambleCount() はまだ呼ばれていないので、executeGamble が使った roundIdx と同一
        int roundIdx = GBSystem.Instance.RefGameData().GetGambleTotalPlayCount()
                         % gambleParam.RoundParamCount;
        int baseAmount = gambleParam.GetRoundParam(roundIdx).BaseAmount;
        int randAmount = gambleParam.GetRoundParam(roundIdx).RandomAmount;

        int result = winAmount;
        int attempts = 0;
        while (result < 0 && attempts < MaxRerollAttempts)
        {
            result = baseAmount
                + Random.Range(-randAmount / RandUnit, randAmount / RandUnit + 1) * RandUnit;
            attempts++;
        }

        if (result < 0)
        {
            PatchLogger.LogInfo("[GambleAlwaysWin] 再抽選上限到達、0 にフォールバック");
            return 0;
        }

        PatchLogger.LogInfo($"[GambleAlwaysWin] 再抽選 {attempts} 回: {winAmount} → {result}");
        return result;
    }
}
