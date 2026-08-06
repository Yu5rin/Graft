# Graft

プロジェクト管理を中核に、コードエディタと、対話型AIが出力したコードの安全な「接ぎ木」を統合したデスクトップ開発ツール。

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
- .NET 8 / Avalonia UI、self-contained の単一実行ファイル（約120MB、ランタイム事前導入は不要）
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
dotnet publish src/Graft -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/win

# Linux 向け
dotnet publish src/Graft -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/linux
```

単一ファイルとして生成される。実行ファイルと同じ階層に
`settings.json` / `projects.json` / `back/` / `logs/` を作るため、専用フォルダへ置くこと。

起動時の JIT を減らすため ReadyToRun を有効にしている（Linux 実測で約1.85秒 → 約0.71秒）。

## 仕様書

| 版 | 内容 |
|---|---|
| `docs/仕様書_Graft_v2.0.md` | 機能の基準。プロジェクト管理・コードエディタ・接ぎ木 |
| `docs/仕様書_Graft_v2.1.md` | 現行実装の基準。Linux 対応と Avalonia 構成 |

## アイコン

アプリアイコンはベクター定義（`src/Graft/Themes/Logo.axaml`）を正とし、
`tools/generate-icon.py` が同じ定義から `src/Graft/Assets/Graft.ico` を生成する。
UI 内のアイコンとトレイアイコンはすべてベクターで描画する。

## 使用ライブラリ

- [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) — MIT License
- [AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit) — MIT License
- [DiffPlex](https://github.com/mmanela/diffplex) — Apache License 2.0
- System.Text.Encoding.CodePages — MIT License（Shift_JIS 対応）

ライセンス全文は実行ファイルに埋め込み、バージョン情報画面から参照できる。
