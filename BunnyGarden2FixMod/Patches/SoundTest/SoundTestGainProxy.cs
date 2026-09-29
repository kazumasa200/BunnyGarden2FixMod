using GB;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.SoundTest;

/// <summary>
/// サウンドテストの音量を 0〜200% にするための代理再生。
///
/// <para>
/// Unity の <c>AudioSource.volume</c> は 1.0 が上限で、ゲームはミキサーの音量も公開していない。
/// そこでゲームの BGM ソースをミュートし、同じクリップを同じ位置から鳴らす代理ソースを用意して、
/// その出力にゲインを掛ける（<see cref="OnAudioFilterRead"/>）。ゲーム側の音量操作（設定・クロスフェード）は
/// 毎フレーム代理へ写すので、ゲイン 1.0 なら聞こえ方は元と同じ。
/// </para>
/// <para>
/// ゲインが 100% のときは代理を使わない。乗り換え（<see cref="AllowBind"/>）は画面を開いている間だけ許し、
/// 閉じた後は今の曲が止まるまで面倒を見て、止まったらミュートを解いて手を離す。
/// </para>
/// </summary>
internal sealed class SoundTestGainProxy : MonoBehaviour
{
    // ponytail: 同期は「ずれたら位置を合わせ直す」だけ。ループ境界やピッチ変更まで厳密に追う必要が出たら見直す
    private const int ResyncThresholdSamples = 4096;

    private AudioSource m_proxy;
    private AudioSource m_target;
    private float m_gain = 1f;

    /// <summary>0〜2。1 のとき代理は使われない。</summary>
    internal float Gain
    {
        get => m_gain;
        set => m_gain = Mathf.Clamp(value, 0f, 2f);
    }

    /// <summary>今鳴っている BGM ソースへ乗り換えてよいか。サウンドテストを開いている間だけ true。</summary>
    internal bool AllowBind;

    private void Awake()
    {
        m_proxy = gameObject.AddComponent<AudioSource>();
        m_proxy.playOnAwake = false;
        m_proxy.spatialBlend = 0f;
    }

    private void OnDestroy() => Unbind();

    internal void Unbind()
    {
        if (m_target != null)
            m_target.mute = false;
        m_target = null;
        if (m_proxy != null)
        {
            m_proxy.Stop();
            m_proxy.clip = null;
        }
    }

    private void LateUpdate()
    {
        if (Mathf.Approximately(m_gain, 1f))
        {
            Unbind();
            return;
        }

        var sm = GBSystem.Instance != null ? GBSystem.Instance.m_sound : null;
        if (sm == null)
        {
            Unbind();
            return;
        }

        if (AllowBind)
        {
            var current = sm.m_bgm[sm.m_currentBGMSource].Source;
            if (current != m_target && current.isPlaying && current.clip != null)
                Bind(current);
        }

        if (m_target == null)
            return;
        if (!m_target.isPlaying)
        {
            // 曲が終わった／ゲームが別の曲に切り替えた。次の曲はゲームにそのまま鳴らさせる
            Unbind();
            return;
        }
        Mirror();
    }

    private void Bind(AudioSource source)
    {
        Unbind();
        m_target = source;
        m_target.mute = true;
        m_proxy.outputAudioMixerGroup = source.outputAudioMixerGroup;
        m_proxy.clip = source.clip;
        m_proxy.loop = source.loop;
        m_proxy.pitch = source.pitch;
        m_proxy.volume = source.volume;
        m_proxy.timeSamples = source.timeSamples;
        m_proxy.Play();
    }

    private void Mirror()
    {
        if (m_proxy.clip != m_target.clip)
        {
            // DLC 曲は同じソースにクリップを差し替えて鳴らすので、その場合は取り直す
            Bind(m_target);
            return;
        }
        m_proxy.volume = m_target.volume;
        m_proxy.loop = m_target.loop;
        m_proxy.pitch = m_target.pitch;
        if (!m_proxy.isPlaying)
            m_proxy.Play();
        if (Mathf.Abs(m_proxy.timeSamples - m_target.timeSamples) > ResyncThresholdSamples)
            m_proxy.timeSamples = m_target.timeSamples;
    }

    // オーディオスレッドで呼ばれる。ゲインは float の読み取りだけなので同期は不要
    private void OnAudioFilterRead(float[] data, int channels)
    {
        float gain = m_gain;
        if (gain == 1f)
            return;
        for (int i = 0; i < data.Length; i++)
            data[i] *= gain;
    }
}
