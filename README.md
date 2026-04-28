# BunnyGarden2FixMod

[バニーガーデン2](https://store.steampowered.com/app/3443820/2/)(海外名:Bunny Garden2)用の解像度修正やフレームレート上限変更などを行うBepInEx5用Modです。
<img width="1920" height="1080" alt="image" src="https://github.com/user-attachments/assets/b4e45f40-5420-4811-8500-4a0c3b4d1e69" />
<img width="1920" height="1000" alt="スクリーンショット 2026-04-16 191718-e" src="https://github.com/user-attachments/assets/f6c86e6b-2ad5-4b5f-bfa8-6ff66fcaf43b" />

## おしらせ
バージョン1.0.3から、BepInEx6にも対応しました！！  
今までのBepInEx5版もひきつづき開発します！！  
また、下で紹介している導入方法はBepInEx5のものになります！ご了承ください～  

## 対応バージョン(MODバージョンv1.0.8.1現在)
- ゲームバージョン1.0.2のみ対応  

## 機能
- 内部解像度を指定することで画質を向上することができる。
- 本来は60で固定されていたフレームレート制限を任意の値にするか、取り払うことができる。
- アンチエイリアスを設定し、さらに画面のガビガビ感(ジャギー)を減らすことができる。
- フリーカメラ機能。キーボード／コントローラー操作、時間停止、表示オーバーレイの切り替えに対応。
- フリーカメラ中のスクリーンショット保存機能。表示オーバーレイを写さず PNG で保存できる。
- ドリンク、フード、会話選択肢の正解を表示させることが出来る。(デフォルトでは無効)
- ストッキングを強制的に非表示にすることができる。(デフォルトでは無効)
- バーに入る前にキャストの出勤順序を変更できる。(デフォルトでは無効)
- チェキ（撮影写真）を高解像度で保存できる。(デフォルトでは無効)
- F7キーで衣装・パンツ・ストッキングを自由に切り替えることができる。(デフォルトでは有効)
- F9キーで旅行・特別なシーンの所持金UI、ボタンガイド、ラブカウンターを非表示にできる。(所持金非表示はデフォルトで有効)
- 色収差エフェクト（画面端のにじみ）を無効化できる。(デフォルトでは無効)

## 導入方法(Steam Deckも対応)
1. [Releases](https://github.com/kazumasa200/BunnyGarden2FixMod/releases/latest)から最新のzipファイルをダウンロードする。(BunnyGarden2FixMod_v1.0.6.1_BepInEx5.zipみたいな感じ)ブラウザによってはブロックするかもしれないので注意。<br>導入時の最新バージョンを入れてください。
<img width="983" height="709" alt="image" src="https://github.com/user-attachments/assets/1ce21405-2b6b-47b4-a32f-d9fce95f76c5" />

上の画像はv1.0.6.1の場合の例です。導入時の最新バージョンを選択してください。  
> [!NOTE]
> BepInEx5とBepInEx6のどっちを入れるか迷った場合や、Modの導入が初めての方はBepInEx5とついた方をダウンロードしてください。  
> 以下の手順はBepInEx5版を前提につくっています。  

2. [BepInEx5](https://github.com/bepinex/bepinex/releases)をダウンロードする。Windowsの場合もSteam Deckの場合も```BepInEx_win_x64_{バージョン名}.zip```をダウンロードする。

3. ゲームのexeがあるディレクトリにBepInEx5の中身を展開。つまり、ゲームのexeとBepInExフォルダやdoorstop_configとかが同じ階層にある状態が正しいということ。
<img width="1535" height="1069" alt="image" src="https://github.com/user-attachments/assets/3a1985df-6f79-4c7d-9a66-31ca5ffa312a" />  

4. (Steam Deckの場合のみ実行) Steamでバニーガーデン2 → 右クリック → 「プロパティ」→「一般」→「起動オプション」に```WINEDLLOVERRIDES="winhttp=n,b" %command%```を入力。

5. 一度ゲームを起動した後、[Releases](https://github.com/kazumasa200/BunnyGarden2FixMod/releases/latest)からダウンロードしたZipを展開し、中にある```net.noeleve.BunnyGarden2FixMod.dll```をBepinExフォルダの中のPluginsの中に入れる。
<img width="1490" height="383" alt="image" src="https://github.com/user-attachments/assets/f24310e1-c5f1-4a08-9195-b25d0fe37377" />

6. もう一度起動するとBepinExフォルダの中のconfigフォルダに```net.noeleve.BunnyGarden2FixMod.cfg```設定ファイルが出来上がるので、それをメモ帳などで変更して解像度の設定やフレームレートなどの設定をする。
<img width="1677" height="1906" alt="image" src="https://github.com/user-attachments/assets/d8cdc40e-7299-46f4-bbf0-ba5d685c38c9" />
上の画像は例です。お好みにどうぞ。


## Config 設定一覧

ゲームを一度起動すると `BepInEx/config/net.noeleve.BunnyGarden2FixMod.cfg` が生成されます。  

**全項目の一覧と詳細は [こちら](docs/configs.md) を参照してください**（[`Configs.yaml`](BunnyGarden2FixMod/Configs.yaml) から自動生成）。  

ゲーム起動中は **F9** キーで設定パネルを開いて多くの項目を直接編集することもできます（`F4` キーでリロード）。

## Tips

- **F4キー** でゲーム起動中に設定ファイルをリロードできます。設定ファイルを変更した後、ゲームを再起動する必要はありません（F4キーを押すのみ）。

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

## 既知の問題点
[Issues](https://github.com/kazumasa200/BunnyGarden2FixMod/issues)をご確認ください。バグや改善点、ほしい機能ありましたら[Issues](https://github.com/kazumasa200/BunnyGarden2FixMod/issues)もしくは[X](https://x.com/kazumasa200)までお願いします。  
要望の際は右上のNew Issueから個別のissueを作ってください。

## お問い合わせ
X(旧Twitter):@kazumasa200  
このModを導入してのライブ配信、スクショ、動画撮影はご自由にどうぞ。ただし、ゲーム自体のガイドラインに従ってください。また、クレジット表記も不要です。
