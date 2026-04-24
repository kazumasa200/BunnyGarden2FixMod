using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;

namespace BunnyGarden2FixMod.Utils;

/// <summary>
/// Steam 経由で起動されたかを判定し、Steam 外起動を検出した場合に
/// <c>steam://rungameid/</c> で再起動してアプリを終了するユーティリティ。
/// </summary>
internal static class SteamLaunchChecker
{
    private const int SteamAppId = 3443820;

    /// <summary>
    /// 再起動ループを防ぐためのタイムスタンプファイル名。
    /// 前回の再起動試行からこの秒数以内なら再試行をスキップする。
    /// </summary>
    private const int RelaunchGuardWindowSec = 30;

    private static readonly string RelaunchGuardFile =
        Path.Combine(Path.GetTempPath(), "bg2mod_steam_relaunch.tmp");

    /// <summary>
    /// Steam 外起動を検出した場合、Steam 経由で再起動する。
    /// </summary>
    /// <returns>
    /// <c>true</c>: 再起動を試みた（呼び出し元は即座に <c>Application.Quit()</c> を
    /// 呼び出して <c>Awake()</c> から return してください）。
    /// <c>false</c>: Steam 経由確認済み／再起動スキップ（処理続行可）。
    /// </returns>
    public static bool CheckAndRelaunchIfNeeded()
    {
        if (IsLaunchedViaSteam())
            return false;

        // 再起動ループ防止: 直近 RelaunchGuardWindowSec 秒以内に試行済みならスキップ
        if (IsRelaunchGuardActive())
        {
            PatchLogger.LogWarning(
                "[SteamLaunchChecker] Steam 外起動を検出しましたが、直近の再起動試行から " +
                $"{RelaunchGuardWindowSec} 秒が経過していないためスキップします。");
            return false;
        }

        PatchLogger.LogWarning(
            $"[SteamLaunchChecker] Steam 経由で起動されていません。" +
            $"Steam 経由で再起動します (AppID: {SteamAppId})...");

        try
        {
            var url = $"steam://rungameid/{SteamAppId}";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            // Process.Start 成功後にガードを書き込む（失敗時は30秒ロックを回避）
            WriteRelaunchGuard();
            return true;
        }
        catch (Exception ex)
        {
            PatchLogger.LogError(
                $"[SteamLaunchChecker] Steam 経由での再起動に失敗しました: {ex.Message}");
            return false;
        }
    }

    // ── 判定ロジック ─────────────────────────────────────────────────────

    private static bool IsLaunchedViaSteam()
    {
        // Steam は起動時に SteamAppId 環境変数をセットする
        var envAppId = Environment.GetEnvironmentVariable("SteamAppId");
        if (envAppId == SteamAppId.ToString())
            return true;

        // steam_appid.txt が存在し AppID が一致する場合はデバッグ起動として許容
        // Steam 本体はカレントディレクトリを優先参照するためゲームルートと CWD を両方確認
        try
        {
            var gameRoot = Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
            var checkDirs = new[] { gameRoot, Directory.GetCurrentDirectory() }
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in checkDirs)
            {
                var file = Path.Combine(dir, "steam_appid.txt");
                if (!File.Exists(file))
                    continue;

                var content = File.ReadAllText(file).Trim();
                if (content == SteamAppId.ToString())
                {
                    PatchLogger.LogInfo(
                        $"[SteamLaunchChecker] steam_appid.txt を検出しました ({file})。デバッグ起動として許容します。");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[SteamLaunchChecker] steam_appid.txt の確認中にエラー: {ex.Message}");
        }

        return false;
    }

    // ── 再起動ループ防止 ─────────────────────────────────────────────────

    private static bool IsRelaunchGuardActive()
    {
        try
        {
            if (!File.Exists(RelaunchGuardFile))
                return false;

            var lastWrite = File.GetLastWriteTimeUtc(RelaunchGuardFile);
            return (DateTime.UtcNow - lastWrite).TotalSeconds < RelaunchGuardWindowSec;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteRelaunchGuard()
    {
        try
        {
            File.WriteAllText(RelaunchGuardFile, SteamAppId.ToString());
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[SteamLaunchChecker] 再起動ガードファイルの書き込みに失敗しました: {ex.Message}");
        }
    }
}
