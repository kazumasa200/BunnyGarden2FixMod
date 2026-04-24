using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace BunnyGarden2FixMod.Utils;

/// <summary>
/// アセンブリに埋め込まれた PNG/JPG を <see cref="Texture2D"/> として取り出すユーティリティ。
/// csproj 側で <c>&lt;EmbeddedResource Include="Resources\xxx.png" /&gt;</c> と指定した前提。
/// リソース名は `{RootNamespace}.Resources.xxx.png` 形式 (例: BunnyGarden2FixMod.Resources.settings.png)。
///
/// シーン遷移でアンロードされないよう <see cref="HideFlags.DontUnloadUnusedAsset"/> を付与する。
/// View 再生成の度に Texture2D がリークしないよう、同一リソース名は static にキャッシュして共有する。
/// </summary>
public static class EmbeddedTexture
{
    private static readonly Dictionary<string, Texture2D> s_cache = new();

    public static Texture2D Load(string resourceName)
    {
        if (s_cache.TryGetValue(resourceName, out var cached) && cached != null)
            return cached;

        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                PatchLogger.LogWarning($"[EmbeddedTexture] リソース未発見: {resourceName}");
                return null;
            }
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
            if (!tex.LoadImage(bytes))
            {
                PatchLogger.LogWarning($"[EmbeddedTexture] LoadImage 失敗: {resourceName}");
                UnityEngine.Object.Destroy(tex);
                return null;
            }
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
            s_cache[resourceName] = tex;
            return tex;
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[EmbeddedTexture] 例外: {resourceName}: {ex.Message}");
            return null;
        }
    }
}
