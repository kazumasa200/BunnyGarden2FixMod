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
/// 呼び出しごとに繰り返さないための設計。
/// </para>
///
/// <para>有効条件（すべて満たすとき）:</para>
/// <list type="bullet">
///   <item>設定 FullscreenUltrawideEnabled が ON</item>
///   <item>本編プレイ中かつバー入店中（BarScene ロード済み or GameData.IsInBar）</item>
///   <item>表示モードに応じた候補解像度が 16:9 より横長（許容誤差 0.05 超）。
///         フルスクリーンでは Width/Height が横長ならそれ、そうでなければメインディスプレイのネイティブ。
///         ウィンドウでは拡張解像度 (ExtraWidth×ExtraHeight) を選択中で、それが横長のときだけ。</item>
/// </list>
/// <para>
/// フルスクリーンとウィンドウで挙動を揃えるため、どちらも「本編中だけ横長・それ以外は 16:9」になる。
/// 切り替えはゲーム側の毎フレームのアスペクトチェック（Transpiler で動的化）が拾って解像度を再適用する。
/// </para>
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

    /// <summary>ウルトラワイド表示を適用すべき状態か（現在の表示モード基準）。</summary>
    internal static bool Active
    {
        get { Refresh(); return s_active; }
    }

    /// <summary>採用する横長解像度の幅（Active のときのみ意味を持つ）。</summary>
    internal static int WideWidth
    {
        get { Refresh(); return s_wideWidth; }
    }

    /// <summary>採用する横長解像度の高さ（Active のときのみ意味を持つ）。</summary>
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

    /// <summary>
    /// フルスクリーンへ切り替える／解像度を再計算するときに使う判定。
    /// 現在の表示モードに依らず「フルスクリーンにしたら横長にすべきか」を答える。
    /// </summary>
    internal static bool WantsFullscreenWide(out int width, out int height)
    {
        width = height = 0;
        return Configs.FullscreenUltrawideEnabled.Value
            && TryPickFullscreen(out width, out height)
            && IsPlayingInBar();
    }

    /// <summary>
    /// 拡張解像度ウィンドウを適用するときに使う判定。
    /// 現在の表示モードに依らず「拡張解像度ウィンドウにしたら横長にすべきか」を答える。
    /// </summary>
    internal static bool WantsWindowWide(out int width, out int height)
    {
        width = height = 0;
        return Configs.FullscreenUltrawideEnabled.Value
            && TryPickWindow(out width, out height)
            && IsPlayingInBar();
    }

    /// <summary>拡張解像度の設定値そのものが横長で、ウルトラワイド表示の対象になるか（メニュー表示用）。</summary>
    internal static bool ExtraSizeIsWide
        => Configs.FullscreenUltrawideEnabled.Value
        && ExceedsBaseAspect(Configs.ExtraWidth.Value, Configs.ExtraHeight.Value);

    private static void Refresh()
    {
        if (Time.frameCount == s_stampedFrame)
            return;
        s_stampedFrame = Time.frameCount;

        // 安い条件から順に評価し、シーン走査を伴う判定は最後に回す
        s_active = Configs.FullscreenUltrawideEnabled.Value
                && (Screen.fullScreen
                        ? TryPickFullscreen(out s_wideWidth, out s_wideHeight)
                        : TryPickWindow(out s_wideWidth, out s_wideHeight))
                && IsPlayingInBar();
    }

    /// <summary>
    /// フルスクリーン時の横長解像度候補。
    /// 優先順: 設定値 Width/Height が横長ならそれ → メインディスプレイのネイティブが横長ならそれ。
    /// </summary>
    private static bool TryPickFullscreen(out int w, out int h)
    {
        w = Configs.Width.Value;
        h = Configs.Height.Value;
        if (ExceedsBaseAspect(w, h))
            return true;

        var main = Display.main;
        if (main != null)
        {
            w = main.systemWidth;
            h = main.systemHeight;
            return ExceedsBaseAspect(w, h);
        }
        return false;
    }

    /// <summary>ウィンドウ時の横長解像度候補。拡張解像度を選択中で、その値が横長のときだけ。</summary>
    private static bool TryPickWindow(out int w, out int h)
    {
        w = Configs.ExtraWidth.Value;
        h = Configs.ExtraHeight.Value;
        return Configs.ExtraActive.Value && ExceedsBaseAspect(w, h);
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
