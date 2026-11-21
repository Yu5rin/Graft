# Graft VBA連携 仕様書・実装指示（v0.2）

この文書は、Graft に「Excel VBA のコードを AI の search/replace 出力で直接書き換える」機能を追加するための仕様と実装指示である。実装は `CLAUDE.md` の流儀（日本語コメントで「なぜ」を厚く書く、警告ゼロ、`Graft.Tests` と `Graft.UiTests` の全件成功を確認してから完了報告）に従うこと。

v0.1 をコードと突き合わせてレビューし（指摘26件）、利用者が判断を仰ぐ10論点をすべて決めたうえで、v0.2 に改めた。コードの名前はファイル名とメソッド名で示す（行番号は変わるので書かない）。**COM と VBE の挙動は Linux の CI で確かめられない。「実機で要確認」と書いた事項は推測であり、実装者は Windows 実機で確かめてから仕様を確定する（付録B）。**

---

## v0.1 からの主な変更点

| 区分 | v0.1 | v0.2 |
|---|---|---|
| 同期の基準 | 本文ハッシュ1本（`baseHash`）。Push 後に VBE の整形差をミラーへ書き戻す | モジュールごとに `mirrorHash`（ミラー側）と `excelHash`（Excel 側）の2本（さらに大文字小文字・空白無視の `excelLooseHash`）。**ミラーは Push 後も書き換えない**。書き戻すと履歴の `HashAfter` と食い違い、取り消しが毎回 E301 になるため（4.3、5.5、6.3、6.5） |
| 末尾改行 | 「末尾の空行を削除」と曖昧 | ハッシュは末尾の CR/LF をすべて除いて計算。ミラーのファイル自体の末尾改行は変えない（6.3） |
| 削除・名前変更 | 「次の Pull でミラーへ戻す」（二重定義などで壊れる） | 解析結果に DELETE／RENAME があれば適用の前に画面側で止める（E814、7.3）。ミラーから消えたモジュールは「削除保留」にして自動 Pull で復活させない（6.5） |
| 書き込み方式 | 全文置き換え。途中失敗でモジュールが空になりうる | 全文置き換え＋控えの書き戻し。戻せなければ E809。プロシージャ属性の消失は実機検証項目にし、結果次第で行範囲方式へ切り替える（5.5） |
| ミラーの既定位置 | `vba-mirrors/<プロジェクトId>/` | `vba-mirrors/<ブック名>-<パスの短いハッシュ>/`。`AppPaths`・`DataDirectory` の移行対象に加え、保存先の移行・場所の変更・削除での扱いを定義（4.2、4.4） |
| 手編集 | 反映の契機が適用・取り消し・復元だけ | 手動の「Excel へ反映」を新設（7.2b）。保存時の自動 Push はしない。次の適用の Push には Push 判定のモジュールも同梱 |
| 拡張子 | 触れていない | VBA プロジェクトに限り `.bas/.cls/.frm` を実効の許可リストへ自動で加える（4.1）。全体の既定は変えない |
| ホットキー | `Vba.PullHotkey` | 初版から削除（再登録処理の作り直しが大きいため） |
| OneDrive／SharePoint | 未記載 | 非対応。選択画面で理由つきで無効（5.2、7.1） |
| COM | STA スレッド＋再試行のみ | 専用スレッド＋呼び出し全体の時間切れ（E805、スレッドは作り直す）。書き込み前に `VBProject.Mode` を確かめ、デザインモード以外は E813。Push 中は中央ペインを処理中表示にし、他の操作を受け付けない。STA か MTA かは実機で決める（5.3、5.5、7.3） |
| Push 失敗時 | 「取り消し」を促し、`History.UndoLatestAsync` を使う | 既定は「ファイルの変更は残す（未反映の印）」。取り消しは選択肢で、`MainViewModel.Hooks.cs` の巻き戻しと同じ作り（rN を名指しで `RevisionRestorer.RestoreAsync(force: true)`）。`UndoLatestAsync` は使わない（7.3） |
| 取り消し・復元後の Push | `HistoryPaneViewModel` の復元完了後 | 待てるフック `AfterRestoreAsync` を足し、3経路（単発の復元、「ここまで戻す」、適用後フック失敗の巻き戻し）すべてで Push（7.4） |
| 差し込み位置 | `ConfirmTargetsSavedAsync` の直後（`MainViewModel.Apply.cs` と記載） | 呼び出し元は `MainViewModel.cs` の `RunDryRunCoreAsync`。解析後の再検査点を `SaveTargetsAndReanalyzeAsync` の直後に追加。Push は `History.LoadAsync` の後・`rolledBack` の判定の前（7.3） |
| 変更しないコア | 「適用エンジンのコアは変更しない」の一言 | 手を入れる箇所を列挙し、変えないものを正確に書き直した（1.2、13） |
| IVbaBridge | 全体で1つの結果、検証をブリッジが担う | モジュール別の結果、検証は Core の `VbaWriteVerifier`、名前での絞り込み、注入方法（5.1、3.3） |
| 実在しない UI | 「＋」メニュー、`ProjectOverrides` の編集画面、プロジェクト別拡張子上書き、3つ目のホットキー枠 | 実在する部品に合わせて書き直した（7.1、7.6、8） |
| 設定 | `Vba` セクションの追加のみ | `SettingsViewModel.BuildSettingsFromFields` と読み込み側にも足す（足さないと設定保存のたびに Vba が既定へ戻る）。`Vba.CallTimeoutMs` を追加、`PullHotkey` を削除（8） |
| エラーコード | E801〜E812 | E801〜E814（E813 マクロ実行中、E814 削除・名前変更の適用不可を新設）。E706 の文言を実際の「この環境では使えない機能です」に訂正（9） |
| テスト | 7項目 | 取り消し後 Push の3経路、削除保留、保存先移行、2プロジェクトでの自動判定、設定保存、時間切れなどを追加（10） |

---

## 1. 目的と基本方針

### 1.1 目的

Excel ブック（.xlsm / .xlsb / .xlam）の VBA モジュールを、Graft の既存の流れ（コンテキスト収集 → AI へ依頼 → 貼り付けて解析 → 差分プレビュー → 適用 → 履歴・取り消し）でそのまま編集できるようにする。

### 1.2 基本方針（最重要）と、変えるもの・変えないもの

- **適用エンジンの本体は変更しない。** ディスク上のファイルを前提にしたまま使う。
- VBA プロジェクトの実体は、Graft が管理する**ミラーフォルダ**（モジュールごとのテキストファイル）とする。Graft から見れば、普通のファイルプロジェクトと区別がつかない。
- Excel との同期は、ミラーフォルダと Excel の間で行う。
  - **取込（Pull）**: Excel → ミラー
  - **反映（Push）**: ミラー → Excel
- 同期処理は、適用・取り消し・復元の**前後**に差し込む。適用処理（`ApplyEngine`）の中には入れない。
- COM を使う部分は Windows 専用とし、`Platform/Windows` に閉じ込める。Linux と未対応環境では `Platform/Null` の実装が「未対応」を返し、UI から機能ごと見えなくする。
- 判定や変換のロジックは `Core/Vba/` に純ロジックとして置き、`Graft.Tests` でテストする。

**変更しないもの**（挙動にも API にも手を入れない。呼び出して使うだけ）

| 対象 | 使い方 |
|---|---|
| `ApplyEngine`（`CheckSelection`、検証、書き込み） | 適用はミラー上のファイルに普通に行う |
| `PathGuard` | ミラーを普通のプロジェクトルートとして検査する。保存先配下のミラーを拒否しないことをテストで固定する（10.1） |
| `SafeFileWriter`、`FileTextIO` | Pull のミラー書き込み、Push 時のミラー読み取りに使う |
| `RevisionStore`、`BackupManager`、`RevisionIndex` | 履歴・バックアップはミラーに対して従来どおり記録される |
| `EncodingDetector` | ミラーの読み込みに使う（UTF-8 を先に判定するので誤判定の心配は小さい） |
| `RevisionRestorer` 本体 | 取り消し・復元の実行に使う。呼び出し側（ViewModel）に待てるフックを足すだけ |
| `DryRunPlanner`、`BlockResolver` | 解析はそのまま。`BlockPlan.AfterText` を画面側で検査する（6.4） |

**手を入れるもの**（必要最小限。13章でも繰り返す）

| 対象 | 変更の内容 |
|---|---|
| `MainViewModel.Apply.cs`（`ApplyCoreAsync`） | `rolledBack` を Push 失敗の取り消しでも立てる構造にする。解析後の再検査点、Push の差し込み、適用中の旗の扱い（7.3） |
| `MainViewModel.cs`（`RunDryRunCoreAsync`、書き込み用の許可拡張子の組み立て、`OnRevisionRestored`） | 解析前の同期、DELETE／RENAME／E811 の検査、実効の許可拡張子（4.1） |
| `MainViewModel.Hooks.cs`（`RollbackAfterHookFailureAsync`） | 巻き戻し成功後に `AfterRestoreAsync` と同じ Push を呼ぶ（7.4）。Push 失敗の取り消しもこれと同じ作りで別メソッドを足す |
| `MainViewModel.ApplyUndoNotice.cs`（`ShowApplyUndoNotice`） | 「Excel へ反映済み」を付け足せるようにする |
| `HistoryPaneViewModel` | 待てるフック `AfterRestoreAsync` を足す（7.4） |
| `ContextCollectViewModel`、`MainViewModel.Prompt.cs` | `InitializeAsync`／`RefreshAsync` の走査の前に Pull。生成箇所で同期サービスを渡す（7.2） |
| `ProjectMatcher`（`BuildOutcome`） | 一致率が同率のとき現在のプロジェクトを優先する（7.3） |
| `ProjectStore`（`RelocateAsync`、`RemoveAsync`）、`ProjectModels.cs` | 同期状態ファイルの付け替え・削除、`Project.Vba` の追加（4.4） |
| `AppPaths`、`DataDirectory`（`KnownDirectoryPaths` と移行処理） | `VbaMirrorsDirectory`／`VbaSyncDirectory` の追加、移行対象への追加、移行後の Root の付け替え（4.4） |
| `Settings`、`SettingsStore`、`SettingsViewModel` | `Vba` セクションの追加、読み込みと保存（8） |
| `ErrorCodes.cs` | E801〜E814 の追加（9） |
| `QuickOpenFileEnumerator`、`FileTreeService` | 実効の許可拡張子（4.1） |
| `ProjectPaneViewModel`、`ProjectPane.axaml` | 「Excel ブックから追加」ボタン、右クリックメニュー、バッジ（7.1、7.6） |
| `Platform` の各 `*PlatformServices`、`IPlatformServices.cs` | `IVbaBridge Vba` の追加と `DescribeEnvironment` の列挙（3.1） |

### 1.3 対象と非対象

| 対象 | 非対象（この版ではやらない） |
|---|---|
| 標準モジュール、クラスモジュール、ドキュメントモジュール（ThisWorkbook / 各シート）、ユーザーフォームの**コード部分** | ユーザーフォームのデザイン（コントロール配置、.frx） |
| 保存済みブック（フルパスを持つもの） | 未保存の新規ブック（Book1 など）。**OneDrive／SharePoint 上のブック**（`FullName` が URL になるもの） |
| 起動中の Excel（複数プロセス可） | Excel が起動していない状態での読み書き、Word / Access など他の Office アプリ |
| 標準モジュール、クラスモジュールの新規作成 | ドキュメントモジュール、フォームの新規作成。**モジュールの削除と名前変更の自動反映**（AI の DELETE／RENAME は適用の前に止める） |
| | 参照設定（References）の編集、VBA プロジェクトのパスワード解除 |
| | Excel 側のモジュールの削除（新規モジュールを含む適用を取り消しても、Excel 側のモジュールは残る） |

### 1.4 前提環境

- 日本語版 Windows を前提にする。VBE の文字コードは CP932 固定ではなく**OS の ANSI コードページ**に従う見込みで（実機で要確認）、検査は CP932 で行う。日本語以外のロケールの Windows では保証しない。
- Graft（x64）から 32bit の Excel を操作しても問題はない（5.2、3.2）。

---

## 2. 用語

| 用語 | 意味 |
|---|---|
| VBA プロジェクト | Graft のプロジェクトのうち、Excel ブックと紐付いたもの（`Project.Vba` が非 null） |
| ミラーフォルダ | VBA プロジェクトの `Project.Root`。モジュールの本文をテキストで保持する |
| モジュール本文 | `CodeModule.Lines(1, CountOfLines)` で得られるテキスト。VBE の画面に見えている内容そのもので、エクスポート時のヘッダ（`VERSION`、`BEGIN`〜`END`、`Attribute VB_Name` など）は含まない |
| 同期基準 | 最後に Pull または Push が成功した時点の、モジュールごとの `mirrorHash`（ミラー側）と `excelHash`（Excel 側）。三方比較の基準になる |
| 整形差 | VBE が書き込み時に自動で直すキーワードの大文字小文字・空白の違い。VBE は識別子の大文字小文字を**プロジェクト全体で**揃える見込み（実機で要確認） |
| Push 判定 | 6.5 で「ミラー側だけが変わった」と判定されたモジュールの状態。「未反映」とも呼ぶ |
| 削除保留 | ミラーのファイルが無くなったが、Excel 側にはモジュールが残っている状態（6.5） |
| 控え | Push の直前に読み取った、Excel 側の現在の本文。書き込み失敗時の書き戻しに使う（5.5） |

---

## 3. 全体構成

### 3.1 追加・変更するファイル

```
src/Graft/
  Core/Vba/
    VbaModuleKind.cs          モジュール種別（Standard / Class / Document / Form）と、COM の Type 値との対応
    VbaMirrorLayout.cs        ミラーのパス ⇔ (種別, コンポーネント名) の相互変換
    VbaTextNormalizer.cs      改行コードと末尾改行の正規化、ハッシュ計算（厳密ハッシュと大文字小文字・空白無視のハッシュ）
    VbaCodePageGuard.cs       CP932 で表現できない文字の検出（解析時・Push 前）
    VbaNameRules.cs           コンポーネント名の妥当性検査、Windows の予約名
    VbaSyncPlanner.cs         三方比較による同期計画（何もしない / Pull / Push / 衝突 / 削除保留 ほか）
    VbaSyncModels.cs          計画・結果・状態のレコード群
    VbaWriteVerifier.cs       書き込み後の読み直し本文と期待値の比較（一致 / 整形差 / 別内容）
    VbaApplyGuard.cs          解析結果から DELETE／RENAME（E814）と CP932 違反（E811）を拾う純ロジック
    VbaExtensionPolicy.cs     VBA プロジェクトの実効の許可拡張子（.bas/.cls/.frm を加える）
    VbaMirrorFolderName.cs    ミラーの既定フォルダ名（ブック名＋パスの短いハッシュ）と、保存先移行時の Root の付け替え計算
  Features/
    VbaSyncService.cs         Planner と Bridge を組み合わせて Pull / Push を実行する
    VbaSyncStateStore.cs      同期基準の保存と読み込み（JsonFileStore を利用）、Id 変更時の付け替え
    ProjectModels.cs          [変更] Project に Vba プロパティを追加
    ProjectStore.cs           [変更] RelocateAsync で同期状態ファイルを付け替え、RemoveAsync で削除の選択肢を受ける
    ProjectMatcher.cs         [変更] 同率のとき現在のプロジェクトを優先
    QuickOpenFileEnumerator.cs / FileTreeService.cs   [変更] 実効の許可拡張子
  Infra/
    AppPaths.cs               [変更] VbaMirrorsDirectory / VbaSyncDirectory / GetVbaSyncFilePath を追加
    DataDirectory.cs          [変更] KnownDirectoryPaths に上記2つを追加。移行時の Root 付け替え（4.4）
    Settings.cs               [変更] Vba 設定セクション（VbaSettings）を追加
    SettingsStore.cs          [変更] Vba の null 置き換えと範囲検証（NormalizeIntRange）
  Platform/
    IVbaBridge.cs             COM 層の抽象（IPlatformService を継承）
    IPlatformServices.cs      [変更] IVbaBridge Vba { get; } を追加。DescribeEnvironment の列挙に含める
    Windows/WindowsVbaBridge.cs
    Windows/ComRunningObjectTable.cs   ROT の列挙（P/Invoke）
    Windows/ComRetry.cs                RPC_E_CALL_REJECTED などの再試行
    Windows/ComWorker.cs               COM 呼び出しを専用スレッドで実行し、全体の時間切れで諦める（STA か MTA かは実機で決める）
    Windows/WindowsPlatformServices.cs [変更]
    Null/NullVbaBridge.cs
    Null/NullPlatformServices.cs       [変更]
    Linux/LinuxPlatformServices.cs     [変更]（NullVbaBridge を返す）
  ViewModels/
    MainViewModel.Vba.cs      適用・取り消しとの連携、Push 失敗時の選択、解析前後の同期
    MainViewModel.cs / MainViewModel.Apply.cs / MainViewModel.Hooks.cs / MainViewModel.ApplyUndoNotice.cs / MainViewModel.Prompt.cs   [変更]（1.2 の表）
    HistoryPaneViewModel.cs   [変更] AfterRestoreAsync フック
    ContextCollectViewModel.cs [変更] 走査の前に Pull
    ProjectPaneViewModel.cs   [変更] 「Excel ブックから追加」、右クリックメニュー、バッジ、削除の確認
    SettingsViewModel.cs      [変更] Vba 設定の読み込みと BuildSettingsFromFields への追加
    VbaWorkbookPickerViewModel.cs
  Views/
    VbaWorkbookPickerWindow.axaml(.cs)
    VbaConflictWindow.axaml(.cs)
    SettingsPanels/VbaSettingsView.axaml(.cs)
    ProjectPane.axaml        [変更]
  Core/ErrorCodes.cs          [変更] E801〜E814 を追加
tests/Graft.Tests/Vba/        純ロジックのテスト
tests/Graft.UiTests/Vba/      FakeVbaBridge を使った画面とフローのテスト
docs/                         仕様書、取扱説明書、Windows実機検証手順、変更履歴に追記
```

### 3.2 依存関係

- Office の相互運用アセンブリ（PIA、`Microsoft.Office.Interop.Excel`、`Microsoft.Vbe.Interop`）は**参照しない**。COM は `dynamic` による遅延バインディングで呼ぶ。理由は、Office のバージョンと bitness に依存させず、Graft の単一ファイル配布と Linux ビルドを崩さないため。`Graft.csproj` は `BuiltInComInteropSupport=true` で、トリミングなしの単一ファイル発行なので、`dynamic` による COM 呼び出しはそのまま使える。
- Graft（x64）から 32bit の Excel を操作しても問題はない。Excel はアウトプロセス COM サーバーなので、bitness が違ってもプロセス間通信で動く。この点はコメントに書いておくこと。
- 新しい NuGet パッケージは追加しない。

### 3.3 注入方法（IVbaBridge の差し替え）

`PlatformServices.Current` は差し替え口のない static で、ViewModel が直接参照している。`FakeVbaBridge` を使えるよう、次の作法にする。

- `MainViewModel` に `IVbaBridge VbaBridge { get; set; }` を足す（`Logger` や `BeforeApplyAsync` と同じく、プロパティで渡す作法）。既定値は `PlatformServices.Current.Vba`。
- `VbaSyncService` は `MainViewModel` が `VbaBridge` から作って持つ。`ProjectPaneViewModel`、`ContextCollectViewModel`、`HistoryPaneViewModel` は `MainViewModel` 経由で `VbaSyncService` を受け取る（`ContextCollectViewModel` はコンストラクタ引数。生成箇所は `MainViewModel.Prompt.cs`）。
- UI テストは `MainViewModel.VbaBridge` に `FakeVbaBridge` を入れる。

---

## 4. データモデル

### 4.1 Project への追加

```csharp
public sealed record VbaLink
{
    /// <summary>紐付けたブックのフルパス（ROT の表示名と照合する。大文字小文字は区別しない）。</summary>
    public string WorkbookFullName { get; init; } = string.Empty;

    /// <summary>適用後に Excel へ自動反映するか。既定は true。</summary>
    public bool AutoPushAfterApply { get; init; } = true;

    /// <summary>反映後にブックを保存するか。既定は false（保存は利用者が Excel で行う）。</summary>
    public bool SaveWorkbookAfterPush { get; init; }
}

// Project に追加
public VbaLink? Vba { get; init; }
```

- `Vba` が null のプロジェクトは、従来どおりのファイルプロジェクトとして扱う。既存の `projects.json` は変更なしで読めること（後方互換のテストを書く。JSON は camelCase なのでキーは `vba`）。
- `Project.Root` はミラーフォルダを指す。
- `AutoPushAfterApply` と `SaveWorkbookAfterPush` は `ProjectOverrides` ではなく `VbaLink` の項目である。変更する画面は 7.6 のプロジェクト右クリックメニューのチェック項目とする。
- **実効の許可拡張子**: 書き込み・クイックオープン・エクスプローラ操作は `Settings.Safety.AllowedExtensions` をそのまま使っており、プロジェクト別の上書き（`ProjectOverrideResolver`）は配線されていない。既定値を増やしても保存済みの設定には届かない。そこで、**VBA プロジェクトに限り、実効の許可拡張子に `.bas`／`.cls`／`.frm` を自動で加える**。全体の既定とユーザーの設定は変えない。
  - 純ロジック `VbaExtensionPolicy.Effective(allowed, project)` を `Core/Vba` に置き、次の3箇所で使う（コンテキスト収集は `AllowedExtensions` を見ていないので対象外）。
    - 書き込み: `MainViewModel.cs` の `PathGuardOptions` の組み立て（`AllowedExtensions = _settings.Safety.AllowedExtensions` の箇所）
    - クイックオープン: `QuickOpenFileEnumerator`
    - エクスプローラ操作（新規・改名・削除の許可判定）: `FileTreeService`
  - `ExplorerFilterService` のコメントが指す一覧も同じ値であることを確かめる。

### 4.2 ミラーフォルダの場所と構造

既定の場所は `<データ保存先>/vba-mirrors/<ブック名>-<ブックフルパスの短いハッシュ>/`（`AppPaths.VbaMirrorsDirectory` の配下）。

- ブック名は拡張子を除き、ファイル名に使えない文字を `_` に置き換え、40 文字までに切る。短いハッシュは、ブックのフルパスを `Path.GetFullPath` で正規化して小文字にした文字列の SHA-256 の先頭 8 桁（16 進）。同名のブックが別の場所にあっても衝突せず、同じブックを再追加すれば同じフォルダになる。
- プロジェクトの Id は従来どおり `Root` から `ProjectStore.CreateId` で導く。Id はパスから決定的に決まるので、同じブックを後で再登録すると、履歴が自動的に復活する（既存の `RemoveAsync` の説明と同じ性質）。
- 作成ダイアログで任意のフォルダも選べる（Git 管理したい場合など）。共有ドライブ上のブックの隣に勝手にフォルダを作らないよう、既定はデータ保存先にする。
- ミラーをデータ保存先の配下に置いても登録できる（`ProjectStore` の登録検査と `PathGuard` が拒否するのは、ルートが保存先の**祖先**になる場合だけ）。この前提をテストで固定する（10.1）。

```
<ミラー>/
  Modules/<名前>.bas      標準モジュール        (vbext_ct_StdModule = 1)
  Classes/<名前>.cls      クラスモジュール      (vbext_ct_ClassModule = 2)
  Forms/<名前>.frm        フォームのコード部分  (vbext_ct_MSForm = 3)
  Documents/<名前>.cls    ドキュメントモジュール (vbext_ct_Document = 100)
```

- ファイルの中身は**モジュール本文だけ**にする。エクスポート形式のヘッダは入れない。
  - 理由1: AI が見るコードと VBE の画面を一致させ、search/replace の一致率を上げるため。
  - 理由2: Push をすべての種別で「CodeModule の全文置き換え」に統一でき、コンポーネントの削除と再インポートが不要になるため。これで `VB_PredeclaredId` などの**モジュール単位**の属性やフォームのデザインを壊さない。ただし**プロシージャ単位**の属性（`VB_UserMemId`、`VB_Description`、マクロのショートカットキーなど）は `CodeModule.Lines` に見えないため、全文置き換えで失われる可能性がある。実機で確かめ、結果次第で行範囲方式へ切り替える（5.5）。
- **文字コードと読み書き**:
  - Pull が新規に作るファイルは UTF-8（BOM なし）・CRLF とする。`Settings` の `NewFileEncoding`／`NewFileBom`／`NewFileNewLine` は**使わない**（Shift_JIS や BOM 付きになりうるため）。既存のファイルに書くときは、そのファイルの文字コード・改行・末尾改行の有無を保つ（`FileTextIO.ReadAsync` が返す `TextShape` をそのまま渡す）。ただし AI が新規作成したファイル（`DryRunPlanner` が全体設定で作る）は設定どおりの形になりうるので、Push の読み取りは必ず `FileTextIO.ReadAsync` で行う。
  - VBE が実際に使うのは OS の ANSI コードページ（日本語環境では CP932）だが、Graft 側では Unicode 文字列として扱い、CP932 で表せるかどうかは解析時と Push の直前に検査する（6.4）。
- ドキュメントモジュールのファイル名はコンポーネント名（`Sheet1`、`ThisWorkbook` など）にする。シートの表示名（「集計」など）ではない。表示名は補足情報として同期状態ファイルに持ち、UI のツールチップで見せる。
- ミラーのルートには、Graft 用の管理ファイルを**置かない**。管理情報は 4.3 のとおりデータ保存先に置く。ミラーの中に置くと、エクスプローラーやコンテキスト収集に混ざるため。

### 4.3 同期状態（VbaSyncStateStore）

保存先: `<データ保存先>/vba-sync/<プロジェクトId>.json`（`AppPaths.GetVbaSyncFilePath(projectId)`）

```json
{
  "version": 1,
  "workbookFullName": "C:\\Work\\計画.xlsm",
  "modules": {
    "Modules/Module1.bas": {
      "kind": "Standard", "component": "Module1",
      "mirrorHash": "sha256…", "excelHash": "sha256…", "excelLooseHash": "sha256…",
      "deletePending": false, "syncedAt": "2026-11-21T10:00:00+09:00"
    },
    "Documents/Sheet1.cls": {
      "kind": "Document", "component": "Sheet1", "caption": "集計",
      "mirrorHash": "…", "excelHash": "…", "excelLooseHash": "…", "deletePending": false, "syncedAt": "…"
    }
  }
}
```

- **同期基準は2本**。`mirrorHash` は「最後に同期したときのミラー側の本文」、`excelHash` は「そのときの Excel 側の本文」のハッシュ。VBE が整形して Excel 側の本文がミラーと違っても、それぞれの基準は別々に持つので、整形差は「変更」にならない。
  - 6.5 の三方比較は、**ミラー ↔ `mirrorHash`**、**Excel ↔ `excelHash`** で行う。
  - `excelLooseHash` は、Excel 側の本文から空白（スペースとタブ）をすべて除き、大文字小文字を無視した形のハッシュ。VBE がプロジェクト全体で揃える大文字小文字の差を「Excel 側の変更」と誤認しないために使う（6.5）。
- 各ハッシュは、正規化（6.3）した本文の SHA-256。
- `deletePending` は 6.5 の「削除保留」。
- 書き込みは既存の `JsonFileStore`（一時ファイル → 置き換え）で行う。
- この版で新設するファイルなので、移行処理は要らない。`version` は将来の形式変更に備える。

### 4.4 データ保存先の移行・場所の変更・削除

ミラーと同期状態はデータ保存先の配下にあるので、保存先の切り替え、プロジェクトの場所変更、削除で次のように扱う。

**`AppPaths` と `DataDirectory`**

- `AppPaths` に `VbaMirrorsDirectory`（`<BaseDirectory>/vba-mirrors`）と `VbaSyncDirectory`（`<BaseDirectory>/vba-sync`）を足す。
- `DataDirectory` の `KnownDirectoryPaths` にこの2つを加える。これで、保存先の移行（`DataDirectoryMigrator` のコピーと検証）の対象になり、移行後の旧保存先の掃除（`StartupCoordinator.DataDirectoryCleanup`）でも消える。`BuildDirectoryPairs` は元と先の並びが一致する前提なので、両方の列挙に同じ順で加えること。
- 掃除で消してよいのは、コピーと検証が済んだあとだけ（既存の流れのまま）。

**移行後の Root の付け替え**

- コピーされた先の `projects.json` には、旧保存先の絶対パスの `Root` が入っている。`Project.Root` が旧 `VbaMirrorsDirectory` の配下にあるプロジェクトは、新 `VbaMirrorsDirectory` の同じ相対位置へ付け替える。Id は Root から導くので**Id が変わる**（`ProjectStore.RelocateAsync` と同じ問題）。
- 付け替えは、移行処理（`MigrateAndSwitchPointer`）のうち、コピーと検証が成功し、ポインタファイルを書き換える**前**に、コピー先に対してだけ行う。失敗したら、移行全体の失敗として扱う（コピー先は既存の流れで片付けられ、旧保存先は無傷で残る）。
  1. コピー先の `projects.json` の対象プロジェクトの `Root` と `Id` を書き換える（計算は純ロジック `VbaMirrorFolderName` の付け替え関数）。
  2. コピー先の `back/<旧Id>` を `back/<新Id>` へ、`vba-sync/<旧Id>.json` を `vba-sync/<新Id>.json` へ移す。
  3. Id を持つほかのファイル（`queue.json`、`layout.json` など）に旧 Id が入っていないかを実装時に `grep` で確かめ、入っていれば同じ変換をかける。
- 利用者が自分で選んだ場所のミラー（旧 `VbaMirrorsDirectory` の外）は、移行しても動かないので、そのまま。

**場所の変更（`ProjectStore.RelocateAsync`）**

- Root の変更で Id が変わるとき、`back/<旧Id>` と同じ扱いで `vba-sync/<旧Id>.json` を `<新Id>.json` へ移す。`VbaSyncStateStore` に付け替えメソッドを持たせ、`RelocateAsync` から呼ぶ。新しい Root が別のフォルダで内容が違う場合は、次の同期で 6.5 の三方比較が働く（基準と食い違うので衝突またはPush判定になる）。

**削除（`ProjectStore.RemoveAsync` と `ProjectPaneViewModel`）**

- VBA プロジェクトの削除では、既存の「履歴も削除するか」に加えて、次を確認する。
  - 「ミラーフォルダと同期状態も削除する」: `Root` が `VbaMirrorsDirectory` の配下のときだけ選べる。ミラーは `ITrashService`（ごみ箱）へ送り、完全には消さない。同期状態ファイルは削除する。
  - 「登録だけ外す」: ミラーと同期状態を残す。同じブックを後で再追加すると、同じフォルダ・同じ Id になり、7.1 で「既存のミラーを使う（三方比較で続ける）」「作り直す」を選べる。
  - 利用者が自分で選んだ場所のミラーは、フォルダを**削除しない**（同期状態ファイルだけ確認の対象）。
- Excel 側のモジュールには何もしない。

---

## 5. COM 層（IVbaBridge）

### 5.1 インターフェース

```csharp
public interface IVbaBridge : IPlatformService
{
    /// <summary>起動中の全 Excel から、開いている保存済みブックを列挙する。選べないものは理由つきで返す。</summary>
    Task<GraftResult<IReadOnlyList<VbaWorkbookInfo>>> ListOpenWorkbooksAsync(CancellationToken ct = default);

    /// <summary>ブックのコンポーネントの種別・名前・本文を読む。names が null なら全件、指定があればその名前だけ。</summary>
    Task<GraftResult<IReadOnlyList<VbaComponentSnapshot>>> ReadComponentsAsync(
        string workbookFullName, IReadOnlyCollection<string>? names, CancellationToken ct = default);

    /// <summary>
    /// 指定したコンポーネントの本文を全文置き換えする（書き込み方式は 5.5。必要なら内部で行範囲方式に切り替える）。
    /// 存在しない標準/クラスモジュールは作成する。結果はモジュール別。
    /// 接続できない・VBA プロジェクトに触れない・デザインモードでない場合は、1件も書かずに全体を失敗にする。
    /// </summary>
    Task<GraftResult<IReadOnlyList<VbaWriteOutcome>>> WriteComponentsAsync(
        string workbookFullName, IReadOnlyList<VbaComponentWrite> writes, CancellationToken ct = default);

    /// <summary>ブックを保存する（SaveWorkbookAfterPush のときだけ呼ぶ）。</summary>
    Task<GraftResult<bool>> SaveWorkbookAsync(string workbookFullName, CancellationToken ct = default);

    /// <summary>VBE で指定モジュールのコードウィンドウを開き、指定行へ移動して前面化する。</summary>
    Task<GraftResult<bool>> ShowInVbeAsync(string workbookFullName, string componentName, int line, CancellationToken ct = default);

    /// <summary>ブックの Excel ウィンドウを前面化する。</summary>
    Task<GraftResult<bool>> ActivateWorkbookAsync(string workbookFullName, CancellationToken ct = default);
}

public sealed record VbaWorkbookInfo(
    string FullName, string Name, VbaWorkbookAvailability Availability, string? UnavailableReason, bool IsReadOnly);
public enum VbaWorkbookAvailability { Available, NoMacroFormat, Locked, AccessNotTrusted, UrlLocation, ProtectedView }
public sealed record VbaComponentSnapshot(string Name, VbaModuleKind Kind, string Code, string? Caption);
public sealed record VbaComponentWrite(string Name, VbaModuleKind Kind, string Code, bool CreateIfMissing);

/// <summary>モジュール別の書き込み結果。検証（一致/整形差/別内容）は Core の VbaWriteVerifier が行う。</summary>
public sealed record VbaWriteOutcome(
    string Name, VbaWriteStatus Status, string? ReadBackCode, GraftIssue? Issue);
public enum VbaWriteStatus { Written, Created, RolledBack, Failed }
```

- `VbaWriteStatus.RolledBack` は、書き込みに失敗し、控えを書き戻して元の状態に戻せたことを表す。戻せなかったときは `Failed` で `Issue` が E809。
- 5.5-4 の検証（正規化ハッシュの比較）は Core の役目。ブリッジは読み直した本文（`ReadBackCode`）を返すだけにし、判定は `VbaWriteVerifier` が行う。ただし「別内容なら控えを書き戻す」処理はブリッジの中にあるため、ブリッジは書き込み後に `VbaWriteVerifier`（Core）を呼んでよい。
- `IsProtected` を `VbaWorkbookInfo` から外し、`Availability` に集約した（ロック中は `Locked`）。

### 5.2 起動中ブックの取得（ROT）

- 保存済みのブックは、Running Object Table（ROT）にフルパスのファイルモニカで登録されている。`GetRunningObjectTable` で `IEnumMoniker` を列挙し、`GetDisplayName` がブックの拡張子（.xlsm / .xlsb / .xlam / .xls / .xlsx）で終わるものを候補にする。そのうえで `GetObject` から Workbook を取り出す。
- **`Marshal.BindToMoniker` は使わない。** ブックが開いていないとき、Excel を起動してファイルを開いてしまうため。紐付けたブックが ROT に見つからなければ E804 を返す。
- .NET 8 には `Marshal.GetActiveObject` がない。ROT を使えば複数の Excel プロセスにも対応できるので、`GetActiveObject("Excel.Application")` の P/Invoke ではなく ROT 列挙を採用する。
- パスの比較は、大文字小文字を区別せず、`Path.GetFullPath` で正規化してから行う。UNC パスとドライブ文字の違いは解決しない（同じブックを別パスで開いた場合は別物として扱う）。この制限は取扱説明書に書く。
- **OneDrive／SharePoint 上のブックは非対応。** 同期フォルダ上のブックは `Workbook.FullName` が `https://…` になる見込みで（実機で要確認）、`Path.GetFullPath` と拡張子判定の前提が崩れる。`FullName` が `http://` または `https://` で始まるものは `Availability = UrlLocation` とし、選択画面に「OneDrive／SharePoint 上のブックは使えません。ローカルのフォルダへ保存してから開き直してください」と理由つきで出して選べなくする。ROT の表示名が URL にならず一覧に出ないブックもありうるので、選択画面の下に「一覧に出ないブックは、OneDrive 同期フォルダ上など対応していない場所にある可能性があります」と常に添える。デスクトップやドキュメントを OneDrive に同期している環境は多いので、取扱説明書にも書く。
- 次は推測で、実機で確かめる（付録B）。
  - Excel と Graft の権限（管理者として実行）が違うと ROT が見えない。
  - 保護ビューのブックは VBProject に触れない（`ProtectedView` として無効にする）。
  - `.xlam` は `Workbooks` に出ないが ROT には出る。
  - 同じパスのブックを2つの Excel プロセスで開いたときの挙動（読み取り専用側の扱い）。

### 5.3 スレッドと再試行と時間切れ

- COM 呼び出しはすべて専用スレッド（`ComWorker`）で行い、UI スレッドを止めない（UI スレッドは `[STAThread]` だが、そこで COM を呼ばない方針は正しい）。呼び出しはキューに積んで直列に処理する。**STA にするか MTA（スレッドプール＋直列化）にするかは、実機検証 9・12 の結果で決める。** イベントを受けず `IMessageFilter` も使わないので、自前のメッセージループは要らない。待機は管理された待ち方で行う。
- Excel がセル編集中やダイアログ表示中だと、`RPC_E_CALL_REJECTED`（0x80010001）や `RPC_E_SERVERCALL_RETRYLATER`（0x8001010A）が返る。`ComRetry` で 200ms 間隔、`Vba.ComTimeoutMs`（既定 5 秒）まで再試行し、それでもだめなら E805 を返す。`IMessageFilter` の登録は、実装量に見合わないのでこの版では行わない。
- **呼び出し全体の時間切れ。** 再試行は「拒否」が返る場合だけに効く。マクロ実行中・中断モード・VBE のモーダル表示中などは、呼び出しが拒否もされず戻ってこない見込みで（実機で要確認）、`CancellationToken` では進行中の COM 呼び出しを止められない。
  - ブリッジの各メソッドは、呼び出し全体に `Vba.CallTimeoutMs`（既定 30 秒）の時間切れを設ける。待つ側が `Task.WhenAny` で諦めて **E805** を返す。
  - 諦めたスレッドは捨てる（`IsBackground = true` にして、アプリの終了を妨げない）。以後の呼び出し用に、新しいスレッドを作り直す。
  - 複数モジュールの Push で一度でも時間切れになったら、残りのモジュールには進まず中断する（以後も固まる可能性が高く、待ち時間が積み上がるため）。
  - この時間切れがあるので、適用中にアプリを閉じようとして `ShellWindow` が `IsApplyInProgress` を待ち続けても、待ち時間は有限になる。
- 取得した COM オブジェクト（Application、Workbook、VBProject、VBComponents、VBComponent、CodeModule）は、使い終わったら `Marshal.FinalReleaseComObject` で解放する。`dynamic` の中間オブジェクトが残ると、Excel を閉じてもプロセスが残る原因になる。そのため、プロパティを連鎖して書かない（`wb.VBProject.VBComponents.Item(x)` のような書き方は禁止）。必ず変数に受けて解放する。

### 5.4 前提条件の検査とエラー

| 状況 | 検出方法 | エラー |
|---|---|---|
| 「VBA プロジェクト オブジェクト モデルへのアクセスを信頼する」がオフ | `Workbook.VBProject` の取得で COMException | E802 |
| VBA プロジェクトがロックされている | `VBProject.Protection == 1`（vbext_pp_locked） | E803 |
| ブックが開いていない | ROT に見つからない | E804 |
| Excel が応答しない | 5.3 の再試行切れ、または呼び出し全体の時間切れ | E805 |
| マクロを持てない形式（.xlsx） | 拡張子で判定 | E806 |
| 読み取り専用で開いている | `Workbook.ReadOnly` | 警告（反映は可能、保存は不可）。`SaveWorkbookAfterPush` のときは E807 |
| マクロの実行中または中断中 | 書き込みの前に `VBProject.Mode`（デザインモード以外。VBIDE の定義は Run=0・Break=1・Design=2 で、P2 の実装は Design=2 とした。実機で要確認）を確かめる | E813 |
| そのほかの COM の失敗 | 上記のどれにも当たらない `COMException`（RPC サーバー利用不可 0x800706BA、切断 0x80010108 など。Excel が終了した場合を含む） | E801 |

- E802 では、Excel の「ファイル → オプション → トラストセンター → トラストセンターの設定 → マクロの設定 → VBA プロジェクト オブジェクト モデルへのアクセスを信頼する」を案内する。**Graft からレジストリ（`AccessVBOM`）を書き換えてはいけない。** セキュリティ設定は利用者自身に変えてもらう。読み取りだけなら、案内の精度を上げる目的でしてもよい。
- E813 は Push の**前**に、1件も書かずに止める（実行中に書き込むとコードが壊れる、あるいは呼び出しが戻らない見込み）。デザインモードかどうかの判定が実機で期待どおり取れるかは、付録Bで確かめる。

### 5.5 書き込みの手順（WriteComponentsAsync）

書き込みの前に、1回だけ次を確認する: 接続できる（E801/E804/E805）、VBA プロジェクトに触れる（E802）、ロックされていない（E803）、デザインモードである（E813）。どれかが違えば、1件も書かずに全体を失敗にする。

そのうえで、各コンポーネントについて、次の順で処理する。**全文置き換え**方式とする。

1. `VBComponents.Item(name)` で取得する。なければ `CreateIfMissing` かつ種別が Standard / Class のときだけ `VBComponents.Add(type)` で作り、`Name` を設定する。それ以外は E808。
2. **控えを取る。** `DeleteLines` の前に、現在の本文（`CountOfLines > 0` なら `Lines(1, CountOfLines)`、0 行なら空）を読んで持つ。新規に作ったモジュールの控えは空。
3. `CodeModule.CountOfLines > 0` なら `DeleteLines(1, CountOfLines)` を呼び、続けて `CodeModule.AddFromString(code)` を呼ぶ。
   - **ここで例外になったら（E805 を含む）、控えを書き戻す**（`DeleteLines` してから控えを `AddFromString`）。書き戻せたら `RolledBack` で、元の理由のエラー（E805 など）を返す。**書き戻しも失敗したら E809** とし、`Failed` で返す。このとき控えの本文を `<データ保存先>/vba-sync/<プロジェクトId>.rescue/<モジュール名>-<日時>.txt` に書き出し、パスをエラーの詳細に載せる（モジュールが空のまま残っている可能性があることを必ず伝える）。
   - 新規に作ったモジュールが失敗したときは、**作ったばかりのそのモジュールだけ**を `VBComponents.Remove` で消す。既存のモジュールを消すことは決してしない。消せなければ E809 に含めて伝える。
4. 書き込み後に `Lines(1, CountOfLines)` を読み直し、`ReadBackCode` として返す。Core の `VbaWriteVerifier` が、正規化（6.3）した期待値と読み直した本文を比べて、次の3つに判定する。VBE は書き込み時に、キーワードの大文字小文字や空白を自動で直すことがある。
   | 判定 | 条件 | 扱い |
   |---|---|---|
   | 一致 | 正規化した本文が等しい | 成功 |
   | 整形差 | **行数が等しく**、スペースとタブをすべて除いて大文字小文字を無視（`OrdinalIgnoreCase`）した文字列が等しい | 成功。E809 を**警告**で出す（「VBE が整形したため、Excel 側の本文は少し異なります」）。**ミラーは書き換えない**。同期基準は 4.3 のとおり、`mirrorHash` には書いたミラー本文のハッシュ、`excelHash` と `excelLooseHash` には読み直した本文のハッシュを入れる |
   | 別内容 | 上記以外（行数が違う、文字が違う、など） | 失敗。手順3と同じく控えを書き戻し、書き戻せたら `RolledBack`、できなければ `Failed`。どちらも E809 を**エラー**で出す |
   - このしきい値（行数の一致と、空白除去・大文字小文字無視）はテストで固定する（10.1）。
5. 新規作成時に VBE が自動で入れる `Option Explicit`（「変数の宣言を強制する」設定）は、手順 3 の `DeleteLines` で消えるので特別扱いしない。

**書き込み方式の見直し（実機で要確認）**

- 全文置き換えでは、プロシージャ単位の属性（既定メンバーの `VB_UserMemId = 0`、`For Each` 用の列挙子 `-4`、`VB_Description`、マクロのショートカットキーなど）が `CodeModule.Lines` に見えず、失われる可能性がある。実機検証 6 で、これらを持つクラスを書き換えても動き続けるかを確かめる（付録B）。
- 失われると分かったら、**変更のあった行範囲だけ** `DeleteLines`／`InsertLines` する方式へ切り替える。切り替えは `WindowsVbaBridge` の内部だけで行い、`IVbaBridge` の契約は変えない（ブリッジは控えを持っているので、控えと新しい本文の先頭と末尾の共通行を除いた範囲を差分として計算できる）。この場合も、控えの書き戻しと読み直しの検証は同じ手順で行う。

---

## 6. 純ロジック（Core/Vba）

### 6.1 VbaModuleKind / VbaMirrorLayout

- COM の `Type` 値と種別の対応: 1 → Standard、2 → Class、3 → Form、100 → Document。それ以外（ActiveX デザイナ = 11 など）は同期の対象外として、一覧で「未対応」と表示する。
- パスと種別の対応は 4.2 の表のとおり。フォルダ名と拡張子の両方が一致しない場合（`Modules/Foo.cls` など）は、同期の対象外として E810 の警告を出す。
- 次も同期の対象外にして E810 の警告を出す（適用そのものは止めない）。
  - ミラーの外や未知のフォルダに Graft が作った新しいファイル
  - 入れ子のフォルダ（`Modules/sub/x.bas` など）
  - Windows の予約名（`CON`、`PRN`、`AUX`、`NUL`、`COM1`〜`COM9`、`LPT1`〜`LPT9`）のコンポーネント名。ミラーのファイルを作れないため、Pull でもスキップして警告する。
- ファイル名の比較は大文字小文字を区別しない（VBA のコンポーネント名の扱いに合わせる）。
- `Documents/`・`Forms/` にだけあって Excel に該当コンポーネントがないファイルは、新規作成できないので**作成不可**（E808 の警告）として、Push の対象にしない。

### 6.2 VbaNameRules

- コンポーネント名は 31 文字以内、先頭は英字、使える文字は英数字とアンダースコア。日本語名は VBE が受け付ける場合もあるが、環境によって挙動が違うので、**新規作成時だけ**拒否する（既存の日本語名モジュールは読み書きしてよい）。
- Windows の予約名（6.1）も新規作成時に拒否する。
- 予約語（`Module`、`Class` など VBA のキーワード）との衝突は検査しない（VBE がエラーを返し、E808 になる）。

### 6.3 VbaTextNormalizer

ハッシュ計算と比較の前に、次の正規化を行う。

- 改行を CRLF に統一する。
- **ハッシュは、末尾の CR と LF をすべて除いた文字列で計算する**（最終行の改行の有無、末尾の空行の数に左右されない。空白だけの行は除かない）。VBE は末尾の空行を保持したりしなかったりするため。
- 行末の空白は**削除しない**（文字列リテラルの行継続などで意味を持つことがあるため）。
- **ミラーのファイル自体の末尾改行は変えない。** 既存のファイルは末尾改行の有無を保ち（`DryRunPlanner` は新規ファイルを末尾改行ありで作り、既存ファイルは元の有無を保つ）、Pull が新規に作るファイルは末尾に CRLF を 1 つ付ける。ハッシュは末尾の改行を除くので、どちらでも結果は同じ。
- Push は、ミラーのファイルを `FileTextIO.ReadAsync` で読み、改行を CRLF にそろえた本文を VBE に渡す。

加えて、`excelLooseHash` 用の「ゆるい形」（スペースとタブをすべて除き、大文字小文字を無視した形）を計算する関数を持つ（5.5-4 の整形差判定と 6.5 で使う）。

### 6.4 VbaCodePageGuard

- 本文を CP932 でエンコードできるかを `EncoderFallback.ExceptionFallback` で検査する。できない文字があれば、ファイル名・行・列・文字（U+XXXX）を列挙して E811 にする。
- **検査する時点は2つ。**
  1. **解析時（適用の前）**: 解析結果の各 `BlockPlan.AfterText` のうち、VBA プロジェクトのミラー内のファイルのものを、画面側で検査する（`VbaApplyGuard`）。既存の E219 は「そのファイルの文字コードで表せない文字」を解析時に警告するが、ミラーは UTF-8 なので働かない。そのため、確認・プレビューの前に E811 を見せる（コアは変えない）。該当ブロックにチェックが付いている間は、適用を始めない（7.3-2）。
  2. **Push の直前**: 手編集したモジュールや、解析を通らずに変わったモジュールの保険として、もう一度検査する。できない文字のあるモジュールは反映しない。
- AI が出しやすい問題の文字を含むテストケースを用意する。**文字の例は実測で固定する。** Python の `cp932` で確認した結果は次のとおり（.NET の結果は異なる可能性があるので、`Graft.Tests` 上の .NET で実測して固定すること）。
  - 表せない: `—`（U+2014、emダッシュ）、`–`（U+2013、enダッシュ）、`•`（U+2022）、`¥`（U+00A5）、ノーブレークスペース（U+00A0）、絵文字
  - 表せる: `～`（U+FF5E、全角チルダ）、`①`〜`⑳`（囲み数字）
  - `〜`（U+301C、波ダッシュ）は、Python の `cp932` では表せない扱いにならないが、.NET の CP932 では `?` に落ちて例外になる可能性がある。**.NET での結果を実測して、そのとおりにテストで固定する。**
- VBE の文字コードは OS の ANSI コードページ（日本語環境では CP932）である見込み（実機で要確認）。1.4 に前提として書いた。

### 6.5 VbaSyncPlanner（三方比較）

モジュールごとに、次の4つのハッシュを比べて判定する。

- `M`: ミラーの現在の本文（`FileTextIO.ReadAsync` で読み、6.3 で正規化）のハッシュ
- `E`: Excel の現在の本文のハッシュ
- `Bm`（`mirrorHash`）、`Be`（`excelHash`）: 同期基準

`mirrorChanged = (M ≠ Bm)`、`excelChanged = (E ≠ Be)`。ただし `excelChanged` が真でも、`excelLooseHash` と現在の Excel 本文のゆるいハッシュが一致するなら「整形差だけ」と見て、`excelChanged = false` とし、`excelHash` だけを更新する（Pull しない）。VBE は識別子の大文字小文字をプロジェクト全体で揃える見込みなので、別のモジュールへの Push で、無関係なモジュールの本文が大文字小文字だけ変わることがある（実機で要確認）。これを「Excel 側の変更」として取り込むと、ミラーが履歴に無い変更を受けて取り消しが E301 になり、ミラーも変わっていれば偽の衝突になるため。

| mirrorChanged | excelChanged | 判定 |
|---|---|---|
| いいえ | いいえ | 何もしない |
| はい | いいえ | **Push**（ミラー側だけが変わった。「未反映」） |
| いいえ | はい | **Pull**（Excel 側だけが変わった） |
| はい | はい | `M` と `E` が同じ、またはゆるい形が同じなら、基準だけ更新（`mirrorHash = M`、`excelHash = E`）。違えば**衝突** |

基準の記録がないとき・片方がないときは次のとおり。

| 状況 | 判定 |
|---|---|
| 記録なし、ミラーにだけある（Standard / Class） | 新規作成の Push |
| 記録なし、ミラーにだけある（Document / Form） | 作成不可（E808 の警告）。Push の対象にしない |
| 記録なし、Excel にだけある | Pull |
| 記録なし、両方ある | 本文が同じなら基準を作るだけ。違えば衝突（初回の食い違い） |
| 記録あり、**ミラーのファイルが無い**、Excel にはある | **削除保留**。`deletePending = true` にして記録する。**自動 Pull では復活させない。** 画面に「ミラーから削除されたモジュール」として警告を出す |
| 記録あり、ミラーにある、**Excel に無い** | 警告（「Excel 側でモジュールが無くなっています」）。ミラーが変わっていなければ何もしない。ミラーが変わっていて Standard / Class なら Push（再作成）、Document / Form なら作成不可 |
| 記録あり、両方無い | 記録を破棄 |
| ミラーのファイルが再び現れた | `deletePending` を解除して、上の通常の判定に戻す |

- **削除保留の解除**: プロジェクトの右クリックメニューの「Excel から復元」（そのモジュールだけを Pull する）で行う。「Excel からも削除…」は**この版には入れない**（Excel 側のモジュールの削除は 1.3 のとおり非対象）。
- 削除保留になる典型は、新規モジュールを含む適用を取り消したとき（ミラーからは消えるが、Excel には残る）である。取り消しの完了時に、その旨を利用者に通知する（7.3）。
- 入力と出力は純粋なレコードにして、COM にもファイルにも触れないこと。全パターンを `Graft.Tests` で網羅する。
- Push 後の再基準化は、Push したモジュールだけ（`mirrorHash`・`excelHash`・`excelLooseHash` を更新）とする。それ以外のモジュールは、次の判定で上の「整形差だけ」の規則が拾う。

---

## 7. 同期の流れ

### 7.1 プロジェクトの作成（Excel ブックから追加）

1. プロジェクトペインの追加ボタン（`AddProjectCommand`。フォルダ選択へ直行する既存のボタン）の隣に、「Excel ブックから追加」ボタンを**足す**（`IVbaBridge.IsSupported` が false なら表示しない）。既存のボタンはメニューにしない。
2. `VbaWorkbookPickerWindow` で、起動中のブックを一覧表示する。名前、フルパス、状態（ロック中、読み取り専用、マクロ不可、アクセス未許可、保護ビュー、OneDrive／SharePoint）を見せ、選べないものは理由つきで無効にする。一覧の下に 5.2 の「一覧に出ないブック」の注意を常に添える。`E802` のときは、案内（5.4）をその場に出す。
3. ミラーフォルダの場所を選ぶ（既定は 4.2）。同じ名前のフォルダと同期状態が既にあるときは、「既存のミラーを使う（三方比較で続ける）」「作り直す」を選ばせる。
4. 全コンポーネントを Read し、ミラーへ書いて同期基準を作る。
5. `Project` を作成する。`Name` の既定はブック名（拡張子なし）。`StandingContext` の既定値として、次の文を入れておく（利用者が編集できる）。

```
これは Excel VBA のプロジェクトです。各ファイルは VBE のモジュール本文そのもので、Attribute 行やエクスポート用のヘッダは含みません。
Modules/ は標準モジュール、Classes/ はクラスモジュール、Documents/ はシートと ThisWorkbook、Forms/ はユーザーフォームのコードです。
新しいモジュールは Modules/ または Classes/ に作ってください。Documents/ と Forms/ に新しいファイルを作ることはできません。
ファイルの削除と名前の変更（DELETE / RENAME）はできません。
Shift_JIS（CP932）で表現できない文字（絵文字、波ダッシュ「〜」、emダッシュ「—」など）は使わないでください。
```

### 7.2 取込（Pull）

- 実行のきっかけは 3 つ。
  - **手動**: プロジェクトの右クリック「Excel から取込」、コマンドパレット（`ShellViewModel.CommandPalette.cs` にコマンドを足す）。**ホットキーは設けない。**
  - **適用の解析（ドライラン）の直前**（7.3）。設定 `Vba.PullBeforeDryRun`
  - **コンテキスト収集**（設定 `Vba.PullOnContextOpen`、既定 true）。位置は、**`ContextCollectViewModel.InitializeAsync`／`RefreshAsync` の走査の前**とする。ウィンドウを開く経路（`ShellWindow` の `ShowDialog`）だけでなく、依頼文コピー（`PromptCopyViewModel`。`{{files}}` や「選んだファイルも付ける」、グローバルホットキー Ctrl+Shift+C）も同じ `InitializeAsync` を呼ぶので、ウィンドウを開かない経路でも古い本文が AI に渡らない。
    - 衝突ダイアログは、コンテキスト収集ウィンドウがモーダルで開いているときは、そのウィンドウを親にして出す。ウィンドウを開かない経路では、メインウィンドウを親にする。
    - Pull に失敗した（E804/E805 など）、または衝突を「中止」した場合は、収集を止めず、ミラーの現在の内容で続ける。警告を 1 件出す（「Excel の最新を取り込めていません」）。
- Planner の結果が Pull のモジュールだけをミラーへ書き、同期基準を更新する（`mirrorHash` と `excelHash` の両方を更新）。**削除保留のモジュールは取り込まない。**
- **Pull の前に未保存の確認をする。** 未保存の編集は三方比較に入らない（ディスク上のミラーは基準と同じに見えるので Pull され、利用者は E702 のバーで初めて気付く）。そこで、Pull するモジュールのうち未保存のタブがあるものは、`BeforeApplyAsync`（`ShellViewModel`）と同じ確認で保存させる。断られたモジュールは Pull しない（警告を出す）。
- 衝突があれば、7.5 の衝突ダイアログを出す。
- 書き込みは既存の `SafeFileWriter`／`FileTextIO.WriteAsync` で行う（4.2 の形）。エディタで開いているタブは、既存の `FileWatchService` の仕組みで再読込される（Pull の書き込みは 250ms 後に `NotifyExternalChangeAsync` へ届き、未保存のないタブは黙って再読込、あるタブは E702。`FileWatchService` は `ExplorerViewModel` の内部にあり、外からは触れない）。

### 7.2b 反映（Push）の手動操作

Graft 上の手編集（エディタ保存、エクスプローラの新規・改名・削除、インライン編集、ファイル取り込み）でミラーが変わっても、適用・取り消し・復元の契機だけでは Excel に届かない。そこで、次を設ける。

- プロジェクトの右クリック「**Excel へ反映**」と、コマンドパレットのコマンド。Planner が **Push 判定**にしたモジュール（未反映）を、まとめて Push する。衝突があれば 7.5 の衝突ダイアログ。
- **保存時の自動 Push はしない。** `DocumentSession` の保存経路に触れると、性能の約束（10万行）に響くため。
- 次の適用の Push では、適用で変わったモジュールに加えて、**6.5 で Push 判定になっているモジュールも同梱する**（7.3-4）。「Excel へ反映: N 件（うち以前からの未反映 M 件）」と表示して、利用者が気付けるようにする。
- 未反映のモジュールがあるプロジェクトは、プロジェクトペインにバッジ（「未反映 N」）を出す（7.6）。
- 手動 Push の流れは 7.3-4 と同じ（衝突の確認、CP932 の検査、書き込み、結果の表示）。ただし、失敗しても取り消しは提案しない（適用ではないため）。

### 7.3 適用時の流れ

既存の `MainViewModel` の流れに、次のとおり差し込む。VBA プロジェクト（`Project.Vba` が非 null かつ `Vba.Enabled`）のときだけ動く。

0. **プロジェクト自動判定**: `ProjectMatcher.BuildOutcome` は、パッチのパスが存在する割合で比べ、同率は一覧の並び順で決まる。VBA のミラーはどのブックでも `Documents/ThisWorkbook.cls`・`Documents/Sheet1.cls`・`Modules/Module1.bas` で同じなので、VBA プロジェクトが2つ以上あると毎回「別のプロジェクトの可能性があります（一致率100%）」が出て、誤って切り替えると別のブックへ適用される。そこで、**一致率が同率のときは現在のプロジェクトを優先する**（`ProjectMatcher.BuildOutcome` の修正。判定に現在のプロジェクトを渡す）。
1. **解析（ドライラン）の前**（`MainViewModel.cs` の `RunDryRunCoreAsync` が `ConfirmTargetsSavedAsync` を呼んだ直後）: パッチの対象モジュールに限って Planner を実行する。`Vba.PullBeforeDryRun` が true のとき自動で行い、false のときは同じ内容を確認ダイアログ（「取り込む」「取り込まずに続ける」「中止」）で行う。
   - Excel 側だけが変わっている → 自動で Pull してから解析を続ける（AI が最新でない本文を前提にしている可能性があるので、ステータスバーに「Excel 側の変更を取り込みました」と出す）。Pull したことは `Logger` に残す。
   - 衝突 → 衝突ダイアログ。「中止」なら解析しない。
   - Excel に接続できない → E804 / E805 などを出し、「ファイルだけ適用する」「中止」を選ばせる。「ファイルだけ適用する」を選んだ場合、変更は「未反映」として残る。
2. **解析の後**（解析結果が出たところ）と、**適用を始める前**（`ApplyAsync` の冒頭、`ApplyEngine.CheckSelection` の前後）に、画面側で次を検査する（`VbaApplyGuard`）。チェックが付いているブロックだけが対象で、該当するものが 1 件でもあれば**適用を始めない**。外せば、そのほかは適用できる。
   - **DELETE／RENAME**（`BlockKind.Delete`、`BlockKind.Rename`）→ **E814**「VBA プロジェクトでは削除・名前変更を適用できません」。理由: 削除と名前変更を Excel へ反映しない設計なので、ミラーだけ変えるとミラーと Excel が食い違い、RENAME は旧名と新名の二重定義になりうる（推測。VBA では同名プロシージャでコンパイルエラーになる見込み）ため。コア（`ApplyEngine`）は変えない。`BlockKind.Mkdir` はミラーのフォルダを作るだけで同期に影響しないので止めない（フォルダは同期の対象外）。
   - **CP932 で表せない文字**（6.4）→ **E811**。
3. **解析後の再検査**（`SaveTargetsAndReanalyzeAsync` の直後）: 解析から「適用」までは、利用者が差分を見る時間である。その間の Excel 側の変更を、書き込み後の Push 直前まで見逃さないよう、ここでもう一度 Planner を対象モジュールに対して実行する。
   - Excel 側だけが変わっている → 「取り込んで解析をやり直す」「このまま続ける（Push のとき衝突として扱う）」「中止」を選ばせる。「取り込んで解析をやり直す」は、`MainViewModel.ApplySave.cs` の「チェック状態を引き継いで解析し直す」処理を再利用する。
   - 衝突 → 衝突ダイアログ。「Excel 側を採用」を選んだときも同じ再解析を使う。
   - 解析の後に手動 Pull やコンテキスト収集の自動 Pull でミラーが変わると、適用エンジンの検証が E301 で中止する（安全側の動き）。その場合は「解析し直してください」と案内する。
4. **適用の成功後**（`ApplyCoreAsync` の `History.LoadAsync` の後・適用後フックの `rolledBack` 判定の前）: `AutoPushAfterApply` が true なら、Excel へ反映する（Push）。`NotifyFilesRewrittenAsync` の直後ではない（その位置では `History.LoadAsync` がまだ走っておらず、履歴の一覧が古い）。
   1. **対象**: `RevisionManifest.Entries` に含まれるミラー内のファイル ∪ 6.5 で Push 判定のモジュール（7.2b）。
   2. **処理中の表示**: Push の間は中央ペインを `Loading` のままにし（`DiscardCurrentPatch`／`KeepOnlyFailedBlocks` が Empty に戻す場合は、Push の開始時に `Loading` へ戻し、終了時に元の状態へ戻す。実装時に両メソッドの中身を確かめて小さいほうを選ぶ）、貼り付けて解析、取り消し（Ctrl+Z を含む）、履歴ペインの操作、プロジェクトの切り替えを受け付けない。既存の `IsApplyInProgress` の旗を Push の完了まで立てておき、足りない入口には同じ旗の検査を足す。COM が固まっても、5.3 の時間切れで有限の時間で戻る。
   3. **Push の直前の再検査**: もう一度 Excel の本文を読み、`excelHash` と比べる（解析から適用までの間に VBE で編集された場合の保護）。違っていたら衝突ダイアログを出す。適用が済んだあとなので、選択肢は「Graft 側で上書き（Excel 側の本文は `vba-sync/<プロジェクトId>.rescue/` に控える）」「反映しない（未反映の印を付ける）」「この適用を取り消す」の3つとし、「Excel 側を採用」は**出さない**（ミラーを Excel の内容に差し替えると、AI のパッチを打ち消し、履歴の `HashAfter` とも食い違うため）。
   4. **CP932 の検査**（6.4）に失敗したモジュールは反映しない（E811）。
   5. **書き込み**（`WriteComponentsAsync`）と結果の処理。成功したモジュールの同期基準を更新する。
   6. **一部でも Push に失敗した場合**（書き込みの失敗、E811 の除外、衝突での「反映しない」を含む）は、ダイアログで次から選ばせる。
      - 「**ファイルの変更は残す（Excel には未反映）**」← **既定のボタン**。変更したファイルはそのまま残し、未反映の印（6.5 の Push 判定。プロジェクトペインのバッジ）が付く。取り消してもパッチは画面に戻らないため、既定は取り消さない。再試行は 7.2b の「Excel へ反映」で行える。
      - 「**もう一度反映する**」（E805／E813 など一時的な理由のとき）。
      - 「**この適用（rN）を取り消す**」。選んだら、**`MainViewModel.Hooks.cs` の `RollbackAfterHookFailureAsync` と同じ作り**で行う: `_revisionStore.ReadAsync(project.Id, revision)` → `_revisionRestorer.RestoreAsync(project.Id, project.Root, summary, force: true)` → `History.LoadAsync`。**rN を名指しで戻し、`History.UndoLatestAsync` は使わない**（この位置では履歴の一覧が最新でなく別のリビジョンを取り消したり、履歴ペインの絞り込みで先頭が変わったり、確認ダイアログが二重になったり、`RevisionRestored` で復元後の Push が二重に走ったりするため）。
        - 取り消しが成功したら、**すでに Excel へ反映できたモジュールを、取り消し後の内容で Push し直す**（Excel を元に戻す）。この Push に失敗しても、さらに取り消すことはしない。警告を出し、未反映の印を付ける。
        - この適用で**新規に作ったモジュールは Excel 側に残る**。取り消しの完了時に「次のモジュールは Excel 側に残っています: …。ミラーからは削除され、『削除保留』になります」と通知する（6.5）。
        - 取り消した場合は、`rolledBack` を立てる。
   7. **成功したら**、ステータスバーの既存の適用通知（`ShowApplyUndoNotice`）に「Excel へ反映済み」を付け足す。`SaveWorkbookAfterPush` が true なら、反映後にブックを保存する（読み取り専用なら E807）。
5. **適用後フックと Git 自動コミット**は、Push の後で従来どおり実行する。**Push の失敗で取り消した場合は、どちらも実行しない**（`rolledBack` の扱いに合わせる）。そのため `ApplyCoreAsync` の `rolledBack` は、フックの戻り値だけでなく Push の取り消しでも立つ構造に変える（現状は「フックが設定されているときだけ代入する」作りなので、構造の変更が要る。`Apply.cs` の「差し込み位置以外は変えない」は守れない）。
6. **適用後フックの失敗による巻き戻し**（`RollbackAfterHookFailureAsync`）が成功したときも、7.4 のとおり Excel へ Push する。Excel は新内容、ミラーだけ旧内容になるのを避けるため。

### 7.4 取り消しと履歴からの復元

取り消し・復元でミラーが変わったときの Push は、**待てるフック**を使って行う。

- `HistoryPaneViewModel` に `Func<IReadOnlyList<string>, Task>? AfterRestoreAsync { get; set; }` を足す（`MainViewModel` の `AfterApplyAsync` と同じ作法）。引数は戻したファイルのパス一覧。
- 既存の `RevisionRestored` は `EventHandler`＋`EventArgs.Empty` で、変更ファイルの一覧を持たず、購読側の `MainViewModel.OnRevisionRestored` は `async void` なので、ここで Push すると復元側の `LoadAsync` や通知と並行に衝突ダイアログが出る。そこで、イベントの発火の**前**に `AfterRestoreAsync` を `await` する。
- フックを呼ぶ経路は**3つ**。
  1. **単発の復元・取り消し**（`HistoryPaneViewModel` の復元の完了点。`RevisionRestorer.RestoreAsync` が返す戻したパスの一覧を使う。`UndoLastAsync`／Ctrl+Z／ステータスバーの「元に戻す」もここを通る）
  2. **「ここまで戻す」**（別経路。戻したパスは `result.Value.Entries`。変更なしのときは発火しないので、フックも呼ばない）
  3. **適用後フック失敗の巻き戻し**（`MainViewModel.Hooks.cs` の `RollbackAfterHookFailureAsync` が成功した後。`RevisionRestorer` を直接呼び、`RevisionRestored` も `AfterRestoreAsync` も通らないため、ここで同じ Push 処理を呼ぶ）
- フックの中身は `VbaSyncService` の Push（VBA プロジェクトのときだけ）。Push の直前に衝突検査を行う。選択肢は「Graft 側で上書き」「反映しない（未反映の印）」の2つで、**「Excel 側を採用」は出さない**（復元の意図を打ち消すため）。
- 7.3-4-6 の取り消しは `RevisionRestorer` を直接呼ぶ（経路3と同じ）ので、`RevisionRestored` は発火せず、Push が二重に走ることはない。取り消し後の Push は 7.3 の中で明示的に呼ぶ。
- 復元で新規ファイルが消える場合（その版では存在しなかったモジュール）は、6.5 の削除保留になる。

### 7.5 衝突ダイアログ（VbaConflictWindow）

- 衝突したモジュールごとに、「Excel 側」と「Graft 側（ミラー）」の差分を、既存の `DiffViewModel`（左右比較）で見せる。`DiffViewModel` は `BlockPlan` を受け取るので、`HistoryPaneViewModel.BuildDiffPlansAsync` にならって、2つの本文から `BlockPlan` を組み立てる。
- モジュールごとに次から選ぶ。選べる項目は、衝突が起きた場面で変わる。

  | 選択肢 | 動作 | 出す場面 |
  |---|---|---|
  | **Graft 側で上書き** | Excel へ Push する（Excel 側の本文は `vba-sync/<プロジェクトId>.rescue/` に控える） | すべて |
  | **Excel 側を採用** | ミラーへ Pull する。適用の前なら、そのモジュールへのブロックが一致しなくなる可能性があるので、解析をやり直す（7.3-3） | 解析の前後、手動の Pull／Push、コンテキスト収集。**適用の後と、復元の後では出さない** |
  | **反映しない** | 何もせず、未反映の印を付ける | 適用の後、復元の後 |
  | **中止** | 何もしない | 適用の前 |

- 「すべてに適用」チェックで一括選択できるようにする。
- 衝突が解決されずに処理が止まるときは、E812 を出す。

### 7.6 その他の操作

- **VBE で開く**: エディタタブと差分ビューの右クリックメニュー（どちらも `EditorPane.axaml`／`DiffView.axaml` に実在する）に「VBE でこの行を開く」を加える。`ShowInVbeAsync` で `CodeModule.CodePane.Show` と `SetSelection` を呼び、VBE のメインウィンドウを前面化する。**前面化は別の実装が要る。** 既存の `ForegroundActivationDecision` は Graft 自身のウィンドウ（`WindowsSingleInstanceGuard`）向けの純粋な判定であり、別プロセスの VBE には使えない。VBE の `MainWindow.HWnd` を取って前面化し、Windows が前面化を許さない場合のために、既存の AttachThreadInput による回避策（`docs/確認手順_AttachThreadInputによる前面化回避策.md`）の考え方を流用する。実機で要確認。
- **ブックを前面化**: プロジェクトの右クリックメニューに追加する（`ActivateWorkbookAsync`）。
- **プロジェクトの右クリックメニュー**（`ProjectPane.axaml` に実在する7項目のメニューに足す。VBA プロジェクトのときだけ表示）: 「Excel から取込」「Excel へ反映」「Excel から復元」（削除保留のモジュールがあるとき）「ブックを前面化」「適用後に Excel へ自動反映する（チェック項目）」「反映後にブックを保存する（チェック項目）」。
- **バッジ**: プロジェクトペインの行の左は、数字キーのバッジだけである。これに並べて「VBA」バッジを付ける（新規）。次の状態を出す。
  - ブックが開いていないときはグレー表示にし、ツールチップに「ブックが開いていません」と出す。
  - 未反映のモジュールがあるときは「未反映 N」を添える。削除保留があれば、その件数もツールチップに出す。
  - `Vba.Enabled = false` のときは「VBA 連携オフ」。
  - **ブックが開いているかを調べ直すタイミング**: Graft のウィンドウが前面化されたとき、プロジェクトペインを開いたとき、Pull／Push の直前と直後。定期的な監視（ポーリング）はしない。コマンドバーのプロジェクト選択（`ShellWindow.axaml` のドロップダウン）にはバッジを出さない。
- `Vba.Enabled = false` のときは、既存の VBA プロジェクトも、COM を呼ばない普通のファイルプロジェクトとして扱う（ミラーは編集・適用できる。同期の操作はすべて無効）。

---

## 8. 設定（Settings.Vba）

| キー | 既定 | 説明 |
|---|---|---|
| `Vba.Enabled` | true | 機能全体の有効・無効。false のときは COM を一切呼ばない（7.6） |
| `Vba.PullOnContextOpen` | true | コンテキスト収集（`ContextCollectViewModel.InitializeAsync`／`RefreshAsync`）の前に自動で Pull する |
| `Vba.PullBeforeDryRun` | true | 解析の直前に、対象モジュールを自動で Pull する（7.3 の 1）。false のときは確認ダイアログで行う |
| `Vba.ComTimeoutMs` | 5000 | 5.3 の再試行の上限（1 回の拒否への再試行）。範囲 1000〜30000 |
| `Vba.CallTimeoutMs` | 30000 | 5.3 の呼び出し全体の時間切れ。範囲 5000〜120000、`ComTimeoutMs` 以上 |

- **`Vba.PullHotkey` は初版から設けない。** 既存のホットキーは `Settings` の `Hotkey`／`PromptHotkey` の2本固定で、再登録（`StartupCoordinator.Hotkey.cs` の `ReapplyHotkey` は、個別解除なしの `UnregisterAll` で2本を扱う）の作り直しが大きいため。Pull はコマンドパレットとプロジェクトの右クリックメニューで行う。
- プロジェクトごとの `AutoPushAfterApply` と `SaveWorkbookAfterPush` は、プロジェクトの右クリックメニューのチェック項目から変える（7.6）。`ProjectOverrides` を編集する画面は存在しない（編集されるのはコンテキスト収集の `Excludes`／`ContextFileStates` だけ）。
- 設定画面には「VBA 連携」パネルを追加する（`Views/SettingsPanels/VbaSettingsView.axaml`。既存のタブは12個なので13個目）。Windows 以外では、パネルごと「この環境では使えない機能です」（E706 の実際の文言）と表示する。
- **設定の保存で `Vba` が消えないようにする。** `SettingsViewModel.BuildSettingsFromFields` は `new Settings { … }` で全項目を組み立て直すので、ここと読み込み側に `Vba` を足さないと、**設定画面で何か保存するたびに Vba が既定値へ戻る**。`SettingsStore` の null 置き換えと、`ComTimeoutMs`／`CallTimeoutMs` の範囲検証（`NormalizeIntRange`）も追加する。JSON は camelCase（`JsonFileStore`）なのでキーは `vba`。「設定を保存しても Vba の値が保たれる」テストを書く（10.2）。

---

## 9. エラーコード

E8xx は未使用なので、VBA 連携に割り当てる。`ErrorCodes.cs` の既存の書き方に合わせること。

- `ErrorCode` 列挙体に、各コードの XML ドキュメントコメント（採番の理由を含む）を書く。
- 見出しと対処の案内は、辞書の `(Summary, Remedy)` の組で登録する（例: `[ErrorCode.E706] = ("この環境では使えない機能です", "代わりの方法を確認してください。…")`）。
- **重大度は辞書に持たない。** `GraftIssue.Of(..., severity:)` で発生のたびに指定する。そのため、見出しに「（警告）」「（衝突）」は入れない。
- 全コードの登録を確かめるテスト（`ErrorCatalogTests`）が既にあるので、通ること。

| コード | 見出し | 発生条件 | 主な対処の案内 |
|---|---|---|---|
| E801 | Excel と通信できませんでした | 5.4 の「そのほかの COM の失敗」（Excel の終了、RPC の切断など） | Excel が起動しているか、ほかのダイアログが開いていないか確認 |
| E802 | VBA プロジェクトへのアクセスが許可されていません | `Workbook.VBProject` の取得で COMException | トラストセンターの設定手順を表示 |
| E803 | VBA プロジェクトがパスワードで保護されています | `VBProject.Protection == 1` | VBE でロックを解除してから再実行 |
| E804 | 紐付けたブックが開いていません | ROT に見つからない | ブックのパスを表示し、「Excel で開く」ボタン（`Process.Start` でファイルを開く）を出す |
| E805 | Excel が応答しません | 再試行切れ、または呼び出し全体の時間切れ | セル編集中ならセルを確定する、ダイアログを閉じる、マクロの実行中でないか確認 |
| E806 | このブックはマクロを保存できない形式です | 拡張子が .xlsx | .xlsm で保存し直す |
| E807 | 読み取り専用のため保存できません | `SaveWorkbookAfterPush` で `ReadOnly` | 反映は済んでいる旨と、手動で別名保存する案内 |
| E808 | モジュールを作成できませんでした | 名前の規則違反、Documents/・Forms/ への新規作成、VBE が拒否 | 名前の規則、または Documents/・Forms/ には新規作成できない旨 |
| E809 | 反映後の内容が一致しません | 書き込み後の読み直しが「整形差」（警告）または「別内容」（エラー）。控えの書き戻しに失敗したときも | 整形差は VBE が自動で直した旨。別内容はミラーと Excel の両方を表示し、再取込を促す。書き戻し失敗は控えのファイルの場所を案内 |
| E810 | 同期の対象外のファイルがあります | フォルダと拡張子の不一致、入れ子、予約名、未対応の種別（警告） | フォルダと拡張子の対応表を表示 |
| E811 | Shift_JIS で表せない文字があります | 解析時と Push の直前の検査（6.4） | 位置と文字を列挙 |
| E812 | Excel 側とファイル側の両方が変更されています | 衝突が解決されずに処理が止まるとき（7.5） | 衝突ダイアログへ誘導 |
| E813 | マクロの実行中または中断中です | 書き込みの前の `VBProject.Mode` がデザインモード以外 | マクロを終了する（リセットする）か、中断を解除してから再実行 |
| E814 | VBA プロジェクトでは削除・名前変更を適用できません | 解析結果に DELETE／RENAME が含まれる（7.3-2） | 該当ブロックのチェックを外す。モジュールの削除・名前変更は VBE で行う |

---

## 10. テスト

### 10.1 Graft.Tests（純ロジック）

- `VbaMirrorLayout`: 全種別の相互変換、不一致（`Modules/x.cls`）、ミラーの外のパス、大文字小文字の違い、入れ子（`Modules/sub/x.bas`）、Windows の予約名
- `VbaTextNormalizer`: LF / CR / CRLF の混在、末尾空行の有無（末尾の CR/LF がすべて除かれること）、空白だけの行は除かれないこと、行末空白を残すこと、ハッシュの一致、ゆるいハッシュ（空白除去・大文字小文字無視）
- `VbaCodePageGuard`: `～`（可）、`〜`（.NET での実測結果で固定）、`—`・`–`・`•`・`¥`・U+00A0（不可）、`①`〜`⑳`（可）、絵文字、サロゲートペア、複数行での位置の正確さ
- `VbaNameRules`: 31 文字の境界、先頭数字、日本語（新規作成時のみ拒否）、予約名
- `VbaSyncPlanner`: 6.5 の表の全パターン（`mirrorChanged`／`excelChanged` の4通り、基準なし、ミラー消失＝削除保留、Excel 消失、整形差だけ〔大文字小文字の別モジュールへの伝播〕の再基準化、削除保留の解除、Document／Form の作成不可）
- `VbaWriteVerifier`: 一致、整形差（スペース・大文字小文字だけ）、行数が違う場合は別内容、同じ行数で文字が違う場合は別内容。**しきい値をここで固定する。**
- `VbaApplyGuard`: DELETE／RENAME の検出（チェックが外れているものは対象外）、MKDIR は止めない、CP932 違反の列挙
- `VbaExtensionPolicy`: VBA プロジェクトのときだけ3拡張子が加わる、ほかのプロジェクトには加わらない、既存の設定を壊さない
- `VbaMirrorFolderName`: 同じブックで同じ名前になる、別の場所の同名ブックで衝突しない、不正文字・長さ、保存先の移行での Root と Id の付け替え
- `VbaSyncStateStore`: 保存と読み込み、壊れた JSON からの回復（既存の `JsonFileStore` の挙動に合わせる）、Id 変更での付け替え
- `Project` の後方互換: `Vba` を持たない既存の `projects.json` が読めること、`Vba` ありで往復できること
- `AppPaths`／`DataDirectory`: `VbaMirrorsDirectory`／`VbaSyncDirectory` が移行の対象になること、旧保存先の掃除で消えること、移行後に Root と Id と `back/`・`vba-sync/` が揃うこと
- `ProjectStore`: `RelocateAsync` で同期状態ファイルが付け替わること、`RemoveAsync` で選択に応じてミラーと同期状態が扱われること、**保存先の配下のミラーを `PathGuard` が拒否しないこと**（適用が通る）
- `ProjectMatcher`: VBA プロジェクトが2つあり、同率のとき現在のプロジェクトが優先されること
- `ErrorCatalogTests`: E801〜E814 がすべて登録されていること
- `Settings`: `Vba` の既定値、範囲検証、JSON の往復

### 10.2 Graft.UiTests（Avalonia.Headless）

- `FakeVbaBridge`（メモリ上でブックとモジュールを持ち、失敗や遅延や整形差を注入できる）を用意する。`MainViewModel.VbaBridge` に入れる（3.3）。
- Null プラットフォームでは「Excel ブックから追加」と設定パネルの中身が出ないこと
- 作成フロー: ブック選択 → ミラー作成 → プロジェクト登録。OneDrive（URL）のブックは理由つきで無効
- 適用フロー: Excel 側だけ変更 → 解析前に自動 Pull される。解析後の再検査で Excel 側が変わっていたら、取り込んで解析し直せる（チェック状態が引き継がれる）
- 適用フロー: DELETE／RENAME を含む → E814 で適用が始まらない。チェックを外せば適用できる
- 適用フロー: CP932 で表せない文字を含む → 解析結果の段階で E811。該当モジュールのチェックを外せば他は適用できる
- 適用フロー: Push 成功 → 通知に「Excel へ反映済み」。VBE の整形差があってもミラーは書き換わらず、取り消しが E301 にならない
- 適用フロー: Push 失敗 → 既定ボタンは「ファイルの変更は残す」で、未反映の印が付く。取り消しを選ぶと rN が名指しで戻り（`UndoLatestAsync` が呼ばれない、確認ダイアログが二重にならない）、反映済みだったモジュールが取り消し後の内容で Push される。このとき適用後フックと Git コミットが実行されない
- 適用フロー: Push 直前の衝突 → 衝突ダイアログ（「Excel 側を採用」が出ない）→ 各選択肢の結果
- 取り消し後の Push の3経路: ① Ctrl+Z／履歴ペインの復元 ② 「ここまで戻す」 ③ 適用後フック失敗の巻き戻し。いずれも Push され、`RevisionRestored` と二重にならない
- 新規モジュールを含む適用の取り消し → 「Excel 側に残っています」の通知、削除保留、次の自動 Pull で復活しないこと。「Excel から復元」で戻ること
- 手動の「Excel へ反映」、次の適用で Push 判定のモジュールが同梱されること
- 未保存のタブがあるモジュールの Pull で確認が出て、断ると Pull されないこと
- コンテキスト収集（`InitializeAsync`／`RefreshAsync`、および依頼文コピー経路）で Pull されること
- 保存先の移行 → ミラーの Root と Id の付け替え、プロジェクトの場所の変更、削除（ミラーと同期状態の確認）
- 設定を保存しても `Vba` の値が保たれること（`BuildSettingsFromFields` の往復）
- 時間切れ: `FakeVbaBridge` が戻らないとき、E805 になり、適用中の旗が下りてアプリを閉じられること
- VBA プロジェクトが2つあるときの自動判定が誤作動しないこと
- 既存の `PerformanceTests` が引き続き通ること（VBA 連携が通常のファイルプロジェクトの性能に影響しないこと。保存時の自動 Push を入れない前提。エディタの保存経路と `DocumentSession` には触れない）

### 10.3 Windows 実機検証（`docs/Windows実機検証手順.md` に節を追加）

1. トラストセンターの設定がオフの状態で追加しようとすると、E802 と手順が出る
2. .xlsm を開いてプロジェクトを作成すると、全種別のモジュールがミラーに出る
3. 標準モジュールの修正を適用すると、VBE に即時反映される（VBE を開いたままでも反映される）
4. Sheet1 のイベントプロシージャを修正すると、シートモジュールに反映され、イベントが動く
5. 新規標準モジュールを含む適用で、モジュールが作成される。その適用を取り消すと、Excel 側にモジュールが残り、削除保留になる
6. クラスモジュール（`VB_PredeclaredId = True` のもの）を書き換えても、モジュール属性が保たれる。**さらに、既定メンバー（`VB_UserMemId = 0`）、`For Each` 用の列挙子（`-4`）、`VB_Description`、マクロのショートカットキーを持つクラス・標準モジュールを書き換えても、それらが動き続けるか／失われるか**を記録する（失われるなら行範囲方式へ切り替える。5.5）
7. ユーザーフォームのコードを書き換えても、フォームのデザインが崩れない
8. VBE で直接編集したあとに適用すると、衝突ダイアログが出る
9. セル編集中に適用すると、E805 になる（5 秒以内）。セルを確定して再実行すると成功する。**マクロの実行中・中断中・VBE のモーダル表示中の呼び出しがどうなるか**（拒否されるか、戻らないか）を記録し、呼び出し全体の時間切れ（30 秒）で E805 になること、`VBProject.Mode` が E813 を正しく検出することを確かめる
10. 32bit 版 Office と 64bit 版 Office の両方で 2〜5 が通る（片方しか用意できない場合は、未確認として記録する）
11. 同じパスのブックを 2 つの Excel プロセスで開いた場合の挙動（読み取り専用側の扱い）を記録する
12. Graft を終了したあとで Excel を閉じたとき、Excel のプロセスが残らない。時間切れでスレッドを捨てたあとも、Excel のプロセスが残らないか記録する。**STA と MTA のどちらで安定するかを比べて決める**
13. OneDrive／SharePoint 同期フォルダ上のブックの `Workbook.FullName` と ROT の表示名が実際にどう見えるか（URL か、ローカルパスか）を記録する
14. Graft を管理者として実行した場合と、Excel を管理者として実行した場合に、ROT でブックが見えるかを記録する
15. 保護ビューで開いたブック、`.xlam`（アドイン）の見え方と、VBProject に触れるかを記録する
16. VBE が書き込み時に行う整形を記録する（`x=1` → `x = 1`、キーワードの大文字化、行末空白の削除）。別のモジュールへの Push で、無関係なモジュールの大文字小文字が変わるか（プロジェクト全体で揃えるか）を記録する
17. 約 100 モジュール・合計約 10 万行のブックで、全件の読み取り（作成・Pull）と Push の所要時間を測る（UI を止めないこと、時間切れに入らないこと）
18. VBE の文字コード（OS の ANSI コードページ）が、日本語環境で CP932 固定か、`〜`（U+301C）などがどう扱われるかを記録する
19. VBE を前面化できるか（`ShowInVbeAsync`）、Windows が前面化を許さない場合の回避が効くか

---

## 11. ドキュメントの更新

- `docs/仕様書_Graft_v2.1.md`: 「VBA 連携」の章を追加する（この文書の 1〜9 を要約し、詳細はこの文書を参照させる。この文書は `docs/仕様書_VBA連携.md` として置く）
- `docs/取扱説明書.md`: 使い方、トラストセンターの設定、制限事項（フォームのデザイン、パスの比較、OneDrive／SharePoint 上のブックは使えないこと、削除と名前変更は自動反映しないこと、新規モジュールの取り消しで Excel 側に残ること、日本語版 Windows 前提）。ミラーを Git 管理する場合、Pull による変更は履歴にもコミットにも入らないので、未コミットの差分が溜まることも書く
- `docs/変更履歴.md`: 該当版の節
- `docs/Windows実機検証手順.md`: 10.3 の節
- `README.md`: 主な機能と動作環境に「Windows 版のみ: Excel VBA の直接編集」を追記する。既知の制限にも追記

---

## 12. 実装の段階

一度に全部入れず、次の順で進めて、段階ごとにテストを全件通すこと。

| 段階 | 内容 | 完了条件 |
|---|---|---|
| P1 | `Core/Vba` の純ロジックとテスト、`IVbaBridge` と Null 実装と注入（3.3）、`Project.Vba` の追加、`ErrorCodes`（E801〜E814）、`Settings.Vba` と `SettingsViewModel` の保存、`AppPaths`／`DataDirectory` の追加と移行時の付け替え、`ProjectStore`（場所の変更・削除）、`ProjectMatcher` の同率対応 | `Graft.Tests` 全件成功、既存の UI テストに影響なし |
| P2 | `WindowsVbaBridge`（ROT、スレッドと時間切れ、再試行、読み書き）、プロジェクト作成、手動の Pull / Push（7.2、7.2b） | 実機検証 1〜7、12、13〜16 |
| P3 | 解析前の自動 Pull、解析後の再検査、E814／E811 の検査、適用後の自動 Push、Push 失敗時の選択と取り消し、衝突ダイアログ、`rolledBack` の構造変更 | UI テスト全件、実機検証 8、9 |
| P4 | 取り消しと復元の連動（`AfterRestoreAsync` の3経路）、VBE で開く、バッジ、右クリックメニュー、設定パネル、ドキュメント更新 | 全テスト成功、実機検証 10、11、17〜19 |

---

## 13. 実装時の注意（まとめ）

- **変更しないもの**は 1.2 の表のとおり: `ApplyEngine`、`PathGuard`、`SafeFileWriter`、`RevisionStore`、`BackupManager`、`EncodingDetector`、`RevisionRestorer` 本体。これらを変更したくなったら、手を止めて確認を取ること。この機能の前提が崩れるため。特に、**履歴の `HashAfter` を更新する API をコアに足して、ミラーを書き戻す方式にしてはいけない**（4.3 の2本の基準で吸収する）。
- **手を入れるもの**は 1.2 の表のとおり: `MainViewModel.Apply.cs` の `rolledBack` 周りの構造、`MainViewModel.cs`、`MainViewModel.Hooks.cs`、`HistoryPaneViewModel` のフック追加、`ProjectMatcher`、`AppPaths`／`DataDirectory`、`ProjectStore`、`SettingsViewModel`／`SettingsStore`／`Settings`、`ContextCollectViewModel`、`QuickOpenFileEnumerator`／`FileTreeService`、`ErrorCodes.cs`、プラットフォーム層の各クラス。変更は最小限にし、コメントに「なぜ」を書くこと。
- COM オブジェクトの解放漏れは、Excel のプロセスが残る形でしか見えない。実機検証 12 を必ず行うこと。
- Excel のトラストセンター設定やレジストリを、Graft から書き換えないこと。
- 自動 Pull でミラーを書き換えるときも、既存の履歴（リビジョン）には記録しない。履歴は「Graft が適用した変更」だけを持つ。代わりに、Pull したことはログ（`Logger`）に残す。
- 性能の約束を守る: 10万行のファイルでも編集が滞らないこと。VBA 連携はエディタの保存経路と `DocumentSession` に触れない（保存時の自動 Push は入れない）。AvaloniaEdit は 11.1.0 に固定されているので、上げない。
- 失敗やスキップがあった場合は、出力を添えて正直に報告すること。COM まわりは Linux の CI では確認できないので、「実機未確認」の項目は完了報告で明記すること。付録B の項目は、確かめた結果をこの文書に反映して版を上げること。

---

## 付録A. レビュー指摘との対応

v0.1 のレビュー（指摘26件）が、v0.2 のどこに反映されたかを示す。

| # | 指摘（要旨） | 反映先 |
|---|---|---|
| A1 | VBE 整形の書き戻しが `HashAfter` を壊す／末尾改行の定義 | 4.3（2本の基準）、5.5-4、6.3、6.5 |
| A2 | Push 失敗の取り消しに `UndoLatestAsync` は使えない／`rolledBack` の構造 | 7.3-4-6、7.3-5 |
| A3 | 取り消し・復元後の Push の経路と待てるフック | 7.4（`AfterRestoreAsync`、3経路） |
| A4 | 新規モジュールの取り消し・DELETE・RENAME が自動 Pull で復活する | 6.5（削除保留）、7.3-2（E814）、7.3-4-6 |
| A5 | 既定のミラー位置と保存先の移行・場所の変更・削除 | 4.2、4.4 |
| A6 | プロジェクト自動判定が VBA プロジェクト複数で誤作動 | 7.3-0（`ProjectMatcher` の同率対応） |
| A7 | COM が固まるとアプリを閉じられない | 5.3（呼び出し全体の時間切れ）、5.4（E813）、7.3-4-2 |
| A8 | 全文置き換えの途中失敗とプロシージャ属性の消失 | 5.5（控え・書き戻し・行範囲方式への切り替え）、10.3-6 |
| B9 | 拡張子の許可 | 4.1（実効の許可拡張子） |
| B10 | 解析後の検査点の不足／差し込み位置の訂正 | 7.3-1、7.3-3 |
| B11 | 手編集の反映と手動 Push が未定義 | 7.2b |
| B12 | 自動 Pull と未保存の編集 | 7.2（Pull 前の未保存確認） |
| B13 | コンテキスト収集の自動 Pull の位置 | 7.2（`InitializeAsync`／`RefreshAsync` の前） |
| B14 | CP932 の検査が遅い／文字の例の訂正 | 6.4、7.3-2 |
| B15 | 5.5 のしきい値、VBE のプロジェクト全体の大文字小文字揃え、設定の食い違い | 5.5-4、6.5、8 |
| B16 | `IVbaBridge` の形と注入方法 | 5.1、3.3 |
| B17 | 実在しない既存 UI・仕組みへの依存 | 7.1、7.6、8、7.6 の VBE 前面化 |
| B18 | 設定の追加（`BuildSettingsFromFields`） | 8 |
| B19 | エラーコードの書式、E801／E812 の発生条件 | 9 |
| B20 | ROT まわり（OneDrive、権限、STA／MTA） | 5.2、5.3、付録B |
| C21 | 用語・章の食い違い、3.1 のファイル一覧 | 1.2、1.3、3.1 |
| C22 | ミラーの読み方（文字コード） | 4.2 |
| C23 | パスの制約（入れ子、予約名） | 6.1、6.2 |
| C24 | Git | 11（取扱説明書）、13 |
| C25 | テスト計画の漏れ | 10 |
| C26 | 性能 | 7.2b、10.2、13 |

## 付録B. 実機で要確認の事項（推測）

次は、この版の仕様の前提だが、Linux の環境では確かめられなかった。Windows 実機で確かめ、結果をこの文書に反映すること。

| 事項 | 前提としている見込み | 確認する場所 |
|---|---|---|
| プロシージャ属性の消失 | 全文置き換えで `VB_UserMemId`、`VB_Description`、ショートカットキーが失われる。失われるなら行範囲方式へ | 5.5、10.3-6 |
| VBE の整形 | 書き込み時に空白・キーワードの大文字小文字を直す。識別子の大文字小文字はプロジェクト全体で揃う | 5.5-4、6.5、10.3-16 |
| 呼び出しが戻らない状況 | マクロ実行中・中断モード・VBE のモーダル表示中は、拒否されず戻らない | 5.3、10.3-9 |
| `VBProject.Mode` | デザインモード（1）かどうかでマクロ実行中・中断中を検出できる | 5.4、10.3-9 |
| STA か MTA か | どちらで安定するかは実測で決める | 5.3、10.3-12 |
| OneDrive／SharePoint | `Workbook.FullName` が URL になる。ROT の表示名がどうなるか | 5.2、10.3-13 |
| ROT の見え方 | 権限が違うと見えない。保護ビューのブックは VBProject に触れない。`.xlam` は ROT に出る | 5.2、10.3-14、10.3-15 |
| 同じブックを2つの Excel で開いたとき | 読み取り専用側の扱い | 5.2、10.3-11 |
| VBE の文字コード | OS の ANSI コードページ（日本語環境では CP932）。`〜`（U+301C）などの扱い | 1.4、6.4、10.3-18 |
| 大規模ブックの所要時間 | 約 100 モジュール・約 10 万行で時間切れに入らない | 10.3-17 |
| VBE の前面化 | Windows が前面化を許さない場合の回避が効く | 7.6、10.3-19 |
| 時間切れで捨てたスレッド | Excel のプロセスが残らない | 5.3、10.3-12 |
