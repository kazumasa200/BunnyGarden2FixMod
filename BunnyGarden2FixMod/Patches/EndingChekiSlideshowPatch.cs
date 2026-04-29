using BunnyGarden2FixMod.ExSave;
using BunnyGarden2FixMod.Utils;
using GB;
using GB.Game;
using GB.Scene;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// エンディング（スタッフクレジット）中に、保存済みチェキを画面左側に
/// フェードイン／フェードアウトで順番に流す演出を追加するパッチ。
///
/// 表示内容: 写真 + グラフィティ（サイン）+ キャスト名ラベル（NameL / NameR）
/// タイミング: BGMクリップ長から末尾ロゴ時間を引き、チェキ枚数で均等分割。
/// </summary>
[HarmonyPatch(typeof(StaffCreditScene), "Start")]
public static class EndingChekiSlideshowPatch
{
    private static bool Prepare()
    {
        PatchLogger.LogInfo("[EndingChekiSlideshow] StaffCreditScene.Start をパッチしました");
        return true;
    }

    private static void Postfix(StaffCreditScene __instance)
    {
        if (!Configs.EndingChekiSlideshow.Value) return;
        __instance.gameObject.AddComponent<ChekiSlideshowBehaviour>();
    }
}

public sealed class ChekiSlideshowBehaviour : MonoBehaviour
{
    // ══════════════════════════════════════════════════════════════════
    // ★ 微調整用パラメータ — ここの値を変えて見た目を調整してください ★
    // ══════════════════════════════════════════════════════════════════

    /// <summary>写真エリアの画面高さに対するサイズ比率</summary>
    private const float ChekiPhotoSizeRatio = 0.35f;

    /// <summary>写真エリアの最大サイズ上限（px）</summary>
    private const float ChekiPhotoSizeMax = 560000f;

    /// <summary>チェキシートの上・左・右のボーダー幅（写真サイズ比率）</summary>
    private const float ChekiBorderRatio = 0.02f;

    /// <summary>チェキシートの下ボーダー幅（写真サイズ比率）。サインエリア分を多めに取る</summary>
    private const float ChekiBottomRatio = 0.19f;

    /// <summary>チェキシートの色（白 = Polaroid 風）</summary>
    private static readonly Color ChekiSheetColor = Color.white;

    /// <summary>チェキ中心の画面幅比率（0=左端 / 1=右端）</summary>
    private const float ChekiCenterX = 0.13f;

    /// <summary>X方向のランダム揺らぎ幅（px）</summary>
    private const float ChekiXJitter = 20f;

    /// <summary>中央からのY方向ランダム変動幅（画面高さ比率）</summary>
    private const float ChekiYVariance = 0.15f;

    /// <summary>ランダム傾きの最大角度（度）</summary>
    private const float ChekiTiltMax = 12f;

    /// <summary>グラフィティの左右マージン（写真サイズ比率）。0 = カード横幅いっぱい</summary>
    private const float ChekiGraffitiMarginRatio = 0f;

    /// <summary>名前ラベルの写真左上からのX オフセット（px）</summary>
    private const float ChekiNameOffsetX = 0f;

    /// <summary>名前ラベルの写真左上からのY オフセット（px、負=下方向）</summary>
    private const float ChekiNameOffsetY = 0f;

    /// <summary>NameR の写真右上からのX オフセット（px、負=左方向）</summary>
    private const float ChekiNameROffsetX = 0f;

    /// <summary>名前ラベルのスケール倍率（1.0 = ネイティブサイズ）</summary>
    private const float ChekiNameScale = 2.0f;

    /// <summary>会社ロゴのために確保する末尾の時間（秒）</summary>
    private const float LogoReserveTime = 18f;

    /// <summary>BGM長取得失敗時のフォールバック長（StaffCreditScene の STAFF_CREDIT_TIME に合わせる）</summary>
    private const float FallbackDuration = 100f;

    // ══════════════════════════════════════════════════════════════════

    // BGM長取得用のリフレクション（GBSystem.m_sound は private）
    private static readonly FieldInfo s_soundField = AccessTools.Field(typeof(GBSystem), "m_sound");

    private static readonly FieldInfo s_bgmListField = AccessTools.Field(AccessTools.TypeByName("GB.SoundManager"), "m_bgm");
    private static readonly FieldInfo s_currentBGMField = AccessTools.Field(AccessTools.TypeByName("GB.SoundManager"), "m_currentBGMSource");

    private const int MaxChekiSlots = 12;
    private const int ChekiTexWidth = 320;
    private const int ChekiTexHeight = 320;

    // ── Unity Start（コルーチン）───────────────────────────────────────
    private IEnumerator Start()
    {
        yield return null; // BGM開始を1フレーム待つ

        var entries = CollectChekiEntries();
        if (entries.Count == 0)
        {
            PatchLogger.LogInfo("[EndingChekiSlideshow] 有効なチェキなし、スキップ");
            yield break;
        }

        float clipLen = GetBGMClipLength();
        if (clipLen < 1f)
        {
            PatchLogger.LogInfo($"[EndingChekiSlideshow] BGM長取得失敗、フォールバック {FallbackDuration}s を使用");
            clipLen = FallbackDuration;
        }

        // 末尾ロゴ時間を除いた実使用時間でタイミングを計算
        float usable = Mathf.Max(clipLen - LogoReserveTime, 0f);
        float interval = usable / entries.Count;
        float fadeDuration = Mathf.Min(interval * 0.25f, 2f);
        float displayDuration = Mathf.Max(interval - fadeDuration * 2f, 0.5f);

        PatchLogger.LogInfo($"[EndingChekiSlideshow] チェキ {entries.Count} 枚 / BGM {clipLen:F1}s / 使用 {usable:F1}s / インターバル {interval:F1}s");

        var canvasGO = CreateChekiRoot();
        try
        {
            var rng = new System.Random();
            foreach (var entry in entries)
            {
                if (canvasGO == null || !this) break;
                yield return ShowCheki(canvasGO.transform, entry, fadeDuration, displayDuration, rng);
            }
        }
        finally
        {
            foreach (var e in entries) e.Dispose();
        }
    }

    // ── チェキエントリー収集（パブリックAPIで直接取得）────────────────
    private static List<ChekiEntry> CollectChekiEntries()
    {
        var list = new List<ChekiEntry>();
        try
        {
            var gameData = GBSystem.Instance?.RefGameData();
            var graffitiRepo = GBSystem.Instance?.RefChekiGraffitiParams();
            if (gameData == null || graffitiRepo == null) return list;

            for (int slot = 0; slot < MaxChekiSlots; slot++)
            {
                var chekiData = gameData.GetChekiData(slot);
                // IsValid 相当: グラフィティインデックスが -1 以外なら有効
                if (chekiData == null || chekiData.GetGraffitiIndex() < 0) continue;

                byte[] rawData = chekiData.GetRawData();
                if (rawData == null || rawData.Length != ChekiTexWidth * ChekiTexHeight * 4) continue;

                // 写真テクスチャ → Sprite。ExSave に hi-res があればそちらを優先し、
                // 失敗・未設定時は vanilla 320 の raw データから構築する。
                Texture2D tex = null;
                if (Configs.ChekiHighResEnabled.Value)
                {
                    string key = ChekiSaveHiResPatch.KeyFor(slot);
                    if (ExSaveStore.CurrentSession.TryGet(key, out byte[] payload)
                        && payload != null && payload.Length >= 4)
                    {
                        tex = ChekiItemLoadHiResPatch.DecodePayload(
                            payload, slot, "[EndingChekiSlideshow]");
                    }
                }
                if (tex == null)
                {
                    tex = new Texture2D(ChekiTexWidth, ChekiTexHeight, TextureFormat.RGBA32, false);
                    tex.LoadRawTextureData(rawData);
                    tex.Apply();
                }
                var photoSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));

                // キャスト情報
                CharID mainCast = chekiData.GetMainCast();
                CharID subCast = chekiData.GetSubCast();
                bool isDrunk = chekiData.IsDrunk();
                int graffitiIdx = chekiData.GetGraffitiIndex();

                var mainGraffiti = graffitiRepo.Get(mainCast, isDrunk);

                // グラフィティ（サイン）スプライト
                // サブキャストがいる場合はペア専用グラフィティを使用する
                Sprite graffitiSprite = null;
                if (subCast != CharID.NUM)
                {
                    var pairGraffiti = graffitiRepo.GetPair();
                    if (pairGraffiti != null && graffitiIdx < pairGraffiti.TextCount)
                        graffitiSprite = pairGraffiti.Text(graffitiIdx);
                }
                else if (mainGraffiti != null && graffitiIdx < mainGraffiti.TextCount)
                {
                    graffitiSprite = mainGraffiti.Text(graffitiIdx);
                }

                // 名前ラベルスプライト
                Sprite nameLSprite = mainGraffiti?.NameL;
                Sprite nameRSprite = null;
                if (subCast != CharID.NUM)
                {
                    var subGraffiti = graffitiRepo.Get(subCast, isDrunk);
                    nameRSprite = subGraffiti?.NameR;
                }

                list.Add(new ChekiEntry(tex, photoSprite, graffitiSprite, nameLSprite, nameRSprite, isPair: subCast != CharID.NUM));
            }
        }
        catch (Exception e)
        {
            PatchLogger.LogError($"[EndingChekiSlideshow] チェキ収集エラー: {e.Message}");
        }
        return list;
    }

    // ── BGMクリップ長取得 ─────────────────────────────────────────────
    private static float GetBGMClipLength()
    {
        try
        {
            if (s_soundField == null || s_bgmListField == null || s_currentBGMField == null) return 0f;
            var sm = s_soundField.GetValue(GBSystem.Instance);
            if (sm == null) return 0f;

            var bgmList = s_bgmListField.GetValue(sm) as System.Collections.IList;
            int idx = (int)s_currentBGMField.GetValue(sm);
            if (bgmList == null || idx < 0 || idx >= bgmList.Count) return 0f;

            var audioSourceEx = bgmList[idx];
            var sourceProp = audioSourceEx.GetType()
                .GetProperty("Source", BindingFlags.Public | BindingFlags.Instance);
            var audioSource = sourceProp?.GetValue(audioSourceEx) as AudioSource;
            return audioSource?.clip?.length ?? 0f;
        }
        catch (Exception e)
        {
            PatchLogger.LogError($"[EndingChekiSlideshow] BGM長取得エラー: {e.Message}");
            return 0f;
        }
    }

    // ── Screen Space Overlay キャンバス作成 ──────────────────────────
    private GameObject CreateChekiRoot()
    {
        var go = new GameObject("BG2ChekiSlideshowCanvas");
        go.transform.SetParent(transform, false); // StaffCreditScene と共に破棄
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1;
        go.AddComponent<CanvasScaler>();
        return go;
    }

    // ── チェキ1枚のフェードイン→表示→フェードアウト ──────────────────
    private static IEnumerator ShowCheki(
        Transform canvasTransform,
        ChekiEntry entry,
        float fadeDuration,
        float displayDuration,
        System.Random rng)
    {
        // ─ 写真サイズとチェキシートのボーダーを計算 ─
        float photo = Mathf.Min(Screen.height * ChekiPhotoSizeRatio, ChekiPhotoSizeMax);
        float border = photo * ChekiBorderRatio;
        float bottom = photo * ChekiBottomRatio;
        float cardW = photo + border * 2f;
        float cardH = photo + border + bottom; // 上/左/右は同幅、下は広め（サインエリア）

        // ─ カード本体（CanvasGroup で一括フェード・回転） ─
        var card = new GameObject("ChekiCard", typeof(RectTransform));
        card.transform.SetParent(canvasTransform, false);

        var cg = card.AddComponent<CanvasGroup>();
        cg.alpha = 0f;
        cg.blocksRaycasts = false;
        cg.interactable = false;

        var rt = (RectTransform)card.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(cardW, cardH);

        float xPos = Screen.width * ChekiCenterX
                   + (float)(rng.NextDouble() * 2.0 * ChekiXJitter - ChekiXJitter);
        float yPos = (float)(rng.NextDouble() * 2.0 * ChekiYVariance - ChekiYVariance) * Screen.height;
        rt.anchoredPosition = new Vector2(xPos, yPos);

        float angle = (float)(rng.NextDouble() * 2.0 * ChekiTiltMax - ChekiTiltMax);
        card.transform.localRotation = Quaternion.Euler(0f, 0f, angle);

        // ─ チェキシート（白い枠：Polaroid 風の白い台紙）─
        var sheet = MakeRectChild(card.transform, "Sheet");
        sheet.anchorMin = Vector2.zero;
        sheet.anchorMax = Vector2.one;
        sheet.offsetMin = sheet.offsetMax = Vector2.zero;
        sheet.gameObject.AddComponent<Image>().color = ChekiSheetColor;

        // ─ 写真エリア（シート内側、下ボーダーを広く確保）─
        // anchorMin/Max = (0,0)→(1,1) で stretch し、offsetMin/Max で余白を指定
        var photoRt = MakeRectChild(card.transform, "Photo");
        photoRt.anchorMin = Vector2.zero;
        photoRt.anchorMax = Vector2.one;
        photoRt.offsetMin = new Vector2(border, bottom);   // 左: border, 下: bottom
        photoRt.offsetMax = new Vector2(-border, -border);  // 右: -border, 上: -border
        if (entry.Photo != null)
            photoRt.gameObject.AddComponent<Image>().sprite = entry.Photo;

        // ─ グラフィティ（写真下部 + 下ボーダーをカバー）─
        // 実チェキでは写真の下部（約下45%）と白い下ボーダー（サインエリア）を
        // 横断してグラフィティ/サインが描かれる。
        // anchorMin=(0,0)/anchorMax=(1,0) でカード下端に張り付け、
        // offsetMax.y で写真下部の高さ + 下ボーダーまで伸ばす。
        if (entry.Graffiti != null)
        {
            var graffitiRt = MakeRectChild(card.transform, "Graffiti");

            // カード横幅いっぱいに引き伸ばし、高さはアスペクト比から算出
            float graffitiMargin = photo * ChekiGraffitiMarginRatio;
            float availW = cardW - border * 2f - graffitiMargin * 2f;
            var nativeSize = entry.Graffiti.rect.size;
            float availH = nativeSize.x > 0f
                ? nativeSize.y / nativeSize.x * availW
                : availW; // フォールバック（正方形扱い）

            // カード下端（border内側）を基準に上方向へ積む
            graffitiRt.anchorMin = graffitiRt.anchorMax = new Vector2(0.5f, 0f);
            graffitiRt.pivot = new Vector2(0.5f, 0f);
            graffitiRt.sizeDelta = new Vector2(availW, availH);
            graffitiRt.anchoredPosition = new Vector2(0f, border);

            graffitiRt.gameObject.AddComponent<Image>().sprite = entry.Graffiti;
        }

        // ─ 名前ラベル（写真エリアの左上/右上にスケールつきで配置）─
        if (entry.NameL != null)
            PlaceNativeLabel(photoRt.transform, "NameL", entry.NameL,
                anchorAndPivot: new Vector2(0f, 1f),
                anchoredPos: new Vector2(ChekiNameOffsetX, ChekiNameOffsetY),
                scale: ChekiNameScale);
        if (entry.NameR != null)
            PlaceNativeLabel(photoRt.transform, "NameR", entry.NameR,
                anchorAndPivot: new Vector2(1f, 1f),
                anchoredPos: new Vector2(ChekiNameROffsetX, ChekiNameOffsetY),
                scale: ChekiNameScale);

        // ─ フェードイン ─
        yield return Fade(cg, 0f, 1f, fadeDuration);

        // ─ 表示 ─
        yield return new WaitForSeconds(displayDuration);

        // ─ フェードアウト ─
        yield return Fade(cg, 1f, 0f, fadeDuration);

        Destroy(card);
    }

    /// <summary>RectTransform を持つ子 GameObject を作成して返す</summary>
    private static RectTransform MakeRectChild(Transform parent, string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    /// <summary>スプライトをネイティブサイズ×scale で配置する（名前ラベル用）</summary>
    private static void PlaceNativeLabel(Transform parent, string name, Sprite sprite,
        Vector2 anchorAndPivot, Vector2 anchoredPos, float scale = 1f)
    {
        var rt = MakeRectChild(parent, name);
        rt.anchorMin = rt.anchorMax = anchorAndPivot;
        rt.pivot = anchorAndPivot;
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = sprite;
        img.SetNativeSize();                    // スプライト本来のピクセルサイズで表示
        rt.sizeDelta *= scale;                  // ChekiNameScale 倍に拡大
        rt.anchoredPosition = anchoredPos;
    }

    // ── フェードコルーチン ────────────────────────────────────────────
    private static IEnumerator Fade(CanvasGroup cg, float from, float to, float duration)
    {
        float elapsed = 0f;
        cg.alpha = from;
        while (elapsed < duration)
        {
            if (cg == null) yield break;
            elapsed += Time.deltaTime;
            cg.alpha = Mathf.Lerp(from, to, elapsed / duration);
            yield return null;
        }
        if (cg != null) cg.alpha = to;
    }

    // ── チェキ1枚分のデータホルダー ──────────────────────────────────
    private sealed class ChekiEntry : IDisposable
    {
        public readonly Sprite Photo;
        public readonly Sprite Graffiti;
        public readonly Sprite NameL;
        public readonly Sprite NameR;

        /// <summary>サブキャストありのペアチェキかどうか</summary>
        public readonly bool IsPair;

        private readonly Texture2D _texture; // Dispose 時に破棄

        public ChekiEntry(Texture2D texture, Sprite photo, Sprite graffiti, Sprite nameL, Sprite nameR, bool isPair)
        {
            _texture = texture;
            Photo = photo;
            Graffiti = graffiti;
            NameL = nameL;
            NameR = nameR;
            IsPair = isPair;
        }

        public void Dispose()
        {
            if (Photo != null) UnityEngine.Object.Destroy(Photo);
            if (_texture != null) UnityEngine.Object.Destroy(_texture);
            // Graffiti / NameL / NameR はゲームのアセットのため Destroy しない
        }
    }
}
