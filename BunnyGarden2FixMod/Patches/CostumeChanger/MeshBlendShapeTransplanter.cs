using System.Collections.Generic;
using BunnyGarden2FixMod.Utils;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// targetMesh の頂点構造を保ったまま、donorMesh の指定 blendShape delta を
/// nearest-neighbor で転写した新 Mesh を生成する共通ユーティリティ。
/// キャッシュは持たない（呼出し側が個別保持する）。
/// </summary>
internal static class MeshBlendShapeTransplanter
{
    /// <summary>
    /// <paramref name="targetMesh"/> を複製し、<paramref name="donorMesh"/> の
    /// <paramref name="shapeNames"/> に列挙された blendShape を nearest-neighbor で移植した Mesh を返す。
    /// 移植対象 shape が donorMesh に存在しない場合はスキップされる。
    /// </summary>
    /// <param name="targetMesh">移植先メッシュ（変更されない。複製して使用）。</param>
    /// <param name="donorMesh">移植元 blendShape を持つメッシュ。</param>
    /// <param name="shapeNames">移植する blendShape 名のリスト。</param>
    /// <param name="logTag">ログ出力に使うタグ文字列（例: "KneeSocksLoader"）。</param>
    /// <returns>移植済み新メッシュ。移植できる shape がゼロの場合は null を返す。</returns>
    internal static Mesh Transplant(Mesh targetMesh, Mesh donorMesh, IReadOnlyList<string> shapeNames, string logTag)
    {
        // 単一ドナー版は複数ドナー版に委譲
        return Transplant(
            targetMesh,
            new (Mesh donor, IReadOnlyList<string> shapeNames)[] { (donorMesh, shapeNames) },
            logTag);
    }

    /// <summary>
    /// <paramref name="targetMesh"/> を複製し、複数ドナーそれぞれの blendShape を
    /// nearest-neighbor で移植した Mesh を返す。
    /// 各ドナーについて独立した nearest-neighbor マップを計算して frame を追加する。
    /// 移植対象 shape が donorMesh に存在しない場合はスキップされる。
    /// </summary>
    /// <param name="targetMesh">移植先メッシュ（変更されない。複製して使用）。</param>
    /// <param name="donors">ドナーと移植する blendShape 名リストのペア列。</param>
    /// <param name="logTag">ログ出力に使うタグ文字列（例: "SwimWearStockingPatch"）。</param>
    /// <returns>移植済み新メッシュ。移植できる shape がゼロの場合は null を返す。</returns>
    internal static Mesh Transplant(
        Mesh targetMesh,
        IReadOnlyList<(Mesh donor, IReadOnlyList<string> shapeNames)> donors,
        string logTag)
    {
        if (targetMesh == null || donors == null || donors.Count == 0) return null;

        var targetVerts = targetMesh.vertices;
        if (targetVerts.Length == 0) return null;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var newMesh = Object.Instantiate(targetMesh);
        newMesh.name = targetMesh.name + "_transplanted";

        int shapesAdded = 0;
        long nearestMsTotal = 0;

        foreach (var (donorMesh, shapeNames) in donors)
        {
            if (donorMesh == null || donorMesh.blendShapeCount == 0) continue;

            var donorVerts = donorMesh.vertices;
            if (donorVerts.Length == 0) continue;

            // このドナー用 nearest-neighbor 索引: targetVert[i] → donorVert の最近傍 index
            long nearestStart = sw.ElapsedMilliseconds;
            var grid = new SpatialGridIndex(donorVerts);
            var nearestMap = new int[targetVerts.Length];
            for (int i = 0; i < targetVerts.Length; i++)
            {
                nearestMap[i] = grid.FindNearest(targetVerts[i]);
            }
            nearestMsTotal += sw.ElapsedMilliseconds - nearestStart;

            foreach (var shapeName in shapeNames)
            {
                int idx = donorMesh.GetBlendShapeIndex(shapeName);
                if (idx < 0) continue;
                // すでに同名 shape がある場合はスキップ（二重追加防止）
                if (newMesh.GetBlendShapeIndex(shapeName) >= 0) continue;

                int frameCount = donorMesh.GetBlendShapeFrameCount(idx);
                for (int f = 0; f < frameCount; f++)
                {
                    var donorDv = new Vector3[donorMesh.vertexCount];
                    var donorDn = new Vector3[donorMesh.vertexCount];
                    var donorDt = new Vector3[donorMesh.vertexCount];
                    donorMesh.GetBlendShapeFrameVertices(idx, f, donorDv, donorDn, donorDt);

                    var newDv = new Vector3[targetMesh.vertexCount];
                    var newDn = new Vector3[targetMesh.vertexCount];
                    var newDt = new Vector3[targetMesh.vertexCount];
                    for (int k = 0; k < targetMesh.vertexCount; k++)
                    {
                        int src = nearestMap[k];
                        newDv[k] = donorDv[src];
                        newDn[k] = donorDn[src];
                        // tangent delta は 0 のまま（SwimWear 移植と同仕様）
                    }
                    float weight = donorMesh.GetBlendShapeFrameWeight(idx, f);
                    newMesh.AddBlendShapeFrame(shapeName, weight, newDv, newDn, newDt);
                }
                shapesAdded++;
            }
        }

        sw.Stop();
        PatchLogger.LogDebug(
            $"[{logTag}] blendShape 移植完了: target={targetMesh.name} verts={targetVerts.Length} donors={donors.Count} shapes={shapesAdded} nearest={nearestMsTotal}ms total={sw.ElapsedMilliseconds}ms");

        // 移植できた shape が 0 件の場合は不要なメッシュを返さない
        if (shapesAdded == 0)
        {
            Object.Destroy(newMesh);
            return null;
        }

        return newMesh;
    }
}
