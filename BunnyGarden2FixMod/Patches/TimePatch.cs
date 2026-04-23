using UnityEngine;
using UnityEngine.InputSystem;

namespace BunnyGarden2FixMod.Patches;

public class TimePatch : MonoBehaviour
{
    private bool _pause = false;
    private int _frames;
    private bool _fastForward = false;

    public static void Initialize(GameObject parent)
    {
        parent.AddComponent<TimePatch>();
    }

    private void LateUpdate()
    {
        // F2 で倍速、F3 で一時停止、F4 で1フレーム進める
        _fastForward = Keyboard.current?[Key.F2].isPressed == true;

        if (Keyboard.current?[Key.F3].wasPressedThisFrame == true)
            _pause = !_pause;

        if (Keyboard.current?[Key.F4].wasPressedThisFrame == true)
        {
            _pause = true;
            _frames = 1;
        }

        if (_frames > 0)
            Time.timeScale = 1f;
        else if (_pause)
            Time.timeScale = 0f;
        else if (_fastForward)
            Time.timeScale = Plugin.ConfigFastForwardSpeed.Value;
        else
            Time.timeScale = 1f;

        _frames = Mathf.Max(0, _frames - 1);
    }
}