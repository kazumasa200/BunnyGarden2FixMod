using BunnyGarden2FixMod.Utils;
using GB;
using GB.Bar.MiniGame;
using GB.Game;
using GB.Scene;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// カラオケ中の衣装差し替えで、Karaoke ミニゲームが旧モデルへ張っていた参照を新モデルへ付け替える。
///
/// <para>
/// Karaoke はモデル生成時に一度だけ FootShadow（腰・両足ボーン）、KaraokeCamera（首ボーン）、
/// HidePantiesSphere（腰ボーン下に生成）を束縛し、毎フレーム参照する。モデルを作り直すと
/// これらが破棄済み Transform を指して LateUpdate が NullReferenceException で止まり、
/// カメラ追従・足元の影・パンツ隠しが壊れる。
/// </para>
/// <para>
/// 読み込みは数フレームかかるため、<see cref="Begin"/> で参照を一時的に外して素通しさせ、
/// <see cref="Complete"/> で新モデルへ束縛し直す。
/// </para>
/// </summary>
internal sealed class KaraokeRebinder
{
    private readonly Karaoke m_karaoke;
    private readonly FootShadow m_footShadow;
    private readonly GameObject m_modelBefore;

    private KaraokeRebinder(Karaoke karaoke, FootShadow footShadow, GameObject modelBefore)
    {
        m_karaoke = karaoke;
        m_footShadow = footShadow;
        m_modelBefore = modelBefore;
    }

    /// <summary>差し替え前に呼ぶ。カラオケ中で、その出演キャストを差し替える場合だけ非 null。</summary>
    public static KaraokeRebinder Begin(EnvSceneBase env, CharID id)
    {
        if (MiniGameBase.s_instance is not Karaoke karaoke) return null;
        if (karaoke.m_cast != id) return null;
        var chara = env.FindCharacter(id);
        if (chara == null) return null;

        // Karaoke.LateUpdate は m_footShadow が null なら何もしないので、読み込み中は外しておく
        var footShadow = karaoke.m_footShadow;
        karaoke.m_footShadow = null;

        // カメラは毎フレーム目標の position を読む。破棄されるボーンの代わりに親（ステージ位置）を追わせる
        if (karaoke.m_camera != null)
            karaoke.m_camera.m_targetTransform = chara.transform.parent;

        return new KaraokeRebinder(karaoke, footShadow, chara);
    }

    /// <summary>ShowCharacter とポーズ復元の後に呼ぶ。</summary>
    public void Complete(EnvSceneBase env, CharID id)
    {
        var chara = env.FindCharacter(id);
        if (chara == null)
        {
            m_karaoke.m_footShadow = m_footShadow;
            return;
        }

        if (m_footShadow != null)
            m_footShadow.Setup(chara.transform);
        m_karaoke.m_footShadow = m_footShadow;

        if (m_karaoke.m_camera != null)
            m_karaoke.m_camera.m_targetTransform = FindDeep(chara.transform, "Neck1_skinJT") ?? chara.transform;

        // 既定衣装へ戻す等で Preload が「Already loaded」と判断した場合、モデルは作り直されない。
        // その場合スフィアは生きているので作り直すと二重になる（同じモデルへ 2 個目が付く）。
        if (ReferenceEquals(chara, m_modelBefore))
        {
            PatchLogger.LogInfo($"[CostumePicker] カラオケ: モデルは作り直されなかったため参照を戻しました: {id}");
            return;
        }

        // 旧スフィアはモデルと一緒に破棄済み。新モデルの腰ボーン下へ作り直し、ステンシル設定も新マテリアルへ施す
        m_karaoke.setupHidePantiesSphere(chara);

        // 曲ごとのスカート距離拘束 (m_mc2UseMaxDistance=false) はここでは再適用しない。
        // 生成直後の MagicaCloth に距離拘束 OFF を入れると、粒子が骨へ追いつく前に自由落下して
        // スカートが落ち続ける。EnableMagicaCloth が付けた既定の拘束 (maxDistance 0.02) のままにする。

        PatchLogger.LogInfo($"[CostumePicker] カラオケの参照を新モデルへ付け替えました: {id}");
    }

    private static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            var hit = FindDeep(root.GetChild(i), name);
            if (hit != null) return hit;
        }
        return null;
    }
}
