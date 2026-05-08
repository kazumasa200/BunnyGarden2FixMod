using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// DIAGNOSTIC: not — production marker, do NOT remove.
/// <see cref="MagicaClothRebuilder.RemapColliderRefs"/> が target hierarchy に donor collider を
/// inject した際の識別マーカー。<see cref="MagicaClothRebuilder.RestoreSkirtCloth"/> から
/// GetComponentsInChildren で発見し、Restore 時に対応 component / GameObject を destroy する。
///
/// 2 種類の inject パターンを区別する:
/// - target に既存 bone GO あり / 同型 component 無 → component を AddComponent (CloneColliderTo)
///   このとき <see cref="DestroyGameObject"/>=false。Restore で同 GO 上の Magica*Collider のみ destroy、
///   GO 自体は target body bone のため残置。
/// - target に bone GO 自体が無 / 親 bone は存在 (Bunnygirl target で MCC 子 GO が欠ける) → 親 bone 配下に
///   新規 GO 作成 + component AddComponent (InjectColliderGo)。このとき <see cref="DestroyGameObject"/>=true。
///   Restore で GO ごと destroy する。
/// </summary>
internal class MagicaClothInjectedColliderMarker : MonoBehaviour
{
    [HideInInspector] public bool DestroyGameObject;
}
