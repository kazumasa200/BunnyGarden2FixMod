using BunnyGarden2FixMod.Utils;
using GB.Scene;
using HarmonyLib;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// <see cref="CharacterHandle.Preload"/> の "Already loaded !!" 早期 return 経路 (= flag=true)
/// では <c>setup()</c> が呼ばれず <see cref="TopsSetupPatch"/> Postfix も発火しない。
///
/// 同伴イベント等で env scene を切り替えても <c>holeScene</c> の <c>CharacterHandle</c> は
/// preserve されるため、Bar 復帰時に同 char/costume の Preload は flag=true 経路で抜ける。
/// この場合、<see cref="TopsLoader"/> の Apply 再 trigger が無くなり、scene unload で
/// <c>s_applied</c> / <c>s_targetSnapshots</c> をクリアした後の状態と乖離して
/// override が視覚的に解除されるケースがある。
///
/// この fallback Postfix は <c>m_chara</c> が既ロード状態 (= flag=true パス) のときのみ
/// <see cref="TopsLoader.ApplyIfOverridden"/> を呼ぶ。flag=false パスでは Postfix 時点で
/// <c>m_chara==null</c> (Unload 直後 + async Load 開始) なので skip し、後続の
/// <see cref="TopsSetupPatch"/> Postfix に Apply を委ねる。
/// </summary>
[HarmonyPatch(typeof(CharacterHandle), nameof(CharacterHandle.Preload))]
internal static class TopsPreloadFallbackPatch
{
    private static bool Prepare()
    {
        bool enabled = Configs.CostumeChangerEnabled?.Value ?? true;
        if (enabled) PatchLogger.LogInfo("[TopsPreloadFallbackPatch] 適用");
        return enabled;
    }

    private static void Postfix(CharacterHandle __instance)
    {
        // Chara==null は flag=false 経路 (Unload 直後 + 非同期 Load 開始の同期セクション)。後続の setup() Postfix で Apply される。
        // flag=true 経路では Chara が常に non-null。両経路は m_chara の null 状態で実用上分離可能。
        if (__instance?.Chara == null) return;
        // 同 InstanceID で再 Apply trigger となる場合は TopsLoader 側の s_applied dedup で skip される。
        // s_applied / s_targetSnapshots は OnSceneUnloaded で Clear されないため、preserve されている
        // target には snapshot が残り、Restore の OriginalMesh が donor mesh で上書きされる事故は起きない。
        TopsLoader.ApplyIfOverridden(__instance);
    }
}
