using BunnyGarden2FixMod.Utils;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// donorMesh の頂点を referenceMesh（の指定 blendShape weight=100 状態）の表面より
/// minOffset メートル外側に押し出した新 Mesh を返すユーティリティ。
/// referenceShape を指定することで、blendShape による変形後の表面を基準にできる。
/// 元 mesh の topology / UV / bone weight / 既存 blendShape はすべてそのまま保持する。
/// </summary>
internal static class MeshSurfaceOffsetAdjuster
{
    /// <summary>
    /// <paramref name="donorMesh"/> を複製し、<paramref name="referenceMesh"/> の
    /// <paramref name="referenceShape"/> を weight=100 で適用した状態の表面より
    /// <paramref name="minOffset"/> メートル外側に頂点を押し出した新 Mesh を返す。
    /// </summary>
    /// <param name="donorMesh">補正対象 (mesh_stockings 元)。変更されない。</param>
    /// <param name="referenceMesh">基準メッシュ (swim mesh_skin_lower の transplanted 後)。</param>
    /// <param name="referenceShape">基準とする blendShape 名。null/空なら base verts を使う。未存在時は null 返却。</param>
    /// <param name="minOffset">押し出し最小距離 (m)。<= 0 なら no-op で null を返す。</param>
    /// <param name="logTag">ログ出力タグ。</param>
    /// <returns>補正済み新 Mesh。no-op or 入力不正時は null。</returns>
    internal static Mesh Adjust(Mesh donorMesh, Mesh referenceMesh, string referenceShape, float minOffset, string logTag)
    {
        if (donorMesh == null || referenceMesh == null) return null;
        if (minOffset <= 0f) return null;

        var donorVerts = donorMesh.vertices;
        if (donorVerts.Length == 0) return null;

        var refVerts = referenceMesh.vertices;
        var refNormals = referenceMesh.normals;
        if (refVerts.Length == 0 || refNormals.Length != refVerts.Length) return null;

        // referenceShape weight=100 を再現する
        // 指定された場合は必ず存在することを期待。無ければ「肌が縮む前の状態」で push してしまい
        // 過剰補正になるため、warn を出して null 返却で安全側に倒す。
        if (!string.IsNullOrEmpty(referenceShape))
        {
            int idx = referenceMesh.GetBlendShapeIndex(referenceShape);
            if (idx < 0 || referenceMesh.GetBlendShapeFrameCount(idx) == 0)
            {
                PatchLogger.LogWarning(
                    $"[{logTag}] reference blendShape '{referenceShape}' が見つからない (referenceMesh={referenceMesh.name})、補正スキップ");
                return null;
            }
            int lastFrame = referenceMesh.GetBlendShapeFrameCount(idx) - 1;
            var dv = new Vector3[refVerts.Length];
            var dn = new Vector3[refVerts.Length];
            var dt = new Vector3[refVerts.Length];
            referenceMesh.GetBlendShapeFrameVertices(idx, lastFrame, dv, dn, dt);
            for (int i = 0; i < refVerts.Length; i++)
            {
                refVerts[i] += dv[i];
                // base + dn の合成。零ベクトル化した場合 normalized は (0,0,0) を返し、
                // 後段の dot は 0 → push 候補となるが、+sn*0 = s で v が s に吸い寄せられるだけ。
                // dn が極端に逆向きの場合は invertGuard でスキップされる。
                refNormals[i] = (refNormals[i] + dn[i]).normalized;
            }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        long gridStart = sw.ElapsedMilliseconds;
        var grid = new SpatialGridIndex(refVerts);
        long gridMs = sw.ElapsedMilliseconds - gridStart;

        var newMesh = Object.Instantiate(donorMesh);
        newMesh.name = donorMesh.name + "_offset";

        // 内側に深く食い込んでいる頂点（reference 法線が逆向き、または別パーツの裏面に
        // 最近傍が引っかかった等）を「外側へ反転 push」して破綻させないためのガード。
        // signed_d < -invertGuardMul * minOffset の場合は補正スキップ。
        const float invertGuardMul = 10f;
        float invertGuard = -minOffset * invertGuardMul;

        int pushed = 0;
        int skippedInverted = 0;
        float maxPushDist = 0f;
        long nearestStart = sw.ElapsedMilliseconds;
        for (int i = 0; i < donorVerts.Length; i++)
        {
            var v = donorVerts[i];
            int j = grid.FindNearest(v);
            var s = refVerts[j];
            var sn = refNormals[j];

            float signedD = Vector3.Dot(v - s, sn);
            if (signedD < invertGuard)
            {
                skippedInverted++;
                continue;
            }
            if (signedD < minOffset)
            {
                // sn 軸方向にだけ動かす（tangent 成分を保持）。
                // s + sn * minOffset で「最近傍点の真上にスナップ」すると tangent 位置を失って
                // 遠い場所にワープし mesh が崩れる。v + sn * delta なら元の頂点位置からの
                // 最小限の押し出しになり、トポロジーが保たれる。
                float delta = minOffset - signedD;
                donorVerts[i] = v + sn * delta;
                if (delta > maxPushDist) maxPushDist = delta;
                pushed++;
            }
        }
        long nearestMs = sw.ElapsedMilliseconds - nearestStart;

        newMesh.vertices = donorVerts;
        newMesh.RecalculateNormals();
        newMesh.RecalculateBounds();

        sw.Stop();
        PatchLogger.LogInfo(
            $"[{logTag}] surface offset 適用: target={donorMesh.name} verts={donorVerts.Length} pushed={pushed} skippedInv={skippedInverted} maxPush={maxPushDist:F4}m offset={minOffset:F4}m grid={gridMs}ms nearest={nearestMs}ms total={sw.ElapsedMilliseconds}ms");

        if (pushed == 0)
        {
            Object.Destroy(newMesh);
            return null;
        }

        return newMesh;
    }
}
