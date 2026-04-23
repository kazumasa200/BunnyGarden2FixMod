using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using GB;

namespace BunnyGarden2FixMod.Utils;

/// <summary>
/// ゲーム本体の <see cref="ConfirmDialog"/> (GBSystem.GetConfirmDialog) を
/// UniTask ベースの YES/NO 関数として扱う薄いラッパ。
///
/// 使用例:
/// <code>
/// var ok = await ConfirmDialogHelper.ShowYesNoAsync("初期化しますか？",
///     this.GetCancellationTokenOnDestroy());
/// if (ok) { /* 実行 */ }
/// </code>
///
/// 注意: ダイアログは GBInput (A/B/Left/Right) を直接読んでいるため、
/// 呼び出し側は表示中に自分の入力抑制ロジックを一時停止する必要がある。
/// </summary>
public static class ConfirmDialogHelper
{
    /// <summary>
    /// 指定テキストでダイアログを開き、ユーザの選択まで待つ。YES=true, NO or キャンセル/破棄=false。
    /// GBSystem が未初期化なら false を返し即座に終了する。
    /// </summary>
    public static async UniTask<bool> ShowYesNoAsync(string text, CancellationToken ct = default)
    {
        var sys = GBSystem.Instance;
        var dialog = sys?.GetConfirmDialog();
        if (dialog == null) return false;

        try { dialog.SetTextWithoutMSGID(text); }
        catch (Exception ex)
        {
            BunnyGarden2FixMod.Utils.PatchLogger.LogWarning($"[ConfirmDialogHelper] SetText 失敗: {ex.Message}");
            return false;
        }
        dialog.Enter();

        try
        {
            // dialog が途中で null/Destroy される可能性も考慮して毎フレームチェック
            await UniTask.WaitUntil(() => dialog == null || dialog.IsSelected(), cancellationToken: ct);
        }
        catch (OperationCanceledException)
        {
            try { if (dialog != null) dialog.Exit(); } catch { /* 破棄済み */ }
            return false;
        }

        if (dialog == null) return false;
        bool yes = dialog.IsYesSelected();
        try { dialog.Exit(); } catch { /* 破棄済みなら無視 */ }

        try
        {
            await UniTask.WaitUntil(() => dialog == null || !dialog.IsActive(), cancellationToken: ct);
        }
        catch (OperationCanceledException) { /* Exit 後のクローズ中キャンセル: 無視 */ }

        return yes;
    }

    /// <summary>
    /// GBSystem の ConfirmDialog が現在アクティブかを返す。呼び出し側の入力抑制判定に使う。
    /// </summary>
    public static bool IsActive()
    {
        var dialog = GBSystem.Instance?.GetConfirmDialog();
        return dialog != null && dialog.IsActive();
    }
}
