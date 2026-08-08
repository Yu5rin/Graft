# 確認手順: 単一ファイル化と Windows 固有バグの修正

対象コミット: `f660661`（ブランチ `claude/spec-implementation-7fhplo`）

この手順書は、**直近の変更が Windows で正しく動くか**を確認するためのものです。
アプリ全体の実機検証は `Windows実機検証手順.md`（14項目）が担当します。こちらとは目的が
違うので、両方をこなす必要はありません。まずはこの手順書だけで構いません。

所要時間の目安: **A〜C で約10分**、D・E まで含めて **約25分**。

---

## 0. なぜこの確認が必要か

Windows 実機で `dotnet test` を実行したところ、**620件中14件が失敗**しました。
Linux（開発環境）と GitHub Actions の CI（Ubuntu）では**全件通過**していたため、
これらは **Windows でしか表面化しない問題**です。

そのうち **2件は製品コードの実バグ**でした。

| # | 症状 | 実害 |
|---|---|---|
| 1 | Git のコミットメッセージが文字化けする | **Git自動コミットが日本語環境で壊れていた** |
| 2 | 設定ファイルの破損復旧が失敗しうる | **設定が壊れたとき復旧できない場合がある** |

残り12件はテスト側の問題（権限が要る操作、OS 依存の前提、後片付けの失敗など）です。

修正は入れましたが、**修正した本人（開発環境）が Linux のため、直ったことを確認できていません。**
確認できるのは Windows 環境だけです。

あわせて、配布物のファイル数を **216 → 4** に減らす変更も入っています。こちらも
Windows での確認が必要です。

---

## 0.1 準備

```powershell
cd C:\Users\YUGO\Graft
git pull origin claude/spec-implementation-7fhplo
git log --oneline -1
```

`f660661` 以降が表示されれば準備完了です。

> **注意**: 以前、ローカルの作業ツリーで多数のファイルが削除されたまま残っていて
> ビルドが失敗したことがありました。`git status --short` で `D` 始まりの行が出る場合は、
> `git restore .` で復元してから進めてください。

---

## A. 自動テスト（必須・約5分）

### 確認項目

前回失敗した14件が緑になり、他を壊していないこと。

### 確認方法

```powershell
cd C:\Users\YUGO\Graft
dotnet build Graft.sln -c Release
dotnet test Graft.sln -c Release 2>&1 | Tee-Object -FilePath $env:TEMP\graft-test.txt
Write-Host "`n===== 結果 =====" -ForegroundColor Cyan
Select-String -Path $env:TEMP\graft-test.txt -Pattern "テスト概要|error TESTERROR" | Select-Object -First 40
```

### 期待される結果

```
テスト概要: 合計: 627, 失敗数: 0, 成功数: 627, スキップ済み数: 0
```

- 件数が **620 → 627** に増えているのは正常です。OS に依存しない形で回帰を防ぐテストを
  7件追加したためです（Windows でしか自然には起きない例外を、例外を投げる偽物を使って
  Linux でも検証できるようにしたもの）。
- **開発者モードが無効な場合**、シンボリックリンクのテスト2件は「失敗」ではなく
  早期終了し、コンソールに理由と対処法が出ます。それが正しい挙動です。

### 確認ファイル

`%TEMP%\graft-test.txt` に全文が残ります。失敗が出た場合はこのファイルを貼ってください。

### 前回失敗した14件（対応表）

| テスト | 前回の症状 | 対応 |
|---|---|---|
| シンボリックリンクがルート内を指す場合は許可される | 特権不足 | 権限が無ければスキップ |
| シンボリックリンク経由でルート外へ出るパスはE201になる | 特権不足 | 同上 |
| 同じ破損ファイルを同時に読んでも例外にならない | `UnauthorizedAccessException` | **製品修正②** |
| Linux上ではCreateIdは大文字小文字が…（ProjectStore） | Linux 前提 | OS ガード＋Windows 用の対テストを新設 |
| gitリポジトリへコミットすると type: summary 形式の… | 文字化け | **製品修正①** |
| 検索とクリアを5回繰り返しても…（メモリ） | 1回目の測定が異常値 | 基準を2回目へ変更（閾値は据え置き） |
| エディタ本文の右クリックメニューが…（2件） | 一時フォルダの削除失敗 | 後片付けを共通化＋リトライ |
| AutoCommit 系（4件） | 文字化け＋後片付け失敗 | **製品修正①**＋後片付け |
| 課題1: Git.AutoCommitを実行中に…（2件） | 同上 | 同上 |

---

## B. 発行物の確認（必須・約3分）

### 確認項目

配布フォルダが 216ファイルから 4ファイルに減っていること。追加のコマンドライン指定なしで
そうなること。

### 確認方法

```powershell
cd C:\Users\YUGO\Graft
Remove-Item -Recurse -Force publish\Graft -ErrorAction SilentlyContinue
dotnet publish src\Graft\Graft.csproj -c Release -r win-x64 --self-contained true -o publish\Graft
dir publish\Graft
```

### 期待される結果

**4ファイルだけ**が並びます。

```
Graft.exe
av_libglesv2.dll
libHarfBuzzSharp.dll
libSkiaSharp.dll
```

- `-p:PublishSingleFile=true` などの指定は**不要**です。`Graft.csproj` に既定として
  書いたので、指定を忘れても4ファイルになります。
- 残る3つの DLL は**描画エンジンのネイティブライブラリ**で、exe には埋め込めません。
  これ以上は減らせません。
- **`Graft.exe` だけを取り出さないでください。** 4つセットで1つの配布物です。
- `.pdb` が混ざっていたら異常です（Release では出ない設定にしています）。

### 確認ファイル

`publish\Graft\` フォルダ。

---

## C. アプリの起動と目視確認（必須・約5分）

### 確認項目

単一ファイル化で壊れやすい箇所が無事なこと。具体的には次の2つの読み込み経路です。

1. **アセンブリ属性からの読み込み**（製作者・著作権の表示）
2. **埋め込みリソースからの読み込み**（同梱ライセンス全文）

この2つは単一ファイル構成で最初に壊れる箇所なので、必ず目で見てください。

### 確認方法

```powershell
.\publish\Graft\Graft.exe
```

起動したら **設定 → バージョン情報** を開きます。

### 期待される結果

ロゴの右に次の4行が出ます。

```
Graft
バージョン 1.0.0.0
ビルド日時: （発行した日時）
製作者: YUGO
Copyright © 2026 YUGO
```

- **`©` が文字化けしていないこと**を確認してください。
- 下にある **DiffPlex** と **AvaloniaEdit** の「ライセンス全文を表示」をそれぞれ開き、
  **英文のライセンスが最後まで表示されること**を確認してください。
  「ライセンスファイルを読み込めませんでした。」と出たら失敗です。

続けて、ツールバーの **「ファイル」** ボタンからコンテキスト収集画面を開き、
**ファイルツリーとアイコンが表示されること**を確認してください（アイコンのリソース読み込みの確認）。

### 確認ファイル

なし（画面の目視のみ）。

---

## D. Git の日本語コミット（製品修正①の実地確認・約7分）

**ここが今回いちばん重要です。** 自動テストでも検証していますが、実際のアプリ操作で
確かめておく価値があります。

### 確認項目

Git 自動コミットのメッセージが文字化けしないこと。

### 確認方法

1. 適当な作業用フォルダを git リポジトリとして用意します。

```powershell
$p = "$env:USERPROFILE\graft-git-test"
Remove-Item -Recurse -Force $p -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $p | Out-Null
cd $p
git init
git config user.name "YUGO"
git config user.email "yugo0801@gmail.com"
"function hello() { return 1; }" | Set-Content -Encoding UTF8 sample.js
git add . ; git commit -m "初期化"
```

2. Graft を起動し、このフォルダをプロジェクトとして登録します。
3. **設定 → 安全性**（または該当欄）で **Git自動コミットを有効**にします。
4. 次のパッチをコピーして、Graft の「解析」→「適用」を実行します。

````
```graft
--- summary: テスト用の変更
--- type: feat
--- file: sample.js
--- search
function hello() { return 1; }
--- replace
function hello() { return 42; }
```
````

5. コミットログを確認します。

```powershell
cd $env:USERPROFILE\graft-git-test
git log --oneline
```

### 期待される結果

```
feat: テスト用の変更 (r1)
```

**これが正解です。**次のようになっていたら**修正が効いていません**。

```
feat: 繝・せ繝育畑縺ｮ螟画峩 (r1)
```

### 確認ファイル

`%USERPROFILE%\graft-git-test\` の git ログ。

### 後片付け

```powershell
Remove-Item -Recurse -Force $env:USERPROFILE\graft-git-test
```

---

## E. 設定ファイルの破損復旧（製品修正②の実地確認・約5分）

### 確認項目

設定ファイルが壊れていても、アプリが起動し、既定値で復旧すること。壊れたファイルが
退避されること。

### 確認方法

Graft は**設定を exe と同じフォルダに置きます**（`settings.json` / `projects.json`）。

1. Graft を終了します。
2. 設定ファイルをわざと壊します。

```powershell
cd C:\Users\YUGO\Graft\publish\Graft
Copy-Item settings.json settings.json.backup -ErrorAction SilentlyContinue
"これは壊れたJSONです" | Set-Content -Encoding UTF8 settings.json
.\Graft.exe
```

3. アプリが**正常に起動する**ことを確認します。
4. 終了してからフォルダを確認します。

```powershell
dir settings*
```

### 期待される結果

- アプリが**エラーで落ちずに起動**し、設定は既定値に戻っている
- `settings.json.corrupt.20260808_HHMMSS` のような**退避ファイルができている**
- `settings.json` が**新しく作り直されている**

`UnauthorizedAccessException` などでアプリが起動しなかったら失敗です。

### 確認ファイル

`publish\Graft\settings.json.corrupt.*`（退避ファイル）

### 後片付け

```powershell
Remove-Item settings.json.corrupt.* -ErrorAction SilentlyContinue
Move-Item settings.json.backup settings.json -Force -ErrorAction SilentlyContinue
```

---

## 報告用メモ

そのままコピーして埋めてください。

```
■ A. 自動テスト
  テスト概要:                （行をそのまま貼る）
  失敗があれば内容:

■ B. 発行物
  publish\Graft のファイル数:      件
  4ファイルだったか:  はい / いいえ
  内訳（いいえの場合）:

■ C. 起動と目視
  製作者「YUGO」が表示された:      はい / いいえ
  「Copyright © 2026 YUGO」が文字化けせず表示:  はい / いいえ
  DiffPlex ライセンス全文が表示:   はい / いいえ
  AvaloniaEdit ライセンス全文が表示: はい / いいえ
  コンテキスト収集のツリーが表示:  はい / いいえ

■ D. Git 日本語コミット
  git log の1行目:                （そのまま貼る）
  文字化けしていたか:  はい / いいえ

■ E. 設定ファイル破損復旧
  アプリが起動したか:  はい / いいえ
  退避ファイルができたか:  はい / いいえ

■ 気づいたこと・その他
```

---

## 付録: 今回の修正の中身

### 製品修正①: Git 出力のエンコーディング

`src/Graft/Features/GitIntegration.cs`

`ProcessStartInfo` に `StandardOutputEncoding` / `StandardErrorEncoding` を指定していなかった。
.NET は既定で OS のコードページ（日本語版 Windows では CP932）を使うため、UTF-8 で
出力する git の結果が壊れていた。Linux は既定が UTF-8 のため表面化しなかった。

BOM なし UTF-8 を明示し、あわせて全 git 呼び出しに
`-c core.quotepath=false -c i18n.logOutputEncoding=UTF-8` を付与した。

### 製品修正②: `UnauthorizedAccessException` の捕捉漏れ

`src/Graft/Infra/JsonFileStore.cs`

`File.Move` 失敗時に `IOException` しか捕捉していなかった。Windows は開いているファイルの
移動に制約があり、並行アクセス時に `UnauthorizedAccessException` を投げる。これは
`IOException` の派生ではないため素通りしていた。

捕捉を広げ、30ms 間隔・最大5回のリトライを追加した（上限で諦める）。
あわせて、並行アクセス時の TOCTOU で `FileNotFoundException` を投げうる別の欠陥も
修正した（こちらは Linux でも再現を確認済み）。

### 配布形式の変更

`src/Graft/Graft.csproj`

`PublishSingleFile=true` / `IncludeNativeLibrariesForSelfExtract=false` /
`EnableCompressionInSingleFile=false` を既定に。

`IncludeNativeLibrariesForSelfExtract` を **false にしているのが要点**。true にすると
ネイティブ DLL を実行時に一時フォルダへ展開するため、初回起動が 5.44 秒まで悪化する
（過去にこれが理由でフォルダ形式へ移行した経緯がある）。false なら展開が起きないため、
Linux 実測で 546〜673ms と、フォルダ形式（798〜1145ms）より速い。
