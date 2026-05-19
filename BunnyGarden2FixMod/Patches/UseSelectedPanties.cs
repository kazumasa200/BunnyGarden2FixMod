using BunnyGarden2FixMod.Utils;
using HarmonyLib;
using GB.Game;
using System.Linq;
using System;
using System.Collections.Generic;
using GB.Game.Params;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// 選んだパンツのみを有効にするパッチ
/// </summary>

[HarmonyPatch(typeof(GameData), nameof(GameData.updateTodaysPanties))]
public static class UseSelectedPanties
{
    private const int maxColors = 5;
    private const int firstIndex = (int)PresentType.Panties_a;
    private const int lastIndex = (int)PresentType.Panties_g;
    private const int maxPanties = lastIndex - firstIndex + 1;

    // 除外するものを指定
    private static readonly Dictionary<CharID, PresentType[]> ExcludedPanties = new()
    {
        { CharID.KANA,  new[] { PresentType.Panties_b, PresentType.Panties_e, PresentType.Panties_g, } },
        { CharID.RIN,   new[] { PresentType.Panties_c, PresentType.Panties_f, PresentType.Panties_g, } },
        { CharID.MIUKA, new[] { PresentType.Panties_c, PresentType.Panties_d, PresentType.Panties_g, } },
        { CharID.ERISA, new[] { PresentType.Panties_d, PresentType.Panties_f, PresentType.Panties_g, } },
        { CharID.KUON,  new[] { PresentType.Panties_b, PresentType.Panties_e, } },
        { CharID.LUNA,  new[] { PresentType.Panties_b,  } },
    };
    private static readonly Dictionary<CharID, bool[]> availablePanties =
        ExcludedPanties.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Aggregate(
                Enumerable.Repeat(true, maxPanties).ToArray(),
                (flag, pType) => { flag[(int)pType - firstIndex] = false; return flag; }
            )
        );

    private static bool Prepare()
    {
        bool isDataValid = availablePanties.Values.All(x => x.Contains(true) && x.Length == maxPanties);
        if (isDataValid)
        {
            PatchLogger.LogInfo("[UseSelectedPanties] GameData.updateTodaysPanties にパッチを適用しました");
            return true;
        }
        else
        {
            PatchLogger.LogInfo("[UseSelectedPanties] avaliablePanties が不正なためパッチをスキップしました");
            return false;
        }
    }

    private static bool Prefix(GameData __instance, CharID charID)
    {
        if (!Configs.UseSelectedPantiesEnabled.Value)
            return true;

        var chardata = __instance.CharData(charID);
        if (chardata == null)
            return true;

        List<int> list = (from x in chardata.PantiesFlag.Select((bool flag, int index) => new ValueTuple<bool, int>(flag, index))
            where !x.Item1
            where availablePanties[charID][x.Item2 / maxColors]
            select x.Item2).OrderBy((int y) => Guid.NewGuid()).ToList<int>();

        List<int> list2 = list.Where((int x) => x / maxColors != chardata.PantiesType).ToList<int>();

        if (list2.Count > 0)
        {
            chardata.PantiesType = list2[0] / maxColors;
            chardata.PantiesColor = list2[0] % maxColors;
            chardata.PantiesFlag[list2[0]] = true;
        }
        else if (list.Count > 0)
        {
            chardata.PantiesType = list[0] / maxColors;
            chardata.PantiesColor = list[0] % maxColors;
            chardata.PantiesFlag[list[0]] = true;
        }
        else
        {
            chardata.ClearPantiesFlag();
            Prefix(__instance, charID); // 再帰呼び出し
        }
        return false;
    }

}
