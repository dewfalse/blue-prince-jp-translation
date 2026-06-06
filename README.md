# blue-prince-jp-translation

Blue Prince の非公式日本語化MODです。

BepInEx IL2CPP プラグインで、Blue Prince のUIテキストとゲーム内テキストを日本語の対訳TSVに置き換え、TextMeshPro 用の日本語フォントを読み込めるようにします。

英語を前提にした謎が解けなくなることを避けるため、日本語へ置換されたテキストの英語原文は、BepInExコンソールと `BepInEx/LogOutput.log` に `[ORIGINAL:...]` として出力されます。同じ原文は1セッション内で1回だけ出力されます。

## 動作確認

Blue Prince 本体バージョン `1.7.1` 時点で動作確認しています。

ゲーム本体のアップデートにより、BepInExのinterop生成物やゲーム内クラス/APIが変わった場合、プラグインが動作しなくなる可能性があります。

## リポジトリ構成

- `src/BluePrinceJP`: BepInExプラグインのC#ソースコード
- `tools/extract`: ローカルのBlue Princeインストールから翻訳候補テキストを抽出する入口スクリプト
- `tools/translation`: `game_ja.tsv` / `lang_ja.tsv` の土台を作成するスクリプト
- `templates`: TSV形式の最小テンプレート
- `docs`: 対訳作成フローのメモ
- `third_party`: リリース作成時のサードパーティ通知メモ

このリポジトリには、Blue Prince本体ファイル、BepInEx本体、フォントファイル、完成版の対訳TSVは含めません。

## ビルド

ローカルのBlue PrinceフォルダにBepInEx IL2CPPを導入し、一度ゲームを起動して `BepInEx/interop` を生成します。

その後、次のようにゲームフォルダを指定してビルドします。

```powershell
dotnet build .\src\BluePrinceJP\BluePrinceJP.csproj -c Release -p:GameDir="E:\SteamLibrary\steamapps\common\Blue Prince"
```

このリポジトリをゲームフォルダ直下に `BluePrinceJP_repo` として置く場合は、`GameDir` を省略できます。

## 対訳ベース作成

Pythonスクリプト用の依存関係を入れます。

```powershell
python -m pip install -r requirements-tools.txt
```

ゲーム内テキストの候補を抽出します。

```powershell
python .\tools\extract\extract_blue_prince_text.py --game-dir "E:\SteamLibrary\steamapps\common\Blue Prince" --out-dir .\extract_output
```

`game_ja.tsv` の土台を作成します。

```powershell
python .\tools\translation\make_translation_base.py --mode game --output .\translations\game_ja.tsv .\extract_output\raw_texts.txt
```

UIテキストは、プラグイン実行時に `BepInEx/BluePrinceJP/lang_en.txt` として出力されます。このファイルから `lang_ja.tsv` の土台を作成します。

```powershell
python .\tools\translation\make_translation_base.py --mode ui --output .\translations\lang_ja.tsv "E:\SteamLibrary\steamapps\common\Blue Prince\BepInEx\BluePrinceJP\lang_en.txt"
```

## リリースzip

リリースzipは `BLUE PRINCE.exe` と同じ階層へ展開する想定です。zipには次のものを含めます。

- BepInEx IL2CPP loader/runtime
- `BepInEx/plugins/BluePrinceJP/BluePrinceJP.dll`
- `BepInEx/BluePrinceJP/NotoSansJP-Medium.ttf`
- `BepInEx/BluePrinceJP/lang_ja.tsv`
- `BepInEx/BluePrinceJP/game_ja.tsv`
- 同梱物に必要なサードパーティライセンス・通知ファイル

ゲーム本体ファイルは含めません。

## 免責

これはファンメイドの非公式MODです。Dogubomb、Raw Fury、Blue Princeの開発元・販売元とは関係ありません。
