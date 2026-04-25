using UnityEngine;
using UnityEngine.InputSystem;

namespace BunnyGarden2FixMod.Patches;

public class TimeController : MonoBehaviour
{
    private bool fastForward;
    private bool stop = false;
    private int frames;

    public static TimeController Initialize(GameObject parent)
        => parent.AddComponent<TimeController>();

    private void OnEnable()
    {
        Plugin.GUICallback += GUICallback;
    }

    private void OnDisable()
    {
        Plugin.GUICallback -= GUICallback;
        Time.timeScale = 1f;
        stop = false;
    }

    private void Update()
    {
        fastForward = Plugin.ConfigFastForward.IsHeld();

        if (Plugin.ConfigTimeStopToggle.IsTriggered())
            stop = !stop;

        if (Plugin.ConfigFrameAdvance.IsTriggered())
        {
            stop = true;
            frames = 1;
        }
    }

    private void LateUpdate()
    {
        if (frames > 0)
            Time.timeScale = 1f;
        else if (stop)
            Time.timeScale = 0f;
        else if (fastForward)
            Time.timeScale = Plugin.ConfigFastForwardSpeed.Value;
        else
            Time.timeScale = 1f;

        frames = Mathf.Max(0, frames - 1);
    }

    private void GUICallback()
    {
        if (!stop)
            return;

        GUI.color = Color.cyan;
        GUILayout.Label($"Time Stop: ON ({Plugin.ConfigTimeStopToggle}=OFF)");
        GUILayout.Label($"Frame Advance: ({Plugin.ConfigFrameAdvance})");
        GUILayout.Label($"Fast Forward: ({Plugin.ConfigFastForward})");
        GUI.color = Color.white;
    }
}