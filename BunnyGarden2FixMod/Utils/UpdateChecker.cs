using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace BunnyGarden2FixMod.Utils;

/// <summary>
/// 起動時に GitHub の最新リリースタグと現在のバージョンを比較し、
/// アップデートがあればダイアログで通知するユーティリティ。
/// </summary>
internal static class UpdateChecker
{
    private const string ApiUrl = "https://api.github.com/repos/kazumasa200/BunnyGarden2FixMod/releases/latest";
    private const string ReleasesUrl = "https://github.com/kazumasa200/BunnyGarden2FixMod/releases/latest";
    private const int TimeoutSec = 10;

    public static IEnumerator Check()
    {
        var req = UnityWebRequest.Get(ApiUrl);
        req.SetRequestHeader("User-Agent", $"BunnyGarden2FixMod/{MyPluginInfo.PLUGIN_VERSION}");
        req.timeout = TimeoutSec;

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            PatchLogger.LogInfo($"[UpdateChecker] バージョン確認をスキップしました: {req.error}");
            req.Dispose();
            yield break;
        }

        var tagName = ExtractTagName(req.downloadHandler.text);
        req.Dispose();

        if (tagName == null)
        {
            PatchLogger.LogInfo("[UpdateChecker] GitHub レスポンスのパースに失敗しました");
            yield break;
        }

        var latest = tagName.TrimStart('v');
        var current = MyPluginInfo.PLUGIN_VERSION;

        if (latest == current)
        {
            PatchLogger.LogInfo($"[UpdateChecker] 最新バージョンです (v{current})");
            yield break;
        }

        PatchLogger.LogWarning($"[UpdateChecker] アップデートがあります: v{current} → v{latest}");

        // ゲームが日本語テキストをレンダリングするまでフォントがメモリに載らないため、
        // ロードされるまで最大 60 秒待機する
        TMP_FontAsset gameFont = FindGameFont();
        if (gameFont == null)
        {
            PatchLogger.LogInfo("[UpdateChecker] 日本語フォントのロードを待機しています...");
            for (int i = 0; i < 60 && gameFont == null; i++)
            {
                yield return new WaitForSeconds(1f);
                gameFont = FindGameFont();
            }
            if (gameFont == null)
                PatchLogger.LogInfo("[UpdateChecker] 日本語フォントが見つからなかったため英字のみで表示します");
            else
                PatchLogger.LogInfo("[UpdateChecker] 日本語フォントを取得しました: " + gameFont.name);
        }
        else
        {
            PatchLogger.LogInfo("[UpdateChecker] 日本語フォントを取得しました: " + gameFont.name);
        }

        ShowUpdateDialog(current, latest, gameFont);
    }

    // ── ダイアログ構築 ────────────────────────────────────────────────

    private static void ShowUpdateDialog(string current, string latest, TMP_FontAsset gameFont)
    {
        // ルート Canvas（最前面）
        var root = new GameObject("BG2UpdateNotification");
        Object.DontDestroyOnLoad(root);

        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 9999;

        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);

        root.AddComponent<GraphicRaycaster>();

        // 暗いバックドロップ
        var backdrop = MakeImage(root.transform, "Backdrop", new Color(0f, 0f, 0f, 0.65f));
        Stretch(backdrop);

        // ダイアログパネル（バイリンガル対応で縦幅を拡大）
        var panel = MakeImage(backdrop.transform, "Panel", new Color(0.12f, 0.12f, 0.14f, 1f));
        var prt = panel.rectTransform;
        prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
        prt.sizeDelta = new Vector2(720f, 430f);

        // タイトル
        MakeLabel(panel.transform, "Title",
            "Modにアップデートがあります！\nAn update is available for this Mod!",
            anchorY: 1f, offsetY: -20f, fontSize: 26, bold: true,
            color: new Color(1f, 0.85f, 0.3f), font: gameFont);

        // バージョン情報（日英4行）
        MakeLabel(panel.transform, "VersionInfo",
            $"現在のバージョン :  v{current}\n最新バージョン    :  v{latest}\n\nCurrent Version :  v{current}\nLatest Version    :  v{latest}",
            anchorY: 0.5f, offsetY: 25f, fontSize: 21,
            color: new Color(0.88f, 0.88f, 0.88f), font: gameFont);

        // 「後で」ボタン
        var dismiss = MakeButton(panel.transform, "DismissBtn", "後で / Later",
            anchorX: 0.25f, offsetY: 30f,
            size: new Vector2(185f, 58f),
            bgColor: new Color(0.3f, 0.3f, 0.32f), font: gameFont);
        dismiss.onClick.AddListener(() => Object.Destroy(root));

        // 「ダウンロードページを開く」ボタン
        var download = MakeButton(panel.transform, "DownloadBtn", "ダウンロードページを開く\nGo to Download Page",
            anchorX: 0.72f, offsetY: 30f,
            size: new Vector2(310f, 58f),
            bgColor: new Color(0.18f, 0.48f, 0.9f), font: gameFont);
        download.onClick.AddListener(() =>
        {
            Application.OpenURL(ReleasesUrl);
            Object.Destroy(root);
        });
    }

    /// <summary>
    /// メモリにロード済みの TMP_FontAsset の中から、日本語グリフ（'の' U+306E）を
    /// 持つフォントを返す。見つからなければ null。
    /// </summary>
    private static TMP_FontAsset FindGameFont()
    {
        var allFonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
        foreach (var f in allFonts)
        {
            if (f != null && f.HasCharacter('の'))
                return f;
        }
        PatchLogger.LogInfo("[UpdateChecker] 日本語対応フォントが見つかりませんでした（英字のみ表示されます）");
        return null;
    }

    // ── UI ヘルパー ───────────────────────────────────────────────────

    private static Image MakeImage(Transform parent, string name, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = color;
        return img;
    }

    private static void Stretch(Image img)
    {
        var rt = img.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    private static void MakeLabel(Transform parent, string name, string text,
        float anchorY, float offsetY, int fontSize, bool bold = false,
        Color color = default, TMP_FontAsset font = null)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        var rt = (RectTransform)go.transform;
        rt.anchorMin = new Vector2(0.05f, anchorY);
        rt.anchorMax = new Vector2(0.95f, anchorY);
        rt.pivot = new Vector2(0.5f, anchorY);
        rt.sizeDelta = new Vector2(0f, fontSize * 6f);   // 複数行を収める余裕を持たせる
        rt.anchoredPosition = new Vector2(0f, offsetY);

        var tmp = go.AddComponent<TextMeshProUGUI>();
        if (font != null) tmp.font = font;
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.color = color == default ? Color.white : color;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.overflowMode = TextOverflowModes.Overflow;
    }

    private static Button MakeButton(Transform parent, string name, string label,
        float anchorX, float offsetY, Vector2 size, Color bgColor,
        TMP_FontAsset font = null)
    {
        // ボタン本体
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(anchorX, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.sizeDelta = size;
        rt.anchoredPosition = new Vector2(0f, offsetY);

        var img = go.AddComponent<Image>();
        img.color = bgColor;

        var btn = go.AddComponent<Button>();
        var cb = btn.colors;
        cb.highlightedColor = bgColor * 1.25f;
        cb.pressedColor = bgColor * 0.75f;
        btn.colors = cb;

        // ボタンラベル
        var textGO = new GameObject("Label", typeof(RectTransform));
        textGO.transform.SetParent(go.transform, false);
        var trt = (RectTransform)textGO.transform;
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = trt.offsetMax = Vector2.zero;

        var tmp = textGO.AddComponent<TextMeshProUGUI>();
        if (font != null) tmp.font = font;
        tmp.text = label;
        tmp.fontSize = 19;
        tmp.color = Color.white;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.fontStyle = FontStyles.Bold;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.overflowMode = TextOverflowModes.Overflow;

        return btn;
    }

    // ── JSON パーサー（依存ライブラリ不要の最小実装）─────────────────

    private static string ExtractTagName(string json)
    {
        const string key = "\"tag_name\":\"";
        var start = json.IndexOf(key);
        if (start < 0) return null;
        start += key.Length;
        var end = json.IndexOf('"', start);
        if (end < 0) return null;
        return json.Substring(start, end - start);
    }
}
