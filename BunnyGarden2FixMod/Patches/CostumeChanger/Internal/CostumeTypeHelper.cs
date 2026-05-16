using GB.Game;

namespace BunnyGarden2FixMod.Patches.CostumeChanger.Internal;

public static class CostumeTypeHelper
{
    /// <summary>
    /// SwimWear / Bunnygirl / 分離型でない DLC をフルボディ衣装として一括判定する。
    /// blacklist 方式: DLC02/03/04/05/07/08 は Tops/Bottoms 分離型なので除外。
    /// 未把握の将来 DLC は安全側で full-body 扱いになる。
    ///
    /// 用途: target 側 additive mode 判定 / donor 拒否。
    /// 注意: ゲーム本体の IsDisableStocking とは別概念。stocking 抑止は別判定。
    /// </summary>
    public static bool IsFullBodyCostume(this CostumeType c)
        => c == CostumeType.SwimWear
           || c == CostumeType.Bunnygirl
           || (c.IsDLC()
               && c != CostumeType.DLC02 && c != CostumeType.DLC03
               && c != CostumeType.DLC04 && c != CostumeType.DLC05
               && c != CostumeType.DLC07 && c != CostumeType.DLC08);
}
