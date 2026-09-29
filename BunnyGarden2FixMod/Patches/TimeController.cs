using UnityEngine;
using UnityEngine.InputSystem;

namespace BunnyGarden2FixMod.Patches;

public class TimeController : MonoBehaviour
{
    /// <summary>時間停止中かどうか。オーバーレイ表示の判定に使う。</summary>
    internal static bool IsTimeStopped { get; private set; }

    private bool fastForward;
    private bool stop = false;
    private int frames;
    private bool wasControlling;

    public static TimeController Initialize(GameObject parent)
        => parent.AddComponent<TimeController>();

    private void OnDisable()
    {
        Time.timeScale = 1f;
        stop = false;
        IsTimeStopped = false;
    }

    private void Update()
    {
        fastForward = Configs.FastForward.IsHeld();

        if (Configs.TimeStopToggle.IsTriggered())
        {
            stop = !stop;
            IsTimeStopped = stop;
        }

        if (Configs.FrameAdvance.IsTriggered())
        {
            stop = true;
            frames = 1;
        }
    }

    private void LateUpdate()
    {
        bool controlling = frames > 0 || stop || fastForward;

        if (frames > 0)
            Time.timeScale = 1f;
        else if (stop)
            Time.timeScale = 0f;
        else if (fastForward)
            Time.timeScale = Configs.FastForwardSpeed.Value;
        else if (wasControlling)
            // MOD の制御から抜けた直後の 1 フレームだけ 1f に戻す。
            // 毎フレーム 1f を書くとゲーム側の ScopedFastForward（ギャンブル演出の
            // A/Y 長押し早送りなど）が LateUpdate で上書きされて機能しなくなるため。
            Time.timeScale = 1f;

        wasControlling = controlling;
        frames = Mathf.Max(0, frames - 1);
    }

}