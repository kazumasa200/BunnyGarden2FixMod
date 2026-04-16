# BunnyGarden2FixMod

[バニーガーデン2](https://store.steampowered.com/app/3443820/2/)(海外名:Bunny Garden2)用の解像度修正やフレームレート上限変更などを行うBepInEx5用Modです。
<img width="3839" height="2159" alt="image" src="https://github.com/user-attachments/assets/b4e45f40-5420-4811-8500-4a0c3b4d1e69" />
<img width="3831" height="1946" alt="スクリーンショット 2026-04-16 191718-e" src="https://github.com/user-attachments/assets/f6c86e6b-2ad5-4b5f-bfa8-6ff66fcaf43b" />

## 対応バージョン(MODバージョンv1.0.2現在)
- ゲームバージョン1.0.1のみ対応  

## 機能
- 内部解像度を指定することで画質を向上することができる。
- 本来は60で固定されていたフレームレート制限を任意の値にするか、取り払うことができる。
- アンチエイリアスを設定し、さらに画面のガビガビ感(ジャギー)を減らすことができる。  

## 導入方法
1. [Releases](https://github.com/kazumasa200/BunnyGarden2FixMod/releases/latest)から最新のzipファイルをダウンロードする。(BunnyGarden2FixMod v1.0.0.zipみたいな感じ)ブラウザによってはブロックするかもしれないので注意。<br>導入時の最新バージョンを入れてください。
<img width="944" height="470" alt="image" src="https://github.com/user-attachments/assets/c187fa9b-e4e1-4575-a2e6-fd8e6a31856d" />  
上の画像はv1.0.0の場合の例です。導入時の最新バージョンを選択してください。  

2. [BepInEx5](https://github.com/bepinex/bepinex/releases)をダウンロードする。Windowsの場合は```BepInEx_win_x64_{バージョン名}.zip```をダウンロードする。

3. ゲームのexeがあるディレクトリにBepInEx5の中身を展開。つまり、ゲームのexeとBepInExフォルダやdoorstop_configとかが同じ階層にある状態が正しいということ。
<img width="1535" height="1069" alt="image" src="https://github.com/user-attachments/assets/3a1985df-6f79-4c7d-9a66-31ca5ffa312a" />

4. 一度ゲームを起動した後、[Releases](https://github.com/kazumasa200/BunnyGarden2FixMod/releases/latest)からダウンロードしたZipを展開し、中にある```net.noeleve.BunnyGarden2FixMod.dll```をBepinExフォルダの中にPluginsの中に入れる。
<img width="1490" height="383" alt="image" src="https://github.com/user-attachments/assets/f24310e1-c5f1-4a08-9195-b25d0fe37377" />

5. もう一度起動するとBepinExフォルダの中のconfigフォルダに```net.noeleve.BunnyGarden2FixMod.cfg```設定ファイルが出来上がるので、それをメモ帳などで変更して解像度の設定やフレームレートなどの設定をする。
<img width="1425" height="748" alt="image" src="https://github.com/user-attachments/assets/14eb1538-503f-4c36-8630-62e9f008c0cb" />

## 既知の問題点
[Issues](https://github.com/kazumasa200/BunnyGarden2FixMod/issues)をご確認ください。バグや改善点、ほしい機能ありましたら[Issues](https://github.com/kazumasa200/BunnyGarden2FixMod/issues)もしくは[X](https://x.com/kazumasa200)までお願いします。  
要望の際は右上のNew Issueから個別のissueを作ってください。

## お問い合わせ
X(旧Twitter):@kazumasa200  
このModを導入してのライブ配信、スクショ、動画撮影はご自由にどうぞ。ただし、ゲーム自体のガイドラインに従ってください。また、クレジット表記も不要です。
