using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using BunnyGarden2FixMod.Utils;
using GB;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace BunnyGarden2FixMod.Patches.SoundTest;

internal sealed class LyricLine
{
    public float Time;    // 音源の先頭からの秒
    public string Text;   // 空なら「何も表示しない」区間
}

/// <summary>
/// 歌詞の取り出し。
/// <list type="bullet">
///   <item>ゲームサイズ版・酔い版: カラオケの譜面 CSV にある行番号とタイミングを使い、本文は MSG から引く（公式・多言語）</item>
///   <item>フル版: ゲーム内にタイミングが無いので、Mod 同梱の LRC を使う（eYe♡とらっかー のみ）</item>
/// </list>
/// 譜面のタイミングは「BPM × 48 分解能」の刻み。音源は bgmTiming の刻みだけ遅れて始まるので、その分を引いて音源時刻へ直す。
/// </summary>
internal static class SoundTestLyrics
{
    private static readonly Dictionary<string, List<LyricLine>> s_cache = new();
    private static readonly Regex s_lrcLine = new(@"^\[(\d+):(\d+(?:\.\d+)?)\](.*)$");

    internal static bool MayHave(Track t, TrackVersion v)
        => t.HasVersions && (v != TrackVersion.Full || HasFullLrc(t));

    // フル版の LRC があるのは eYe♡とらっかー（DLC_KARAOKE_*_1、6 人とも同じオケ）だけ
    private static bool HasFullLrc(Track t)
        => t.Kind == TrackKind.KaraokeDlc && t.Dlc.ToString().EndsWith("_1", StringComparison.Ordinal);

    internal static IEnumerator Load(Track t, TrackVersion v, Action<List<LyricLine>> done)
    {
        string key = $"{t.Kind}:{t.Bgm}:{t.Dlc}:{v}";
        if (s_cache.TryGetValue(key, out var cached))
        {
            done(cached);
            yield break;
        }

        List<LyricLine> lines = null;
        if (!t.HasVersions)
        {
        }
        else if (v == TrackVersion.Full)
        {
            if (HasFullLrc(t))
                lines = LoadEmbeddedLrc("DLC001_full.lrc");
        }
        else if (t.Kind == TrackKind.KaraokeBase)
        {
            string path = $"Karaoke/{SoundTestCatalog.CastFile[t.Cast]}/001/info{(v == TrackVersion.Drunk ? "_drunk" : "")}.csv";
            var h = Addressables.LoadAssetAsync<TextAsset>(path);
            yield return h;
            if (h.IsValid() && h.Result != null)
            {
                lines = ParseChart(h.Result.text, isDlc: false);
                Addressables.Release(h);
            }
            else
                PatchLogger.LogWarning($"[SoundTest] 譜面を読めませんでした: {path}");
        }
        else
        {
            string path = $"Karaoke/{t.Dlc}/001/info{(v == TrackVersion.Drunk ? "_drunk" : "")}.csv";
            var h = GBSystem.Instance.LoadDLCAsync<TextAsset>(t.Dlc, path);
            yield return h;
            // DLC のハンドルはゲーム側が管理しているので解放しない
            if (h.IsValid() && h.Result != null)
                lines = ParseChart(h.Result.text, isDlc: true);
            else
                PatchLogger.LogWarning($"[SoundTest] DLC の譜面を読めませんでした: {path}");
        }

        lines ??= new List<LyricLine>();
        s_cache[key] = lines;
        done(lines);
    }

    /// <summary>time 以前に始まった最後の行。無ければ -1。</summary>
    internal static int IndexAt(List<LyricLine> lines, float time)
    {
        int index = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Time > time)
                break;
            index = i;
        }
        return index;
    }

    private static List<LyricLine> ParseChart(string csv, bool isDlc)
    {
        var l = csv.Split('\n');
        float bpm = float.Parse(l[0].Trim(), CultureInfo.InvariantCulture);
        int bgmTiming = int.Parse(l[2].Trim(), CultureInfo.InvariantCulture);
        string group = l[11].Trim();
        var numbers = l[12].Trim().Split(',');
        var timings = l[13].Trim().Split(',');
        var msg = GBSystem.Instance.RefMessage();

        var result = new List<LyricLine>();
        for (int i = 0; i < numbers.Length && i < timings.Length; i++)
        {
            int timing = int.Parse(timings[i].Trim(), CultureInfo.InvariantCulture);
            float seconds = (timing - bgmTiming) / (bpm * 48f) * 60f;

            string text = "";
            string number = numbers[i].Trim();
            if (number != "ZZ" && int.TryParse(number, out int n))
            {
                string name = $"KARAOKE_{group}_{n - 1}";
                try
                {
                    if (isDlc)
                    {
                        int id = msg.GetDLCMSGID(name);
                        if (id > 0)
                            text = msg.RefText((MSGID)id);
                    }
                    else if (MSGID.TryParse(name, out var mid))
                    {
                        text = msg.RefText(mid);
                    }
                }
                catch (Exception)
                {
                    text = "";
                }
            }
            result.Add(new LyricLine { Time = seconds, Text = (text ?? "").Replace("\n", " ").Trim() });
        }
        return result;
    }

    private static List<LyricLine> LoadEmbeddedLrc(string fileName)
    {
        var result = new List<LyricLine>();
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BunnyGarden2FixMod.Resources.soundtest." + fileName);
        if (stream == null)
        {
            PatchLogger.LogWarning($"[SoundTest] 歌詞リソースがありません: {fileName}");
            return result;
        }
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            var m = s_lrcLine.Match(line);
            if (!m.Success)
                continue;
            float seconds = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60f
                          + float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            // 行末の ← は校正待ちの印。表示には出さない
            result.Add(new LyricLine { Time = seconds, Text = m.Groups[3].Value.Replace("←", "").Trim() });
        }
        return result;
    }
}
