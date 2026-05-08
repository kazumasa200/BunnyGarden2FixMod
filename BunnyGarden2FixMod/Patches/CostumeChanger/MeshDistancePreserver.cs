using BunnyGarden2FixMod.Utils;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// donor 服メッシュの per-vert signed normal distance を、donor skin から target skin への差分で補正し、
/// 移植後も donor 元のフィット感を target skin に対して再現するユーティリティ。
///
/// アルゴリズム (3 パス):
///   Pass 1 (cloth → donor skin 初回サンプリング):
///     for each cloth vert v_i:
///       d_donor[i] = signedNormalDistance(v_i, donor_skin)
///
///   Pass 2 (donor skin clipping resolve、minOffset > 0 のときのみ):
///     めり込んでいる cloth vert (d_donor[i] &lt; minOffset) ごとに、近傍 donor skin 頂点を
///     法線方向逆向きに「内側へ凹ませる」ことで d_donor を minOffset まで持ち上げる。
///     skin 頂点単位で複数 cloth vert の deficit を逆距離² 重み平均で集計。
///     cloth vert 自体は触らない → 隣接頂点の topology 関係が完全に保存され、push 量分散による
///     裏面 flip が原理的に発生しない。KNN サンプリングが自然な smoothing を提供する。
///
///   Pass 3 (補正済み donor skin で再サンプリング + 標準 preservation):
///     for each cloth vert v_i:
///       d_donor_eff[i] = signedNormalDistance(v_i, modified_donor_skin)   ≥ minOffset for prev clipping
///       d_target[i]    = signedNormalDistance(v_i, target_skin)
///       push           = (d_donor_eff[i] - d_target[i]) * snAvg_target * skinShare
///       v_i_new        = v_i + push
///
///   Pass 4 (skin 由来 boneWeight 転送、Pass 3 にインライン):
///     for each cloth vert v_i (Pass 3 の donor sample 直後、neighbors KNN 結果を流用):
///       t                = saturate(d_donor_eff / weightFalloffOuter)   // 0=skin, 1=donor
///       skin_bw_avg      = inverse-distance² 重み平均(donor skin verts の remapped boneWeights)
///       blended          = (1-t) * skin_bw_avg + t * donor_bw[i]   // 4-slot top-k 正規化
///       newBoneWeights[i] = blended
///
///   push 方向は target skin 法線を採用（設計目標は「target 視点での距離を donor と一致させる」）。
///   K=3 逆距離² 重み平均で skin 表面位置・法線を推定（MeshPenetrationResolver と同手法）。
///   minOffset == 0 のとき Pass 2 全 skip = 純粋 distance preservation (退化挙動)。
///   weightFalloffOuter == 0 のとき Pass 4 全 skip = boneWeight 転送なし (donor 元の weight を維持)。
///
/// ガード:
///   donor skin / target skin いずれかのカバー外 (最近傍距離 &gt; maxNeighborDist=10mm) → push=0 でスキップ。
///   全頂点 push≈0 なら null を返す（差し替え不要）。
/// </summary>
internal static class MeshDistancePreserver
{
    /// <summary>
    /// donor 服メッシュを per-vert distance preservation で補正した新 Mesh を返す。
    /// donor / target の skin reference は複数 Mesh を結合して 1 つの参照面とする
    /// （上半身 + 下半身を統合し、ワンピース型衣装の下半身頂点も適切な近傍を見つけられるように）。
    /// 結合対象 Mesh のうち null は無視する。
    /// </summary>
    /// <param name="donorCostumeSmr">補正対象 donor 服 SMR (mesh_costume 等)。preload エントリの SMR を渡し、
    /// sharedMesh / bones / boneWeights を読む。SMR 自体は変更されない。</param>
    /// <param name="donorSkinSmrs">donor 側 skin SMR 列 (mesh_skin_upper / mesh_skin_lower 等)。null 要素は無視。</param>
    /// <param name="targetSkinSmrs">target 側 skin SMR 列 (Babydoll 基準)。null 要素は無視。</param>
    /// <param name="maxNeighborDist">K-NN 最近傍探索の最大距離（メートル）。これを超えるとカバー外として skip。</param>
    /// <param name="minOffset">donor skin からの最小距離 safety margin（メートル）。Pass 2 で donor skin を
    /// 部分的に内側へ凹ませることで d_donor を最低この値まで持ち上げる anti-clipping safety。
    /// 0 以下で Pass 2 を完全 skip = 純粋 distance preservation。
    /// (v1.0.5 以前は target 側 floor だったが、隣接頂点間の push 量分散による裏面 flip を防ぐため donor 側 resolve に変更)</param>
    /// <param name="skinSampleRadius">skin 表面推定で半径内の全 skin 頂点を距離重み平均するための半径
    /// （メートル）。0 以下で K=3 固定（既定挙動）。半径内に頂点が無い場合 K=3 にフォールバック。</param>
    /// <param name="weightFalloffOuter">skin 由来 boneWeight 転送 (Pass 4) の falloff 距離（メートル）。
    /// 各 cloth vert で <c>t = saturate(d_donor_eff / weightFalloffOuter)</c> として skin 由来 boneWeight と
    /// donor 元 boneWeight を線形 blend（t=0: 100% skin, t=1: 100% donor）。0 以下で Pass 4 完全 skip。
    /// donor cloth が target body に移植された後、関節曲げで cloth が肌から剥離・突き抜けるのを防ぐ。</param>
    /// <param name="logTag">ログ出力タグ。</param>
    /// <returns>補正済み新 Mesh。全頂点 push が実質ゼロなら null（差し替え不要）。</returns>
    internal static Mesh Preserve(SkinnedMeshRenderer donorCostumeSmr, SkinnedMeshRenderer[] donorSkinSmrs, SkinnedMeshRenderer[] targetSkinSmrs, float maxNeighborDist, float minOffset, float skinSampleRadius, float weightFalloffOuter, string logTag)
    {
        var donorMesh = donorCostumeSmr != null ? donorCostumeSmr.sharedMesh : null;
        if (donorMesh == null)
        {
            PatchLogger.LogWarning($"[{logTag}] distance preserve 中止: donorMesh が null");
            return null;
        }

        var donorVerts   = donorMesh.vertices;
        var donorNormals = donorMesh.normals;
        if (donorVerts.Length == 0)
        {
            PatchLogger.LogWarning($"[{logTag}] distance preserve 中止: donor verts 空 ({donorMesh.name})");
            return null;
        }
        if (donorNormals == null || donorNormals.Length != donorVerts.Length)
        {
            PatchLogger.LogWarning($"[{logTag}] distance preserve 中止: donor normals 不整合 ({donorMesh.name}, normals={donorNormals?.Length ?? -1}/{donorVerts.Length})");
            return null;
        }

        // BoneWeight + 骨名解決 (skinShare scaling 用)。失敗しても scaling 無効化で続行。
        var donorBoneWeights = donorMesh.boneWeights;
        var donorBones = donorCostumeSmr.bones;
        bool boneScalingOk = donorBoneWeights != null && donorBoneWeights.Length == donorVerts.Length
                             && donorBones != null && donorBones.Length > 0;
        if (!boneScalingOk)
            PatchLogger.LogInfo($"[{logTag}] distance preserve: BoneWeight scaling 無効 (boneWeights={donorBoneWeights?.Length ?? -1}/{donorVerts.Length}, bones={donorBones?.Length ?? -1}) — push 量は per-vert 1.0 倍");

        if (!CombineSkinSmrs(donorSkinSmrs,
            out var dSkinVerts, out var dSkinNormals,
            out var dSkinCombinedBw, out var dSkinCombinedBoneNames,
            out var dSkinBoneNames, out var dSkinSummary))
        {
            PatchLogger.LogWarning($"[{logTag}] distance preserve 中止: donorSkin 結合失敗 ({dSkinSummary})");
            return null;
        }

        if (!CombineSkinSmrs(targetSkinSmrs,
            out var tSkinVerts, out var tSkinNormals,
            out _, out _,
            out var tSkinBoneNames, out var tSkinSummary))
        {
            PatchLogger.LogWarning($"[{logTag}] distance preserve 中止: targetSkin 結合失敗 ({tSkinSummary})");
            return null;
        }

        // skin が使う骨名の和集合。donor / target どちらかで使われていれば「肌に bind される骨」とみなす。
        var skinBoneNames = new HashSet<string>(dSkinBoneNames);
        skinBoneNames.UnionWith(tSkinBoneNames);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        long gridStart = sw.ElapsedMilliseconds;
        var donorSkinGrid  = new SpatialGridIndex(dSkinVerts);
        var targetSkinGrid = new SpatialGridIndex(tSkinVerts);
        long gridMs = sw.ElapsedMilliseconds - gridStart;

        // Pass 4 用: skin combined boneWeights を donor cloth SMR's bones index 空間に re-remap。
        // disable 条件:
        //   weightFalloffOuter <= 0          → feature off
        //   !boneScalingOk                    → donor cloth に boneWeights / bones なし
        //   dSkinCombinedBw == null           → skin SMR のいずれかが boneWeights / bones 不整合
        //   donorClothBoneIdxByName.Count==0  → donor cloth bones[] が全 null
        BoneWeight[] dSkinRemappedToCloth = null;
        if (weightFalloffOuter > 0f && boneScalingOk && dSkinCombinedBw != null && dSkinCombinedBoneNames != null)
        {
            var donorClothBoneIdxByName = new Dictionary<string, int>();
            for (int b = 0; b < donorBones.Length; b++)
            {
                var bone = donorBones[b];
                if (bone == null) continue;
                if (!donorClothBoneIdxByName.ContainsKey(bone.name))
                    donorClothBoneIdxByName[bone.name] = b;
            }
            if (donorClothBoneIdxByName.Count > 0)
            {
                dSkinRemappedToCloth = new BoneWeight[dSkinCombinedBw.Length];
                for (int s = 0; s < dSkinCombinedBw.Length; s++)
                {
                    var src = dSkinCombinedBw[s];
                    var dst = new BoneWeight();
                    int slotsKept = 0;
                    int Try(int srcIdx, float w, ref BoneWeight d, int kept)
                    {
                        if (w <= 0f) return kept;
                        if (srcIdx < 0 || srcIdx >= dSkinCombinedBoneNames.Length) return kept;
                        var name = dSkinCombinedBoneNames[srcIdx];
                        if (!donorClothBoneIdxByName.TryGetValue(name, out var clothIdx)) return kept;
                        switch (kept)
                        {
                            case 0: d.boneIndex0 = clothIdx; d.weight0 = w; break;
                            case 1: d.boneIndex1 = clothIdx; d.weight1 = w; break;
                            case 2: d.boneIndex2 = clothIdx; d.weight2 = w; break;
                            case 3: d.boneIndex3 = clothIdx; d.weight3 = w; break;
                        }
                        return kept + 1;
                    }
                    slotsKept = Try(src.boneIndex0, src.weight0, ref dst, slotsKept);
                    slotsKept = Try(src.boneIndex1, src.weight1, ref dst, slotsKept);
                    slotsKept = Try(src.boneIndex2, src.weight2, ref dst, slotsKept);
                    slotsKept = Try(src.boneIndex3, src.weight3, ref dst, slotsKept);
                    dSkinRemappedToCloth[s] = dst;
                }
            }
        }
        bool weightTransferEnabled = dSkinRemappedToCloth != null;
        // Pass 4 で書き換える boneWeight 配列 (skip 頂点は donor 原状維持で初期化)。
        var newBoneWeights = weightTransferEnabled ? (BoneWeight[])donorBoneWeights.Clone() : null;

        // カバー外判定閾値: 呼び出し側 (Configs) から指定。最小フロアで負値弾く。
        if (maxNeighborDist <= 0f)
        {
            PatchLogger.LogWarning($"[{logTag}] distance preserve 中止: maxNeighborDist={maxNeighborDist:F4}m が無効");
            return null;
        }
        float maxNeighborDistSq = maxNeighborDist * maxNeighborDist;

        // K=3 逆距離² 重み平均: 三角形パッチ相当の局所表面推定
        const int neighborK = 3;
        const float weightEps = 1e-8f;

        // invertGuard: 法線逆向きや別パーツへの誤近傍ヒットを弾く。
        // 旧 MeshPenetrationResolver の invertGuardMul=10 と同方針で、d_donor / d_target 算出後に
        // それぞれの絶対値を基準に動的計算する（カバー外チェック後のため通常は到達しないが、
        // K=3 重み平均が想定外の方向に振れた場合の defensive guard として残す）。
        const float invertGuardMul = 10f;
        // 動的計算のフロア (1mm * 10 = 10mm)。distanceQ がほぼ 0 のときに 0 ガードで誤検出しないよう。
        const float invertGuardFloor = -0.001f * invertGuardMul;

        var neighbors = new List<int>(16);
        var disp = new Vector3[donorVerts.Length];

        int pushed = 0, outOfRange = 0, donorFallback = 0, targetFallback = 0, skippedInverted = 0;
        int boneScaleZeroed = 0;     // skinShare ≈ 0 で push を実質ゼロ化した頂点数
        float maxPush = 0f;
        // skinShare 帯別頂点数: <0.1 / <0.5 / <0.9 / >=0.9
        var shareBand = new int[4];

        // ============================================================
        // Pass 1: cloth → donor skin 初回サンプリング
        //   d_donor[i] と「Pass 3 で skip すべき頂点」(out-of-range / inverted) を pre-compute する。
        //   Pass 3 でも donor skin 再サンプリングするので snAvg_donor は cache 不要 (modSkin で取り直す)。
        // ============================================================
        const byte STATUS_OK         = 0;
        const byte STATUS_OUT_RANGE  = 1;
        const byte STATUS_INVERTED   = 2;
        var pass1Status = new byte[donorVerts.Length];
        var d_donor    = new float[donorVerts.Length];
        int pass1DonorFallback = 0;

        long pass1Start = sw.ElapsedMilliseconds;
        for (int i = 0; i < donorVerts.Length; i++)
        {
            var v = donorVerts[i];
            int outcome = SampleSkinSurface(donorSkinGrid, dSkinVerts, dSkinNormals, v,
                skinSampleRadius, maxNeighborDistSq, neighborK, weightEps, neighbors,
                out var sAvg, out var snAvg);
            if (outcome == SAMPLE_OUT_OF_RANGE) { pass1Status[i] = STATUS_OUT_RANGE; continue; }
            if (outcome == SAMPLE_K_FALLBACK) pass1DonorFallback++;

            float d = Vector3.Dot(v - sAvg, snAvg);
            float donorGuard = Mathf.Min(invertGuardFloor, -Mathf.Abs(d) * invertGuardMul);
            if (d < donorGuard) { pass1Status[i] = STATUS_INVERTED; continue; }
            d_donor[i] = d;
        }
        long pass1Ms = sw.ElapsedMilliseconds - pass1Start;

        // ============================================================
        // Pass 2: donor skin 「めり込み箇所」を内側へ凹ませる
        //   clipping cloth vert (d_donor < minOffset) ごとに、近傍 donor skin 頂点へ
        //   inward push 量 (deficit = minOffset - d_donor) を逆距離² 重み平均で集計。
        //   skin 頂点単位で集計して dSkinNormals 逆向きに移動。
        //   minOffset == 0 のとき完全 skip (純粋 preservation)。
        // ============================================================
        long pass2Start = sw.ElapsedMilliseconds;
        var modSkinVerts = dSkinVerts;        // 退化挙動なら参照そのまま (clone 不要)
        var modDonorSkinGrid = donorSkinGrid; // 同上
        int clippingCount = 0;
        int skinDented = 0;
        float maxDent = 0f;

        if (minOffset > 0f)
        {
            var skinPushAmount = new float[dSkinVerts.Length];
            var skinPushWeight = new float[dSkinVerts.Length];
            var skinScratch = new List<int>(64);

            for (int i = 0; i < donorVerts.Length; i++)
            {
                if (pass1Status[i] != STATUS_OK) continue;
                if (d_donor[i] >= minOffset) continue;
                clippingCount++;

                float deficit = minOffset - d_donor[i];
                var v = donorVerts[i];

                skinScratch.Clear();
                if (skinSampleRadius > 0f)
                {
                    donorSkinGrid.FindWithinRadius(v, skinSampleRadius, skinScratch);
                    if (skinScratch.Count == 0)
                        donorSkinGrid.FindKNearest(v, neighborK, skinScratch);
                }
                else
                {
                    donorSkinGrid.FindKNearest(v, neighborK, skinScratch);
                }

                for (int k = 0; k < skinScratch.Count; k++)
                {
                    int sj = skinScratch[k];
                    float dsq = (dSkinVerts[sj] - v).sqrMagnitude;
                    float w = 1f / (dsq + weightEps);
                    skinPushAmount[sj] += deficit * w;
                    skinPushWeight[sj] += w;
                }
            }

            if (clippingCount > 0)
            {
                var modVerts = (Vector3[])dSkinVerts.Clone();
                for (int sj = 0; sj < modVerts.Length; sj++)
                {
                    if (skinPushWeight[sj] <= 0f) continue;
                    float amt = skinPushAmount[sj] / skinPushWeight[sj];
                    if (amt <= 0f) continue;
                    // 法線が単位ベクトル前提 (Mesh.normals は通常 normalized) だが、
                    // ゲームアセット側で 0/非正規化が混入していても dent 量がブレないよう防御正規化。
                    var n = dSkinNormals[sj];
                    float nLenSq = n.sqrMagnitude;
                    if (nLenSq < 1e-12f) continue;
                    if (nLenSq < 0.999f || nLenSq > 1.001f) n /= Mathf.Sqrt(nLenSq);
                    modVerts[sj] -= n * amt;   // inward = -normal
                    skinDented++;
                    if (amt > maxDent) maxDent = amt;
                }
                modSkinVerts = modVerts;
                modDonorSkinGrid = new SpatialGridIndex(modSkinVerts);
            }
        }
        long pass2Ms = sw.ElapsedMilliseconds - pass2Start;

        // ============================================================
        // Pass 3: 補正 donor skin で d_donor 再算出 + target skin と比較して push 算出
        //   cloth vert 自体は触らず、push は target 法線方向のみ。
        // ============================================================
        long passStart = sw.ElapsedMilliseconds;
        // outOfRange / donorFallback 内訳。Pass 別 / skin 別に集計するため一旦分けて最後に合算。
        int outOfRangeP1Donor = 0, outOfRangeP3Donor = 0, outOfRangeTarget = 0;
        int donorFallbackP3 = 0;
        // Pass 4 統計
        int weightTransferred = 0;
        float maxWeightLoss = 0f;
        // per-vert アロケーション排除のため reuse buffer (typical 4–8 keys)。
        var blendScratch = weightTransferEnabled ? new Dictionary<int, float>(8) : null;
        var blendScratchKeys = weightTransferEnabled ? new List<int>(8) : null;
        var blendScratchSorted = weightTransferEnabled ? new List<KeyValuePair<int, float>>(8) : null;
        for (int i = 0; i < donorVerts.Length; i++)
        {
            if (pass1Status[i] == STATUS_OUT_RANGE) { outOfRangeP1Donor++; continue; }
            if (pass1Status[i] == STATUS_INVERTED) { skippedInverted++; continue; }

            var v = donorVerts[i];

            // ---- donor skin (補正版) での signed distance ----
            float d_donor_eff;
            {
                int outcome = SampleSkinSurface(modDonorSkinGrid, modSkinVerts, dSkinNormals, v,
                    skinSampleRadius, maxNeighborDistSq, neighborK, weightEps, neighbors,
                    out var sAvg, out var snAvg);
                if (outcome == SAMPLE_OUT_OF_RANGE) { outOfRangeP3Donor++; continue; }
                if (outcome == SAMPLE_K_FALLBACK) donorFallbackP3++;
                d_donor_eff = Vector3.Dot(v - sAvg, snAvg);
                // d_donor_eff の絶対値ベースの動的ガード (Pass 1 と対称)。
                // Pass 2 で skin を凹ませた結果、局所的に snAvg が想定外に振れた場合の defensive guard。
                float donorGuardP3 = Mathf.Min(invertGuardFloor, -Mathf.Abs(d_donor_eff) * invertGuardMul);
                if (d_donor_eff < donorGuardP3) { skippedInverted++; continue; }
            }

            // ---- Pass 4: skin 由来 boneWeight 転送 (distance falloff blend) ----
            // 直前の donor sample で neighbors に KNN 結果が入っているのを流用してコスト追加最小化。
            // 距離 d_donor_eff が小さい (skin 密着) ほど skin 由来 weight 比率を上げる。
            if (weightTransferEnabled)
            {
                // 0=skin (密着), 1=donor (離脱)。負値 (= cloth が skin より内側) は 0 にクランプして 100% skin。
                float t = Mathf.Clamp01(d_donor_eff / weightFalloffOuter);
                var blended = ComputeBlendedBoneWeight(
                    v, neighbors, modSkinVerts, dSkinRemappedToCloth, donorBoneWeights[i],
                    t, weightEps, blendScratch, blendScratchKeys, blendScratchSorted, out var loss);
                newBoneWeights[i] = blended;
                if (loss > maxWeightLoss) maxWeightLoss = loss;
                weightTransferred++;
            }

            // ---- target skin での signed distance ----
            float d_target;
            Vector3 snAvgTarget;
            {
                int outcome = SampleSkinSurface(targetSkinGrid, tSkinVerts, tSkinNormals, v,
                    skinSampleRadius, maxNeighborDistSq, neighborK, weightEps, neighbors,
                    out var sAvg, out var snAvg);
                if (outcome == SAMPLE_OUT_OF_RANGE) { outOfRangeTarget++; continue; }
                if (outcome == SAMPLE_K_FALLBACK) targetFallback++;

                snAvgTarget = snAvg;
                d_target = Vector3.Dot(v - sAvg, snAvg);
                // d_target の絶対値ベースの動的ガード
                float targetGuard = Mathf.Min(invertGuardFloor, -Mathf.Abs(d_target) * invertGuardMul);
                if (d_target < targetGuard) { skippedInverted++; continue; }
            }

            // ---- push 量計算 ----
            // 距離保存のみ: 目標 d = d_donor_eff (Pass 2 で minOffset 以上に持ち上げ済み)。
            // target 法線方向に push し、skinShare で物理骨専属頂点を保護。
            float push = d_donor_eff - d_target;

            float skinShare = 1f;
            if (boneScalingOk)
            {
                var bw = donorBoneWeights[i];
                skinShare = 0f;
                if (bw.weight0 > 0f && IsSkinBone(donorBones, bw.boneIndex0, skinBoneNames)) skinShare += bw.weight0;
                if (bw.weight1 > 0f && IsSkinBone(donorBones, bw.boneIndex1, skinBoneNames)) skinShare += bw.weight1;
                if (bw.weight2 > 0f && IsSkinBone(donorBones, bw.boneIndex2, skinBoneNames)) skinShare += bw.weight2;
                if (bw.weight3 > 0f && IsSkinBone(donorBones, bw.boneIndex3, skinBoneNames)) skinShare += bw.weight3;
                if (skinShare > 1f) skinShare = 1f;

                int band = skinShare < 0.1f ? 0 : (skinShare < 0.5f ? 1 : (skinShare < 0.9f ? 2 : 3));
                shareBand[band]++;
            }
            push *= skinShare;
            if (Mathf.Abs(push) < 1e-6f)
            {
                if (boneScalingOk && skinShare < 0.05f) boneScaleZeroed++;
                continue;
            }

            disp[i] = snAvgTarget * push;
            if (Mathf.Abs(push) > maxPush) maxPush = Mathf.Abs(push);
            pushed++;
        }
        long passMs = sw.ElapsedMilliseconds - passStart;
        // 合算 (互換性維持のため outOfRange / donorFallback の総数も保持)。
        outOfRange = outOfRangeP1Donor + outOfRangeP3Donor + outOfRangeTarget;
        donorFallback = pass1DonorFallback + donorFallbackP3;

        sw.Stop();
        string shareBandStr = boneScalingOk
            ? $"shareBand[<.1/<.5/<.9/>=.9]={shareBand[0]}/{shareBand[1]}/{shareBand[2]}/{shareBand[3]} boneScaleZeroed={boneScaleZeroed}"
            : "shareBand=N/A";
        string weightTransferStr = weightTransferEnabled
            ? $"weightTransferred={weightTransferred} maxWeightLoss={maxWeightLoss:F3}"
            : "weightTransfer=disabled";
        PatchLogger.LogInfo(
            $"[{logTag}] distance preserve: donor={donorMesh.name}({donorVerts.Length}v) " +
            $"donorSkin=[{dSkinSummary}] targetSkin=[{tSkinSummary}] range={maxNeighborDist:F4}m minOffset={minOffset:F4}m skinSampleR={skinSampleRadius:F4}m weightFalloff={weightFalloffOuter:F4}m " +
            $"clipping={clippingCount} skinDented={skinDented}(maxDent={maxDent:F4}m) " +
            $"pushed={pushed} outOfRange={outOfRange}(p1Donor={outOfRangeP1Donor}/p3Donor={outOfRangeP3Donor}/target={outOfRangeTarget}) " +
            $"donorFallback={donorFallback}(p1={pass1DonorFallback}/p3={donorFallbackP3}) targetFallback={targetFallback} skippedInverted={skippedInverted} maxPush={maxPush:F4}m " +
            $"{shareBandStr} {weightTransferStr} " +
            $"grid={gridMs}ms p1={pass1Ms}ms p2={pass2Ms}ms p3={passMs}ms total={sw.ElapsedMilliseconds}ms");

        // 全頂点 push が実質ゼロかつ Pass 4 boneWeight 転送も無し → mesh 差し替え不要
        if (pushed == 0 && weightTransferred == 0) return null;

        // 補正済み mesh を新規生成 (元 mesh は変更しない)
        var newVerts = (Vector3[])donorVerts.Clone();
        for (int i = 0; i < newVerts.Length; i++)
        {
            if (disp[i].sqrMagnitude > 0f)
                newVerts[i] += disp[i];
        }

        var adjMesh = Object.Instantiate(donorMesh);
        adjMesh.name = donorMesh.name + "_distpres";
        adjMesh.vertices = newVerts;
        if (newBoneWeights != null)
            adjMesh.boneWeights = newBoneWeights;
        adjMesh.RecalculateNormals();
        adjMesh.RecalculateBounds();
        return adjMesh;
    }

    /// <summary>
    /// cloth vert <paramref name="v"/> について、neighbors 内 skin 頂点の boneWeight を逆距離² 重み平均で
    /// 集計し、distance falloff 値 <paramref name="t"/> (0=skin, 1=donor、負値はクランプで 0 = skin に張り付く)
    /// で donor 元 boneWeight と blend して 4-slot top-k の正規化 BoneWeight を返す。
    /// truncate (top-4 制限) で落ちた weight 合計を <paramref name="weightLoss"/> に出力。
    /// blend 結果 total &lt;= 0 (skin coverage ゼロ + donor 元も 0) の場合は donor 元 boneWeight をそのまま返す。
    ///
    /// scratchKeys / scratchSorted は per-call 再利用バッファ (MeshDistancePreserver.Preserve 呼び出し内で
    /// 呼び出し毎に再利用、Preserve スコープ外には持ち出さない)。
    /// </summary>
    private static BoneWeight ComputeBlendedBoneWeight(
        Vector3 v,
        List<int> neighbors,
        Vector3[] modSkinVerts,
        BoneWeight[] dSkinRemappedToCloth,
        BoneWeight donorBw,
        float t,
        float weightEps,
        Dictionary<int, float> scratch,
        List<int> scratchKeys,
        List<KeyValuePair<int, float>> scratchSorted,
        out float weightLoss)
    {
        weightLoss = 0f;
        scratch.Clear();

        // Phase 1: skin 由来 weight 集計 (distance² 重み)
        float wSum = 0f;
        for (int k = 0; k < neighbors.Count; k++)
        {
            int sj = neighbors[k];
            if (sj < 0 || sj >= dSkinRemappedToCloth.Length) continue;
            var sbw = dSkinRemappedToCloth[sj];
            float dsq = (modSkinVerts[sj] - v).sqrMagnitude;
            float w = 1f / (dsq + weightEps);
            if (sbw.weight0 > 0f) AddOrAccum(scratch, sbw.boneIndex0, sbw.weight0 * w);
            if (sbw.weight1 > 0f) AddOrAccum(scratch, sbw.boneIndex1, sbw.weight1 * w);
            if (sbw.weight2 > 0f) AddOrAccum(scratch, sbw.boneIndex2, sbw.weight2 * w);
            if (sbw.weight3 > 0f) AddOrAccum(scratch, sbw.boneIndex3, sbw.weight3 * w);
            wSum += w;
        }

        // Phase 2: skin 集計を per-bone 正規化して (1-t) で blend に積む
        if (wSum > 0f && scratch.Count > 0)
        {
            float invW = 1f / wSum;
            float skinScale = (1f - t) * invW;
            // scratch を直接 in-place で scaling。foreach で iterate しながらの値変更を避けるため
            // keys スナップショット (scratchKeys を再利用、LINQ ToList 回避)。
            scratchKeys.Clear();
            foreach (var k in scratch.Keys) scratchKeys.Add(k);
            for (int kk = 0; kk < scratchKeys.Count; kk++)
                scratch[scratchKeys[kk]] *= skinScale;
        }
        else
        {
            // skin 集計が空 = scratch も空、後段の donor 加算のみで blend が決まる
            scratch.Clear();
        }

        // Phase 3: donor 元 weight を t で加算
        if (donorBw.weight0 > 0f) AddOrAccum(scratch, donorBw.boneIndex0, t * donorBw.weight0);
        if (donorBw.weight1 > 0f) AddOrAccum(scratch, donorBw.boneIndex1, t * donorBw.weight1);
        if (donorBw.weight2 > 0f) AddOrAccum(scratch, donorBw.boneIndex2, t * donorBw.weight2);
        if (donorBw.weight3 > 0f) AddOrAccum(scratch, donorBw.boneIndex3, t * donorBw.weight3);

        if (scratch.Count == 0) return donorBw;

        // Phase 4: top-4 + 正規化 (LINQ OrderByDescending.ToList を回避、List.Sort with delegate で in-place)
        scratchSorted.Clear();
        foreach (var kv in scratch) scratchSorted.Add(kv);
        scratchSorted.Sort(s_descByValue);

        int kept = Mathf.Min(4, scratchSorted.Count);
        float total = 0f;
        for (int k = 0; k < kept; k++) total += scratchSorted[k].Value;
        for (int k = kept; k < scratchSorted.Count; k++) weightLoss += scratchSorted[k].Value;
        if (total <= 0f) return donorBw;

        float invTotal = 1f / total;
        var result = new BoneWeight();
        if (kept > 0) { result.boneIndex0 = scratchSorted[0].Key; result.weight0 = scratchSorted[0].Value * invTotal; }
        if (kept > 1) { result.boneIndex1 = scratchSorted[1].Key; result.weight1 = scratchSorted[1].Value * invTotal; }
        if (kept > 2) { result.boneIndex2 = scratchSorted[2].Key; result.weight2 = scratchSorted[2].Value * invTotal; }
        if (kept > 3) { result.boneIndex3 = scratchSorted[3].Key; result.weight3 = scratchSorted[3].Value * invTotal; }
        return result;
    }

    // Phase 4 sort 用の delegate (毎 vert の lambda アロケーションを避けるため static cache)。
    private static readonly System.Comparison<KeyValuePair<int, float>> s_descByValue =
        (a, b) => b.Value.CompareTo(a.Value);

    private static void AddOrAccum(Dictionary<int, float> dict, int key, float value)
    {
        if (dict.TryGetValue(key, out var existing)) dict[key] = existing + value;
        else dict[key] = value;
    }

    /// <summary>
    /// 複数の skin SMR を 1 つの verts/normals 配列に結合し、それらが boneWeights で参照する骨名を収集する。
    /// null 要素 / sharedMesh が無い / verts/normals 不整合 / bones 不整合の SMR は無視する。
    /// 結合後の verts が 0 件なら false を返す。
    /// <para>
    /// <paramref name="combinedBoneWeights"/> と <paramref name="combinedBoneNames"/> は boneWeight 転送 (Pass 4) 用:
    /// 全 SMR の bones[] を name で union した <c>combinedBoneNames</c> 配列を構築し、各 vertex の BoneWeight を
    /// この index 空間に remap した <c>combinedBoneWeights</c> を返す。SMR 横断で同名 bone は同 combined index に
    /// 統合される。boneWeights / bones が一つでも不整合な SMR があれば <c>combinedBoneWeights = null</c> で返却
    /// (= boneWeight 転送機能 disable シグナル)。
    /// </para>
    /// </summary>
    private static bool CombineSkinSmrs(
        SkinnedMeshRenderer[] smrs,
        out Vector3[] combinedVerts, out Vector3[] combinedNormals,
        out BoneWeight[] combinedBoneWeights, out string[] combinedBoneNames,
        out HashSet<string> boneNames, out string summary)
    {
        combinedVerts = null;
        combinedNormals = null;
        combinedBoneWeights = null;
        combinedBoneNames = null;
        boneNames = new HashSet<string>();
        if (smrs == null || smrs.Length == 0)
        {
            summary = "(empty)";
            return false;
        }

        int total = 0;
        var parts = new List<(Vector3[] v, Vector3[] n, BoneWeight[] bw, Transform[] bones, string name)>(smrs.Length);
        var summaryParts = new List<string>(smrs.Length);
        bool allSmrsHaveWeights = true;
        foreach (var smr in smrs)
        {
            if (smr == null || smr.sharedMesh == null) continue;
            var m = smr.sharedMesh;
            var v = m.vertices;
            var n = m.normals;
            if (v == null || v.Length == 0) continue;
            if (n == null || n.Length != v.Length) continue;
            var bw = m.boneWeights;
            var bones = smr.bones;
            bool hasWeights = bw != null && bones != null && bones.Length > 0 && bw.Length == v.Length;
            if (!hasWeights) allSmrsHaveWeights = false;
            parts.Add((v, n, hasWeights ? bw : null, hasWeights ? bones : null, m.name));
            summaryParts.Add($"{m.name}({v.Length}v)");
            total += v.Length;

            // boneWeights 参照の骨名を収集 (skinShare 用)
            if (hasWeights)
            {
                for (int i = 0; i < bw.Length; i++)
                {
                    var b = bw[i];
                    if (b.weight0 > 0f) AddBoneName(boneNames, bones, b.boneIndex0);
                    if (b.weight1 > 0f) AddBoneName(boneNames, bones, b.boneIndex1);
                    if (b.weight2 > 0f) AddBoneName(boneNames, bones, b.boneIndex2);
                    if (b.weight3 > 0f) AddBoneName(boneNames, bones, b.boneIndex3);
                }
            }
        }
        if (total == 0)
        {
            summary = "(no valid skins)";
            return false;
        }

        combinedVerts = new Vector3[total];
        combinedNormals = new Vector3[total];
        int offset = 0;
        foreach (var p in parts)
        {
            System.Array.Copy(p.v, 0, combinedVerts, offset, p.v.Length);
            System.Array.Copy(p.n, 0, combinedNormals, offset, p.n.Length);
            offset += p.v.Length;
        }
        summary = string.Join("+", summaryParts);

        // combined boneWeights / boneNames を構築 (Pass 4 boneWeight transfer 用)。
        // 全 SMR で weights 整合する場合のみ提供。1 つでも欠ければ null 返却。
        if (allSmrsHaveWeights)
        {
            var nameToCombinedIdx = new Dictionary<string, int>();
            var combinedNamesList = new List<string>();
            combinedBoneWeights = new BoneWeight[total];
            int wOffset = 0;
            foreach (var p in parts)
            {
                // Per-SMR の bones index → combined index へ事前マッピング
                var localToCombined = new int[p.bones.Length];
                for (int b = 0; b < p.bones.Length; b++)
                {
                    var bone = p.bones[b];
                    if (bone == null) { localToCombined[b] = -1; continue; }
                    if (!nameToCombinedIdx.TryGetValue(bone.name, out var cIdx))
                    {
                        cIdx = combinedNamesList.Count;
                        combinedNamesList.Add(bone.name);
                        nameToCombinedIdx[bone.name] = cIdx;
                    }
                    localToCombined[b] = cIdx;
                }
                // 各 vertex の boneIndex を combined 空間に remap
                for (int i = 0; i < p.bw.Length; i++)
                {
                    var src = p.bw[i];
                    var dst = new BoneWeight();
                    int slotsKept = 0;
                    void Try(int srcIdx, float w)
                    {
                        if (w <= 0f) return;
                        if (srcIdx < 0 || srcIdx >= localToCombined.Length) return;
                        int cIdx = localToCombined[srcIdx];
                        if (cIdx < 0) return;
                        switch (slotsKept)
                        {
                            case 0: dst.boneIndex0 = cIdx; dst.weight0 = w; break;
                            case 1: dst.boneIndex1 = cIdx; dst.weight1 = w; break;
                            case 2: dst.boneIndex2 = cIdx; dst.weight2 = w; break;
                            case 3: dst.boneIndex3 = cIdx; dst.weight3 = w; break;
                        }
                        slotsKept++;
                    }
                    Try(src.boneIndex0, src.weight0);
                    Try(src.boneIndex1, src.weight1);
                    Try(src.boneIndex2, src.weight2);
                    Try(src.boneIndex3, src.weight3);
                    combinedBoneWeights[wOffset + i] = dst;
                }
                wOffset += p.v.Length;
            }
            combinedBoneNames = combinedNamesList.ToArray();
        }

        return true;
    }

    private static void AddBoneName(HashSet<string> set, Transform[] bones, int idx)
    {
        if (idx < 0 || idx >= bones.Length) return;
        var b = bones[idx];
        if (b == null) return;
        set.Add(b.name);
    }

    private static bool IsSkinBone(Transform[] bones, int idx, HashSet<string> skinBoneNames)
    {
        if (idx < 0 || idx >= bones.Length) return false;
        var b = bones[idx];
        if (b == null) return false;
        return skinBoneNames.Contains(b.name);
    }

    // SampleSkinSurface の戻り値
    private const int SAMPLE_OK            = 0;
    private const int SAMPLE_OUT_OF_RANGE  = 1;
    private const int SAMPLE_K_FALLBACK    = 2;

    /// <summary>
    /// donor 服頂点 v に対し skin 表面位置 sAvg / 法線 snAvg を距離重み平均で推定する。
    /// skinSampleRadius > 0 のとき: 半径内の全 skin 頂点を平均（surrounding points smoothing）。
    /// 半径内に頂点が無いか skinSampleRadius == 0 のとき: K=neighborK の最近傍で平均（フォールバック）。
    /// 最近傍 1 点が maxNeighborDistSq を超える場合 SAMPLE_OUT_OF_RANGE を返す。
    /// K-NN フォールバックで K 件未満しか得られなかった場合 SAMPLE_K_FALLBACK を返す。
    /// </summary>
    private static int SampleSkinSurface(
        SpatialGridIndex grid, Vector3[] verts, Vector3[] normals,
        Vector3 v, float skinSampleRadius, float maxNeighborDistSq,
        int neighborK, float weightEps, List<int> scratch,
        out Vector3 sAvg, out Vector3 snAvg)
    {
        sAvg = default;
        snAvg = default;

        scratch.Clear();
        bool radiusMode = skinSampleRadius > 0f;
        if (radiusMode)
        {
            grid.FindWithinRadius(v, skinSampleRadius, scratch);
            if (scratch.Count == 0)
            {
                // 半径内に無ければ K=3 にフォールバック
                grid.FindKNearest(v, neighborK, scratch);
            }
        }
        else
        {
            grid.FindKNearest(v, neighborK, scratch);
        }

        if (scratch.Count == 0) return SAMPLE_OUT_OF_RANGE;

        // カバー外チェック: 最近傍 1 点 (radius モードでは scratch[0] が必ずしも最近傍ではないため
        // 全件中の最小距離で判定すると正確だが、コスト/精度のバランスで scratch[0] で判定する)
        int n0 = scratch[0];
        float n0DistSq = (verts[n0] - v).sqrMagnitude;
        if (n0DistSq > maxNeighborDistSq)
        {
            // radius モードでは scratch[0] が偏ることがあるので、全件で最小再判定する
            float minDistSq = n0DistSq;
            for (int k = 1; k < scratch.Count; k++)
            {
                float dsq = (verts[scratch[k]] - v).sqrMagnitude;
                if (dsq < minDistSq) minDistSq = dsq;
            }
            if (minDistSq > maxNeighborDistSq) return SAMPLE_OUT_OF_RANGE;
        }

        bool fallback = !radiusMode && scratch.Count < neighborK;

        Vector3 sSum = Vector3.zero, snSum = Vector3.zero;
        float wSum = 0f;
        for (int k = 0; k < scratch.Count; k++)
        {
            int nj = scratch[k];
            var sp = verts[nj];
            float dsq = (sp - v).sqrMagnitude;
            float w = 1f / (dsq + weightEps);
            sSum  += sp          * w;
            snSum += normals[nj] * w;
            wSum  += w;
        }
        sAvg = sSum / wSum;
        snAvg = snSum.sqrMagnitude > 1e-12f ? snSum.normalized : normals[scratch[0]];

        return fallback ? SAMPLE_K_FALLBACK : SAMPLE_OK;
    }
}
