using System.Collections.Generic;
using BunnyGarden2FixMod.Utils;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// 指定 blendShape の delta を、anchor 頂点群からの距離で線形フェードさせるユーティリティ。
/// 各頂点で scale = clamp01(distToNearestAnchor / falloffRadius)（anchor 直近で 0、半径以上で 1）。
///
/// Unity API は既存 blendShape frame を直接編集できないので、
/// 1) 全 shape を読み出し、2) ClearBlendShapes、3) 対象 shape は scale 倍した delta で、
/// 非対象 shape は元 delta で AddBlendShapeFrame し直す形で実現する。
///
/// 想定用途: 水着 swim mesh_skin_lower の skin_stocking 系 blendShape を、
/// 隣接 mesh（mesh_skin_upper など）の境界付近でだけフェードさせ、ウエスト等で段差を出さない。
/// </summary>
internal static class BlendShapeFalloffApplier
{
    /// <summary>
    /// <paramref name="mesh"/> の <paramref name="shapesToScale"/> の各 shape 名について、
    /// 全 frame の delta を per-vertex で <paramref name="falloffRadius"/> に基づきフェードする。
    /// 対象外 shape はそのまま再 add される。
    /// </summary>
    /// <param name="mesh">対象 mesh。in-place で blendShape を書き換える。</param>
    /// <param name="shapesToScale">フェード対象 shape 名リスト。mesh に存在しなければ無視。</param>
    /// <param name="anchorVerts">距離計算に使う anchor 頂点列（mesh と同 mesh-local 座標系前提）。</param>
    /// <param name="falloffRadius">フェード半径 (m)。&lt;= 0 で no-op。</param>
    /// <param name="logTag">ログ用タグ。</param>
    /// <returns>スケールを適用した shape の数。0 なら何もしていない。</returns>
    internal static int Apply(Mesh mesh, IReadOnlyList<string> shapesToScale, Vector3[] anchorVerts, float falloffRadius, string logTag)
    {
        if (mesh == null || mesh.vertexCount == 0) return 0;
        if (shapesToScale == null || shapesToScale.Count == 0) return 0;
        if (anchorVerts == null || anchorVerts.Length == 0) return 0;
        if (falloffRadius <= 0f) return 0;

        int shapeCount = mesh.blendShapeCount;
        if (shapeCount == 0) return 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var verts = mesh.vertices;
        var anchorGrid = new SpatialGridIndex(anchorVerts);
        var scale = new float[verts.Length];

        long anchorStart = sw.ElapsedMilliseconds;
        int faded = 0;
        float minScale = 1f;
        for (int i = 0; i < verts.Length; i++)
        {
            int j = anchorGrid.FindNearest(verts[i]);
            if (j < 0) { scale[i] = 1f; continue; }
            float d = (verts[i] - anchorVerts[j]).magnitude;
            float sc = Mathf.Clamp01(d / falloffRadius);
            scale[i] = sc;
            if (sc < 0.999f) faded++;
            if (sc < minScale) minScale = sc;
        }
        long anchorMs = sw.ElapsedMilliseconds - anchorStart;

        // 既存 shape を全件読み出し（対象 shape は scale 倍する）
        var scaleSet = new HashSet<string>(shapesToScale);
        var saved = new List<(string name, List<(float weight, Vector3[] dv, Vector3[] dn, Vector3[] dt)> frames)>(shapeCount);
        int scaledShapes = 0;
        for (int s = 0; s < shapeCount; s++)
        {
            string name = mesh.GetBlendShapeName(s);
            bool shouldScale = scaleSet.Contains(name);
            int frameCount = mesh.GetBlendShapeFrameCount(s);
            var frames = new List<(float, Vector3[], Vector3[], Vector3[])>(frameCount);
            for (int f = 0; f < frameCount; f++)
            {
                float w = mesh.GetBlendShapeFrameWeight(s, f);
                var dv = new Vector3[verts.Length];
                var dn = new Vector3[verts.Length];
                var dt = new Vector3[verts.Length];
                mesh.GetBlendShapeFrameVertices(s, f, dv, dn, dt);
                if (shouldScale)
                {
                    // dv は変位、dn/dt は方向。一様スケール (0..1) ならシェーディング上は近似で問題ないが、
                    // 完全に正規直交を保つわけではないので、極端な強調が必要なら別途 RecalculateNormals を検討する。
                    for (int i = 0; i < verts.Length; i++)
                    {
                        float sc = scale[i];
                        dv[i] *= sc;
                        dn[i] *= sc;
                        dt[i] *= sc;
                    }
                }
                frames.Add((w, dv, dn, dt));
            }
            if (shouldScale) scaledShapes++;
            saved.Add((name, frames));
        }

        // Clear して同じ順序で再 add（index と name の対応を保つ）
        mesh.ClearBlendShapes();
        foreach (var (name, frames) in saved)
        {
            foreach (var (w, dv, dn, dt) in frames)
            {
                mesh.AddBlendShapeFrame(name, w, dv, dn, dt);
            }
        }

        sw.Stop();
        PatchLogger.LogDebug(
            $"[{logTag}] blendShape falloff: mesh={mesh.name} verts={verts.Length} scaled={scaledShapes}/{shapeCount} radius={falloffRadius:F4}m anchors={anchorVerts.Length} faded={faded} minScale={minScale:F2} anchor={anchorMs}ms total={sw.ElapsedMilliseconds}ms");

        return scaledShapes;
    }
}
