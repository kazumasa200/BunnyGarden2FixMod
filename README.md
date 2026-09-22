# BunnyGarden2FixMod

<div align="right">

[![日本語](https://img.shields.io/badge/日本語-ff69b4?style=flat-square)](README.md)&nbsp;[![English](https://img.shields.io/badge/English-lightgrey?style=flat-square)](README.en.md)

</div>

[バニーガーデン2](https://store.steampowered.com/app/3443820/2/)(海外名:Bunny Garden2)用の解像度修正やフレームレート上限変更などを行うBepInEx5用Modです。
<img width="1920" height="1080" alt="image" src="https://github.com/user-attachments/assets/b4e45f40-5420-4811-8500-4a0c3b4d1e69" />
<img width="1920" height="1000" alt="スクリーンショット 2026-04-16 191718-e" src="https://github.com/user-attachments/assets/f6c86e6b-2ad5-4b5f-bfa8-6ff66fcaf43b" />

## 対応バージョン(MODバージョンv1.0.12.1現在)
- ゲームバージョン1.0.5および1.0.6に対応  

## 機能
- 内部解像度を指定することで画質を向上することができる。
- 本来は60で固定されていたフレームレート制限を任意の値にするか、取り払うことができる。
- アンチエイリアスを設定し、さらに画面のガビガビ感(ジャギー)を減らすことができる。
- フリーカメラ機能。キーボード／コントローラー操作、時間停止、表示オーバーレイの切り替えに対応。フリーカメラの映像をメイン画面・PiP・サブモニターに出力先選択できる。
- スクリーンショット保存機能（フリーカメラ対応）。ゲーム UI や MOD のオーバーレイ抜きの PNG が撮れる。
- ドリンク、フード、会話選択肢の正解を表示させることが出来る。(デフォルトでは無効)
- ストッキングを強制的に非表示にすることができる。(デフォルトでは無効)
- バーに入る前にキャストの出勤順序を変更できる。(デフォルトでは無効)
- チェキ（撮影写真）を高解像度で保存できる。(デフォルトでは無効)
- F7キーで衣装・パンツ・ストッキングを自由に切り替えることができる。別キャストの上衣・下衣を移植する衣装チェンジャー機能つき。(デフォルトでは有効)
- F9キーで旅行・特別なシーンの所持金UI、ボタンガイド、ラブカウンターを非表示にできる。(所持金非表示はデフォルトで有効)
- 色収差エフェクト（画面端のにじみ）を無効化できる。(デフォルトでは無効)
- 胸の形状を平坦化するオプション。キャラクターごとに調整可能。(デフォルトでは無効)

MOD 独自の UI（F9 設定パネル・衣装チェンジャー等）は、ゲームの言語設定（日本語／英語／簡体字／繁体字）に自動で追従します。言語の追加は下記「[翻訳（多言語対応）](#翻訳多言語対応)」を参照してください。

## 導入方法(Steam Deckも対応)
1. [Releases](https://github.com/kazumasa200/BunnyGarden2FixMod/releases/latest)から最新のzipファイルをダウンロードする。(BunnyGarden2FixMod_v1.0.6.1_BepInEx5.zipみたいな感じ)ブラウザによってはブロックするかもしれないので注意。<br>導入時の最新バージョンを入れてください。
<img width="983" height="709" alt="image" src="https://github.com/user-attachments/assets/1ce21405-2b6b-47b4-a32f-d9fce95f76c5" />

上の画像はv1.0.6.1の場合の例です。導入時の最新バージョンを選択してください。  
> [!NOTE]
> BepInEx5とBepInEx6のどっちを入れるか迷った場合や、Modの導入が初めての方はBepInEx5とついた方をダウンロードしてください。  
> 以下の手順はBepInEx5版を前提につくっています。  

2. [BepInEx5](https://github.com/bepinex/bepinex/releases)をダウンロードする。Windowsの場合もSteam Deckの場合も```BepInEx_win_x64_{バージョン名}.zip```をダウンロードする。

3. ゲームのexeがあるディレクトリにBepInEx5のZipの中身を全て展開。フォルダ以外のファイルもお忘れなく。つまり、ゲームのexeとBepInExフォルダやdoorstop_configとかが同じ階層にある状態が正しいということ。
<img width="1535" height="1069" alt="image" src="https://github.com/user-attachments/assets/3a1985df-6f79-4c7d-9a66-31ca5ffa312a" />  

4. (Steam Deckの場合のみ実行) Steamでバニーガーデン2 → 右クリック → 「プロパティ」→「一般」→「起動オプション」に```WINEDLLOVERRIDES="winhttp=n,b" %command%```を入力。

5. 一度Steamのプレイボタンからゲームを起動した後、[Releases](https://github.com/kazumasa200/BunnyGarden2FixMod/releases/latest)からダウンロードしたZipを展開し、中にある```net.noeleve.BunnyGarden2FixMod.dll```をBepinExフォルダの中のPluginsの中に入れる。
<img width="1490" height="383" alt="image" src="https://github.com/user-attachments/assets/f24310e1-c5f1-4a08-9195-b25d0fe37377" />

> [!IMPORTANT]
> `BepInEx/` の直下ではなく、**`BepInEx/plugins/` フォルダの中**に入れてください。

6. もう一度起動するとBepinExフォルダの中のconfigフォルダに```net.noeleve.BunnyGarden2FixMod.cfg```設定ファイルが出来上がるので、それをメモ帳などで変更して解像度の設定やフレームレートなどの設定をする。
<img width="1677" height="1906" alt="image" src="https://github.com/user-attachments/assets/d8cdc40e-7299-46f4-bbf0-ba5d685c38c9" />
上の画像は例です。お好みにどうぞ。


## Config 設定一覧

ゲームを一度起動すると `BepInEx/config/net.noeleve.BunnyGarden2FixMod.cfg` が生成されます。  

**全項目の一覧と詳細は [こちら](docs/configs.md) を参照してください**（[`Configs.yaml`](BunnyGarden2FixMod/Configs.yaml) から自動生成）。  

ゲーム起動中は **F9** キーで設定パネルを開いて多くの項目を直接編集することもできます（`F4` キーでリロード）。

## Tips

- **F4キー** でゲーム起動中に設定ファイルをリロードできます。設定ファイルを変更した後、ゲームを再起動する必要はありません（F4キーを押すのみ）。

## 翻訳（多言語対応）

MOD の UI は日本語（内蔵）と英語を同梱しており、ゲームの言語設定に応じて自動で切り替わります。

**ビルド不要**で翻訳の追加・上書きができます。次の場所に外部言語ファイルを置いてください:

```
BepInEx/plugins/lang/<code>.json
```

- `<code>` は `en` / `zhCN` / `zhtw` のいずれか（`ja` は内蔵の既定）。
- `lang` フォルダは MOD の DLL（`net.noeleve.BunnyGarden2FixMod.dll`）と同じ階層に置きます。DLL をサブフォルダに入れている場合は、そのフォルダの中に `lang` を作ってください。
- ファイルは「元の日本語文字列 → 訳文」のフラットな JSON 辞書です。キーが無い場合は日本語にフォールバックします。
- 外部ファイルは同梱訳より優先されるため、既存訳の微修正にも使えます。

`zhCN.json` / `zhtw.json` など他言語の Pull Request も歓迎です。

## 開発者向け: Config / ホットキーの追加方法
<details>
<summary>開発者向け詳細</summary>
  
新しい設定項目は [`BunnyGarden2FixMod/Configs.yaml`](BunnyGarden2FixMod/Configs.yaml) に 1 ブロック書くだけで追加できます。`tools/ConfigGen` がビルド時に YAML を読んで [`Generated/Configs.g.cs`](BunnyGarden2FixMod/Generated/Configs.g.cs) を再生成し、`Plugin.Awake` から `Configs.BindAll(Config)` で BepInEx に Bind されます（F9 パネルへの行追加もメタデータ経由で自動）。

### Config エントリの追加

`Configs.yaml` の該当 `section:` 配下に追記します。

**bool / int / float**:

```yaml
- name: NewToggle              # 静的フィールド名 → Configs.NewToggle
  label: 新トグルラベル         # F9 パネル表示名 + .cfg description 1 行目
  type: bool                   # bool / int / float / enum / hotkey
  default: false
  description: 何をするトグルかの補足説明。
  ui:                          # F9 パネルに行を出すなら指定（任意）
    kind: toggle               # または slider
```

**スライダー（数値 + range）**:

```yaml
- name: NewSlider
  label: 新スライダー
  type: float
  default: 0.5
  range: [0.0, 1.0]
  description: 説明。
  ui:
    kind: slider
    step: 0.1
    format: '{0:F2}'           # C# 書式指定
```

**enum**:

```yaml
- name: NewMode
  label: モード選択
  type: enum
  enumType: BunnyGarden2FixMod.MyMode  # 完全修飾の enum 型名
  default: ModeA
  description: 説明。
```

### ホットキーの追加

`type: hotkey` を使うと `.cfg` に **Keyboard + Gamepad の 2 entry が自動で展開** され、`HotkeyConfig` でラップされたフィールドになります。

```yaml
- name: MyToggle
  label: 何かのトグル
  key: ToggleSomething              # .cfg では XxxKey / XxxButton の suffix で展開
  type: hotkey
  defaultKey: F8                    # UnityEngine.InputSystem.Key の名前
  defaultButton: Y                  # ControllerButton の名前（省略で keyboard のみ）
  description: 共通説明（KB/Pad 両方に出る）。
  controllerDescription: ControllerModifier と同時押しが必要です。  # Pad 専用注記（任意）
```

### パッチコードからの参照

```csharp
using BunnyGarden2FixMod;

if (!Configs.NewToggle.Value) return;            // bool / int / float / enum
var v = Configs.NewSlider.Value;
if (Configs.MyToggle.IsTriggered()) { ... }      // hotkey: KB or Pad の押下
if (Configs.MyToggle.IsHeld()) { ... }
```

### ビルド・反映

```bash
dotnet build BunnyGarden2FixMod/BunnyGarden2FixMod.csproj         # BepInEx 5
dotnet build BunnyGarden2FixMod/BunnyGarden2FixMod.csproj -p:BepInExVersion=6  # BepInEx 6
```

MSBuild Target が YAML / ConfigGen 自身の変更を検出して `Generated/Configs.g.cs` を自動再生成します。`net.noeleve.BunnyGarden2FixMod.dll` を `BepInEx/plugins/` にコピーするとゲーム起動時に `.cfg` に新エントリが書き出され、F9 パネルにも自動で行が追加されます。
</details>

## 開発者向け: テスト
<details>
<summary>開発者向け詳細</summary>

`BunnyGarden2FixMod.Tests/`（xUnit）で **純関数** を単体テストします。対象は UnityEngine の `Vector3` / `Mathf` 等と `System` のみに依存するロジックで、実機の `UnityEngine.CoreModule.dll` を参照して検証します。BepInEx / Harmony / Plugin に依存するコードはテスト対象外です（実機で目視確認）。

### 実行

リポジトリルートから実行します。

```bash
dotnet test BunnyGarden2FixMod.Tests/BunnyGarden2FixMod.Tests.csproj
```

- ターゲットフレームワークは `net9.0` です。
- 実機の `UnityEngine.CoreModule.dll` を参照します。既定パスは Steam の `BUNNY GARDEN 2_Data/Managed` です。別の場所にインストールしている場合は `-p:UnityManagedDir="C:/path/to/BUNNY GARDEN 2_Data/Managed"` で上書きしてください。
- dll が見つからない場合は csproj の `CheckUnityManagedDir` ターゲットが原因を示すエラーを出します。

### テストの追加

1. テスト対象を **純関数**（UnityEngine + System のみに依存。BepInEx 型を参照しない）として用意します。
2. `BunnyGarden2FixMod.Tests.csproj` の `Compile Include` の `ItemGroup` に 1 行追加し、ソースを直リンクします（本体 dll は参照しません）。

   ```xml
   <Compile Include="../BunnyGarden2FixMod/Patches/.../Foo.cs" Link="Linked/Foo.cs" />
   ```

   `internal` 型でも同一アセンブリ化されアクセス可能です。
3. `BunnyGarden2FixMod.Tests/FooTests.cs` を作成します（`using Xunit;` と対象の namespace を `using`）。
4. `dotnet test ...` で実行します。

サンプル: `SpatialGridIndexTests.cs`（`SpatialGridIndex.cs` をリンクしてテスト）、`SmokeTest.cs`（ツールチェーン検証）。
</details>

## 既知の問題点
[Issues](https://github.com/kazumasa200/BunnyGarden2FixMod/issues)をご確認ください。バグや改善点、ほしい機能ありましたら[Issues](https://github.com/kazumasa200/BunnyGarden2FixMod/issues)もしくは[X](https://x.com/kazumasa200)までお願いします。  
要望の際は右上のNew Issueから個別のissueを作ってください。

## ライセンス
この Mod は **MIT ライセンス**で公開しています。詳しくは [LICENSE](LICENSE) をご覧ください。  
加えて、原作の開発元である**株式会社qureate**に対しては、この Mod のソースコードの一部または全部を
（クローズドソースの本編を含む）製品へ組み込むことを、著作権表示の義務なしで許諾しています。

## お問い合わせ
X(旧Twitter):@kazumasa200  
このModを導入してのライブ配信、スクショ、動画撮影はご自由にどうぞ。ただし、ゲーム自体のガイドラインに従ってください。また、クレジット表記も不要です。
