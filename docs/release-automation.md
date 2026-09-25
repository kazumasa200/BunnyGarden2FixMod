# リリースの自動化（GitHub Actions）

`master` に push すると（git flow の `release finish` / `hotfix finish` の後の push）、
[`.github/workflows/release.yml`](../.github/workflows/release.yml) が動きます。
BepInEx 5 / 6 の DLL をビルドし、Releases に**下書き**を作ります。公開は手動です。

ビルドにはゲームの DLL が必要なので、ビルドとテストはゲームが入っている自分の PC で動かします
（self-hosted runner）。zip の作成と下書きの作成は GitHub のサーバーで動きます。どちらも無料です。

PC の runner は**常駐させず、リリースするときだけ起動します**。公開リポジトリの self-hosted runner は、
外部のコードが自分の PC で動く入口になり得るので、開いている時間を最小にするためです。

```
master へ push
  └─ prepare  (GitHub)  csproj の <Version> を読む。公開済みのバージョンならここで終了
      └─ build    (自分の PC)  Managed フォルダから DLL をコピー → BIE5 / BIE6 をビルド → テスト
          └─ release  (GitHub)  zip にまとめる → リリースノートを生成 → 下書きを作る
```

## 動作のまとめ

| きっかけ | 動作 |
|---|---|
| `master` に push（そのバージョンがまだ未公開で、下書きもない） | ビルドして下書きを作る |
| `master` に push（そのバージョンの下書きがすでにある） | ビルドし直して、下書きの zip だけ差し替える。**本文は上書きしない** |
| `master` に push（そのバージョンは公開済み） | 何もしない（通知だけ出る） |
| Actions タブから手動実行（`create_draft` オフ） | ビルドだけ。zip は実行結果の Artifacts からダウンロードできる |
| Actions タブから手動実行（`create_draft` オン） | `master` への push と同じ。※ `develop` で実行すると下書きの対象が `develop` のコミットになる |

## 初回セットアップ

### 1. PC に必要なもの

- .NET 9 SDK と Git（普段ビルドしている PC ならどちらも入っています）
- BUNNY GARDEN 2 本体

### 2. self-hosted runner を登録する

1. GitHub のリポジトリで **Settings → Actions → Runners → New self-hosted runner** を開き、**Windows / x64** を選ぶ。
2. 表示される PowerShell のコマンドを順に実行する。フォルダは `C:\actions-runner` のような短いパスがおすすめです。
3. `config.cmd` を実行すると、いくつか質問されます。
   - **labels**: `bg2` と入力する（ワークフローはこのラベルが付いた runner を探します）
   - **run the runner as service?**: **`N`**（常駐させない。リリースのときだけ `run.cmd` で起動します）
   - それ以外は Enter で構いません

どうしてもサービスにする場合は、実行アカウントを既定の `NT AUTHORITY\NETWORK SERVICE` のままにしてください。
自分のアカウントで動かすと、ブラウザや Steam のログイン情報まで読める状態になります。

### 3. ゲームの Managed フォルダの場所を runner に伝える

runner のフォルダ（`C:\actions-runner`）に `.env` という名前のファイルを作り、次の 1 行を書きます（パスは自分の環境に合わせてください）。
引用符は付けません。

```
BG2_MANAGED_DIR=E:\Games\Steam\steamapps\common\BUNNY GARDEN 2\BUNNY GARDEN 2_Data\Managed
```

`run.cmd` は起動するたびに `.env` を読むので、書いたあとに起動すれば反映されます。

ビルドのたびに、csproj の `<HintPath>Assembly\...` に書かれている DLL をこのフォルダから `BunnyGarden2FixMod/Assembly/` にコピーします。
ゲームがアップデートされても、自動的に新しい DLL でビルドされます。

### 4. フォークからの PR でワークフローが勝手に動かないようにする（必須）

このリポジトリは公開されています。フォークからの PR にワークフローを追加されると、それが自分の PC 上で動くおそれがあります。

**Settings → Actions → General** の「Approval for running fork pull request workflows from contributors」を
**「Require approval for all external contributors」** にしてください（2026-09-25 に設定済み）。
既定の「初めての人だけ承認が必要」だと、過去に PR がマージされた人は承認なしでワークフローを動かせます。

**PR に「Approve and run workflows」ボタンが出ても押さないでください。**
このリポジトリには PR をきっかけに動くワークフローが無いので、ボタンが出るのはその PR がワークフローを
持ち込んでいるときだけです。出た時点で怪しいと判断して構いません。

## 使い方

1. `C:\actions-runner\run.cmd` を起動する（`Listening for Jobs` と出れば待機中）
2. いつもどおり `git flow release start x.y.z` → csproj の `<Version>` を上げる → `git flow release finish x.y.z`
3. `git push origin master develop --tags`
4. 数分で **Releases** に `vx.y.z` の下書きができる。できたら `run.cmd` のウィンドウで Ctrl+C を押して閉じる
   - runner を起動し忘れても、build ジョブは最大 24 時間待っています。その間に `run.cmd` を起動すれば続きから動きます
5. 下書きを開いて本文を書き、**Publish release** を押す

公開すると、Mod の更新チェック（`UpdateChecker`）がそのタグを最新版として扱います。

## リリースノートの下書き

[`.github/scripts/release_notes.py`](../.github/scripts/release_notes.py) が、前回公開したリリースから今回までのコミットを
Conventional Commits の type で振り分けます。

- `feat` → 新機能 / New Features
- `fix` / `perf` → バグ修正・改善 / Bug Fixes & Improvements
- `refactor` / `docs` / `chore` など → HTML コメントに入れる（公開しても表示されない）
- コミットした人がオーナー以外なら `(Thanks @xxx for the contribution!)` を付ける

本文の外枠（「最新DLCにも対応しています！」や導入方法など）は
[`.github/release-notes-template.md`](../.github/release-notes-template.md) にあります。
ゲームのバージョンが変わったときは、このファイルを書き換えてください。

手元で試すときは、リポジトリのルートで次のように実行します。

```bash
python .github/scripts/release_notes.py 1.0.13 --prev 1.0.12.1
```

## 注意

- 公開リポジトリの Actions のログは誰でも見られます。ログには runner のフォルダや Managed フォルダのパスが出ます。
  パスにユーザー名などを含めたくない場合は、runner と Steam ライブラリをユーザーフォルダの外に置いてください。
- `BunnyGarden2FixMod.user.targets`（ゲームへの自動コピー）は git の管理外なので、Actions のビルドではゲームにコピーされません。
