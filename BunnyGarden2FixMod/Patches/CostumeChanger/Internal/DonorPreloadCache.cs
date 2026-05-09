using BunnyGarden2FixMod.Utils;
using Cysharp.Threading.Tasks;
using GB;
using GB.Game;
using GB.Scene;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger.Internal;

/// <summary>
/// Tops/Bottoms 等の donor preload 共通機構。
///
/// 各 Loader はこの cache をインスタンス化し、独自の Entry 型 (preloaded SMR list 等を保持) を
/// <typeparamref name="TEntry"/> として渡す。cache は以下を担う:
///   - donor parent GameObject の生成と <see cref="CharacterHandle.Preload"/>
///   - in-flight タスクキャッシュ（同一 (donor, costume) への重複起動防止）
///   - preload 完了後の race recovery（await 中に別 caller が同 key を先に登録した場合）
///   - Entry の保持と <see cref="IsHostParent"/> による host 配下判定
///
/// SMR フィルタ / Entry 構造体の組み立ては <c>buildEntry</c> delegate で各 Loader 側に委譲する。
/// </summary>
internal sealed class DonorPreloadCache<TEntry>
{
    private readonly Dictionary<(CharID Donor, CostumeType Costume), TEntry> _donors = new();
    // 進行中の preload タスクをキーごとに共有することで、同一 (donor, costume) への
    // 重複呼出が二重 GameObject 生成や CharacterHandle.Preload 多重発火を起こさないようにする。
    // 単一スレッド (Unity main thread) 前提のため lock 不要。
    private readonly Dictionary<(CharID Donor, CostumeType Costume), UniTask<bool>> _inFlight = new();

    private readonly string _logPrefix;
    private readonly Func<CharID, CostumeType, GameObject, CharacterHandle, List<SkinnedMeshRenderer>, TEntry> _buildEntry;
    private readonly Func<TEntry, bool> _isValid;

    private GameObject _hostRoot;

    /// <param name="logPrefix">"[TopsLoader]" / "[BottomsLoader]" 等。</param>
    /// <param name="buildEntry">
    /// preload 完了後に呼ばれる factory。引数は (donor, costume, donorParent, handle, allSmrs)。
    /// SMR フィルタ・追加ログ出力・Entry 構築をここで行う。
    /// </param>
    /// <param name="isValid">Entry が「有効な preload」かを判定する（SMR list 非空 等）。</param>
    public DonorPreloadCache(
        string logPrefix,
        Func<CharID, CostumeType, GameObject, CharacterHandle, List<SkinnedMeshRenderer>, TEntry> buildEntry,
        Func<TEntry, bool> isValid)
    {
        _logPrefix = logPrefix;
        _buildEntry = buildEntry;
        _isValid = isValid;
    }

    /// <summary>preload host を設定する。Loader の Initialize で 1 回呼ぶ。</summary>
    public void SetHostRoot(GameObject hostRoot)
    {
        _hostRoot = hostRoot;
    }

    /// <summary>
    /// 指定の parent GameObject が donor preload host 配下かを判定。
    /// CostumeChangerPatch / 他 Loader の ApplyIfOverridden から donor preload 経路の override
    /// 適用を抑止するために参照する。
    /// </summary>
    public bool IsHostParent(GameObject parent)
    {
        if (parent == null || _hostRoot == null) return false;
        return parent.transform.IsChildOf(_hostRoot.transform);
    }

    public bool TryGet((CharID Donor, CostumeType Costume) key, out TEntry entry) =>
        _donors.TryGetValue(key, out entry);

    public bool ContainsKey((CharID Donor, CostumeType Costume) key) =>
        _donors.ContainsKey(key);

    /// <summary>
    /// 指定 (donor, costume) を preload する。既ロードなら即時 <see cref="UniTask.FromResult"/> を返す。
    /// 進行中なら同タスクに合流する。
    /// 戻り値は preload に成功して <c>isValid(entry)</c> が true を返したかどうか。
    /// </summary>
    public UniTask<bool> PreloadAsync(CharID donor, CostumeType costume)
    {
        var key = (donor, costume);
        if (_donors.TryGetValue(key, out var cached))
            return UniTask.FromResult(_isValid(cached));
        if (_inFlight.TryGetValue(key, out var pending))
            return pending;

        // UniTaskCompletionSource は内部 continuation を multi-cast 保持するため
        // pre-completion で複数の await/Forget/ContinueWith が安全。
        // .Preserve() の MemoizeSource は OnCompleted を underlying source に forward するだけで
        // 単一 continuation slot 制約を解消しないため使用不可。
        var tcs = new UniTaskCompletionSource<bool>();
        _inFlight[key] = tcs.Task;
        RunPreloadWorker(donor, costume, tcs).Forget();
        return tcs.Task;
    }

    private async UniTaskVoid RunPreloadWorker(CharID donor, CostumeType costume, UniTaskCompletionSource<bool> tcs)
    {
        var key = (donor, costume);
        bool result = false;
        try
        {
            result = await PreloadInternal(donor, costume);
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"{_logPrefix} preload worker 例外: {donor}/{costume}: {ex}");
        }
        finally
        {
            // TrySetResult を先に呼ぶことで、同期的に発火する continuation 中も
            // _inFlight に entry が残り、再帰的な PreloadAsync(同 key) は
            // 完了済みの tcs.Task を即座に返すため二重 worker 起動を防ぐ。
            tcs.TrySetResult(result);
            _inFlight.Remove(key);
        }
    }

    private async UniTask<bool> PreloadInternal(CharID donor, CostumeType costume)
    {
        var key = (donor, costume);
        try
        {
            if (_hostRoot == null)
            {
                PatchLogger.LogWarning($"{_logPrefix} preload 前に Initialize が必要: {donor}/{costume}");
                return false;
            }

            await UniTask.WaitUntil(() => GBSystem.Instance != null && GBSystem.Instance.RefSaveData() != null);

            var donorParent = new GameObject($"Donor_{donor}_{costume}");
            donorParent.transform.SetParent(_hostRoot.transform, false);
            donorParent.SetActive(false);

            var handle = new CharacterHandle(donorParent);
            handle.Preload(donor, new CharacterHandle.LoadArg { Costume = costume });
            await UniTask.WaitUntil(() => handle.IsPreloadDone());

            // await 再開後に別 caller が同 key を先に登録している場合は自分の donorParent を破棄して
            // 既存エントリに合流する（Wardrobe での donor 連打による二重 preload 防止）。
            if (_donors.TryGetValue(key, out var raceWinner))
            {
                UnityEngine.Object.Destroy(donorParent);
                return _isValid(raceWinner);
            }

            var allSmrs = donorParent.GetComponentsInChildren<SkinnedMeshRenderer>(true).ToList();
            var entry = _buildEntry(donor, costume, donorParent, handle, allSmrs);
            _donors[key] = entry;
            return _isValid(entry);
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"{_logPrefix} preload 失敗: {donor}/{costume}: {ex}");
            return false;
        }
    }
}
