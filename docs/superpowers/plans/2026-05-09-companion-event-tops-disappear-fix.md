# 同伴イベント中の Tops/Bottoms Config 変更後 Bar 復帰で mesh_skin/mesh_costume が消える bug 修正

## 症状

- Bar で Tops override を有効化済み
- 同伴イベント (`EscortedEntryScene` + `Talk2DScene`) 中に F1 BepInEx Configuration Manager で Tops 系 Config (`TopsDistancePreserveRange`, `TopsSkinShrink` 等) を変更
- そのまま Bar に遷移 (`EscortedEntryScene.gotoBar` → `BarScene`)
- Bar キャラの `mesh_skin_upper`, `mesh_skin_lower`, `mesh_costume` が消える
- `mesh_costume_skirt*` 等 Bottoms 系は表示される

## 根本原因

`TopsLoader.OnDistancePreserveParamChanged` (および `BottomsLoader.OnBottomsSkinShrinkParamChanged`) は live tune 時に:

1. `SkinShrinkCoordinator.InvalidateCache()` で `s_cache` の transient pushed Mesh を全 `Destroy`
2. `InvalidateDistancePreserveCache()` (Tops のみ) で `s_resolvedCache` の corrected Mesh を全 `Destroy`
3. `env.FindCharacter(target)` で **現在の env scene のキャラのみ** に `ApplyDirectly`

同伴イベント中は `GetActiveEnvScene() = Talk2DScene`。HoleScene preserved の Bar キャラ (m_holeScene の `CharacterHandle`) は Talk2DScene の `m_characters` に含まれないため、`env.FindCharacter(target)` で見つからず再 Apply されない。

結果:
- HoleScene キャラの `mesh_costume.sharedMesh` は `s_resolvedCache` 由来の corrected Mesh を参照 → step 2 で destroyed → Unity-null → 不可視
- HoleScene キャラの `mesh_skin_upper/lower.sharedMesh` は `s_cache` 由来の transient pushed Mesh を参照 → step 1 で destroyed → Unity-null → 不可視
- `mesh_costume_skirt*` 等 Bottoms 系は `s_resolvedCache` を経由せず直接 `SwapSmr` で donor mesh (永続 addressables asset) を参照 → 影響なし

なお `BottomsLoader.OnSceneUnloaded` で `SkinShrinkCoordinator.ClearScene()` を呼ぶ実装も併存しており、scene unload で `s_entries` が空になる。これにより `RefreshAllByConfig` も HoleScene キャラに対して no-op になり、skin の自動 rewind も走らない (両 loader を通じた多重防御欠如)。

## 対象ファイル

1. `BunnyGarden2FixMod/Patches/CostumeChanger/TopsLoader.cs` — `OnDistancePreserveParamChanged` (166-207)
2. `BunnyGarden2FixMod/Patches/CostumeChanger/BottomsLoader.cs` — `OnBottomsSkinShrinkParamChanged` (316-...)

## 変更方針

両 live tune handler のキャラ解決ロジックを「現 env のみ」→「現 env + HoleScene」に拡張する。`env.FindCharacter` と `holeScene.FindCharacter` の両方を試し、見つかった GameObject すべてに `ApplyDirectly` を呼ぶ。GameObject の `InstanceID` で dedup (Bar 中等で同一 GameObject を 2 回 Apply しないため)。

### TopsLoader.OnDistancePreserveParamChanged

```csharp
private static void OnDistancePreserveParamChanged(object sender, System.EventArgs e)
{
    InvalidateDistancePreserveCache();
    SkinShrinkCoordinator.InvalidateCache();

    var sys = GBSystem.Instance;
    if (sys == null) return;
    var env = sys.GetActiveEnvScene();
    var holeScene = sys.GetHoleScene();
    if (env == null && holeScene == null) return;

    var snapshot = TopsOverrideStore.EnumerateOverrides().ToList();
    if (snapshot.Count == 0)
    {
        SkinShrinkCoordinator.RefreshAllByConfig();
        return;
    }

    int reapplied = 0;
    var seen = new HashSet<int>();

    foreach (var kv in snapshot)
    {
        var target = kv.Key;
        var entry = kv.Value;

        // 同 charID で env 側 / HoleScene 側で別 GameObject になりうる
        // (companion event 中は env=Talk2DScene の char と HoleScene preserved char が並存)。
        // 両方に適用し InstanceID で dedup する。
        TryReapply(env?.FindCharacter(target), entry);
        if (!ReferenceEquals(env, holeScene))
            TryReapply(holeScene?.FindCharacter(target), entry);
    }

    void TryReapply(GameObject charObj, TopsOverrideStore.Entry entry)
    {
        if (charObj == null) return;
        if (!seen.Add(charObj.GetInstanceID())) return;
        try
        {
            ApplyDirectly(charObj, entry.DonorChar, entry.DonorCostume);
            reapplied++;
        }
        catch (System.Exception ex)
        {
            PatchLogger.LogWarning(
                $"[TopsLoader] live tune 再適用失敗: char={charObj.name}, donor={entry.DonorChar}/{entry.DonorCostume}: {ex}");
        }
    }

    SkinShrinkCoordinator.RefreshAllByConfig();
    PatchLogger.LogDebug($"[TopsLoader] distance preserve param 変更 → {reapplied} 個 再適用 (...)");
}
```

### BottomsLoader.OnBottomsSkinShrinkParamChanged

同パターン。`BottomsOverrideStore.EnumerateOverrides()` を回して env + holeScene 両方で `FindCharacter` → `ApplyDirectly` (dedup)。

## 設計判断

### なぜ「HoleScene も探索」アプローチか

代替案として:
- (A) `BottomsLoader.OnSceneUnloaded` の `ClearScene()` を削除して `s_entries` を保持する案: skin meshes は `RefreshAllByConfig` で rewind されるが、`mesh_costume` は `s_resolvedCache` 経由のため `RefreshAllByConfig` の対象外。**Tops の `mesh_costume` 消失は治らない**。
- (B) `InvalidateDistancePreserveCache` / `InvalidateCache` を deferred 化する案: 影響範囲大、設計改修必要。
- (C) 本案 (env + HoleScene 探索 + ApplyDirectly): `ApplyDirectly` が `RestoreFor + Apply` で全段 (skin swap, distance preserve, skin shrink) を再構築するため、`mesh_costume` も `mesh_skin_*` も同時に修復。最小 diff。

(C) を採用。

### なぜ HoleScene のみ追加で十分か

env scene として登場するのは: BarScene (HoleScene 経由), Talk2DScene, VipRoomScene, FittingRoom 等。このうち Bar キャストを保持するのは HoleScene のみ。他の env で Tops override 対象キャラが現れる場合は env.FindCharacter で従来通り捕捉される。

つまり「現 env か HoleScene」のいずれかで必ず捕捉できる。両方探索すれば companion event 等で env と HoleScene が異なる GameObject を持つケースもカバーできる。

### dedup の必要性

env が HoleScene の proxy となる場合 (Bar 滞在中) は env.FindCharacter と holeScene.FindCharacter が同一 GameObject を返す可能性が高い。`InstanceID` ベースの `HashSet<int> seen` で 2 重 Apply を防止する。

## 影響範囲

| シナリオ | 従来挙動 | 修正後挙動 |
|---|---|---|
| Bar 滞在中の Tops live tune | env=Bar (`BarScene`)、env.FindCharacter で取得 → ApplyDirectly | env と HoleScene の両方を探索するが、両者の `FindCharacter` が同じ GameObject (実体は HoleScene 側にある m_chara) を返す可能性が高い。仮に別インスタンスでも `seen` HashSet で dedup される。挙動変化なし |
| 同伴イベント中の Tops live tune | env=Talk2DScene の char のみ ApplyDirectly。HoleScene char は放置 → Bar 復帰で消失 | env=Talk2DScene の char (slot 0 の m_cast) + HoleScene preserved char の両方に ApplyDirectly。Talk2DScene と HoleScene は別 EnvSceneBase インスタンスで `m_characterRoot` も別物のため両 GameObject は別 InstanceID と想定。`seen` で dedup されることはなく両方適用される |
| FittingRoom 中の Tops live tune | 同様に HoleScene char 放置 | 同様に HoleScene char も再 Apply (2 重防御) |
| Bottoms live tune (本 fix の Bottoms 側 handler) | mesh_skin_* のみ (skirt は影響無) | 同様に HoleScene char の mesh_skin_* も保護 |
| **Bottoms-only HoleScene char** に Tops live tune が走った場合 | 既存挙動: Tops handler は TopsOverrideStore のみ列挙するため触らない。`SkinShrinkCoordinator.InvalidateCache()` で skin transient が destroy され、`BottomsLoader.OnSceneUnloaded` で `s_entries` が clear 済だと `RefreshAllByConfig` で rewind されず破損残留 | **本 fix では未対応**。Tops handler は Tops override のみ列挙するため Bottoms-only char は引き続き未保護。実害は「Bottoms-only override 状態の HoleScene char が companion event 中の **Tops** live tune で skin 消失」する稀ケースに限られる。本 bug の主訴 (Tops 側 mesh_costume 消失) と原因系統が別なので、別計画で対応 (`SkinShrinkCoordinator.ClearScene()` 廃止 or RefreshAllByConfig 順序入替) |

## テスト方針

ユーザー手動テスト (再現 → 修正確認):

1. Bar で Tops override を有効化 (例: ERISA → KANA SwimWear)
2. ERISA を選んで同伴イベント開始
3. 同伴イベント中に F1 Configuration Manager で `TopsSkinShrink` または `TopsDistancePreserveRange` のスライダーを動かす (任意の値変更)
4. イベント終了 → Bar に戻る
5. **修正前**: Bar で ERISA の `mesh_skin_upper`, `mesh_skin_lower`, `mesh_costume` が消失。skirt のみ表示
6. **修正後**: 全 SMR 正常表示 — **このとき visible に戻ることをもって「destroyed Mesh 起因の不可視」仮説を実機確認する**。修正後も消える場合は別経路 (例: SetActive、render layer ずれ、null reference 起因) を疑い再調査

退行テスト:

- Bar 滞在中の Tops/Bottoms live tune が従来通り反映される (env と HoleScene が同一 GameObject を返すケースで dedup が効くこと確認)
- Bar 内 picker (F7) からの Tops 変更が従来通り動作
- ApplyStocking NRE ガード (`ApplyStockingNullGuardPatch`) と干渉しない
- companion event 中の Tops live tune 直後に同イベント内で Talk2DScene の m_cast が visible のままであること (env 側 Apply も従来通り効くこと確認)

## 他 live tune handler との関係

grep で `SettingChanged` を購読している箇所を列挙:

- `TopsLoader.OnDistancePreserveParamChanged` (本 fix 対象)
- `BottomsLoader.OnBottomsSkinShrinkParamChanged` (本 fix 対象)
- `CostumePickerController.OnStockingTuneChanged` / `OnStockingShapeFalloffChanged`: **picker open 中のみ動作 (`m_view.IsShown` ガード)**。companion event 中は picker 閉じている前提なので env-only pattern による消失問題は発生しない (no-op)。本 fix とは独立
- `SettingsController.cs` 等の hotkey 系: SMR には触らない。無関係

つまり同根原因の handler は Tops/Bottoms の 2 つのみ。両方を本 fix で同時に直す。

## YAGNI 確認

- HoleScene 経路追加のみ。新機能・新抽象は追加しない
- `seen` HashSet は per-call。永続化しない
- `SkinShrinkCoordinator.ClearScene` 廃止等の design 改修はスコープ外 (本 bug の修復に不要)

## リスク

- HoleScene char と env char が同じ InstanceID の場合: `seen` HashSet で dedup されるため 1 回のみ Apply。問題なし
- HoleScene char が破棄済 (Unity-null): `FindCharacter` が null を返す → `TryReapply` が即 return。問題なし
- ApplyDirectly 中の例外: 既存の try/catch でログのみ出力し継続。本修正でも同方針
- **`ApplyDirectly` 内 `RestoreFor` の SMR sharedMesh 復旧経路**: HoleScene char の `mesh_costume.sharedMesh` が live tune 直後の時点で既に destroyed Mesh (Unity-null) を保持していても、`RestoreFor` は `s_targetSnapshots[(instanceId, "mesh_costume", false)].OriginalMesh` を書き戻す。OriginalMesh は Apply 前に capture された target の元 costume mesh (addressables 所有・永続) なので、destroyed Mesh の上書きで正しく素状態に戻る。続く `Apply` で donor mesh への swap → (e) 距離保存補正 → s_resolvedCache に新 corrected mesh 登録、と進み visible 復帰
- **同伴イベント中 Apply のコスト**: 1 entry あたり env 側 + HoleScene 側で最大 2 回 ApplyDirectly。`s_resolvedCache` は live tune 直後 `InvalidateDistancePreserveCache` で空のため、両方とも cache miss → `MeshDistancePreserver.Preserve` が走る。companion event は m_cast 1 体のみが該当するので 2 倍コストの実害は軽微。**ログ末尾で `reapplied` 件数を出すことで spike が観測可能**
- **dedup 識別子**: 計画記述では「同 InstanceID なら dedup」としているが、`BarScene` と `HoleScene` の `FindCharacter` 戻り値が同 InstanceID か否かは実装に依存する。仮に別インスタンスでも `seen` は per-call なので 2 重 Apply による副作用 (RestoreFor 2 回 → snapshot Remove → Apply 1 回目で再 capture) は冪等で安全。実害なし

## メモリ更新

修正後、`feedback_live_tune_holescene_split.md` を新規追加:
- env != HoleScene の状況 (companion event, FittingRoom 等) で live tune を行うときは HoleScene preserved char も再 Apply 対象に含める必要がある
- Why: `InvalidateCache` 系で destroyed Mesh が HoleScene char の SMR.sharedMesh から残留参照される
- How to apply: 新規 live tune handler を追加するときは env + HoleScene 両方を探索する
