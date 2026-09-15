using System.Collections.Generic;
using GB.Scene;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// 衣装差し替えの前後で、シーン上の全キャラのポーズ・表情・位置を保つためのスナップショット。
///
/// <para>
/// 衣装の差し替えはキャラのモデルを作り直すため Animator が初期状態から始まる。さらにゲーム側の
/// <c>EnvSceneBase.ShowCharacter</c> は <c>CharacterHandle.SetActive(true)</c> 経由で全キャラの
/// 表情を LAUGH に戻し、<c>VipRoomScene.ShowCharacter</c> は現在キャストのモーションを IDLE に
/// 戻す。そのため復元は <b>ShowCharacter の後</b> に行わないと上書きされる。
/// </para>
/// <para>
/// 位置は 2 つの理由で戻す必要がある。ミニゲーム（カラオケ等）は <c>ChangeParent</c> を通さずに
/// モデルを別の親（ステージ側の CharacterRoot）へ直接付け替えるため、作り直されたモデルは
/// 元の親に戻ってしまう。またルートモーションで動いた分は新モデルへ引き継がれない。
/// そのためモデルが入れ替わったキャラだけ、親を付け直してからローカル位置・回転を戻す。
/// </para>
/// </summary>
internal sealed class CharacterPoseSnapshot
{
    private struct LayerState
    {
        public int Hash;
        public float Time;
        public float Weight;
    }

    private sealed class Entry
    {
        public CharacterHandle Handle;
        public GameObject Model;
        public Transform Parent;
        public LayerState[] Layers;
        public Vector3 LocalPosition;
        public Quaternion LocalRotation;
    }

    private readonly List<Entry> m_entries = new();

    private CharacterPoseSnapshot() { }

    /// <summary>シーン上でモデルを持つ全キャラの状態を記録する。差し替え前（旧モデルが生きている間）に呼ぶ。</summary>
    public static CharacterPoseSnapshot Capture(EnvSceneBase env)
    {
        var snap = new CharacterPoseSnapshot();
        if (env?.m_characters == null) return snap;

        foreach (var handle in env.m_characters)
        {
            var model = handle?.Chara;
            if (model == null) continue;
            var anim = model.GetComponent<Animator>();
            if (anim == null) continue;

            var layers = new LayerState[anim.layerCount];
            for (int i = 0; i < layers.Length; i++)
            {
                // 遷移中は遷移先を採る。遷移元を復元すると差し替え直前に始まった表情・モーションが失われる。
                var info = anim.IsInTransition(i) ? anim.GetNextAnimatorStateInfo(i) : anim.GetCurrentAnimatorStateInfo(i);
                layers[i] = new LayerState { Hash = info.fullPathHash, Time = info.normalizedTime, Weight = anim.GetLayerWeight(i) };
            }

            snap.m_entries.Add(new Entry
            {
                Handle = handle,
                Model = model,
                Parent = model.transform.parent,
                Layers = layers,
                LocalPosition = model.transform.localPosition,
                LocalRotation = model.transform.localRotation,
            });
        }
        return snap;
    }

    /// <summary>
    /// 親・ローカル位置・回転を、モデルが入れ替わったキャラへ書き戻す。
    /// <c>ShowCharacter</c> の<b>前</b>に呼ぶ。非アクティブでも Transform は動かせるため、
    /// MagicaCloth が有効化される時点でモデルが正しい場所にあり、布の初期位置がずれない。
    /// </summary>
    public void RestoreTransforms()
    {
        foreach (var e in m_entries)
        {
            var model = e.Handle?.Chara;
            if (model == null || ReferenceEquals(model, e.Model)) continue;

            if (e.Parent != null && model.transform.parent != e.Parent)
                model.transform.SetParent(e.Parent, false);
            model.transform.localPosition = e.LocalPosition;
            model.transform.localRotation = e.LocalRotation;
        }
    }

    /// <summary>Animator の全レイヤーを書き戻す。<c>ShowCharacter</c> の<b>後</b>に呼ぶ。</summary>
    public void RestoreAnimation()
    {
        foreach (var e in m_entries)
        {
            var model = e.Handle?.Chara;
            if (model == null) continue;
            var anim = model.GetComponent<Animator>();
            if (anim == null) continue;

            int n = Mathf.Min(anim.layerCount, e.Layers.Length);
            for (int i = 0; i < n; i++)
            {
                var l = e.Layers[i];
                // 差し替え後の AnimatorController に無い state（DLC 差など）は飛ばす
                if (l.Hash != 0 && anim.HasState(i, l.Hash))
                    anim.Play(l.Hash, i, l.Time);
                anim.SetLayerWeight(i, l.Weight);
            }
            // 表示フレーム内でボーンを確定させ、初期ポーズが 1 フレーム見えるのを防ぐ
            anim.Update(0f);
        }
    }
}
