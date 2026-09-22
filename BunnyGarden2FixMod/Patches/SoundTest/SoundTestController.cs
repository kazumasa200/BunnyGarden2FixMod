using System.Collections.Generic;
using BunnyGarden2FixMod.Utils;
using Cysharp.Threading.Tasks;
using GB;
using HarmonyLib;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.SoundTest;

/// <summary>
/// ゲームのサウンドテスト（<c>GB.Extra.SoundTest</c>）を Mod の画面に置き換える。
/// 呼び出し側（エクストラメニュー・自宅の音楽アプリ）は <c>Show()</c> を投げて <c>IsDone</c> を見張るだけなので、
/// <c>Show</c> を横取りして自前の画面を出し、閉じるときに <c>m_isDone</c> を立てれば両方の導線がそのまま動く。
/// 操作はゲーム版と同じ（↑↓ 選択 / A 再生・停止 / B か右クリックで閉じる）に ←→ の版切り替えを足したもの。
/// </summary>
public class SoundTestController : MonoBehaviour
{
    private static SoundTestController s_instance;

    private SoundTestView m_view;
    private SoundTestGainProxy m_gain;
    private GB.Extra.SoundTest m_host;
    private List<Track> m_tracks = new();
    private int m_selected;
    private TrackVersion m_version;
    private int m_playingIndex = -1;
    private TrackVersion m_playingVersion;

    private List<LyricLine> m_lyrics;
    private readonly List<int> m_lyricDisplayIndex = new();   // 歌詞の行 → 表示行（空行は -1）
    private int m_lyricRequest;

    private static readonly TrackVersion[] s_versions = { TrackVersion.Full, TrackVersion.GameSize, TrackVersion.Drunk };

    /// <summary>右スティックを倒し切ったときの歌詞スクロール速度（px/秒）。</summary>
    private const float LyricScrollSpeed = 600f;

    private int m_volumePercent;
    private float m_volumeSaveAt = -1f;   // 操作が止まってから設定ファイルへ書く

    private SoundTestRepeatMode m_repeat;
    private bool m_playbackSeen;          // 再生開始後に一度でも鳴っているのを見たか（DLC の非同期ロード中を「終了」と誤認しない）

    private bool IsOpen => m_host != null;

    /// <summary>サウンドテストを開いている間は true。DLC カラオケの音量補正（×1.3）を外すための旗。</summary>
    internal static bool IsActive { get; private set; }

    public static SoundTestController Initialize(GameObject parent)
        => s_instance = parent.AddComponent<SoundTestController>();

    internal static bool TryOpen(GB.Extra.SoundTest host)
    {
        if (s_instance == null)
            return false;
        s_instance.Open(host);
        return true;
    }

    private void Awake()
    {
        // UIDocument は 1 GameObject に 1 つ、かつ子に置くと親と同じ PanelSettings を要求される。
        // どちらも避けるため、親を持たない独立した GameObject に載せる
        var go = new GameObject("BG2FixMod.SoundTestView");
        DontDestroyOnLoad(go);
        m_view = go.AddComponent<SoundTestView>();
        m_view.OnRowClicked += i => { Select(i); TogglePlay(); };
        m_view.OnVersionClicked += SetVersion;
        m_view.OnSeek += Seek;
        m_view.OnPlayClicked += TogglePlay;
        m_view.OnRepeatClicked += CycleRepeat;
        m_view.OnCloseClicked += Close;
        m_view.OnVolumeChanged += percent => ApplyVolume(Mathf.RoundToInt(percent), updateSlider: false);
        m_view.OnVolumeCommitted += percent => ApplyVolume(Mathf.RoundToInt(percent), updateSlider: false);

        // ゲインフィルタは AudioSource と同じ GameObject に要るので、音声専用の GameObject を分ける
        var audio = new GameObject("BG2FixMod.SoundTestAudio");
        DontDestroyOnLoad(audio);
        m_gain = audio.AddComponent<SoundTestGainProxy>();
    }

    private void Open(GB.Extra.SoundTest host)
    {
        m_host = host;
        IsActive = true;
        GBSystem.Instance.StopBGM();
        m_tracks = SoundTestCatalog.Build();
        m_selected = 0;
        m_version = TrackVersion.Full;
        m_playingIndex = -1;
        m_volumePercent = Configs.SoundTestVolume.Value;
        m_gain.Gain = m_volumePercent / 100f;
        m_gain.AllowBind = true;
        m_repeat = Configs.SoundTestRepeat.Value;
        try
        {
            m_view.Show();
            m_view.SetVolume(Configs.SoundTestVolume.Value);
        }
        catch (System.Exception e)
        {
            // 画面が作れなければ元のメニューへ戻し、ゲームを止めない
            PatchLogger.LogError($"[SoundTest] 画面の構築に失敗しました: {e}");
            Close();
            return;
        }
        RenderList();
        RenderDetail();
        RequestLyrics();
        m_view.SetRepeat((int)m_repeat, RepeatLabel(m_repeat));
    }

    private void Close()
    {
        if (!IsOpen)
            return;
        m_view.Hide();
        SaveVolumeIfDirty();
        m_gain.AllowBind = false;   // 鳴っている曲は最後まで面倒を見るが、次の曲には手を出さない
        var host = m_host;
        m_host = null;
        IsActive = false;
        m_lyricRequest++;
        GBSystem.Instance.PlayCancelSE();
        // 呼び出し側がこれを見て元のメニューへ戻る。曲はゲーム版と同じく止めない（自宅では聴き続けられる）
        host.m_isDone = true;
    }

    private void Update()
    {
        if (!IsOpen)
            return;
        var sys = GBSystem.Instance;
        if (sys == null)
        {
            Close();
            return;
        }
        if (sys.IsPauseMenuActive())
            return;

        if (GBInput.UpTriggeredR)
            Select((m_selected - 1 + m_tracks.Count) % m_tracks.Count);
        else if (GBInput.DownTriggeredR)
            Select((m_selected + 1) % m_tracks.Count);
        else if (GBInput.LeftTriggeredR)
            CycleVersion(-1);
        else if (GBInput.RightTriggeredR)
            CycleVersion(1);
        else if (GBInput.ATriggered)
            TogglePlay();
        else if (GBInput.YTriggered)
            CycleRepeat();
        else if (GBInput.BTriggered || GBInput.RightClick)
        {
            Close();
            return;
        }

        // L/R で ±1%、ZL/ZR で ±10%。押しっぱなしで連続
        int volumeDelta = (GBInput.RTriggeredR ? 1 : 0) - (GBInput.LTriggeredR ? 1 : 0)
                        + (GBInput.ZRTriggeredR ? 10 : 0) - (GBInput.ZLTriggeredR ? 10 : 0);
        if (volumeDelta != 0)
            ApplyVolume(m_volumePercent + volumeDelta, updateSlider: true);
        if (m_volumeSaveAt >= 0f && Time.unscaledTime >= m_volumeSaveAt)
            SaveVolumeIfDirty();

        // 右スティックで歌詞を送る（上に倒すと上へ）
        float stick = GBInput.RStick.y;
        if (Mathf.Abs(stick) > 0.2f)
            m_view.ScrollLyrics(-stick * LyricScrollSpeed * Time.unscaledDeltaTime);

        UpdateProgress();
    }

    private void Select(int index)
    {
        if (index == m_selected || index < 0 || index >= m_tracks.Count)
            return;
        m_selected = index;
        GBSystem.Instance.PlaySelectSE();
        RenderList();
        RenderDetail();
        RequestLyrics();
    }

    private void CycleVersion(int direction)
    {
        var t = m_tracks[m_selected];
        if (!t.HasVersions)
            return;
        SetVersion(((int)m_version + direction + s_versions.Length) % s_versions.Length);
    }

    private void SetVersion(int index)
    {
        var version = s_versions[index];
        if (version == m_version)
            return;
        m_version = version;
        GBSystem.Instance.PlaySelectSE();
        RenderDetail();
        RequestLyrics();
        // 再生中の曲の版を変えたら、その版で鳴らし直す
        if (m_playingIndex == m_selected)
            StartPlayback(m_selected, m_version);
    }

    private void CycleRepeat()
    {
        m_repeat = (SoundTestRepeatMode)(((int)m_repeat + 1) % 3);
        Configs.SoundTestRepeat.Value = m_repeat;
        GBSystem.Instance.PlaySelectSE();
        m_view.SetRepeat((int)m_repeat, RepeatLabel(m_repeat));
    }

    private static string RepeatLabel(SoundTestRepeatMode mode) => mode switch
    {
        SoundTestRepeatMode.One => Loc.Tr("1曲"),
        SoundTestRepeatMode.All => Loc.Tr("全曲"),
        _ => Loc.Tr("なし"),
    };

    private void StartPlayback(int index, TrackVersion version)
    {
        SoundTestCatalog.Play(m_tracks[index], version, 0f, loop: m_repeat == SoundTestRepeatMode.One);
        m_playingIndex = index;
        m_playingVersion = version;
        m_playbackSeen = false;
    }

    /// <summary>曲が最後まで鳴り終わったときの処理。1 曲リピートはループさせているのでここへ来ない。</summary>
    private void OnTrackEnded()
    {
        if (m_repeat == SoundTestRepeatMode.All)
        {
            int next = NextUnlocked(m_playingIndex);
            if (next >= 0)
            {
                Select(next);
                StartPlayback(next, m_version);
                RenderList();
                return;
            }
        }
        // リピートなし: 無音（BGM.None）に切り替えて止まる
        SoundTestCatalog.Stop();
        m_playingIndex = -1;
        RenderList();
    }

    private int NextUnlocked(int from)
    {
        for (int step = 1; step <= m_tracks.Count; step++)
        {
            int i = (from + step) % m_tracks.Count;
            if (SoundTestCatalog.IsUnlocked(m_tracks[i]))
                return i;
        }
        return -1;
    }

    private void TogglePlay()
    {
        var t = m_tracks[m_selected];
        if (!SoundTestCatalog.IsUnlocked(t))
        {
            GBSystem.Instance.PlayBeepSE();
            return;
        }
        GBSystem.Instance.PlayDecideSE();
        if (m_playingIndex == m_selected && m_playingVersion == m_version)
        {
            SoundTestCatalog.Stop();
            m_playingIndex = -1;
        }
        else
        {
            StartPlayback(m_selected, m_version);
        }
        RenderList();
    }

    private void ApplyVolume(int percent, bool updateSlider)
    {
        percent = Mathf.Clamp(percent, 0, 200);
        if (percent == m_volumePercent)
            return;
        m_volumePercent = percent;
        m_gain.Gain = percent / 100f;
        if (updateSlider)
            m_view.SetVolume(percent);
        m_volumeSaveAt = Time.unscaledTime + 0.5f;
    }

    private void SaveVolumeIfDirty()
    {
        if (m_volumeSaveAt < 0f)
            return;
        m_volumeSaveAt = -1f;
        if (Configs.SoundTestVolume.Value != m_volumePercent)
            Configs.SoundTestVolume.Value = m_volumePercent;
    }

    private void Seek(float fraction)
    {
        if (m_playingIndex < 0)
            return;
        SoundTestCatalog.Progress(out _, out float length, out _);
        if (length <= 0f)
            return;
        SoundTestCatalog.Play(m_tracks[m_playingIndex], m_playingVersion, fraction * length, loop: m_repeat == SoundTestRepeatMode.One);
        m_playbackSeen = false;
    }

    private void UpdateProgress()
    {
        SoundTestCatalog.Progress(out float time, out float length, out bool playing);
        m_view.SetProgress(time, length, playing && m_playingIndex >= 0);

        if (m_playingIndex >= 0)
        {
            SoundTestCatalog.SetLoop(m_repeat == SoundTestRepeatMode.One);
            if (playing)
                m_playbackSeen = true;
            else if (m_playbackSeen)
            {
                OnTrackEnded();
                return;
            }
        }

        bool lyricsFollow = m_lyrics != null && m_playingIndex == m_selected && m_playingVersion == m_version && playing;
        if (!lyricsFollow)
        {
            m_view.SetLyricCurrent(-1);
            return;
        }
        int index = SoundTestLyrics.IndexAt(m_lyrics, time);
        m_view.SetLyricCurrent(index >= 0 ? m_lyricDisplayIndex[index] : -1);
    }

    private void RenderList()
    {
        var msg = GBSystem.Instance.RefMessage();
        var labels = new List<string>(m_tracks.Count);
        var locked = new List<bool>(m_tracks.Count);
        foreach (var t in m_tracks)
        {
            bool unlocked = SoundTestCatalog.IsUnlocked(t);
            locked.Add(!unlocked);
            labels.Add(unlocked ? t.ResolveTitle(msg) : Loc.Tr("？？？"));
        }
        m_view.RenderList(labels, m_selected, m_playingIndex, locked);
    }

    private void RenderDetail()
    {
        var t = m_tracks[m_selected];
        if (!SoundTestCatalog.IsUnlocked(t))
        {
            m_view.RenderDetail(Loc.Tr("？？？"), KindLabel(t), Loc.Tr("条件を満たすと聴けるようになります"), null, 0);
            return;
        }
        string[] versions = t.HasVersions
            ? new[] { Loc.Tr("フル"), Loc.Tr("ゲームサイズ"), Loc.Tr("酔い") }
            : null;
        m_view.RenderDetail(t.ResolveTitle(GBSystem.Instance.RefMessage()), KindLabel(t), t.Note, versions, (int)m_version);
    }

    private static string KindLabel(Track t) => t.Kind switch
    {
        TrackKind.KaraokeBase => Loc.Tr("カラオケ"),
        TrackKind.KaraokeDlc => Loc.Tr("カラオケ（DLC）"),
        TrackKind.Ending => Loc.Tr("エンディング"),
        _ => "BGM",
    };

    private void RequestLyrics()
    {
        int token = ++m_lyricRequest;
        m_lyrics = null;
        m_lyricDisplayIndex.Clear();
        var t = m_tracks[m_selected];

        if (!SoundTestCatalog.IsUnlocked(t) || !t.HasVersions)
        {
            m_view.RenderLyrics(System.Array.Empty<string>(), "");
            return;
        }
        if (!SoundTestLyrics.MayHave(t, m_version))
        {
            m_view.RenderLyrics(System.Array.Empty<string>(), Loc.Tr("フル版の歌詞データはありません（ゲームサイズ版・酔い版で表示できます）"));
            return;
        }
        m_view.RenderLyrics(System.Array.Empty<string>(), Loc.Tr("読み込み中…"));
        StartCoroutine(SoundTestLyrics.Load(t, m_version, lines =>
        {
            if (token != m_lyricRequest)
                return;
            ApplyLyrics(lines);
        }));
    }

    private void ApplyLyrics(List<LyricLine> lines)
    {
        m_lyrics = lines;
        m_lyricDisplayIndex.Clear();
        var display = new List<string>();
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line.Text))
            {
                m_lyricDisplayIndex.Add(-1);
                continue;
            }
            m_lyricDisplayIndex.Add(display.Count);
            display.Add(line.Text);
        }
        m_view.RenderLyrics(display, Loc.Tr("この曲の歌詞データはありません"));
    }
}

/// <summary>
/// ゲームは DLC カラオケの譜面用音源（ゲームサイズ・酔い）を BGM 音量の 1.3 倍で鳴らす（ミニゲーム中の演出）。
/// サウンドテストではフル版と同じ音量で聴きたいので、開いている間は補正を外す。
/// </summary>
[HarmonyPatch(typeof(SoundManager), "GetKaraokeVolume")]
internal static class SoundTestKaraokeVolumePatch
{
    private static void Prefix(ref bool isKaraoke)
    {
        if (SoundTestController.IsActive)
            isKaraoke = false;
    }
}

[HarmonyPatch(typeof(GB.Extra.SoundTest), nameof(GB.Extra.SoundTest.Show))]
internal static class SoundTestShowPatch
{
    private static bool Prefix(GB.Extra.SoundTest __instance, ref UniTask __result)
    {
        if (!Configs.SoundTestEnabled.Value)
            return true;
        __instance.m_isDone = false;
        if (!SoundTestController.TryOpen(__instance))
            return true;
        __result = UniTask.CompletedTask;
        return false;
    }
}
