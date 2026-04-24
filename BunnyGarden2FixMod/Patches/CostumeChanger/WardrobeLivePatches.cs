using BunnyGarden2FixMod.Utils;
using GB.Scene;
using HarmonyLib;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// ライブ変更（FittingRoom 等）によるパンツ／ストッキング切替を履歴に記録するパッチ群。
///
/// <para>
/// Preload 経由ではなく、<see cref="CharacterHandle.ReloadPanties"/> や
/// <see cref="CharacterHandle.ApplyStocking"/> が直接呼ばれたタイミングで
/// 履歴を記録する。Costume は Preload 経由でしか変わらないため <see cref="CostumeChangerPatch"/>
/// の Postfix で十分だが、Panties/Stocking は FittingRoom のライブプレビュー中に
/// Preload を経由せず差し替わるため、こちら側の Postfix も必要。
/// </para>
///
/// <para>
/// 既存 <see cref="DisableStockingPatch"/> は <c>ApplyStocking</c> の Prefix で
/// <c>type=0</c> に強制する。本クラスの <c>ApplyStocking</c> Postfix は
/// その Prefix より後に走るため、最終的に画面に表示される type を記録する。
/// これは MOD のポリシー「見た＝画面に出た」と整合する。
/// </para>
/// </summary>
internal static class WardrobeLivePatches
{
    [HarmonyPatch(typeof(CharacterHandle), nameof(CharacterHandle.ReloadPanties))]
    public static class ReloadPantiesRecordPatch
    {
        private static bool Prepare()
        {
            bool enabled = Plugin.ConfigCostumeChangerEnabled?.Value ?? true;
            if (enabled) PatchLogger.LogInfo("[WardrobeLivePatches.ReloadPanties] 適用");
            return enabled;
        }

        // CharacterHandle.ReloadPanties(int colorType, int pantiesType, Action onLoaded)
        private static void Postfix(CharacterHandle __instance, int __0, int __1)
        {
            if (__instance == null) return;
            var id = __instance.GetCharID();
            // シグネチャ順: (colorType, pantiesType) なので __0=color, __1=type
            // 最新 LoadArg は current 絞り関係なく更新（SetCurrentCast 時の反映用）
            WardrobeLastLoadArg.UpdatePanties(id, type: __1, color: __0);
            if (!WardrobeHistoryGate.ShouldRecord(id)) return;
            PantiesViewHistory.MarkViewed(id, type: __1, color: __0);
        }
    }

    [HarmonyPatch(typeof(CharacterHandle), nameof(CharacterHandle.ApplyStocking))]
    public static class ApplyStockingRecordPatch
    {
        private static bool Prepare()
        {
            bool enabled = Plugin.ConfigCostumeChangerEnabled?.Value ?? true;
            if (enabled) PatchLogger.LogInfo("[WardrobeLivePatches.ApplyStocking] 適用");
            return enabled;
        }

        // CharacterHandle.ApplyStocking(int type) — async UniTask だが
        // Harmony は外側 stub に Postfix を掛けられ、引数は取得可能。
        // 既存 DisableStockingPatch が Prefix で __0 を書き換えるが、
        // Postfix はその書換え後の値を見る（記録は「画面に表示される値」の方が整合的）。
        private static void Postfix(CharacterHandle __instance, int __0)
        {
            if (__instance == null) return;
            if (KneeSocksLoader.IsPreloading) return; // マテリアルプリロード中は記録しない
            var id = __instance.GetCharID();
            // KneeSocks 系 override 中は __0 が 0 に注入済み。実際の override 値を記録する。
            // __0 == 0 のみで判定することで、FittingRoom 等が独立して ApplyStocking(non-0) を
            // 呼んだとき KneeSocks override が残っていても誤記録しないようにする。
            int stockingToRecord = __0 == 0
                && StockingOverrideStore.TryGet(id, out var ovStk)
                && StockingOverrideStore.IsKneeSocksType(ovStk)
                ? ovStk : __0;
            WardrobeLastLoadArg.UpdateStocking(id, stockingToRecord);
            if (!WardrobeHistoryGate.ShouldRecord(id)) return;
            StockingViewHistory.MarkViewed(id, stockingToRecord);
        }
    }
}
