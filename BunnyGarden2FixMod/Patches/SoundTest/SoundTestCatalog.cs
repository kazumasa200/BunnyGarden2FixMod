using System;
using System.Collections.Generic;
using BunnyGarden2FixMod.Utils;
using Cysharp.Threading.Tasks;
using GB;
using GB.DLC;
using GB.Game.Params;
using GB.Save;

namespace BunnyGarden2FixMod.Patches.SoundTest;

internal enum TrackKind { Bgm, KaraokeBase, KaraokeDlc, Ending }

/// <summary>曲が終わったときの動き。設定ファイルにも保存する。</summary>
public enum SoundTestRepeatMode
{
    One,    // 同じ曲を繰り返す
    All,    // 次の曲へ進む（一覧を一周）
    None,   // 止まる
}

/// <summary>カラオケ曲の版。ゲーム内の音源はフル・ゲームサイズ・酔い歌唱の 3 つ。</summary>
internal enum TrackVersion { Full, GameSize, Drunk }

internal sealed class Track
{
    public TrackKind Kind;
    public SoundManager.BGM Bgm;      // Bgm / Ending。KaraokeBase はゲームサイズ版の値を基準に持つ
    public DLCs.DLCID Dlc;            // KaraokeDlc
    public int Cast = -1;             // 0..5、キャストに紐づかない曲は -1
    public string TitleMsg;           // MSGID 名。MSG テーブルは画面を開いた時点で解決する
    public string TitleText;          // 解決済み、または Mod 側で付けた題名
    public string Note;               // 使用場所の一言（翻訳済み）
    public SoundUnlockType Unlock = SoundUnlockType.None;

    public bool HasVersions => Kind == TrackKind.KaraokeBase || Kind == TrackKind.KaraokeDlc;

    public string ResolveTitle(MessageManager msg)
    {
        if (TitleText != null)
            return TitleText;
        if (!MSGID.TryParse(TitleMsg, out var id))
            return TitleText = TitleMsg;
        // タイトル画面など MSG テーブルが未ロードの間は例外になるので、そのときは次回また試す
        try { return TitleText = msg.RefText(id); }
        catch (Exception) { return TitleMsg; }
    }
}

/// <summary>
/// サウンドテストの曲一覧。ゲームの <c>SoundTestParams</c>（BGM 30 曲＋カラオケのフル版）と
/// DLC カラオケに、通常は選べないスタッフロール曲を足す。カラオケは版（フル／ゲームサイズ／酔い）を
/// 1 行にまとめ、再生時に版へ展開する。
/// </summary>
internal static class SoundTestCatalog
{
    internal static readonly string[] CastFile = { "Kana", "Rin", "Miuka", "Erisa", "Kuon", "Luna" };
    internal static readonly string[] CastNameJa = { "花奈", "凜", "美羽香", "英梨紗", "黒音", "瑠那" };

    private static readonly Dictionary<SoundManager.BGM, string> s_notes = new()
    {
        [SoundManager.BGM.Title] = "タイトル画面と、エクストラのスタッフクレジット",
        [SoundManager.BGM.Home] = "自宅（通常）",
        [SoundManager.BGM.HomeDebt] = "自宅（借金が残っているとき）",
        [SoundManager.BGM.Kana] = "バニーガーデン店内BGM（花奈を指名中）",
        [SoundManager.BGM.Rin] = "バニーガーデン店内BGM（凜を指名中）",
        [SoundManager.BGM.Miuka] = "バニーガーデン店内BGM（美羽香を指名中）",
        [SoundManager.BGM.Erisa] = "バニーガーデン店内BGM（英梨紗を指名中）",
        [SoundManager.BGM.Kuon] = "バニーガーデン店内BGM（黒音を指名中）",
        [SoundManager.BGM.Luna] = "バニーガーデン店内BGM（瑠那を指名中）",
        [SoundManager.BGM.Birthday] = "バニーガーデン店内BGM（キャストの誕生日）",
        [SoundManager.BGM.Minigame] = "ミニゲーム（手押し相撲・かるた・ツイスター）",
        [SoundManager.BGM.FoodMinigame] = "ミニゲーム「あーん」",
        [SoundManager.BGM.Cheki] = "チェキ撮影",
        [SoundManager.BGM.Hopeful] = "プロローグ",
        [SoundManager.BGM.HorseRacingFinish] = "競馬のゴール",
        [SoundManager.BGM.CastDrunk] = "バニーガーデン店内BGM（キャストが酔っているとき）",
        [SoundManager.BGM.PlayerDrunk] = "バニーガーデン店内BGM（自分が酔っているとき）",
        [SoundManager.BGM.BothDrunk] = "バニーガーデン店内BGM（二人とも酔っているとき）",
        [SoundManager.BGM.Extra] = "エクストラメニュー",
        [SoundManager.BGM.WeekdayEncount] = "平日の街での出会い",
        [SoundManager.BGM.EscortedEntry] = "同伴出勤",
        [SoundManager.BGM.NormalAfter] = "アフター（通常）",
        [SoundManager.BGM.HighClassAfter] = "アフター（高級店）",
        [SoundManager.BGM.HolidayAfter] = "旅行",
        [SoundManager.BGM.HolidayAfterSleep] = "旅行：夜",
        [SoundManager.BGM.PrePropose] = "告白直前のアフター・誕生日のアフター",
        [SoundManager.BGM.ProposeConfirmed] = "告白が成立したとき",
        [SoundManager.BGM.LonelyEnd] = "孤独エンドのスタッフロール",
        [SoundManager.BGM.KirakiraEvent] = "エピローグ",
        [SoundManager.BGM.SteelFrame] = "鉄骨渡り",
    };

    internal static List<Track> Build()
    {
        var sys = GBSystem.Instance;
        var msg = sys.RefMessage();
        var list = new List<Track>();

        foreach (var p in sys.RefSoundTestParam().Params)
        {
            if (p.KaraokeSize != KaraokeSize.None)
            {
                int cast = ((int)p.BGMId - (int)SoundManager.BGM.KaraokeIDBegin) / 1000;
                list.Add(new Track
                {
                    Kind = TrackKind.KaraokeBase,
                    Cast = cast,
                    Bgm = (SoundManager.BGM)((int)SoundManager.BGM.KaraokeIDBegin + cast * 1000 + 1),
                    TitleMsg = p.Title,
                    Unlock = p.UnlockType,
                    Note = string.Format(Loc.Tr("{0}のカラオケ曲。VIP ルームでの持ち歌"), Loc.Tr(CastNameJa[cast])),
                });
            }
            else
            {
                list.Add(new Track
                {
                    Kind = TrackKind.Bgm,
                    Bgm = p.BGMId,
                    TitleMsg = p.Title,
                    Unlock = p.UnlockType,
                    Note = s_notes.TryGetValue(p.BGMId, out var note) ? Loc.Tr(note) : "",
                });
            }
        }

        foreach (var id in sys.QueryHasDLCKaraoke())
        {
            string name = id.ToString();   // 例: DLC_KARAOKE_KANA_1
            int cast = Array.FindIndex(CastFile, c => name.Contains("_" + c.ToUpperInvariant() + "_"));
            string title = name;
            try { title = msg.RefText((MSGID)msg.GetDLCMSGID($"KARAOKE_TITLE_{id}_0")); }
            catch (Exception) { }
            list.Add(new Track
            {
                Kind = TrackKind.KaraokeDlc,
                Dlc = id,
                Cast = cast,
                TitleText = title,
                Note = cast >= 0
                    ? string.Format(Loc.Tr("{0}の DLC カラオケ曲"), Loc.Tr(CastNameJa[cast]))
                    : Loc.Tr("DLC カラオケ曲"),
            });
        }

        // スタッフロール曲はゲームのサウンドテストに載っていない
        for (int c = 0; c < CastFile.Length; c++)
        {
            list.Add(new Track
            {
                Kind = TrackKind.Ending,
                Cast = c,
                Bgm = SoundManager.BGM.StaffCreditKana + c,
                TitleText = string.Format(Loc.Tr("スタッフロール（{0}）"), Loc.Tr(CastNameJa[c])),
                Unlock = (SoundUnlockType)c,
                Note = string.Format(Loc.Tr("{0}エンドのスタッフロール"), Loc.Tr(CastNameJa[c])),
            });
        }
        list.Add(new Track
        {
            Kind = TrackKind.Ending,
            Bgm = SoundManager.BGM.StaffCreditHarem,
            TitleText = Loc.Tr("スタッフロール（ハーレム）"),
            Unlock = SoundUnlockType.Harem,
            Note = Loc.Tr("ハーレムエンドのスタッフロール。通常のサウンドテストには無い曲"),
        });
        return list;
    }

    /// <summary>解放判定。ゲームの SoundTest.unlock と同じ規則で、DLC 曲は導入済みかどうか。</summary>
    internal static bool IsUnlocked(Track t)
    {
        var sys = GBSystem.Instance;
        if (t.Kind == TrackKind.KaraokeDlc)
            return sys.IsDLCInstalled(t.Dlc);

        var save = sys.RefSaveData();
        switch (t.Unlock)
        {
            case SoundUnlockType.None: return true;
            case SoundUnlockType.Lonely: return save.IsShowLonelyRoute();
            case SoundUnlockType.SteelFrame: return save.GetMiniGameUnlockState(SaveData.MINIGAME_NUM - 1) > ExtraUnlockState.LOCKED;
            case SoundUnlockType.Any: return save.IsAnyCastCleared();
            default: return save.IsClearRoute((int)t.Unlock);
        }
    }

    internal static SoundManager.BGM ResolveBgm(Track t, TrackVersion v)
    {
        if (t.Kind != TrackKind.KaraokeBase)
            return t.Bgm;
        // 酔い版は +10、フル版は +100（SoundManager.BGM の採番規則）
        return t.Bgm + (v == TrackVersion.Full ? 100 : v == TrackVersion.Drunk ? 10 : 0);
    }

    /// <summary>頭出し位置を指定して再生する。同じ曲の再指定は無視されるので、先に止めてから鳴らす。</summary>
    internal static void Play(Track t, TrackVersion v, float time, bool loop)
    {
        var sys = GBSystem.Instance;
        sys.StopBGMImmediate();
        if (t.Kind == TrackKind.KaraokeDlc)
            sys.PlayBGM(t.Dlc, true, time, loop, 1f, v == TrackVersion.Drunk, v != TrackVersion.Full).Forget();
        else
            sys.PlayBGM(ResolveBgm(t, v), true, time, loop, 1f, false);
    }

    /// <summary>
    /// 今の BGM ソースのループ指定を上書きする。ゲームは通常 BGM を必ずループで鳴らすので、
    /// 1 曲リピート以外のときは毎フレーム false を入れて曲の終わりを作る。
    /// </summary>
    internal static void SetLoop(bool loop)
    {
        var sm = GBSystem.Instance.m_sound;
        var src = sm.m_bgm[sm.m_currentBGMSource].Source;
        if (src.loop != loop)
            src.loop = loop;
    }

    internal static void Stop() => GBSystem.Instance.StopBGM();

    internal static void Progress(out float time, out float length, out bool playing)
    {
        var sm = GBSystem.Instance.m_sound;
        var src = sm.m_bgm[sm.m_currentBGMSource].Source;
        time = src.time;
        length = src.clip != null ? src.clip.length : 0f;
        playing = src.isPlaying;
    }
}
