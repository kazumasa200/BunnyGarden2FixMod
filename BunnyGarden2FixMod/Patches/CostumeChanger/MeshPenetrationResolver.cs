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
    /// 入力 mesh は in-place 変更しない（戻り値で新規 Mesh を返す）。
    /// <paramref name="referenceShape"/> 例: <c>"blendShape_skin_lower.skin_stocking"</c>。
    /// </summary>
    /// <param name="minOffset">stocking 食い込み判定閾値 兼 push 後最小距離 (m)。&lt;= 0 で stocking push 無効。</param>
    /// <param name="skinPushAmount">stocking 押し出し後表面から内側へ skin を置く目標距離 (m)。&lt;= 0 で skin push 無効。</param>
    /// <param name="skinAnchorVerts">falloff anchor (隣接 skin 頂点)。null/空で falloff 無し。</param>
    /// <param name="useSkinNormalForPush">true で skin push axis に skin 自身の外向き法線を使う。
    /// Tops cloth は frill/双面で法線が反転するため cloth 法線軸だと z-fighting 悪化、skin 法線で固定する方が安定。</param>
    /// <param name="clothSampleRadius">skin push pass の cloth 表面推定半径 (距離重み平均)。&lt;= 0 で K=3 固定。
    /// pass 1 (cloth push) は対象外。</param>
    /// <param name="useScatterPush">true で skin push を「scatter from cloth」モードに切替。
    /// 各 cloth 頂点で pushNeeded を計算 → kernel 重み付き平均 displacement を skin に再分配。
    /// waist 縫い目等で隣接 skin SMR 境界の push 量を連続化する目的。kernel: w = (1 - dsq/R²)、R = clothSampleRadius (>0 必須)。</param>
    /// <returns>(adjusted donor, adjusted skin)。null なら no-op。</returns>
    internal static (Mesh donor, Mesh skin) Resolve(
        Mesh donorMesh, Mesh referenceMesh, string referenceShape,
        float minOffset, float skinPushAmount,
        Vector3[] skinAnchorVerts, float skinFalloffRadius,
        string logTag,
        bool useSkinNormalForPush = false,
        float clothSampleRadius = 0f,
        bool useScatterPush = false)
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

            // anchor AABB を計算して、(AABB + falloffRadius) 外の query 頂点は nearest 距離が
            // falloffRadius を超えることが確定 → skinScale=1 (フェード無し) で grid 探索 skip。
            // anchor が compact (face/eye 等) で query 頂点 (skin) が遠方に分散するパターンで
            // SpatialGridIndex.FindNearest が degenerate cellSize で 60 秒級の shell scan に陥る
            // 問題を回避する。
            Vector3 anchorMin = skinAnchorVerts[0], anchorMax = skinAnchorVerts[0];
            for (int i = 1; i < skinAnchorVerts.Length; i++)
            {
                var p = skinAnchorVerts[i];
                if (p.x < anchorMin.x) anchorMin.x = p.x; else if (p.x > anchorMax.x) anchorMax.x = p.x;
                if (p.y < anchorMin.y) anchorMin.y = p.y; else if (p.y > anchorMax.y) anchorMax.y = p.y;
                if (p.z < anchorMin.z) anchorMin.z = p.z; else if (p.z > anchorMax.z) anchorMax.z = p.z;
            }
            float fr = skinFalloffRadius;
            float exMinX = anchorMin.x - fr, exMaxX = anchorMax.x + fr;
            float exMinY = anchorMin.y - fr, exMaxY = anchorMax.y + fr;
            float exMinZ = anchorMin.z - fr, exMaxZ = anchorMax.z + fr;

            var anchorGrid = new SpatialGridIndex(skinAnchorVerts);
            for (int i = 0; i < refVerts.Length; i++)
            {
                var v = refVertsBase[i];
                if (v.x < exMinX || v.x > exMaxX ||
                    v.y < exMinY || v.y > exMaxY ||
                    v.z < exMinZ || v.z > exMaxZ)
                {
                    skinScale[i] = 1f;
                    continue;
                }
                int j = anchorGrid.FindNearest(v);
                if (j < 0) { skinScale[i] = 1f; continue; }
                float d = (v - skinAnchorVerts[j]).magnitude;
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

        // 早期 AABB cull 用の donor 範囲。skin vert がこの外なら nearest donor までの距離 > maxNeighborDist
        // が確定するため、grid 探索 (radius mode で 729 cells/query 等) を skip して outOfRange 同等扱い。
        // 病的ケース (小 donor + 大 skin ref + 大 sampleR) で FindWithinRadius の cell lookup 爆発を防ぐ。
        // 拡張量は maxNeighborDist + minOffset: pass 1 で donor 自身が最大 minOffset 外側に push され得るため、
        // post-push donor 位置を安全側でカバーする (Tops は minOffset=0 で影響無し、Stocking は数 mm 拡張)。
        Vector3 cullMin = Vector3.zero, cullMax = Vector3.zero;
        if (skinPushAmount > 0f && donorGrid != null)
        {
            cullMin = donorVerts[0];
            cullMax = donorVerts[0];
            for (int i = 1; i < donorVerts.Length; i++)
            {
                var p = donorVerts[i];
                if (p.x < cullMin.x) cullMin.x = p.x; else if (p.x > cullMax.x) cullMax.x = p.x;
                if (p.y < cullMin.y) cullMin.y = p.y; else if (p.y > cullMax.y) cullMax.y = p.y;
                if (p.z < cullMin.z) cullMin.z = p.z; else if (p.z > cullMax.z) cullMax.z = p.z;
            }
            float cullExpand = maxNeighborDist + Mathf.Max(0f, minOffset);
            cullMin.x -= cullExpand; cullMin.y -= cullExpand; cullMin.z -= cullExpand;
            cullMax.x += cullExpand; cullMax.y += cullExpand; cullMax.z += cullExpand;
        }
        if (skinPushAmount > 0f && donorGrid != null && hasDonorNormals && useScatterPush)
        {
            // ===== scatter from cloth mode =====
            // 各 skin 頂点 V について、半径 R 内の全 cloth 頂点を集めて kernel 重み付き
            // displacement 平均を計算する。同 cloth 周辺にいる複数 skin 頂点 (waist 縫い目で
            // 接する upper/lower 等) が同じ cloth 集合から類似の displacement を得て連続化。
            // R = clothSampleRadius (>0)、kernel = 1 - dsq/R² (sqrt 不要)。
            float scatterRadius = clothSampleRadius > 0f ? clothSampleRadius : maxNeighborDist;
            float scatterRadiusSq = scatterRadius * scatterRadius;
            float invertGuardLocal = invertGuard;

            long pStart = sw.ElapsedMilliseconds;
            for (int i = 0; i < refVerts.Length; i++)
            {
                var s = refVerts[i];

                if (s.x < cullMin.x || s.x > cullMax.x ||
                    s.y < cullMin.y || s.y > cullMax.y ||
                    s.z < cullMin.z || s.z > cullMax.z)
                {
                    skinOutOfRange++;
                    continue;
                }

                neighbors.Clear();
                donorGrid.FindWithinRadius(s, scatterRadius, neighbors);
                if (neighbors.Count == 0) continue;

                float scaledPush = skinPushAmount * skinScale[i];
                if (scaledPush <= 0f) continue;

                Vector3 dispSum = Vector3.zero;
                float wSum = 0f;
                int contribs = 0;

                for (int k = 0; k < neighbors.Count; k++)
                {
                    int nj = neighbors[k];
                    var vp = donorVerts[nj] + donorDisp[nj];
                    float dsq = (vp - s).sqrMagnitude;
                    if (dsq > scatterRadiusSq) continue;
                    float w = 1f - dsq / scatterRadiusSq;
                    if (w <= 1e-6f) continue;

                    var vn = donorNormals[nj];
                    Vector3 pushAxis = useSkinNormalForPush ? refNormals[i] : vn;

                    // gather mode と同じ符号定義を維持: signedD = Vector3.Dot(vp - s, pushAxis)。
                    //   signedD > 0 → s は cloth (vp) の -pushAxis 側 = 内側
                    //   signedD < 0 → s が cloth を突き抜けて外側
                    //   signedD < invertGuard (大きく負) → 反転ガード (cloth 法線逆向き)
                    float signedD = Vector3.Dot(vp - s, pushAxis);
                    if (signedD < invertGuardLocal) { skinSkippedInverted++; continue; }

                    // この cloth 頂点 vp に対して「s を scaledPush 内側まで押すなら」の必要 push 量
                    // pushNeeded = scaledPush - signedD
                    //   signedD = 0 (s 表面): scaledPush
                    //   signedD > 0 (内側): 不足分のみ
                    //   signedD < 0 (突き抜け): scaledPush + |signedD|
                    //   signedD ≥ scaledPush: 既に十分内側 → 0 寄与
                    float pushNeeded = scaledPush - signedD;
                    if (pushNeeded <= 0f)
                    {
                        // 寄与は 0 だが kernel 重みは加算する (周辺 cloth が「もう押す必要なし」と
                        // 判断しているなら平均 displacement も小さくなるべき)。
                        wSum += w;
                        continue;
                    }
                    dispSum += -pushAxis * (pushNeeded * w);
                    wSum += w;
                    contribs++;
                }

                if (wSum <= 0f || contribs == 0) continue;

                Vector3 disp = dispSum / wSum;
                float dispMag = disp.magnitude;
                if (dispMag <= 0f) continue;
                // dispMag は kernel 平均で scaledPush を超えないが、丸め誤差で僅かに超えうるので cap。
                if (dispMag > scaledPush)
                {
                    disp *= scaledPush / dispMag;
                    dispMag = scaledPush;
                }
                skinDisp[i] = disp;
                skinHits++;

                int band = skinScale[i] < bandBoundaryMax ? 0 : (skinScale[i] < bandMidMax ? 1 : 2);
                bandCount[band]++;
                bandSum[band] += dispMag;
                if (dispMag > bandMax[band]) bandMax[band] = dispMag;
            }
            skinDetectMs = sw.ElapsedMilliseconds - pStart;
        }
        else if (skinPushAmount > 0f && donorGrid != null && hasDonorNormals)
        {
            // ===== gather mode (既存) =====
            long pStart = sw.ElapsedMilliseconds;
            for (int i = 0; i < refVerts.Length; i++)
            {
                var s = refVerts[i];

                // 早期 AABB cull: donor AABB ± maxNeighborDist の外なら nearest donor までの距離 > maxNeighborDist
                // 確定なので、後段 outOfRange と同等扱いで grid 探索を skip する。
                if (s.x < cullMin.x || s.x > cullMax.x ||
                    s.y < cullMin.y || s.y > cullMax.y ||
                    s.z < cullMin.z || s.z > cullMax.z)
                {
                    skinOutOfRange++;
                    continue;
                }

                Vector3 vAvg, vnAvg;

                // clothSampleRadius > 0: 半径内の全 cloth 頂点を採用 (MeshDistancePreserver.skinSampleRadius と同方針)。
                //   半径内に頂点が無ければ K=3 にフォールバック。skin 表面推定の頂点単位ノイズを smoothing。
                // clothSampleRadius <= 0: K=3 固定 (Stocking 既存挙動)。
                neighbors.Clear();
                bool radiusMode = clothSampleRadius > 0f;
                if (radiusMode)
                {
                    donorGrid.FindWithinRadius(s, clothSampleRadius, neighbors);
                    if (neighbors.Count == 0)
                        donorGrid.FindKNearest(s, neighborK, neighbors);
                }
                else
                {
                    donorGrid.FindKNearest(s, neighborK, neighbors);
                }
                if (neighbors.Count == 0) continue;
                {
                    // カバー外チェック: K=3 モードでは scratch[0] が必ず最近傍だが、radius モードでは
                    // 必ずしも先頭が最近傍とは限らないため、radius モード時は全件 scan で最小距離を再判定。
                    int n0 = neighbors[0];
                    var vp0 = donorVerts[n0] + donorDisp[n0];
                    float n0DistSq = (vp0 - s).sqrMagnitude;
                    if (n0DistSq > maxNeighborDist * maxNeighborDist)
                    {
                        bool outOfRange = true;
                        if (radiusMode)
                        {
                            float minDistSq = n0DistSq;
                            for (int k = 1; k < neighbors.Count; k++)
                            {
                                var vpK = donorVerts[neighbors[k]] + donorDisp[neighbors[k]];
                                float dsq = (vpK - s).sqrMagnitude;
                                if (dsq < minDistSq) minDistSq = dsq;
                            }
                            outOfRange = minDistSq > maxNeighborDist * maxNeighborDist;
                        }
                        if (outOfRange)
                        {
                            skinOutOfRange++;
                            continue;
                        }
                    }
                }
                if (!radiusMode && neighbors.Count < neighborK) skinFallback++;

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

                // 押し方向 axis を選択:
                //   useSkinNormalForPush=false (Stocking 既定): cloth 外向き法線 vnAvg を使う。
                //     stocking のように tight な単面 cloth では skin 法線とほぼ一致するため安定。
                //   useSkinNormalForPush=true (Tops 等): skin 外向き法線 refNormals[i] を使う。
                //     mesh_costume の frill / sleeve / 双面ジオメトリで cloth 法線が局所反転すると
                //     -vnAvg が外側を向き、skin が cloth 方向 (= 外側) へ押されて逆効果になるため、
                //     skin 法線基準で押し方向を固定する。
                Vector3 pushAxis = useSkinNormalForPush ? refNormals[i] : vnAvg;

                // signedD = dot(vAvg - s, pushAxis): pushAxis 軸上での「skin から cloth surface vAvg までの符号付き距離」。
                //   signedD > 0 → skin は cloth 表面 (vAvg) の -pushAxis 側 = 内側にいる（通常）
                //   signedD = 0 → skin はちょうど cloth 表面上
                //   signedD < 0 → skin が cloth を突き抜けて外側に出ている（食い込み）
                float signedD = Vector3.Dot(vAvg - s, pushAxis);
                if (signedD < invertGuard) { skinSkippedInverted++; continue; }

                float scaledPush = skinPushAmount * skinScale[i];
                if (scaledPush <= 0f) continue;

                // まず skin を cloth 押し出し後表面 (vAvg) まで揃え、そこから更に
                // scaledPush だけ内側 (-pushAxis 方向) に押し込む。
                // 目標位置 = vAvg + (-pushAxis) * scaledPush
                // 法線方向の必要変位 = scaledPush - signedD
                //   signedD < 0 (突き抜け): scaledPush + |signedD| をフル push
                //   0 <= signedD < scaledPush: 不足分のみ push
                //   signedD >= scaledPush: 既に十分内側 → 何もしない（外へ引き戻さない）
                float pushDist = scaledPush - signedD;
                if (pushDist > 0f)
                {
                    skinDisp[i] = -pushAxis * pushDist;
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
