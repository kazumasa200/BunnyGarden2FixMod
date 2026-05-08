using BunnyGarden2FixMod.Utils;
using GB.Game;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger.UI;

/// <summary>
/// Wardrobe DEBUG タブの SMR / MagicaCloth スキャン結果と選択状態を保持する。
/// Rebuild(GameObject) でターゲットキャラ配下を再走査する。
///
/// MagicaCloth2 への DLL 直接 reference を避け、AppDomain 走査 + reflection で
/// 型・SerializeData (property) → clothType (field) を 2 段で読む。
/// reflection 失敗時は "?" fallback で行表示は維持（abort せずベストエフォート）。
/// </summary>
internal class DebugInspectorState
{
    public CharID Char { get; private set; } = CharID.NUM;
    public List<SkinnedMeshRenderer> Smrs { get; } = new();
    public List<string> SmrPaths { get; } = new();           // chara root 相対 path
    public List<string> MagicaInfos { get; } = new();        // "path | clothType=Bone..."
    public HashSet<int> CheckedSmrInstanceIds { get; } = new();

    private static Type s_magicaClothType;
    private static bool s_magicaClothTypeResolveAttempted;
    private static PropertyInfo s_serializeDataProp;
    private static FieldInfo s_clothTypeField;

    /// <summary>
    /// 現在の SMR リストの instanceId 集合（dead 整合チェック用）。
    /// </summary>
    public HashSet<int> CurrentSmrInstanceIds()
    {
        var set = new HashSet<int>();
        foreach (var smr in Smrs)
        {
            if (smr != null) set.Add(smr.GetInstanceID());
        }
        return set;
    }

    /// <summary>
    /// 指定キャラ配下を再スキャン。dead instanceId は CheckedSmrInstanceIds から除去する。
    /// chara が null なら全リストクリア + Char = NUM。
    /// </summary>
    public void Rebuild(CharID id, GameObject chara)
    {
        Char = id;
        Smrs.Clear();
        SmrPaths.Clear();
        MagicaInfos.Clear();

        if (chara == null)
        {
            CheckedSmrInstanceIds.Clear();
            return;
        }

        var charaTr = chara.transform;
        foreach (var smr in chara.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr == null) continue;
            Smrs.Add(smr);
            SmrPaths.Add(GetRelativePath(charaTr, smr.transform));
        }

        BuildMagicaInfos(chara);

        var alive = CurrentSmrInstanceIds();
        CheckedSmrInstanceIds.RemoveWhere(idv => !alive.Contains(idv));
        MeshHighlighter.ForgetDeadInstances(alive);
    }

    private void BuildMagicaInfos(GameObject chara)
    {
        var magicaType = ResolveMagicaClothType();
        if (magicaType == null)
        {
            MagicaInfos.Add("(MagicaCloth2 type 未解決)");
            return;
        }

        var components = chara.GetComponentsInChildren(magicaType, true);
        if (components == null || components.Length == 0)
        {
            MagicaInfos.Add("(MagicaCloth component なし)");
            return;
        }

        if (s_serializeDataProp == null)
            s_serializeDataProp = magicaType.GetProperty("SerializeData", BindingFlags.Public | BindingFlags.Instance);

        var charaTr = chara.transform;
        foreach (var c in components)
        {
            if (c == null) continue;
            string path = GetRelativePath(charaTr, c.transform);
            string clothType = ReadClothType(c);
            MagicaInfos.Add($"{path} | clothType={clothType}");
        }
    }

    private static string ReadClothType(Component comp)
    {
        try
        {
            var sd = s_serializeDataProp?.GetValue(comp);
            if (sd == null) return "?";
            if (s_clothTypeField == null || s_clothTypeField.DeclaringType != sd.GetType())
                s_clothTypeField = sd.GetType().GetField("clothType", BindingFlags.Public | BindingFlags.Instance);
            var v = s_clothTypeField?.GetValue(sd);
            return v?.ToString() ?? "?";
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[MeshInspector] clothType reflection 失敗 ({comp?.name}): {ex.Message}");
            return "?";
        }
    }

    private static Type ResolveMagicaClothType()
    {
        if (s_magicaClothType != null) return s_magicaClothType;
        if (s_magicaClothTypeResolveAttempted) return null;
        s_magicaClothTypeResolveAttempted = true;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType("MagicaCloth2.MagicaCloth", false);
                if (t != null) { s_magicaClothType = t; return t; }
            }
            catch { /* 一部 dynamic assembly で例外あり、無視 */ }
        }
        return null;
    }

    private static string GetRelativePath(Transform root, Transform target)
    {
        if (target == null) return "(null)";
        if (target == root) return target.name;
        var sb = new StringBuilder(target.name);
        var t = target.parent;
        while (t != null && t != root)
        {
            sb.Insert(0, "/");
            sb.Insert(0, t.name);
            t = t.parent;
        }
        return sb.ToString();
    }
}
