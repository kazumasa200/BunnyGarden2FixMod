namespace BunnyGarden2FixMod;

public enum AntiAliasingType
{
    Off,
    FXAA,
    TAA,
    MSAA2x,
    MSAA4x,
    MSAA8x,
}

/// <summary>チェキ高解像度版を ExSave に保存する際の画像フォーマット。</summary>
public enum ChekiImageFormat
{
    /// <summary>PNG 無劣化圧縮。サイズ 1/5〜1/20・エンコード 50〜200ms/枚</summary>
    PNG,

    /// <summary>JPG 劣化圧縮。サイズ 1/20〜1/50・エンコード 30〜100ms/枚</summary>
    JPG,
}
