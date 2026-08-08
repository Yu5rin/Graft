# Graft

プロジェクト管理を中核に、コードエディタと、対話型AIが出力したコードの安全な「接ぎ木」を統合したデスクトップ開発ツール。

> 版の数え方: 配布物の版は **v1.0.0**（初回リリース）。
> `docs/` にある「仕様書 v2.1」は仕様書自体の版で、配布物の版とは別に数える。

AIチャットで得たコードをコピー＆ペースト1回で反映し、変更履歴をリビジョンとして蓄積する。
ファイル全文形式と search/replace 形式の両方を解釈し、適用前に必ずバックアップを取得する。

## 3本柱

1. **プロジェクト管理** — 複数プロジェクトの登録・切替・状態管理
2. **コードエディタ** — タブ、エクスプローラ、検索置換、横断検索、折りたたみ、Gitガター
3. **接ぎ木** — AI出力のパッチを安全に適用し、リビジョンとして蓄積する

## 設計目標

- **トークン消費の最小化** — search/replace 形式を第一級の入力として扱う
- **変更の追跡可能性** — 適用単位をリビジョンとして記録する
- **製品相当の完成度** — UI・エラー処理・異常系からの復帰を市販ソフトの水準で作り込む

## 動作環境

- Windows 10 (21H2 以降) / Windows 11、x64
- Linux（X11 / Wayland）、x64。glibc ベースのディストリビューション
- .NET 8 / Avalonia UI、self-contained の単一ファイル形式（実行ファイル＋ネイティブDLL3つで約121MB、ランタイム事前導入は不要）
- インストーラ・管理者権限・レジストリ書き込みは不要
- ネットワーク通信は一切行わない

OS 固有機能（トレイ常駐・グローバルホットキー・クリップボード監視・ごみ箱・
ファイルマネージャ連携・システムテーマ追従・多重起動防止）は `Platform/` の抽象越しに扱い、
利用できない環境ではその機能だけを無効にして動作を続ける。
Wayland ではグローバルホットキーが使えないため、起動時にその旨を通知する。

## 構成

```
Graft.sln
├── src/Graft/
│   ├── Core/       パーサ、マッチング、適用、バックアップ、レキサ（UI非依存）
│   ├── Features/   プロジェクト管理、収集、横断検索、フック、Git（UI非依存）
│   ├── Infra/      設定、ログ（UI非依存）
│   ├── Platform/   OS 固有機能の抽象と実装（Windows / Linux / Null）
│   ├── Editor/     AvaloniaEdit によるエディタ層
│   ├── ViewModels/ 自前の軽量 MVVM
│   ├── Views/      画面
│   ├── Themes/     カラートークン、シンタックス配色、SVGアイコン、ロゴ
│   └── Assets/     アプリアイコン、同梱ライセンス
├── tests/Graft.Tests/   単体テスト（xUnit + FluentAssertions）
├── tests/Graft.UiTests/ Avalonia headless による UI テスト（画面なしで実描画）
├── docs/                仕様書
└── tools/               アイコン生成スクリプト
```

`Core/` `Features/` `Infra/` `Platform/`（抽象と Null 実装）は UI フレームワークを参照しない。
テストプロジェクトがこれらのソースを直接取り込んで `net8.0` としてビルドするため、
UI 非依存が機械的に保証される。

## ビルド

```
dotnet build Graft.sln
dotnet test Graft.sln
```

Windows / Linux のどちらのホストでもビルドとテストが完結する。UI テストは Avalonia の
headless 環境で実際に描画するため、画面のない CI 上でもレイアウト崩れとリソース解決の
失敗を検出できる。

## 発行

```
# Windows 向け
dotnet publish src/Graft -c Release -r win-x64 --self-contained true -o publish/win

# Linux 向け
dotnet publish src/Graft -c Release -r linux-x64 --self-contained true -o publish/linux
```

発行物は `Graft.exe`（Linuxは `Graft`）とネイティブDLL3つの**計4ファイル**になる
（単一ファイル形式・自己展開なし。`PublishSingleFile` 等はcsproj側の既定値なので、
上記コマンドに `-p:` の追加指定は不要）。**exeファイルだけを取り出して配置しないこと**。
同梱の3つのDLLがないと `FileNotFoundException` で起動できない。実行ファイルと同じ階層に
`settings.json` / `projects.json` / `back/` / `logs/` を作るため、4ファイルをまとめて
専用の場所へ置くこと。

起動時の JIT を減らすため ReadyToRun を有効にしている（Linux 実測で約1.85秒 → 約0.72秒）。

以前は単一ファイル（`PublishSingleFile`）として発行していたが、`IncludeNativeLibrariesForSelfExtract`
（ネイティブDLLを実行時に一時フォルダへ自己展開する設定）が true だったために初回起動が
約5.44秒かかり、いったんフォルダ形式へ移行した。しかしフォルダ形式は発行物が216ファイル・
約109MBに散らばり、依存DLLを子フォルダへまとめようとしても `Graft.deps.json` が期待する
NuGetパッケージ相対パス（`lib/net8.0/Avalonia.Base.dll`）が崩れて起動不能になるなど、
整理のしようがなかった。そこで今回、`IncludeNativeLibrariesForSelfExtract` を明示的に
**false** にしたまま単一ファイル形式へ戻した。false であれば実行時展開が発生せず
（過去の5.44秒はここが原因だった）、管理コードもバンドルから直接読まれるため遅くならない。
実際、Linux実測（同一条件）でフォルダ形式 798〜1145ms に対し単一ファイル形式は
622〜637msと、むしろ高速だった。発行物も216ファイルから4ファイルに減っている。

## 仕様書

| 版 | 内容 |
|---|---|
| `docs/仕様書_Graft_v2.0.md` | 機能の基準。プロジェクト管理・コードエディタ・接ぎ木 |
| `docs/仕様書_Graft_v2.1.md` | 現行実装の基準。Linux 対応と Avalonia 構成 |

## Windows実機での検証

開発・自動テストはLinux上で行っているため、Windows固有機能（自動起動・クリップボード監視・
ごみ箱等）は実機での確認が別途必要になる。手順は `docs/Windows実機検証手順.md`、検証用
サンプルの生成スクリプトは `tools/New-WindowsTestSample.ps1` を参照。

## アイコン

アプリアイコンはベクター定義（`src/Graft/Themes/Logo.axaml`）を正とし、
`tools/generate-icon.py` が同じ定義から `src/Graft/Assets/Graft.ico` を生成する。
UI 内のアイコンとトレイアイコンはすべてベクターで描画する。

## ライセンス

本ソフトウェア自体は MIT License で公開している。

```
Copyright (c) 2026 YUGO
```

全文はリポジトリ直下の [`LICENSE`](./LICENSE) を参照。

## 使用ライブラリのライセンス

Graft が依存する各ライブラリは、それぞれ以下のライセンスの下で配布されている
（上記の「本ソフトウェアのライセンス」とは別）。

- [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) — MIT License
- [AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit) — MIT License
- [DiffPlex](https://github.com/mmanela/diffplex) — Apache License 2.0
- System.Text.Encoding.CodePages — MIT License（Shift_JIS 対応）

ライセンス全文は実行ファイルに埋め込み、バージョン情報画面から参照できる。
