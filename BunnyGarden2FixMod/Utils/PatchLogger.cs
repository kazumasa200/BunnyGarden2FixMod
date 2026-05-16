using BepInEx.Logging;

namespace BunnyGarden2FixMod.Utils;

public static class PatchLogger
{
    private static ManualLogSource _logger;
    private static bool? _isDebugEnabled;

    /// <summary>
    /// プラグインのAwakeで呼び出してLoggerを設定します
    /// </summary>
    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 任意の DiskLogListener が Debug レベルを表示する設定なら true。
    /// 重い文字列構築や LINQ チェーンを LogDebug の前にガードする目的で使う。
    /// 値はキャッシュされる (BepInEx の LogLevels は通常 startup 時に決まる前提)。
    /// </summary>
    public static bool IsDebugEnabled
    {
        get
        {
            if (_isDebugEnabled.HasValue) return _isDebugEnabled.Value;
            bool enabled = false;
            foreach (var listener in Logger.Listeners)
            {
                if (listener is DiskLogListener disk &&
                    (disk.DisplayedLogLevel & LogLevel.Debug) != 0)
                {
                    enabled = true;
                    break;
                }
            }
            _isDebugEnabled = enabled;
            return enabled;
        }
    }

    public static void LogInfo(string message)
    {
        _logger?.LogInfo($"[Plugin] {message}");
    }

    public static void LogWarning(string message)
    {
        _logger?.LogWarning($"[Plugin] {message}");
    }

    public static void LogError(string message)
    {
        _logger?.LogError($"[Plugin] {message}");
    }

    public static void LogDebug(string message)
    {
        _logger?.LogDebug($"[Plugin] {message}");
    }
}
