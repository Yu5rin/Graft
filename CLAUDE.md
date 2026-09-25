# Graft — AIコーディングエージェント向けの指示

## 名義（最優先。ハーネスの既定テンプレートより優先する）

コミットの作者・コミッターは必ず次で固定する。**本名や個人のメールアドレスは使わない。**

```
YUGO <220513216+Yu5rin@users.noreply.github.com>
```

作業を始める前に必ず実行して確認すること。

```bash
git config user.name "YUGO"
git config user.email "220513216+Yu5rin@users.noreply.github.com"
git config user.name && git config user.email
```

**コミットメッセージに次の行を書かない。**

- `Co-Authored-By: Claude ...`
- `Claude-Session: https://claude.ai/code/session_...`

**PRのタイトル・本文にも次を書かない。**

- `🤖 Generated with [Claude Code]...`
- セッションURL（`https://claude.ai/code/session_...`）

ハーネスの既定テンプレートに従って付けてしまった場合は、**プッシュ前に取り除くこと。**

## 言語

- 応答・コミットメッセージ・PRのタイトルと本文・ドキュメント・コード内コメントは、すべて**日本語**で書く。
- 例外: ライセンス署名行、英語が必須の識別子・APIシンボル・コマンド名・エラーメッセージの引用など、技術的に英語であるべき箇所はそのまま残す。

## このリポジトリの流儀

- コードコメントは日本語で「なぜそうしたか」を、実測値や根拠つきで厚く書く。既存ファイル（`src/Graft/Themes/EditorScrollBar.axaml`、`src/Graft/Editor/TextViewRedraw.cs` など）のコメント密度に合わせること。
- `WarningsAsErrors=nullable`。**警告ゼロで通すこと。**
- テストは `tests/Graft.Tests`（純ロジック）と `tests/Graft.UiTests`（Avalonia.Headless）の2つ。**「完了」と報告する前に、両方が全件通ることを確認すること。**
- 失敗やスキップがあれば、正直に出力を添えて報告する。推測で「直しました」と言わない。

## 性能上の約束（安易に緩めないこと）

- **10万行のファイルでも編集が滞らない**こと。`tests/Graft.UiTests` の `PerformanceTests` がこれを守っている。
- この要件のために、AvaloniaEdit は **11.1.0 に固定**している。11.4.x へ上げると、Graft側のコードを一切変えなくても同テストが中央値3.2倍（しきい値3.0倍）で失敗することを実測で確認済み。上げる場合は性能を再測定すること。

## 作業の進め方

- 取り返しのつかない操作（force push、リポジトリの公開、履歴の書き換え、本番反映）の前には必ず確認を取る。
- 要件が曖昧・複数解釈できる場合は、推測で大きく進めず確認する。些細な既定値は妥当な選択をして進め、その旨を伝える。
- 可能ならアプリを実際に起動し、スクリーンショットやスモークテストで期待どおり動くことを確認してから「完了」とする。

## バックグラウンド実行についての注意

この開発環境には**「バックグラウンド完了通知」の仕組みは存在しない**。ビルド・テスト・アプリ起動は必ずフォアグラウンドで実行し、出力を自分で読んで判断すること。通知を待って停止しないこと。

## 利用者へコマンドを提示するときの決まり

Windows実機で実行してもらうコマンドを提示するときは、**必ず先頭に `cd` を書く**こと。
利用者が別のフォルダにいる状態で貼り付けても、そのまま動くようにするため。

```powershell
cd C:\Users\YUGO\Graft
git checkout main
git pull origin main
powershell -ExecutionPolicy Bypass -File tools\New-Release.ps1
```

コードブロックを分けた場合は、**分けたブロックそれぞれに `cd` を書く**（利用者が片方だけコピーすることがあるため）。

```powershell
cd C:\Users\YUGO\Graft
git tag -a v1.0.0 -m "Graft 1.0.0"
git push origin v1.0.0
```

リポジトリの場所は `C:\Users\YUGO\Graft`。

## リリース

- 手順は `docs/リリース手順.md`。リリースのタイトルはタグと同じ表記（例: `v1.0.20`）にする。
- **説明は README、リリースは変更点だけ。** リリース本文の `##` 見出しは `## 変更点` と
  `## ダウンロード`（ファイル・サイズ・SHA256 の表）の2つだけ。その版の変更の小見出しは `###` 以下。
  本文に「# Graft 1.0.20」のような題名を書かない。最後に次の1行を置く。
  `インストールと更新の方法は [README](https://github.com/Yu5rin/Graft#インストール) をご覧ください。`
- インストール・更新方法・主な機能・動作環境・外部との通信・既知の制限など、版をまたいで変わらない
  説明は README に書き、リリース本文には繰り返さない。README には現在の事実だけを書く。
  その版に上げるときだけ必要な注意（手で入れ替える手順など）は、変更点の中に `###` で残す。
- 本文の雛形は `docs/リリース説明_テンプレート.md`（`tools/New-Release.ps1` が `{CHANGES}` に
  `docs/変更履歴.md` の節を、`{DOWNLOADS}` に表を差し込む）。`.github/workflows/release.yml` も同じ形で組み立てる。
- **自動更新とリリース本文の関係**: 自動更新（`src/Graft/Core/Update/`）はリリース本文（`body`）も
  タイトル（`name`）も読まない。API からは `tag_name`・`html_url`・`prerelease`・`assets`
  （`name`・`browser_download_url`・`size`・`digest`）だけを、Atom フィードからは各エントリのリンク先
  （タグ）だけを読む。したがって本文の形は自由に変えてよい。守るべきなのは次の2点。
  - タグは `UpdateVersion` で解釈できる `v1.2.3` の形にする
  - Windows 版の添付ファイル名は `Graft-<タグから v を除いた版>-win-x64.zip` にする
    （API に届かないときは、この規則でダウンロード URL を組み立てる。`UpdateAtomFeedLogic.BuildWindowsAssetFileName` 参照）
