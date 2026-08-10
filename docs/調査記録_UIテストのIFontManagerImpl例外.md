# 調査記録: UIテストでCIが不定期に落とす `IFontManagerImpl` 例外

対象: `tests/Graft.UiTests`（Avalonia.Headless 11.2.3 / xUnit 2.7.0）
最終更新: 2026-08-10（`217a18c` 時点での調査）

## 症状

CI（Ubuntu ランナー、2コア相当）で不定期に、実行順序上たまたま最後にディスパッチャの
キューが流れたテストが次の例外で失敗する。失敗するテスト名は毎回変わり、失敗した
テスト自身はウィンドウを一切生成していないことも多い（巻き添え）。

```
System.InvalidOperationException : Unable to locate 'Avalonia.Platform.IFontManagerImpl'.
   at Avalonia.Media.FontManager.get_Current()
   at Avalonia.Media.TextFormatting.TextFormatterImpl.FormatLine(...)
   at AvaloniaEdit.Rendering.TextView.BuildVisualLine(...)
   at AvaloniaEdit.Rendering.TextView.CreateAndMeasureVisualLines(Size availableSize)
   at AvaloniaEdit.Rendering.TextView.MeasureOverride(Size availableSize)
```

## 仕組み（逆コンパイルで確認済み）

`Avalonia.Headless.XUnit`（`AvaloniaTestFramework`）は、xUnitの標準ランナーを
オーバーライドし、**アセンブリ内の全テストを`HeadlessUnitTestSession`経由でシリアルに**
（`SetupSyncContext(1)`で強制的に並列度1）実行する。テストケース1件ごとに
`HeadlessUnitTestSession.Dispatch(...)`を呼び、その内部では次の3段階が**1テストごとに**
起きる。

1. `EnsureApplication()`が`AvaloniaLocator.EnterScope()`で新しいスコープに入り、
   `Dispatcher.ResetForUnitTests()`→`AppBuilder.SetupUnsafe()`でアプリを再構築する
   （`IFontManagerImpl`等のプラットフォームサービスはこのスコープに登録される）。
2. テスト本体（コンストラクタ→テストメソッド→`Dispose()`→`AfterTestMethodInvokedAsync`）が
   実行され、その直後に`Avalonia.Headless.XUnit`側が`Dispatcher.UIThread.RunJobs(null)`を
   自動で1回呼ぶ（`AvaloniaTestCaseRunner.RunTest`の`onAfterTestInvoked`）。
3. `using`ブロックを抜けるときに`EnsureApplication()`の後始末が走る。**この順序が
   `scope.Dispose()`（`IFontManagerImpl`registrationの破棄）→`Dispatcher.ResetForUnitTests()`
   （保留ジョブの破棄）の順**（`Avalonia.Headless`側のソース、`HeadlessUnitTestSession.
   EnsureApplication()`のローカル関数を逆コンパイルして確認済み）。

つまり、**2の自動`RunJobs()`で拾いきれなかった保留中のレイアウト・描画ジョブ**が
3の`scope.Dispose()`の後に走ると、フォント基盤（`IFontManagerImpl`）が消えた状態で
`AvaloniaEdit.Rendering.TextView.MeasureOverride`が呼ばれ、この例外になる。

重要な点: このスコープは**テストごと**に作り直されるため、「テストAで漏らした保留ジョブが
テストBの実行中に例外を起こす」ためには、テストAのウィンドウが**テストA自身の
`EnsureApplication()`スコープが破棄されるまでの間に**新たな描画ジョブを積む必要がある
（例: `Window.Show()`しただけでレイアウトが未完了、キャレット点滅等の
`DispatcherTimer`が生きたままになっている、等）。`Dispatcher.ResetForUnitTests()`は
毎テストの開始・終了両方で呼ばれるため、原理的には「テストをまたいで」漏れが持ち越される
はずはないが、実際には`ShownWindowTracker`導入前後でCIの再現頻度が変わった実績があり、
**閉じ忘れたウィンドウ・ダイアログが同一テストの後始末フェーズで問題を起こす**という
説明が最も整合する。

## これまでの対策と再発

- `8613d1f`: `tests/Graft.UiTests/TestSupport/ShownWindowTracker.cs`を新設。
  `Show()`したウィンドウを`Track()`しておき、テストの`Dispose()`で逆順に`Close()`＋
  最後に`Dispatcher.UIThread.RunJobs()`を呼ぶ。一度はCIが安定したが、その後の機能追加
  （`EditorTabReorderTests`等）で再発した。

## 今回（`217a18c`時点）の調査

### 試した再現条件

すべて`taskset`でCPUを制限し、`dotnet test tests/Graft.UiTests -c Release --no-build`を
実行して確認した（実行順序はxUnitの既定の発見順で固定、`AvaloniaTestFramework`が並列度を
強制的に1にするため試行間で順序は変わらない）。

| # | 条件 | 結果 |
|---|---|---|
| 1 | `taskset -c 0,1` + `DOTNET_ThreadPool_ForceMinWorkerThreads=1`/`ForceMaxWorkerThreads=2` | **15分でセッションタイムアウト**（`user`時間はわずか5秒 = ほぼ無音のハング。`IFontManagerImpl`ではなく別種の詰まり。スレッドプールを極端に絞る設定とheadlessの内部実装の組み合わせが原因と推測。**この設定はCIでは使われていないため、以降の試行では外した**） |
| 2 | `taskset -c 0,1`（スレッドプール設定なし） | 成功（391/391） |
| 3 | `taskset -c 0,1` | 成功（391/391） |
| 4 | `taskset -c 0,1` | **1件失敗** — ただし`IFontManagerImpl`ではなく`NewFileRevealTests.拡張子なしのファイル名でも作成できる`が`explorer.SelectedNode`のnull比較で失敗（ファイル監視・ツリー反映のタイミング起因と見られる、別種の既存の不安定さ。詳細は下記） |
| 5 | `taskset -c 0,1` | 成功（391/391） |
| 6 | `taskset -c 0,1` + 他2コアをビジーループで飽和 | 成功（391/391） |
| 7 | `taskset -c 0,1` + 他2コアをビジーループで飽和 | 成功（391/391） |
| 8 | `taskset -c 0` （1コアのみ） | 成功（391/391） |

**`IFontManagerImpl`例外そのものは今回も再現できなかった。** 正直に報告する。

### 副産物: 別の不安定なテストを発見（本タスクの対象外、修正は見送り）

試行4で`NewFileRevealTests.拡張子なしのファイル名でも作成できる`が
`explorer.SelectedNode`のnull比較で失敗した。スタックトレースに`IFontManagerImpl`は
含まれず、`FluentAssertions`の通常のアサーション失敗であり、症状が異なる
（ファイルシステムウォッチャー由来のタイミング不安定と見られる）。**本タスクの対象
（`IFontManagerImpl`）ではないため、ここでは修正しない。** 別枠での調査を推奨する。

### コードレビューで見つけた、確度の高い閉じ忘れ

再現は取れなかったが、`ShownWindowTracker`のドキュメントが明言する「閉じ忘れ」の実例を
複数、コードレビューで特定した。**特に1件は、後述のとおり実際に毎回発生していたことを
実行して確認済み**（不定期どころか常時再現するバグだった）。

1. **`tests/Graft.UiTests/ShortcutsWindowTests.cs`（確度: 高・実証済み）**
   `ツールバーのボタンで一覧が要求される`・
   `フォーカスが無い間はCtrlスラッシュで一覧が要求される`の2テストは、
   `shell.RequestOpenShortcuts`へ検証用ハンドラを追加購読するだけのつもりだったが、
   `ShellWindow`のコンストラクタも同じイベントを購読しており（`OpenShortcutsCommand`→
   `RequestOpenShortcuts`→`ShellWindow.OnRequestOpenShortcuts`→
   `new ShortcutsWindow(); _ = window.ShowDialog(this)`）、**テストが実際にボタン/
   ショートカットを操作すると本物の`ShortcutsWindow`が開く**。テストはその参照を
   持たないため`ShownWindowTracker`へ`Track()`できず、閉じ忘れたまま終わっていた。
   下記「今回の修正」で実証・修正済み。

2. **`EditorTests.cs`（確度: 高）** `TextEditor`（AvaloniaEditの`TextView`を内包）を
   載せた`Window`を`Show()`するテストが複数あるが、`Close()`も`ShownWindowTracker`への
   登録もしていなかった。例外のスタックトレースが指す`AvaloniaEdit.Rendering.TextView`を
   直接含む唯一のテストファイル群であり、最有力候補。

3. **`TabCloseButtonVisibilityTests.cs`（確度: 高）** `EditorPane`（AvaloniaEditの
   `TextEditor`を内包）を載せた`Window`を`Show()`するが、同様に閉じ忘れていた。

4. **`SearchReplaceAllTests.cs` / `SearchPerformanceTests.cs`（確度: 中）**
   `SearchView`を載せた`Window`を`Show()`するが閉じ忘れていた。AvaloniaEditは
   含まないが、`ShownWindowTracker`の方針（「表示されたまま終わるとレイアウトが
   保留になりうる」）に従えば同じリスクを持つ。

5. **`ControlThemeTests.cs`（確度: 低）** 同様に閉じ忘れていたが、各操作の直後に
   必ず`CaptureRenderedFrame()`で強制的にレイアウト・描画を確定させているため、
   テスト終了時点で保留ジョブが残っている可能性は他より低い。念のため揃えた。

いずれも「表示したまま誰も`Close()`しない」という、`ShownWindowTracker`のドキュメントが
警告する典型パターンに一致する。

## 今回の修正

### 1. 上記5ファイルすべてを`ShownWindowTracker`経由の後始末に統一

`EditorTests.cs`・`TabCloseButtonVisibilityTests.cs`・`SearchReplaceAllTests.cs`・
`SearchPerformanceTests.cs`・`ControlThemeTests.cs`を`IDisposable`にし、
`Show()`するすべての`Window`を`_windows.Track(...)`でラップした。

### 2. 再発防止の仕組み: `ShownWindowTracker`に「閉じ忘れたオーナー付きダイアログ」の検出を追加

`ShownWindowTracker.Dispose()`で、`Track()`済みウィンドウを閉じる**直前**に
`Window.OwnedWindows`（`ShowDialog(owner)`で開かれ、まだ閉じられていない子ウィンドウ）を
確認するようにした。見つかった場合はベストエフォートで閉じたうえで、**全ウィンドウの
後始末とRunJobs()が終わったあとに`InvalidOperationException`を投げて、そのテスト自身を
失敗させる**（後始末そのものはフォント基盤が生きているうちに必ず終える設計のため、
アサーション追加による新たな不安定要因にはならない）。

この仕組みは**上記1のShortcutsWindowTestsの閉じ忘れを実際に検出した**
（修正前のコードで実行すると次の2件が確実に失敗する。不定期ではなく常時再現する）。

```
Failed ツールバーの「?」ボタンでショートカット一覧が要求される
System.InvalidOperationException : ShowDialog等で開かれたまま閉じられていないウィンドウを
検出しました: ShortcutsWindow(owner=ShellWindow)。...

Failed テキスト入力欄・エディタにフォーカスが無い間はCtrl+/で一覧が要求される
System.InvalidOperationException : ShowDialog等で開かれたまま閉じられていないウィンドウを
検出しました: ShortcutsWindow(owner=ShellWindow)。...
```

`ShortcutsWindowTests.cs`側は、イベント発火後に`window.OwnedWindows`を辿って
実際に開いたダイアログを閉じるよう修正した（`CloseRealShortcutsDialogIfOpened`）。

この仕組みは「`Track()`済みウィンドウのオーナー付き子ウィンドウ」だけを対象にしており、
`ShownWindowTracker`を使っていないテストクラスの漏れや、オーナーの無い独立した
`Window`の漏れまでは検出できない（後者は今回1〜5の修正で個別に手当てした）。
Avalonia内部APIへのリフレクション等、より網羅的な検出は不安定化のリスクがあると
判断し見送った。

## 次に再発したときに試すべきこと

- `IFontManagerImpl`例外が出たら、まず**CIのログでその直前に完了したテストクラス**を
  確認する（`[xUnit.net ...] Finished`の直前に走っていたテスト）。巻き添え先ではなく、
  漏らした側の特定に直結する。
- `ShownWindowTracker`を使っていないテストクラスで`new Window`ないし`Show()`を
  していないか、`grep -rn "\.Show()\|new Window("`と`grep -rln ShownWindowTracker`の
  差分を都度取る（本調査で使った手法。今回は該当5ファイルを検出できた）。
- `AvaloniaEdit`のキャレット点滅用`DispatcherTimer`など、`CaptureRenderedFrame()`後にも
  非同期に再度レイアウトを要求しうる仕掛けが無いか（`Dispatcher.ResetForUnitTests()`が
  毎テストの前後で呼ばれるため通常は持ち越されないはずだが、`RunJobs()`と
  `ResetForUnitTests()`の間のごく短い窓で新規ジョブが積まれる経路が無いかは
  未検証）。
- 診断コードとして、`ShownWindowTracker.Dispose()`の`RunJobs()`直後に
  `Dispatcher.UIThread`の保留ジョブ数を（内部APIへのリフレクション経由で）ログ出力する
  仕組みを一時的に仕込むと、「後始末後にまだ保留があるテスト」を機械的に洗い出せる
  可能性がある（今回は時間の制約と、内部APIへの依存が生む不安定化リスクを踏まえて
  見送った）。
- CI環境（実際のGitHub Actions Ubuntuランナー）特有のタイミング（コンテナの
  スケジューリング揺らぎ等）がローカルの`taskset`では再現しない可能性がある。
  再発時はCIのジョブに一時的な診断ログ出力を仕込んで実行するのが最も確実。
