using BunnyGarden2FixMod.Utils;
using GB.Bar.MiniGame;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.Ultrawide;

/// <summary>
/// カラオケのサイリウム（専用カメラで描いたレンダーテクスチャ）を画面の比率に追従させる。
///
/// <para>
/// <c>Psyllium.Setup</c> は曲の開始時点の画面サイズでレンダーテクスチャを作るが、
/// ウルトラワイドへの切り替えはその後（バー入店・ミニゲーム開始をゲーム側の
/// アスペクトチェックが拾った次のフレーム）に起きる。結果、画面は横長なのに
/// テクスチャは 16:9 のままとなり、受け皿の RawImage へ引き伸ばされて
/// サイリウムの形と位置が崩れる。画面サイズと食い違っていたら作り直す。
/// </para>
/// <para>
/// 併せて、RawImage が固定サイズ（親にストレッチしない）ときだけ、幅をテクスチャの
/// 比率へ揃える。ストレッチしている場合は親のレイアウトに任せる。
/// </para>
/// </summary>
internal static class UltrawidePsylliumFitter
{
    internal static void Tick()
    {
        if (MiniGameBase.s_instance is not Karaoke karaoke)
            return;

        var ui = karaoke.m_ui;
        var psyllium = ui != null ? ui.m_psyllium : null;
        var texture = psyllium != null ? psyllium.m_renderTexture : null;
        if (texture == null)
            return;

        if (texture.width != Screen.width || texture.height != Screen.height)
            Recreate(ui, psyllium, texture);

        FitImageWidth(ui);
    }

    /// <summary>レンダーテクスチャを現在の画面サイズで作り直し、カメラと RawImage を繋ぎ直す。</summary>
    private static void Recreate(KaraokeUI ui, Psyllium psyllium, RenderTexture old)
    {
        // 生成条件はゲーム側 Psyllium.Setup と同じにする（深度 24・ARGB32）
        var created = new RenderTexture(Screen.width, Screen.height, 24, RenderTextureFormat.ARGB32);

        psyllium.m_renderTexture = created;
        if (psyllium.m_renderCamera != null)
            psyllium.m_renderCamera.targetTexture = created;
        if (ui.m_image != null)
            ui.m_image.texture = created;

        // 差し替え後の破棄。Psyllium.Release は m_renderTexture を破棄するので、作り直した分も後始末される。
        Object.Destroy(old);
        PatchLogger.LogInfo($"[Ultrawide] サイリウムの描画テクスチャを画面サイズへ作り直しました: {old.width}x{old.height} → {created.width}x{created.height}");
    }

    private static void FitImageWidth(KaraokeUI ui)
    {
        var image = ui.m_image;
        if (image == null || image.texture == null)
            return;

        var rect = image.rectTransform;
        // 横方向にストレッチするアンカーなら幅は親が決めるので触らない
        if (!Mathf.Approximately(rect.anchorMin.x, rect.anchorMax.x))
            return;

        var size = rect.rect;
        if (size.height <= 0f || image.texture.height <= 0)
            return;

        float textureAspect = (float)image.texture.width / image.texture.height;
        if (Mathf.Abs(textureAspect - size.width / size.height) < 0.01f)
            return;

        rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, size.height * textureAspect);
    }
}
