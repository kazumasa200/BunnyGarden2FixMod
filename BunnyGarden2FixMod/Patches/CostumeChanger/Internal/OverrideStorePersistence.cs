using BunnyGarden2FixMod.ExSave;
using BunnyGarden2FixMod.Utils;
using MessagePack;
using System;
using System.Collections.Generic;

namespace BunnyGarden2FixMod.Patches.CostumeChanger.Internal;

/// <summary>
/// Override*Store 共通の ExSave 永続化 helper。
///
/// 各 Store に重複していた Read / Write / 失敗保護パターンを集約する:
///   - <see cref="TryReadFromExSave{T}"/>: 起動時に CommonData から bytes を読み出して deserialize する。
///   - <see cref="WriteToExSave{T}"/>: in-memory dict を serialize して CommonData に書き込む。
///   - 直前の rehydrate が例外で失敗していたら write をスキップして元データを保護する。
///
/// 注意:
///   - <typeparamref name="T"/> は MessagePack-CSharp の StandardResolver で解決可能な型でなければならない。
///     primitive 型 (<c>byte</c> 等) は組込 resolver で OK。カスタム POCO を渡す場合は
///     <c>[MessagePackObject]</c> 付きの **public class** で宣言する（internal だと StandardResolver が
///     解決できず write 実行時に失敗する。memory: feedback_messagepack_public_required.md）。
///   - serialize/deserialize には <see cref="ExSaveData.s_options"/> を使用する。
/// </summary>
internal static class OverrideStorePersistence
{
    /// <summary>
    /// ExSave の CommonData から指定 key を読んで deserialize する。
    /// PersistCostumeOverrides=false / entry なし / 失敗のいずれかで <c>false</c> を返す。
    /// 失敗時は <paramref name="rehydrateFailed"/> を <c>true</c> に設定し、以後の write を抑止させる。
    /// </summary>
    /// <returns>復元に成功したら true（dict が非 null）。</returns>
    public static bool TryReadFromExSave<T>(
        string exSaveKey,
        string logPrefix,
        bool persistEnabled,
        out Dictionary<int, T> dict,
        out bool rehydrateFailed)
    {
        dict = null;
        rehydrateFailed = false;

        if (!persistEnabled)
        {
            PatchLogger.LogInfo($"{logPrefix} rehydrate skip: PersistCostumeOverrides=false");
            return false;
        }
        if (!ExSaveStore.CommonData.TryGet(exSaveKey, out byte[] bytes) || bytes == null || bytes.Length == 0)
        {
            PatchLogger.LogInfo($"{logPrefix} rehydrate skip: ExSave entry なし");
            return false;
        }

        try
        {
            dict = MessagePackSerializer.Deserialize<Dictionary<int, T>>(bytes, ExSaveData.s_options);
            return true;
        }
        catch (Exception ex)
        {
            rehydrateFailed = true;
            PatchLogger.LogWarning($"{logPrefix} ExSave rehydrate 失敗、空で続行 + 次回保存もスキップして元データ保護: {ex.Message} (bytes={bytes?.Length ?? 0})");
            return false;
        }
    }

    /// <summary>
    /// in-memory dict を serialize して ExSave の CommonData に書き込む。
    /// PersistCostumeOverrides=false または <paramref name="rehydrateFailed"/>=true のときはスキップ。
    /// 例外時は warn ログを出すが in-memory 状態は維持する。
    /// </summary>
    public static void WriteToExSave<T>(
        string exSaveKey,
        string logPrefix,
        bool persistEnabled,
        bool rehydrateFailed,
        Func<Dictionary<int, T>> buildDict)
    {
        if (!persistEnabled) return;
        if (rehydrateFailed)
        {
            PatchLogger.LogWarning($"{logPrefix} ExSave 書込スキップ: 直前の rehydrate 失敗データを保護中（再起動 or 修復まで dict のみ更新）");
            return;
        }
        try
        {
            var dict = buildDict();
            byte[] bytes = MessagePackSerializer.Serialize(dict, ExSaveData.s_options);
            ExSaveStore.CommonData.Set(exSaveKey, bytes);
            PatchLogger.LogDebug($"{logPrefix} write: {dict.Count} 個 → {bytes.Length} bytes");
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"{logPrefix} ExSave 書込失敗、in-memory 維持: {ex.Message}");
        }
    }
}
