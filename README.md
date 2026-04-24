# BunnyGarden2FixMod

[バニーガーデン2](https://store.steampowered.com/app/3443820/2/)(海外名:Bunny Garden2)用の解像度修正やフレームレート上限変更などを行うBepInEx5用Modです。
<img width="1920" height="1080" alt="image" src="https://github.com/user-attachments/assets/b4e45f40-5420-4811-8500-4a0c3b4d1e69" />
<img width="1920" height="1000" alt="スクリーンショット 2026-04-16 191718-e" src="https://github.com/user-attachments/assets/f6c86e6b-2ad5-4b5f-bfa8-6ff66fcaf43b" />

## おしらせ
バージョン1.0.3から、BepInEx6にも対応しました！！  
今までのBepInEx5版もひきつづき開発します！！  
また、下で紹介している導入方法はBepInEx5のものになります！ご了承ください～  

## 対応バージョン(MODバージョンv1.0.6.1現在)
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
メモ帳などで開いて以下の項目を変更してください。

### [Resolution] 解像度・フレームレート

| キー | デフォルト | 説明 |
|------|-----------|------|
| `Width` | `1920` | 内部解像度の幅（横）。16:9 以外の値を入れると自動的に最大 16:9 に変換されます |
| `Height` | `1080` | 内部解像度の高さ（縦）。同上 |
| `ExtraWidth` | `2560` | ゲーム内 OptionMenu の DISPLAY 項目に追加される拡張解像度（ウィンドウモード）の幅。16:9 以外の値を入れると自動的に最大 16:9 に変換されます |
| `ExtraHeight` | `1440` | 同上 高さ。既定 `2560×1440`（WQHD） |
| `FrameRate` | `60` | フレームレート上限。`-1` にすると上限を撤廃します |

### [AntiAliasing] アンチエイリアシング

| キー | デフォルト | 説明 |
|------|-----------|------|
| `AntiAliasingType` | `MSAA8x` | アンチエイリアスの種類。`Off` / `FXAA` / `TAA` / `MSAA2x` / `MSAA4x` / `MSAA8x` から選択。右に行くほど高品質ですが重くなります |

### [Camera] フリーカメラ

| キー | デフォルト | 説明 |
|------|-----------|------|
| `Sensitivity` | `10` | フリーカメラのマウス感度 |
| `Speed` | `2.5` | フリーカメラの移動速度 |
| `FastSpeed` | `20` | 高速移動速度（Shift 押しながら移動） |
| `SlowSpeed` | `0.5` | 低速移動速度（Ctrl 押しながら移動） |
| `ControllerEnabled` | `true` | `true` にするとフリーカメラの切り替えと操作にゲームパッド入力を使用します |
| `ControllerToggleModifier` | `Select` | フリーカメラ切り替え用の修飾ボタン |
| `ControllerToggleFreeCam` | `Y` | フリーカメラ ON/OFF に使うボタン |
| `ControllerToggleFixedFreeCam` | `X` | フリーカメラ中の固定 ON/OFF に使うボタン（修飾ボタンと同時押し） |
| `ControllerToggleTimeStop` | `B` | フリーカメラ中の時間停止 ON/OFF に使うボタン（修飾ボタンと同時押し） |
| `ControllerToggleScreenshot` | `A` | フリーカメラ中のスクリーンショット保存に使うボタン（修飾ボタンと同時押し） |
| `TimeStopToggleKey` | `T` | フリーカメラ中の時間停止 ON/OFF に使うキーボードキー |
| `ScreenshotKey` | `P` | フリーカメラ中のスクリーンショット保存に使うキーボードキー |
| `ControllerTriggerDeadzone` | `0.35` | ZL / ZR を押下扱いにするしきい値。トリガーの遊びやドリフトがある場合は値を上げてください |
| `HideGameUiInFreeCam` | `true` | `true` にするとフリーカメラ中はゲーム本体の UI を自動で隠します |

フリーカメラは **F5** キーで ON/OFF、**F6** キーでカメラ固定のトグルができます。  
コントローラーの既定操作は **Select + Y** で ON/OFF、フリーカメラ中は **Select + X** で固定切り替え、**Select + B** で時間停止、**Select + A** でスクリーンショット保存です。  
オーバーレイ表示は **Ctrl + F5** または **Select + Start** で表示／非表示を切り替えられます。

#### フリーカメラ操作

| 入力 | 動作 |
|------|------|
| **WASD / 矢印キー** | 前後左右に移動 |
| **Q / E** | 上下に移動 |
| **Shift / Ctrl** | 高速移動 / 低速移動 |
| **マウス** | 視点移動 |
| **T** | 時間停止 ON / OFF |
| **P** | フリーカメラのスクリーンショットを PNG 保存 |
| **Ctrl + F5** | オーバーレイ表示 ON / OFF |
| **左スティック** | 前後左右に移動 |
| **右スティック** | 視点移動 |
| **ZL / ZR** | 下 / 上に移動 |
| **L / R** | 低速移動 / 高速移動 |
| **Select + X** | 固定モード ON / OFF |
| **Select + B** | 時間停止 ON / OFF |
| **Select + A** | フリーカメラのスクリーンショットを PNG 保存 |
| **Select + Start** | オーバーレイ表示 ON / OFF |

> **注意**: フリーカメラ中は誤操作を避けるため、既定ではゲーム本体の UI を自動で隠します。終了確認や確認ダイアログが表示された場合は、自動的に UI が復帰し、終了確認ではフリーカメラも自動解除されます。固定モード中は時間停止を有効化できません。フリーカメラのスクリーンショットは `BepInEx/screenshots/net.noeleve.BunnyGarden2FixMod/` に PNG で保存され、フリーカメラの表示オーバーレイは写りません。

### [Appearance] 外見

| キー | デフォルト | 説明 |
|------|-----------|------|
| `DisableStockings` | `false` | `true` にするとキャストのストッキングを非表示にします |

### [Conversation] 会話

| キー | デフォルト | 説明 |
|------|-----------|------|
| `ContinueVoiceOnTap` | `false` | `true` にすると会話送り時にボイスが途中で途切れなくなります。次の台詞のボイス再生で自然に上書きされるか、ボイスが最後まで再生されます |

### [Cheki] チェキ高解像度保存

| キー | デフォルト | 説明 |
|------|-----------|------|
| `HighResEnabled` | `false` | `true` にするとチェキを高解像度で保存します。`false` の場合は本体既定（320×320）のままです |
| `Size` | `1024` | 保存解像度（ピクセル）。64〜2048 の正方形サイズ。`HighResEnabled` が `false` の場合は無視されます |
| `ImageFormat` | `PNG` | 保存フォーマット。`PNG`（無劣化）/ `JPG`（圧縮・小サイズ）|
| `JpgQuality` | `90` | `ImageFormat=JPG` のときの品質（1〜100）。値が小さいほど小サイズ・低画質になります |

> **注意**: 高解像度データは `BepInEx/data/net.noeleve.BunnyGarden2FixMod/` フォルダに保存されます（Steam Cloud Save の対象外）。PCを移行する場合はこのフォルダを手動でコピーしてください。MODを外しても本体セーブ（320×320版）は破損しません。

### [Ending] エンディング

| キー | デフォルト | 説明 |
|------|-----------|------|
| `ChekiSlideshow` | `true` | `true` にするとエンディング中に撮影済みのチェキをスライドショーで表示します |

### [CastOrder] キャスト出勤順変更

| キー | デフォルト | 説明 |
|------|-----------|------|
| `Enabled` | `false` | `true` にするとバーに入る前にキャストの出勤順序を変更できます |

#### 操作方法

config で `Enabled = true` にした上で、ホール画面（バーに入る前）で操作します。

| キー | 動作 |
|------|------|
| **F1** | 編集モード 開始 / 終了 |
| **1〜5** キー（1回目） | そのキャストを選択（黄色表示） |
| **1〜5** キー（2回目） | 選択中のキャストと入れ替え |

- 画面左上にキャストの現在の並び順が表示されます
- バーに入店した後は変更できません（自動的に編集モードが終了します）
- 日付が変わった場合も自動的に編集モードが終了します

### [CostumeChanger] 衣装変更

F7キー（既定）でWardrobeパネルを開き、表示中のキャストの衣装・パンツ・ストッキングを自由に切り替えることができます。DLC衣装にも対応しています。

| キー | デフォルト | 説明 |
|------|-----------|------|
| `Enabled` | `true` | `true` にすると衣装変更UIとパッチを有効化します |
| `Hotkey` | `F7` | 衣装変更UIの表示トグルキー。`UnityEngine.InputSystem.Key` enum名で指定（例: `F8`, `BackQuote`） |
| `RespectGameCostumeOverride` | `true` | `true` にすると、試着室などゲームが特定の衣装を強制するシーンではMOD側の衣装変更を一時的に停止します。これを有効にすることで、ゲーム内のイベントと衣装の競合を防げます |

#### 操作方法

Wardrobeパネル表示中、以下のキーで操作できます。

| キー | 動作 |
|------|------|
| **A / ←** | 左のタブに切替 |
| **D / →** | 右のタブに切替 |
| **W / ↑** | 選択を上に移動 |
| **S / ↓** | 選択を下に移動 |
| **Enter** | 選択したアイテムを適用（既に適用中なら解除） |
| **R** | 全タブのoverrideをリセット |
| **Esc** | パネルを閉じる |
| **マウスクリック** | 行をクリックで即適用・解除。キャスト名をクリックで対象切替 |

- パネル上のカーソル外にある操作はゲームに素通しされます
- フィッティングルーム動作中はパネルを開けません
- 複数のキャストが画面に表示されている場合は、パネル上のキャスト名をクリックして対象を切り替えられます

### [Cheat] チート

| キー | デフォルト | 説明 |
|------|-----------|------|
| `UltimateSurvivor` | `false` | `true` にすると鉄骨渡りミニゲームで落下しなくなります |
| `GambleAlwaysWin` | `false` | `true` にするとギャンブルで負けなくなります（損失が発生しません） |
| `Enabled` | `false` | `true` にすると会話選択肢・ドリンク・フードの正解をゲーム内に表示します。会話選択肢は先頭に ★（好感度UP）/ ▼（好感度DOWN）が付きます。ドリンク・フードは背景色が緑（お気に入り）/ 黄（旬）/ 赤（嫌い）に変わります |

## Tips

- **F4キー** でゲーム起動中に設定ファイルをリロードできます。設定ファイルを変更した後、ゲームを再起動する必要はありません（F4キーを押すのみ）。

## 既知の問題点
[Issues](https://github.com/kazumasa200/BunnyGarden2FixMod/issues)をご確認ください。バグや改善点、ほしい機能ありましたら[Issues](https://github.com/kazumasa200/BunnyGarden2FixMod/issues)もしくは[X](https://x.com/kazumasa200)までお願いします。  
要望の際は右上のNew Issueから個別のissueを作ってください。

## お問い合わせ
X(旧Twitter):@kazumasa200  
このModを導入してのライブ配信、スクショ、動画撮影はご自由にどうぞ。ただし、ゲーム自体のガイドラインに従ってください。また、クレジット表記も不要です。
