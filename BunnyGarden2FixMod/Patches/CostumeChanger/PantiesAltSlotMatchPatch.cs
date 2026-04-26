using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BunnyGarden2FixMod.Utils;
using GB;
using GB.Game;
using GB.Scene;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// 水着 / バニーガールでも Panties 切替が反映されるよう、
/// CharacterHandle.findPantiesMaterialIndex を拡張するフォールバックパッチ。
/// 加えて override 解除時に元の肌色 panty material を復元する。
/// </summary>
[HarmonyPatch(typeof(CharacterHandle), "findPantiesMaterialIndex")]
public static class PantiesAltSlotMatchPatch
{
    // 末尾境界 (_|$) で m_panties_skinny_* 等への誤マッチを防ぐ。
    private static readonly Regex AltSlotRegex = new(@"m_panties_(skin|bunny)(_|$)", RegexOptions.Compiled);

    private static readonly AccessTools.FieldRef<CharacterHandle, CharID> s_idRef = ResolveIdRef();
    private static readonly AccessTools.FieldRef<CharacterHandle, CharacterHandle.LoadArg> s_lastLoadArgRef = ResolveLastLoadArgRef();

    private static readonly Dictionary<CharID, CapturedOriginal> s_originalCache = new();

    private struct CapturedOriginal
    {
        public int SlotIndex;
        public Material Material;
        public CostumeType Costume;
    }

    private static AccessTools.FieldRef<CharacterHandle, CharID> ResolveIdRef()
    {
        try { return AccessTools.FieldRefAccess<CharacterHandle, CharID>("m_id"); }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[PantiesAltSlotMatch] m_id FieldRef 取得失敗: {ex.Message}");
            return null;
        }
    }

    private static AccessTools.FieldRef<CharacterHandle, CharacterHandle.LoadArg> ResolveLastLoadArgRef()
    {
        try { return AccessTools.FieldRefAccess<CharacterHandle, CharacterHandle.LoadArg>("m_lastLoadArg"); }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[PantiesAltSlotMatch] m_lastLoadArg FieldRef 取得失敗: {ex.Message}");
            return null;
        }
    }

    // 将来 m_panties_skin/bunny を使う新コスが追加されたらここに足すこと（無いと cache 誤無効化で復元バグ）。
    private static bool IsAltSlotCostume(CostumeType c) =>
        c == CostumeType.SwimWear || c == CostumeType.Bunnygirl;

    private static bool TryGetCostume(CharacterHandle handle, out CostumeType costume)
    {
        costume = default;
        if (handle == null || s_lastLoadArgRef == null) return false;
        try
        {
            var arg = s_lastLoadArgRef(handle);
            if (arg == null) return false;
            costume = arg.Costume;
            return true;
        }
        catch { return false; }
    }

    static bool Prepare()
    {
        PatchLogger.LogInfo("[PantiesAltSlotMatch] パッチ適用");
        return true;
    }

    static void Postfix(CharacterHandle __instance, Material[] mat, ref int __result)
    {
        if (!Plugin.ConfigPantiesAltSlotMatch.Value) return;

        bool hasId = TryGetCharID(__instance, out var charId);
        bool hasCostume = TryGetCostume(__instance, out var costume);

        // 衣装変更検知: SlotIndex は衣装ごとに anatomical 意味が違う（例: KANA SwimWear slot[0]=panty,
        // KANA Bunnygirl slot[0]=skin）。古いキャッシュを別衣装で書き戻すと skin スロットに panty
        // material を当てて肌が崩れるため、衣装が変わったら無効化して fallback 再キャプチャに任せる。
        if (hasId && hasCostume && s_originalCache.TryGetValue(charId, out var existing)
            && existing.Costume != costume)
        {
            s_originalCache.Remove(charId);
            PatchLogger.LogDebug($"[PantiesAltSlotMatch] cache invalidate (costume change): char={charId}, {existing.Costume}→{costume}");
        }

        // override 適用後の slot は m_panties_a_* となり vanilla regex にマッチして __result>=0 になる。
        // この時もキャッシュ維持しないと「2 回目 override → clear で復元できない」バグになるため、
        // 通常コス (Casual/Uniform 等) のときだけ無効化する。
        if (__result >= 0)
        {
            if (hasId && hasCostume && !IsAltSlotCostume(costume))
                s_originalCache.Remove(charId);
            return;
        }

        if (mat == null) return;

        // Postfix の例外漏れは ReloadPanties を巻き込んで装備不適用 (可視的失敗) になるため握りつぶす。
        try
        {
            if (Plugin.ConfigPantiesAltSlotOverrideOnly.Value)
            {
                if (!hasId) return;
                if (!PantiesOverrideStore.TryGet(charId, out _, out _)) return;
            }

            for (int i = 0; i < mat.Length; i++)
            {
                var m = mat[i];
                if (m == null) continue;
                if (AltSlotRegex.IsMatch(m.name))
                {
                    // costume 不明だと invalidate ロジックで誤判定 (default(CostumeType) は enum 0 値の
                    // 衣装と偶発一致の可能性) のため、確実に取れるときだけキャッシュする。
                    if (hasId && hasCostume && !s_originalCache.ContainsKey(charId))
                    {
                        s_originalCache[charId] = new CapturedOriginal
                        {
                            SlotIndex = i,
                            Material = m,
                            Costume = costume,
                        };
                        PatchLogger.LogDebug($"[PantiesAltSlotMatch] capture: char={charId}, slot={i}, mat='{m.name}', costume={costume}, mat.Length={mat.Length}");
                    }
                    __result = i;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[PantiesAltSlotMatch] フォールバック検索失敗: {ex.Message}");
        }
    }

    /// <summary>override 解除時にキャッシュ済み元 material をスロットへ書き戻す。</summary>
    internal static void TryRestoreOriginal(CharID id)
    {
        if (!s_originalCache.TryGetValue(id, out var entry))
        {
            PatchLogger.LogDebug($"[PantiesAltSlotMatch] restore skip (no cache): char={id}");
            return;
        }

        if (entry.Material == null)
        {
            PatchLogger.LogDebug($"[PantiesAltSlotMatch] restore skip (fake-null mat): char={id}, slot={entry.SlotIndex}");
            s_originalCache.Remove(id);
            return;
        }

        try
        {
            var env = GBSystem.Instance?.GetActiveEnvScene();
            var charObj = env?.FindCharacter(id);
            if (charObj == null)
            {
                PatchLogger.LogDebug($"[PantiesAltSlotMatch] restore skip (no charObj): char={id}");
                return; // シーン外: 次の機会に回す
            }

            var lower = charObj.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .FirstOrDefault(x => x != null && x.name == "mesh_skin_lower");
            if (lower == null) return;

            var materials = lower.materials;
            if (entry.SlotIndex < 0 || entry.SlotIndex >= materials.Length)
            {
                PatchLogger.LogDebug($"[PantiesAltSlotMatch] restore skip (slot OOB): char={id}, slot={entry.SlotIndex}, mat.Length={materials.Length}");
                s_originalCache.Remove(id);
                return;
            }

            var meshName = lower.sharedMesh != null ? lower.sharedMesh.name : "<null>";
            var beforeName = materials[entry.SlotIndex] != null ? materials[entry.SlotIndex].name : "<null>";
            PatchLogger.LogDebug($"[PantiesAltSlotMatch] restore: char={id}, slot={entry.SlotIndex}, before='{beforeName}', after='{entry.Material.name}', sharedMesh='{meshName}', mat.Length={materials.Length}");

            materials[entry.SlotIndex] = entry.Material;
            lower.materials = materials;
            s_originalCache.Remove(id);
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[PantiesAltSlotMatch] 元 material 復元失敗: {ex.Message}");
        }
    }

    // シーン跨ぎで Material instance が無効化されると fake-null ガードで cache 消費 → 復元不発になるため、
    // 境界で全クリアして fallback 再キャプチャに任せる。Plugin.Awake で sceneUnloaded に subscribe。
    internal static void OnSceneUnloaded(Scene scene)
    {
        s_originalCache.Clear();
    }

    private static bool TryGetCharID(CharacterHandle handle, out CharID id)
    {
        id = default;
        if (handle == null || s_idRef == null) return false;
        id = s_idRef(handle);
        return true;
    }
}

[HarmonyPatch(typeof(PantiesOverrideStore), nameof(PantiesOverrideStore.Clear))]
public static class PantiesAltSlotRestoreOnClearPatch
{
    static void Postfix(CharID id) => PantiesAltSlotMatchPatch.TryRestoreOriginal(id);
}
