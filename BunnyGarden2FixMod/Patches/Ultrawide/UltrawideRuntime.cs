using GB;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BunnyGarden2FixMod.Patches.Ultrawide;

/// <summary>
/// ウルトラワイド表示の「いま有効か」と比率計算を一手に引き受ける状態スナップショット。
///
/// <para>
/// 判定はフレーム単位でキャッシュする。ゲーム側 16:9 チェックの Transpiler・毎フレームの
/// UI 補正・解像度計算がそれぞれ何度も問い合わせてくるため、シーン走査を含む判定を
/// 呼び出しごとに繰り返さないための設計（旧実装は呼び出しの度にフル判定していた）。
/// </para>
///
/// <para>有効条件（すべて満たすとき）:</para>
/// <list type="bullet">
///   <item>設定 FullscreenUltrawideEnabled が ON</item>
///   <item>フルスクリーン表示中</item>
///   <item>本編プレイ中かつバー入店中（BarScene ロード済み or GameData.IsInBar）</item>
///   <item>採用候補の解像度が 16:9 より横長（許容誤差 0.05 超）</item>
/// </list>
/// </summary>
internal static class UltrawideRuntime
{
    /// <summary>基準となる 16:9 のアスペクト比。</summary>
    internal const float BaseAspect = 16f / 9f;

    /// <summary>「16:9 より横長」とみなすアスペクト比の許容誤差。</summary>
    private const float AspectSlack = 0.05f;

    private static int s_stampedFrame = -1;
    private static bool s_active;
    private static int s_wideWidth;
    private static int s_wideHeight;

    /// <summary>ウルトラワイド表示を適用すべき状態か。</summary>
    internal static bool Active
    {
        get { Refresh(); return s_active; }
    }

    /// <summary>採用するネイティブ解像度の幅。</summary>
    internal static int WideWidth
    {
        get { Refresh(); return s_wideWidth; }
    }

    /// <summary>採用するネイティブ解像度の高さ。</summary>
    internal static int WideHeight
    {
        get { Refresh(); return s_wideHeight; }
    }

    /// <summary>現在適用すべき画面アスペクト比（無効時は 16:9）。</summary>
    internal static float CurrentAspect
    {
        get
        {
            Refresh();
            return s_active ? (float)s_wideWidth / s_wideHeight : BaseAspect;
        }
    }

    /// <summary>16:9 を 1 とした横方向の倍率（無効時は 1）。</summary>
    internal static float WidthScale => CurrentAspect / BaseAspect;

    /// <summary>
    /// 16:9 基準の UI 座標系で、画面端が何ピクセル外側へ広がるか（片側分）。
    /// バー HUD の「画面外に隠す」座標の補正に使う。
    /// </summary>
    internal static float EdgeExtension(float referenceWidth = 1920f)
        => referenceWidth * (WidthScale - 1f) * 0.5f;

    /// <summary>
    /// ゲーム本体の 16:9 チェックへ Transpiler 経由で注入されるアスペクト値。
    /// ウルトラワイド無効時は元の定数と同じ 16:9 を返すため、挙動が変わらない。
    /// </summary>
    internal static float AspectForEngineChecks() => CurrentAspect;

    private static void Refresh()
    {
        if (Time.frameCount == s_stampedFrame)
            return;
        s_stampedFrame = Time.frameCount;

        (s_wideWidth, s_wideHeight) = PickWideResolution();

        // 安い条件から順に評価し、シーン走査を伴う判定は最後に回す
        s_active = Configs.FullscreenUltrawideEnabled.Value
                && Screen.fullScreen
                && ExceedsBaseAspect(s_wideWidth, s_wideHeight)
                && IsPlayingInBar();
    }

    /// <summary>
    /// ウルトラワイド時に使う解像度の候補を選ぶ。
    /// 優先順: 設定値が横長ならそれ → メインディスプレイのネイティブが横長ならそれ
    /// → 現在の画面解像度。
    /// </summary>
    private static (int w, int h) PickWideResolution()
    {
        int cw = Configs.Width.Value;
        int ch = Configs.Height.Value;
        if (ExceedsBaseAspect(cw, ch))
            return (cw, ch);

        var main = Display.main;
        if (main != null && ExceedsBaseAspect(main.systemWidth, main.systemHeight))
            return (main.systemWidth, main.systemHeight);

        var now = Screen.currentResolution;
        return (now.width, now.height);
    }

    private static bool ExceedsBaseAspect(int w, int h)
        => w > 0 && h > 0 && (float)w / h > BaseAspect + AspectSlack;

    private static bool IsPlayingInBar()
    {
        var sys = GBSystem.Instance;
        if (sys == null || !sys.IsIngame)
            return false;

        if (IsSceneLoaded("BarScene"))
            return true;

        try
        {
            return sys.RefGameData().IsInBar();
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSceneLoaded(string sceneName)
    {
        int count = SceneManager.sceneCount;
        for (int i = 0; i < count; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (scene.isLoaded && scene.name == sceneName)
                return true;
        }
        return false;
    }
}
