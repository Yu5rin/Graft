# Graft

対話型AIが出力したコードを、ローカルのプロジェクトファイルへ安全に接ぎ木する Windows アプリケーション。

AIチャットで得たコードをコピー＆ペースト1回で反映し、変更履歴をリビジョンとして蓄積する。
ファイル全文形式と search/replace 形式の両方を解釈し、適用前に必ずバックアップを取得する。

## 設計目標

1. **トークン消費の最小化** — search/replace 形式を第一級の入力として扱う
2. **変更の追跡可能性** — 適用単位をリビジョンとして記録する
3. **製品相当の完成度** — UI・エラー処理・異常系からの復帰を市販ソフトの水準で作り込む

## 動作環境

- Windows 10 (21H2 以降) / Windows 11、x64
- .NET 8 / WPF、self-contained の単一 exe
- ランタイム事前導入・インストーラ・管理者権限・レジストリ書き込みは不要
- ネットワーク通信は一切行わない

## 構成

```
Graft.sln
├── src/Graft/            WPF アプリ本体
│   ├── Core/             パーサ、マッチング、適用、バックアップ、レキサ（UI非依存）
│   ├── Features/         プロジェクト管理、コンテキスト収集、フック、ホットキー（UI非依存）
│   ├── Infra/            設定、ログ（UI非依存）
│   ├── ViewModels/       自前の軽量 MVVM
│   ├── Views/            画面
│   ├── Themes/           カラートークン、シンタックス配色、SVGアイコン、ロゴ
│   └── Assets/           アプリアイコン、同梱ライセンス
├── tests/Graft.Tests/    単体テスト（xUnit + FluentAssertions）
└── tools/                アイコン生成スクリプト
```

`Core/` `Features/` `Infra/` は WPF を参照しない。テストプロジェクトはこれらのソースを
直接取り込んで `net8.0` としてビルドするため、UI 非依存が機械的に保証される。

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
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## アイコン

アプリアイコンはベクター定義（`src/Graft/Themes/Logo.xaml`）を正とし、
`tools/generate-icon.py` が同じ定義から `src/Graft/Assets/Graft.ico` を生成する。
UI 内のアイコンとトレイアイコンはすべてベクターで描画する。

## 使用ライブラリ

- [DiffPlex](https://github.com/mmanela/diffplex) — Apache License 2.0（全文を `src/Graft/Assets/DiffPlex-LICENSE.txt` に同梱）
