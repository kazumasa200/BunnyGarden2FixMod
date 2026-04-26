using BunnyGarden2FixMod.Utils;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// donor mesh (mesh_stockings) と reference mesh (swim mesh_skin_lower) の食い込みを
/// 2 パスの近傍探索で検出し、両側に押し出して解消するユーティリティ。
///
/// pass 1 (stocking push): donor 頂点ごとに skin 表面までの符号付き距離 signedD を計算。
///   signedD &lt; minOffset の場合、donor を法線方向に (minOffset - signedD) だけ外側へ push。
///   signedD &gt;= minOffset の場合は不変。
///
/// pass 2 (skin push): skin 頂点ごとに、pass1 で押し出された後の donor 表面までの
///   符号付き距離 signedD を計算。
///   目標は「stocking 表面 (vAvg) からさらに skinPushAmount だけ内側に skin を置く」こと。
///   必要押し込み量 = skinPushAmount - signedD（signedD は内側で正、突き抜けで負）。
///     突き抜け (signedD &lt; 0) → skinPushAmount + |signedD| のフル押し込み
///     不足 (0 &lt;= signedD &lt; skinPushAmount) → 不足分のみ
///     既に十分内側 (signedD &gt;= skinPushAmount) → 何もしない（外へ引き戻さない）
///
/// 旧 MeshSurfaceOffsetAdjuster + MeshSurfaceShrinker を統合した上位互換。
/// 検出を 1 回しか走らせないため一様 shrink より高速かつ局所的に作用する。
/// </summary>
internal static class MeshPenetrationResolver
{
    /// <summary>
    /// donor と reference を 1 パスで処理し、食い込みを解消した両 mesh を返す。
    /// </summary>
    /// <param name="donorMesh">補正対象 donor (mesh_stockings 元)。変更されない。</param>
    /// <param name="referenceMesh">基準 mesh (swim mesh_skin_lower の transplanted 後)。変更されない。</param>
    /// <param name="referenceShape">基準 mesh の weight=100 適用 blendShape 名 (例 "blendShape_skin_lower.skin_stocking")。</param>
    /// <param name="minOffset">stocking 食い込み判定閾値 兼 stocking push 後の最小距離 (m)。&lt;= 0 で stocking push 無効。</param>
    /// <param name="skinPushAmount">stocking 押し出し後表面から内側へ skin を置く目標距離 (m)。
    /// 突き抜けがあれば「表面まで揃える + skinPushAmount」のフル押し込みになる。&lt;= 0 で skin push 無効。</param>
    /// <param name="skinAnchorVerts">skin push の falloff anchor (隣接 skin mesh 頂点)。null/空なら falloff 無し。</param>
    /// <param name="skinFalloffRadius">skin push の境界フェード半径 (m)。&lt;= 0 で falloff 無し。</param>
    /// <param name="logTag">ログ出力タグ。</param>
    /// <returns>(adjusted donor, adjusted skin)。それぞれ null なら no-op（差し替え不要）。</returns>
    internal static (Mesh donor, Mesh skin) Resolve(
        Mesh donorMesh, Mesh referenceMesh, string referenceShape,
        float minOffset, float skinPushAmount,
        Vector3[] skinAnchorVerts, float skinFalloffRadius,
        string logTag)
    {
        if (donorMesh == null || referenceMesh == null) return (null, null);
        if (minOffset <= 0f && skinPushAmount <= 0f) return (null, null);

        var donorVerts = donorMesh.vertices;
        if (donorVerts.Length == 0) return (null, null);
        var donorNormals = donorMesh.normals;
        bool hasDonorNormals = donorNormals != null && donorNormals.Length == donorVerts.Length;

        var refVerts = referenceMesh.vertices;
        var refNormals = referenceMesh.normals;
        if (refVerts.Length == 0 || refNormals.Length != refVerts.Length) return (null, null);

        // falloff 距離は blendShape 適用前の base 座標で測る (skinAnchorVerts も base のため、
        // post-push の refVerts と比較すると境界で常に push 量だけ離れて falloff が無効化する)。
        bool needFalloffBase = skinPushAmount > 0f && skinAnchorVerts != null && skinAnchorVerts.Length > 0 && skinFalloffRadius > 0f;
        Vector3[] refVertsBase = needFalloffBase ? (Vector3[])refVerts.Clone() : null;

        // referenceShape weight=100 を再現する
        if (!string.IsNullOrEmpty(referenceShape))
        {
            int idx = referenceMesh.GetBlendShapeIndex(referenceShape);
            if (idx < 0 || referenceMesh.GetBlendShapeFrameCount(idx) == 0)
            {
                PatchLogger.LogWarning(
                    $"[{logTag}] reference blendShape '{referenceShape}' が見つからない (referenceMesh={referenceMesh.name})、補正スキップ");
                return (null, null);
            }
            int lastFrame = referenceMesh.GetBlendShapeFrameCount(idx) - 1;
            var dv = new Vector3[refVerts.Length];
            var dn = new Vector3[refVerts.Length];
            var dt = new Vector3[refVerts.Length];
            referenceMesh.GetBlendShapeFrameVertices(idx, lastFrame, dv, dn, dt);
            for (int i = 0; i < refVerts.Length; i++)
            {
                refVerts[i] += dv[i];
                refNormals[i] = (refNormals[i] + dn[i]).normalized;
            }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        long gridStart = sw.ElapsedMilliseconds;
        var refGrid = new SpatialGridIndex(refVerts);
        var donorGrid = skinPushAmount > 0f ? new SpatialGridIndex(donorVerts) : null;
        long gridMs = sw.ElapsedMilliseconds - gridStart;

        // skinScale[i] = clamp(distToAnchor / falloffRadius, 0, 1)
        var skinScale = new float[refVerts.Length];
        long anchorMs = 0;
        if (needFalloffBase)
        {
            long aStart = sw.ElapsedMilliseconds;
            var anchorGrid = new SpatialGridIndex(skinAnchorVerts);
            for (int i = 0; i < refVerts.Length; i++)
            {
                int j = anchorGrid.FindNearest(refVertsBase[i]);
                if (j < 0) { skinScale[i] = 1f; continue; }
                float d = (refVertsBase[i] - skinAnchorVerts[j]).magnitude;
                skinScale[i] = Mathf.Clamp01(d / skinFalloffRadius);
            }
            anchorMs = sw.ElapsedMilliseconds - aStart;
        }
        else
        {
            for (int i = 0; i < refVerts.Length; i++) skinScale[i] = 1f;
        }

        // invertGuard: 法線逆向きや別パーツへの誤近傍ヒットを弾く
        const float invertGuardMul = 10f;
        float invertGuardBase = Mathf.Max(minOffset, skinPushAmount);
        float invertGuard = -invertGuardBase * invertGuardMul;

        // 近傍重み付け補間: K-nearest を逆距離² 重みで補間して surface 推定点を出す。
        // K=3 にすると三角形パッチ相当の重み付けになり、頂点位置がズレていても
        // 局所表面に近い position/normal が得られる。
        const int neighborK = 3;
        const float weightEps = 1e-8f;

        var donorDisp = new Vector3[donorVerts.Length];
        var skinDisp = new Vector3[refVerts.Length];
        var neighbors = new System.Collections.Generic.List<int>(16);

        // ------- pass 1: stocking push (donor 頂点 → 近傍 skin の重み付け平均) -------
        int pushed = 0, skippedInverted = 0, stockingFallback = 0;
        float maxStockingPush = 0f;
        long stockingMs = 0;
        if (minOffset > 0f)
        {
            long pStart = sw.ElapsedMilliseconds;
            for (int i = 0; i < donorVerts.Length; i++)
            {
                var v = donorVerts[i];
                Vector3 sAvg, snAvg;

                neighbors.Clear();
                refGrid.FindKNearest(v, neighborK, neighbors);
                if (neighbors.Count == 0) continue;
                if (neighbors.Count < neighborK) stockingFallback++;

                {
                    Vector3 sSum = Vector3.zero, snSum = Vector3.zero;
                    float wSum = 0f;
                    for (int k = 0; k < neighbors.Count; k++)
                    {
                        int nj = neighbors[k];
                        var sp = refVerts[nj];
                        float dsq = (sp - v).sqrMagnitude;
                        float w = 1f / (dsq + weightEps);
                        sSum += sp * w;
                        snSum += refNormals[nj] * w;
                        wSum += w;
                    }
                    sAvg = sSum / wSum;
                    snAvg = snSum.sqrMagnitude > 1e-12f ? snSum.normalized : refNormals[neighbors[0]];
                }

                // signedD = dot(v - sAvg, snAvg): skin 側 reference frame での stocking の位置。
                // snAvg は skin の外向き法線。
                //   signedD > 0 → stocking が skin 表面の +snAvg 側 = 外側にいる（通常）
                //   signedD = 0 → stocking がちょうど skin 表面上
                //   signedD < 0 → stocking が skin に食い込んでいる
                float signedD = Vector3.Dot(v - sAvg, snAvg);
                if (signedD < invertGuard) { skippedInverted++; continue; }

                // まず stocking を skin 表面 (sAvg) まで揃え、そこから更に minOffset
                // だけ外側 (+snAvg 方向) に押し出す。
                // 目標位置 = sAvg + snAvg * minOffset
                // 法線方向の必要変位 = minOffset - signedD
                //   signedD < 0 (食い込み): minOffset + |signedD| をフル push
                //   0 <= signedD < minOffset: 不足分のみ push
                //   signedD >= minOffset: 既に十分外側 → 何もしない（内側へ引き戻さない）
                float pushDist = minOffset - signedD;
                if (pushDist > 0f)
                {
                    donorDisp[i] = snAvg * pushDist;
                    if (pushDist > maxStockingPush) maxStockingPush = pushDist;
                    pushed++;
                }
            }
            stockingMs = sw.ElapsedMilliseconds - pStart;
        }

        // ------- pass 2: skin push (skin 頂点 → 近傍 donor の重み付け平均) -------
        // skin 頂点ごとに直接判定するため、donor 数より skin 数が多くても全頂点をカバーできる。
        // pass1 で押し出された donor 位置 (donorVerts[nj] + donorDisp[nj]) を参照することで、
        // stocking push で既に十分なクリアランスが取れた箇所では skin push が発火しない。
        //
        // 押し込み方向は donor 側の重み付け法線 vnAvg（pass1 の snAvg と対称）を採用する。
        // skin 頂点自体の法線 sn を使うと、近傍 donor 群と方向が乖離して押し込みが横方向に
        // 流れることがあるため。
        int skinHits = 0, skinSkippedInverted = 0, skinFallback = 0, skinOutOfRange = 0;
        // donor のカバー外 (例: ストッキング上端より上の腹部) の skin 頂点を弾く。
        // 遠方 donor の K-nearest 平均は仮想 surface が歪んで偽 push を生む。
        // 閾値は donor mesh 平均エッジ長 ~2-3mm の 3-4 倍。
        const float maxNeighborDist = 0.010f;
        long skinDetectMs = 0;
        // skinScale 帯別 push 量集計: 0=boundary(<0.25) / 1=mid(<0.75) / 2=full(>=0.75)
        const float bandBoundaryMax = 0.25f;
        const float bandMidMax = 0.75f;
        var bandCount = new int[3];
        var bandMax = new float[3];
        var bandSum = new float[3];
        if (skinPushAmount > 0f && donorGrid != null && hasDonorNormals)
        {
            long pStart = sw.ElapsedMilliseconds;
            for (int i = 0; i < refVerts.Length; i++)
            {
                var s = refVerts[i];
                Vector3 vAvg, vnAvg;

                neighbors.Clear();
                donorGrid.FindKNearest(s, neighborK, neighbors);
                if (neighbors.Count == 0) continue;
                {
                    int n0 = neighbors[0];
                    var vp0 = donorVerts[n0] + donorDisp[n0];
                    if ((vp0 - s).sqrMagnitude > maxNeighborDist * maxNeighborDist)
                    {
                        skinOutOfRange++;
                        continue;
                    }
                }
                if (neighbors.Count < neighborK) skinFallback++;

                {
                    Vector3 vSum = Vector3.zero, vnSum = Vector3.zero;
                    float wSum = 0f;
                    for (int k = 0; k < neighbors.Count; k++)
                    {
                        int nj = neighbors[k];
                        var vp = donorVerts[nj] + donorDisp[nj];
                        float dsq = (vp - s).sqrMagnitude;
                        float w = 1f / (dsq + weightEps);
                        vSum += vp * w;
                        vnSum += donorNormals[nj] * w;
                        wSum += w;
                    }
                    vAvg = vSum / wSum;
                    vnAvg = vnSum.sqrMagnitude > 1e-12f ? vnSum.normalized : donorNormals[neighbors[0]];
                }

                // signedD = dot(vAvg - s, vnAvg): donor 側 reference frame での skin の位置。
                // vnAvg は stocking の外向き法線。
                //   signedD > 0 → skin は stocking 表面 (vAvg) の -vnAvg 側 = 内側にいる（通常）
                //   signedD = 0 → skin はちょうど stocking 表面上
                //   signedD < 0 → skin が stocking を突き抜けて外側に出ている（食い込み）
                float signedD = Vector3.Dot(vAvg - s, vnAvg);
                if (signedD < invertGuard) { skinSkippedInverted++; continue; }

                float scaledPush = skinPushAmount * skinScale[i];
                if (scaledPush <= 0f) continue;

                // まず skin を stocking 押し出し後表面 (vAvg) まで揃え、そこから更に
                // scaledPush だけ内側 (-vnAvg 方向) に押し込む。
                // 目標位置 = vAvg + (-vnAvg) * scaledPush
                // 法線方向の必要変位 = scaledPush - signedD
                //   signedD < 0 (突き抜け): scaledPush + |signedD| をフル push
                //   0 <= signedD < scaledPush: 不足分のみ push
                //   signedD >= scaledPush: 既に十分内側 → 何もしない（外へ引き戻さない）
                float pushDist = scaledPush - signedD;
                if (pushDist > 0f)
                {
                    skinDisp[i] = -vnAvg * pushDist;
                    skinHits++;

                    int band = skinScale[i] < bandBoundaryMax ? 0 : (skinScale[i] < bandMidMax ? 1 : 2);
                    bandCount[band]++;
                    bandSum[band] += pushDist;
                    if (pushDist > bandMax[band]) bandMax[band] = pushDist;
                }
            }
            skinDetectMs = sw.ElapsedMilliseconds - pStart;
        }

        // donor 補正 mesh
        Mesh adjDonor = null;
        if (pushed > 0 && minOffset > 0f)
        {
            adjDonor = Object.Instantiate(donorMesh);
            adjDonor.name = donorMesh.name + "_offset";
            for (int i = 0; i < donorVerts.Length; i++)
            {
                donorVerts[i] += donorDisp[i];
            }
            adjDonor.vertices = donorVerts;
            adjDonor.RecalculateNormals();
            adjDonor.RecalculateBounds();
        }

        // skin 補正 mesh
        // base 頂点を直接書き換える（runtime に skin_stocking blendShape weight=100 が
        // 加算されるため、base からの -sn*y で visual 位置が y だけ内側へ寄る）
        Mesh adjSkin = null;
        float maxSkinPush = 0f;
        if (skinHits > 0)
        {
            var skinBaseVerts = referenceMesh.vertices;
            for (int i = 0; i < skinBaseVerts.Length; i++)
            {
                var d = skinDisp[i];
                if (d.sqrMagnitude > 0f)
                {
                    skinBaseVerts[i] += d;
                    float m = d.magnitude;
                    if (m > maxSkinPush) maxSkinPush = m;
                }
            }
            adjSkin = Object.Instantiate(referenceMesh);
            adjSkin.name = referenceMesh.name + "_resolved";
            adjSkin.vertices = skinBaseVerts;
            adjSkin.RecalculateBounds();
            // base normals は意図的に再計算しない（局所的な内側 offset でシェーディングは
            // ほぼ不変。再計算するとアンビエント差が出やすいので避ける）。
        }

        sw.Stop();
        float bandMean0 = bandCount[0] > 0 ? bandSum[0] / bandCount[0] : 0f;
        float bandMean1 = bandCount[1] > 0 ? bandSum[1] / bandCount[1] : 0f;
        float bandMean2 = bandCount[2] > 0 ? bandSum[2] / bandCount[2] : 0f;
        PatchLogger.LogDebug(
            $"[{logTag}] penetration resolve: donor={donorMesh.name}({donorVerts.Length}v) ref={referenceMesh.name}({refVerts.Length}v) " +
            $"stockingPushed={pushed} skippedInv={skippedInverted} fallback={stockingFallback} stockingMax={maxStockingPush:F4}m " +
            $"skinPushed={skinHits} skinSkippedInv={skinSkippedInverted} skinFallback={skinFallback} skinOutOfRange={skinOutOfRange} skinMax={maxSkinPush:F4}m " +
            $"skinBand[B/M/F]: n={bandCount[0]}/{bandCount[1]}/{bandCount[2]} " +
            $"max=({bandMax[0]:F4}/{bandMax[1]:F4}/{bandMax[2]:F4})m " +
            $"mean=({bandMean0:F4}/{bandMean1:F4}/{bandMean2:F4})m " +
            $"grid={gridMs}ms anchor={anchorMs}ms stocking={stockingMs}ms skinDetect={skinDetectMs}ms total={sw.ElapsedMilliseconds}ms");

        return (adjDonor, adjSkin);
    }
}
