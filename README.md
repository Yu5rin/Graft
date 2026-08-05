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
- .NET 8 / WPF、self-contained の単一 exe（約142MB、ランタイム事前導入は不要）
- インストーラ・管理者権限・レジストリ書き込みは不要
- ネットワーク通信は一切行わない

Linux 対応（Avalonia UI への移行）を計画中。`docs/仕様書_Graft_v2.1.md` を参照。

## 構成

```
Graft.sln
├── src/Graft/
│   ├── Core/       パーサ、マッチング、適用、バックアップ、レキサ（UI非依存）
│   ├── Features/   プロジェクト管理、収集、横断検索、フック、Git（UI非依存）
│   ├── Infra/      設定、ログ（UI非依存）
│   ├── Editor/     AvalonEdit によるエディタ層
│   ├── ViewModels/ 自前の軽量 MVVM
│   ├── Views/      画面
│   ├── Themes/     カラートークン、シンタックス配色、SVGアイコン、ロゴ
│   └── Assets/     アプリアイコン、同梱ライセンス
├── tests/Graft.Tests/   単体テスト（xUnit + FluentAssertions）
├── docs/                仕様書
└── tools/               アイコン生成スクリプト
```

`Core/` `Features/` `Infra/` は UI フレームワークを参照しない。テストプロジェクトがこれらの
ソースを直接取り込んで `net8.0` としてビルドするため、UI 非依存が機械的に保証される。

## ビルド

```
dotnet build Graft.sln
dotnet test tests/Graft.Tests
```

Windows 以外のホストでもビルド検証できるよう `EnableWindowsTargeting` を有効にしている
（実行は Windows のみ）。

## 発行

```
dotnet publish src/Graft -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

`publish\Graft.exe` が単一ファイルとして生成される。実行ファイルと同じ階層に
`settings.json` / `projects.json` / `back/` / `logs/` を作るため、専用フォルダへ置くこと。

## 仕様書

| 版 | 内容 |
|---|---|
| `docs/仕様書_Graft_v2.0.md` | 現行実装の基準。プロジェクト管理・コードエディタ・接ぎ木 |
| `docs/仕様書_Graft_v2.1.md` | Linux 対応（Avalonia 移行）の計画 |

## アイコン

アプリアイコンはベクター定義（`src/Graft/Themes/Logo.xaml`）を正とし、
`tools/generate-icon.py` が同じ定義から `src/Graft/Assets/Graft.ico` を生成する。
UI 内のアイコンとトレイアイコンはすべてベクターで描画する。

## 使用ライブラリ

- [AvalonEdit](https://github.com/icsharpcode/AvalonEdit) — MIT License
- [DiffPlex](https://github.com/mmanela/diffplex) — Apache License 2.0
- System.Text.Encoding.CodePages — MIT License（Shift_JIS 対応）

ライセンス全文は実行ファイルに埋め込み、バージョン情報画面から参照できる。
