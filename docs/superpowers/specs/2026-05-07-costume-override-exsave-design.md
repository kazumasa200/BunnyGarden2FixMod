# 衣装 Override の ExSave 永続化 — 設計

## 概要

`CostumeOverrideStore` / `BottomsOverrideStore` / `TopsOverrideStore` / `PantiesOverrideStore` / `StockingOverrideStore` が保持するキャラ毎の MOD override 衣装状態を、`.exmod` サイドカー (ExSave Common バケット) に永続化する。ゲーム再起動・スロット切替を跨いで衣装が復元される。

## ゴール / 非ゴール

**ゴール:**
- 5 種の OverrideStore の状態をセーブ → 再起動 → ロードで保持する。
- `Configs.PersistCostumeOverrides` で機能 on/off できる（デフォ true）。
- 既存 OverrideStore の `Set/Clear/TryGet` シグネチャを変えず、callsite を改変しない。

**非ゴール:**
- スロットごとに別々の衣装プロファイル（= スロット別保存）。今回は Common バケット（スロット非依存）で全スロット共通。将来要件が出た場合は POCO key を `costume.override.slot.{N}` 形式に分割すれば後方互換的に移行可能（後方互換エスケープハッチ）。
- ロード後に既に表示中のシーン上のキャラへの即時再 apply。BG2 はスロットロード時に必ずキャラ生成系パッチ（下記再適用ポイント）が走るので任せる。
- ChekiHighRes 系のような ConfigEntry off で機能停止する gate 設計の再現以上の冗長機構（最小 1 個 ConfigEntry）。

## 方針サマリー

| 項目 | 採用 | 却下 |
| --- | --- | --- |
| 保存スコープ | Common バケット（スロット非依存） | スロット紐付け（`AllSlots[CurrentSaveSlot]`） / ハイブリッド |
| 真実のソース | dict + ExSave mirror（読みは dict） | ExSave シングルソース（毎回 byte[] パース） |
| 復元タイミング | `LoadFromPath` 直後に rehydrate、再 apply は既存 `TryGet` 経路任せ | `Saves.Load` 後に強制的にシーン上キャラへ再 apply |
| シリアライズ単位 | ストア別 all-in-one MessagePack POCO（4 entry） | キャラ別キー（manual byte layout） / 全 store 統合 1 entry |
| 機能 on/off | `Configs.PersistCostumeOverrides` 1 個 | 4 種別個 / 設定なし |
| Config 追加方法 | `Configs.yaml` ファサード（ConfigGen 自動生成） | `Plugin.cs` で直接 `Config.Bind(...)` |

## アーキテクチャ

```
Saves.Load Postfix
  → ExSaveStore.LoadFromPath（path 解決成功時）
      → AllSlots = Deserialize(...)
      → OnAllSlotsReplaced(Loaded)              // InvalidateCommonCaches を rename・拡張
          → CostumeViewHistory.ResetDedup ほか  // 既存
          → CostumeOverrideStore.RehydrateFromExSave()
          → BottomsOverrideStore.RehydrateFromExSave()
          → TopsOverrideStore.RehydrateFromExSave()
          → PantiesOverrideStore.RehydrateFromExSave()
          → StockingOverrideStore.RehydrateFromExSave()

Saves.Load Postfix（path 解決失敗）/ Saves.CreateNewData Postfix
  → ExSaveStore.Reset
      → AllSlots = new()
      → OnAllSlotsReplaced(Reset)
          → ViewHistory dedup reset                     // 既存
          → 各 OverrideStore.ClearMemory()              // 新規 (5 種)

OverrideStore.Set / Clear (callsite 無改変)
  → s_overrides 更新
  → if (Configs.PersistCostumeOverrides.Value)
        WriteToExSave()                                  // dict 全体を MessagePack 化して CommonData.Set
                                                         // ディスク I/O は Saves.Save フックに便乗
```

### Rehydrate 後の再適用パッチ点（参考）

ロード直後はキャラ生成のパスが必ず走るため、`RehydrateFromExSave` で dict が復元されればキャラ表示時に既存パッチが override を読む。本仕様では新パッチを追加せず、以下の既存パッチ点に乗る:

| 対象 | 再適用ポイント |
| --- | --- |
| Costume (same-char) | `CharacterHandle.Preload` Prefix（`CostumeChangerPatch`）が `CostumeOverrideStore.TryGet` ヒット時に `arg.Costume` を上書き |
| Costume (cross-char) | `CharacterHandle.setup` Postfix の `CrossCharCostumeLoader.ApplyIfOverridden` 経路（cross-char Override は arg.Costume 注入では復元されないため） |
| Bottoms | `BottomsSetupPatch`（既存）が `BottomsOverrideStore.TryGet` を読み donor mesh を transplant |
| Tops | `TopsSetupPatch` / `TopsPreloadFallbackPatch`（既存）が `TopsOverrideStore.TryGet` を読み donor mesh を transplant |
| Panties | 既存 setup 系パッチ（`PantiesAltSlotMatchPatch` 等）が `PantiesOverrideStore.TryGet` を読む |
| Stocking | 既存 setup 系パッチ / `SwimWearStockingPatch` 等が `StockingOverrideStore.TryGet` を読む |

`CurrentSaveSlot=-1`（タイトル直後・アルバム閲覧中）でロード前にユーザーが `Set` を呼んでも、その時点で表示中のキャラに即時反映はしない（次のシーン遷移で正しくなる）。これは現状の override 動作と同じ。

## ExSave キー & データレイアウト

すべて `ExSaveStore.CommonData` に格納。値は MessagePack (LZ4BlockArray) で直列化（`ExSaveData` 全体に追従）。

| Key | 直列化前の型 | POCO の要否 |
| --- | --- | --- |
| `costume.override.all` | `Dictionary<int, byte>` | 不要（`int = (int)CharID`、`byte = (byte)CostumeType`） |
| `bottoms.override.all` | `Dictionary<int, BottomsOverrideExSaveEntry>` | `Entry { byte DonorChar; byte DonorCostume }` |
| `tops.override.all` | `Dictionary<int, TopsOverrideExSaveEntry>` | `Entry { byte DonorChar; byte DonorCostume }` |
| `panties.override.all` | `Dictionary<int, PantiesOverrideExSaveEntry>` | `Entry { byte Type; byte Color }` |
| `stocking.override.all` | `Dictionary<int, byte>` | 不要（`byte = stocking 値 (0..7)`） |

POCO は `[MessagePackObject]` / `[Key]` 属性付きで各 store ファイル末尾に `internal` 宣言。enum を直接 serialize せず byte に明示 cast することで、将来 enum 値が増減しても fail-safe に倒す。

### 後方互換 / 破損耐性

- 旧 `.exmod`（衣装 entry 無し）→ `CommonData.TryGet` が false → dict は空のまま、従来挙動。
- byte[] が壊れている → `MessagePackSerializer.Deserialize` が `MessagePackSerializationException` 等を投げる → warning ログ + dict を空に（包括 `catch (Exception)` で捕捉）。`ExSaveData.Deserialize` 全体の「破損時は空で続行、主セーブには触らない」フェイルセーフ方針を踏襲。
- 未知 CharID（`>= CharID.NUM`）/ 無効値（`CostumeType.Num`、`Bunnygirl` bottoms、stocking 範囲外、panties 範囲外）→ rehydrate 時にも既存 `Set` のバリデーションを通すため捨てられる。

## ファイル変更（追加なし、編集 7 ファイル）

| ファイル | 変更内容 |
| --- | --- |
| `BunnyGarden2FixMod/Configs.yaml` | `CostumeChanger` セクション末尾に `PersistCostumeOverrides` (bool, default true) を追加。`Generated/Configs.g.cs` は ConfigGen により自動再生成（生成物は git 管理されているため一緒にコミット）。 |
| `BunnyGarden2FixMod/ExSave/ExSaveStore.cs` | `InvalidateCommonCaches()` を `OnAllSlotsReplaced(AllSlotsReplaceMode mode)` に rename・拡張。`enum AllSlotsReplaceMode { Loaded, Reset }`。各 OverrideStore の `RehydrateFromExSave()` / `ClearMemory()` を呼ぶ。`InvalidateCommonCaches` は private で外部 callsite なし、内部 2 箇所（`Reset` / `LoadFromPath` 末尾）の置換のみ。 |
| `BunnyGarden2FixMod/ExSave/ExSaveData.cs` | `s_options` を `private` から `internal static readonly` に可視性緩和（DRY 化のため各 OverrideStore からも参照可能に） |
| `Patches/CostumeChanger/CostumeOverrideStore.cs` | mirror 書込 + rehydrate / clear（POCO 不要） |
| `Patches/CostumeChanger/BottomsOverrideStore.cs` | mirror + rehydrate / clear + `BottomsOverrideExSaveEntry` POCO |
| `Patches/CostumeChanger/TopsOverrideStore.cs` | mirror + rehydrate / clear + `TopsOverrideExSaveEntry` POCO（Bottoms と完全同パターン） |
| `Patches/CostumeChanger/PantiesOverrideStore.cs` | mirror + rehydrate / clear + `PantiesOverrideExSaveEntry` POCO |
| `Patches/CostumeChanger/StockingOverrideStore.cs` | mirror + rehydrate / clear（POCO 不要） |

### Configs.yaml への追加内容

```yaml
    - name: PersistCostumeOverrides
      label: 衣装変更状態を保存する
      type: bool
      default: true
      description: |
        Wardrobe で変更した衣装・パンツ・ストッキング・下衣移植の状態を MOD サイドカー (.exmod) に保存し、
        ゲーム再起動後も復元します。保存先は `BepInEx/data/net.noeleve.BunnyGarden2FixMod/`（Steam Cloud 対象外）。
        false の間に変更した内容は永続化されません。既に保存済の override 値は ExSave に残るため、
        true に戻すと過去保存分が復元されます。
      ui:
        kind: toggle
```

挿入位置: `RespectGameCostumeOverride` の直後、`CostumeChangerShow`（hotkey）より前。

## API 詳細

### 各 OverrideStore に追加するメンバ

```csharp
private const string ExSaveKey = "<store>.override.all";

public static void RehydrateFromExSave()
{
    s_overrides.Clear();
    if (!Configs.PersistCostumeOverrides.Value) return;
    if (!ExSaveStore.CommonData.TryGet(ExSaveKey, out byte[] bytes) || bytes == null || bytes.Length == 0)
        return;
    try
    {
        var dict = MessagePackSerializer.Deserialize<Dictionary<int, T>>(bytes, ExSaveData.s_options);
        foreach (var kv in dict)
            SetValidatedNoMirror((CharID)kv.Key, kv.Value /* + 必要な変換 */);
    }
    catch (Exception ex)  // MessagePackSerializationException / IO / その他
    {
        PatchLogger.LogWarning($"[<Store>OverrideStore] ExSave rehydrate 失敗、空で続行: {ex.Message}");
    }
}

public static void ClearMemory() => s_overrides.Clear();

private static void WriteToExSave()
{
    if (!Configs.PersistCostumeOverrides.Value) return;
    try
    {
        var dict = BuildSerializableDict();   // s_overrides → Dictionary<int, T>
        byte[] bytes = MessagePackSerializer.Serialize(dict, ExSaveData.s_options);
        ExSaveStore.CommonData.Set(ExSaveKey, bytes);
    }
    catch (Exception ex)
    {
        PatchLogger.LogWarning($"[<Store>OverrideStore] ExSave 書込失敗、in-memory 維持: {ex.Message}");
    }
}
```

### Set / Clear の改修

既存バリデーションは保持。dict 更新後、config true なら `WriteToExSave()` を呼ぶ。

**戻り値の保持**: `Set` が `bool` を返す store（例: `BottomsOverrideStore.Set(target, donor, costume)`）は callsite が分岐に使っているため、`SetValidatedNoMirror` も同じ `bool` を返し、`Set` はその戻り値を caller に伝播し、true の場合のみ `WriteToExSave()` を呼ぶ。`void` 返しの store（Costume / Panties / Stocking）も内部 helper に分離するパターンは同じだが、簡潔化のため `void` のまま。

```csharp
// Bottoms 例
public static bool Set(CharID target, CharID donor, CostumeType costume)
{
    bool ok = SetValidatedNoMirror(target, donor, costume);
    if (ok && Configs.PersistCostumeOverrides.Value)
        WriteToExSave();
    return ok;
}

private static bool SetValidatedNoMirror(CharID target, CharID donor, CostumeType costume)
{
    // 既存 Set 内のバリデーションと dict 投入をそのまま移植
    if (target >= CharID.NUM || donor >= CharID.NUM) return false;
    if (costume == CostumeType.Num) return false;
    if (costume == CostumeType.Bunnygirl) return false;
    s_overrides[target] = new Entry(donor, costume);
    return true;
}
```

`Clear` も同様に dict から remove → config true なら `WriteToExSave`。

### ExSaveStore の改修

```csharp
public enum AllSlotsReplaceMode { Loaded, Reset }

private static void OnAllSlotsReplaced(AllSlotsReplaceMode mode)
{
    CostumeViewHistory.ResetDedup();
    PantiesViewHistory.ResetDedup();
    StockingViewHistory.ResetDedup();

    if (mode == AllSlotsReplaceMode.Loaded)
    {
        CostumeOverrideStore.RehydrateFromExSave();
        BottomsOverrideStore.RehydrateFromExSave();
        PantiesOverrideStore.RehydrateFromExSave();
        StockingOverrideStore.RehydrateFromExSave();
    }
    else  // Reset
    {
        CostumeOverrideStore.ClearMemory();
        BottomsOverrideStore.ClearMemory();
        PantiesOverrideStore.ClearMemory();
        StockingOverrideStore.ClearMemory();
    }
}
```

`InvalidateCommonCaches` の既存 callsite（`Reset` / `LoadFromPath` 末尾の 2 箇所）を `OnAllSlotsReplaced(AllSlotsReplaceMode.Reset)` / `OnAllSlotsReplaced(AllSlotsReplaceMode.Loaded)` に置換。`InvalidateCommonCaches` 自体は private で外部 callsite なし。

## ライフサイクル整合性のリスク検討

- **`Saves.Save` 中に `Set/Clear` が走った場合**: 既存 `ExSaveLifecyclePatch` は `await` 前に `CommitSession` 同期実行＋ `AllSlots.Serialize()` を await 前に同期で行ってから async 書込にしているため、`Saves.Save` の `await` 中に `s_overrides` が変わって ExSave に書き込まれても、書込中の byte[] には影響しない（既にスナップショット済）。
- **`CurrentSaveSlot=-1` 時の `Set`**: `WriteToExSave` は `CommonData.Set` するだけで slot に依存しない。次の `Saves.Save` 経路では `CommitSession` はスキップされるが、`AllSlots.Serialize()` 自体は走るため `Common` バケットの内容も含めてサイドカーに書き出される（既存 ViewHistory 系がこの経路で永続化されているのと同じ）。
- **新規データ作成中の Set**: `Saves.CreateNewData` は同期想定。Reset 後に Set が走っても dict と ExSave の双方に整合的に書かれる。
- **`Saves.Load` 失敗（path 解決不可）**: `ExSaveStore.Reset` 経路 → `OnAllSlotsReplaced(Reset)` → `ClearMemory` で dict 空に。「ロード失敗時に既に Set 済の override も in-memory から消える」副作用があるが、ViewHistory dedup reset と同じ既存挙動。
- **MessagePack 例外伝播**: serialize / deserialize 例外は warning ログにとどめ、本体保存・ロードに影響させない。
- **Stocking rehydrate と KneeSocksLoader 初期化順序**: `RehydrateFromExSave` のバリデーションは静的 const 範囲チェック (`Min..Max`) のみで KneeSocksLoader 初期化に依存しない。順序問題なし。
- **旧形式 `.exmod` 移行 (`MigrateIfNeeded`)**: 主セーブ隣 → `BepInEx/data/...` への移動は本機能と直交。先に移行が走り、その後通常の Deserialize → rehydrate に進むため衝突なし。

## config off / on 切替の仕様（明確化）

ConfigEntry の Description（上記 yaml）にも明記:

- **false 中**: `Set/Clear` は dict のみ更新。`WriteToExSave` スキップ → ExSave 上の entry は更新されない（古い snapshot のまま残る）。`RehydrateFromExSave` も no-op。
- **false → true 切替後の次の `Saves.Load`**: 過去保存分が rehydrate されて復活。**直前 false 中に変更した override は ExSave にいないため復元されない**（仕様）。
- **true → false 切替**: 既存 ExSave entry は ExSave 上に残り続ける（書換は止まる）。

ユーザー視点で混乱しやすいのは「false 中に変えた値は次回起動時に消えて、その前に保存されていた古い値に戻る」点。Description 1 行で許容範囲。

## テスト計画

### ビルド
- BIE5: `dotnet build`（warning 0 / error 0）
- BIE6: `dotnet build -p:BepInExVersion=6`（warning 0 / error 0）

### 手動シナリオ（4 種すべて確認）

1. **基本永続化**: Wardrobe で衣装変更 → セーブ → ゲーム終了 → 再起動 → ロード → キャラ表示で衣装が復元される。
2. **config off**: `PersistCostumeOverrides = false` → 衣装変更 → セーブ → 再起動 → ロード → 復元されない（vanilla 衣装）。
3. **新規データ**: 衣装変更状態でセーブ後、新規データ作成 → dict / ExSave 共に衣装 entry 消失（in-memory dict もクリア）。
4. **旧 `.exmod`（衣装 entry 無し）**: 既存 ChekiHighRes だけのセーブを開く → 空 dict、既存挙動と同一。
5. **破損 byte[]**: 手動で `.exmod` の衣装 entry を壊した状態でロード → warning ログ、dict 空、ゲーム動作継続。
6. **config off→on 復元**: off 中に override 変更 → 何も保存されない → on に戻す → 次のロードで「off に切替える前」の保存値が復元される（ユーザー混乱しない範囲か確認）。

各種 store 個別:
- Costume: 任意キャラを別 CostumeType に変更（same-char）
- Costume: cross-char Override 経由（`CrossCharCostumeLoader.ApplyIfOverridden` での再適用）
- Bottoms: cross-char donor (例: ERISA → SwimWear) を変更
- Panties: type / color の組
- Stocking: KneeSocks 系 (5–7) と通常系 (1–4) 双方

## 想定外のエッジケース

- **`CharID.NUM` 拡張**: 将来ゲームアプデでキャラが増えた場合、過去の ExSave に存在する index がそのまま有効。
- **`CostumeType` 値域逸脱**: byte cast に収まらない値（256+）は将来的にもありえない（ゲーム本体の enum 範囲）。`Set` のバリデーションで弾かれる。
- **MOD 削除後の `.exmod` 残骸**: ExSave 全体の事情と同じ。MOD 再導入時に entry が復活する仕様。

## 残作業（implementation 後）

- ConfigGen 実行で `Generated/Configs.g.cs` を再生成しコミット
- ビルド成功後、`fixmod-add-patch` skill 流儀でデプロイ → ゲーム起動して手動シナリオ実行
- `code-review` skill で実装レビュー

## code-review 却下メモ (2026-05-07 第二ラウンド)

- 🟢 LOW: 5 store の MessagePack mirror パターンを `ExSaveStore` の helper に集約 — 却下理由: 今回 PR スコープ外、refactor は別 PR で。`s_options` の internal 露出も含めて整理すべきだが今は最小差分を優先。
- 🟢 LOW: `PreloadAndReapplyAsync` の skin donor preload を `UniTask.WhenAll` で並列化 — 却下理由: rehydrate 末尾の Forget 先行起動で実運用ではほぼ cache hit。性能影響無視できる。
- 🟢 LOW: `TopsOverrideStore.EnumerateOverrides` が dict を直返ししている — 却下理由: コメント「列挙中の Add/Remove は呼出し側の責務」で既知。現状の callsite (rehydrate sync 実行直後の Forget 起動) では race しない。

## plan-review 却下メモ

- `OnAllSlotsReplaced` rename の callsite 影響確認の脚注追加（plan-reviewer 提案 #8）— 却下理由: spec 本文の「ファイル変更」表で「private で外部 callsite なし」を明記済みのため脚注重複。
- 「`s_options` private→internal の影響範囲確認」の事前明記（plan-reviewer 改善提案 nit）— 却下理由: `ExSaveData.s_options` は ExSave 内部実装で外部公開意図なし、internal 化は同 assembly 内のみ。実装時に確認で十分、spec に記述するほどではない。
- config 厳密化案 (false 中も WriteToExSave で entry を最新化) — 却下理由: plan-reviewer 自身が「仕様簡潔さを失う」と推奨度低。Description 1 行で誤解は防げる。
