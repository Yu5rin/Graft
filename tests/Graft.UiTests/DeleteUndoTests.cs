using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 課題2: ごみ箱削除のアプリ内復元（Ctrl+Z）のキー配線・アプリ終了時の掃除を、実際の
/// ShellWindow経由のキー操作で検証する。退避→復元の往復自体（ファイル・フォルダ・複数件
/// スタック・同名衝突）は<see cref="DeleteUndoStore"/>単体でGraft.Tests側（高速・確実）が
/// 押さえているため、ここではキールーティングとステータスバー通知・終了時の掃除に絞る。
///
/// エディタ本体（AvaloniaEdit）へのheadlessでのキー入力ルーティングは、他のテスト
/// （ShortcutsWindowTests・EditorTests参照）と同じ理由で素直に届かないため、「テキスト入力欄に
/// フォーカスがある」ことは標準のTextBox（クイックオープンの検索欄）で代表させる。
/// ShellWindow.Keyboard.csのinTextInput判定はTextBox/TextPresenter/TextAreaのいずれでも
/// 同じ分岐（早期return）を通るため、これでエディタのCtrl+Zと同じ非干渉を担保できる。
/// </summary>
public class DeleteUndoTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-delete-undo", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;

    public DeleteUndoTests()
    {
        _appDirectory = Path.Combine(_root, "app");
        _projectDirectory = Path.Combine(_root, "project");
        Directory.CreateDirectory(_appDirectory);
        Directory.CreateDirectory(_projectDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果に影響しない。
        }

        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "削除するとステータスバーに案内が出て、エクスプローラにフォーカスがある状態のCtrl+Zで元の場所へ戻る（内容も一致）")]
    public async Task エクスプローラフォーカス中のCtrlZで削除が元に戻る()
    {
        var filePath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(filePath, "取り消しの確認用の内容\n2行目").ConfigureAwait(true);

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await WaitForAsync(() => shell.Explorer.RootNodes.Count > 0).ConfigureAwait(true);

        // エクスプローラのペインを表示し、ツリーへフォーカスを移した状態で削除する
        // （実際の操作と同じ順序。削除直後はツリーの再構築でフォーカスを失うため、
        // ExplorerView.axaml.csが取り消し通知と同時にフォーカスを戻す仕組みも合わせて検証する）。
        shell.SelectSideView(SideViewKind.Explorer);
        // Avalonia Headlessでは、直前まで非表示だった領域（IsVisible=falseからtrueへ切り替わった
        // 直後のExplorerViewControl）が実際にレイアウト・アタッチされる（Control.Focus()が
        // 成立する前提）には、レイアウト・レンダリングのジョブを処理させる必要がある
        // （PumpDispatcherのコメント参照）。ここで一度明示的に進めてから最初のFocus()を呼ぶ。
        PumpDispatcher();
        var treeView = window.GetControl<ExplorerView>("ExplorerViewControl").GetControl<TreeView>("FileTreeView");
        treeView.Focus();
        var focusCounter = new FocusRequestCounter(shell.Explorer);

        var node = shell.Explorer.RootNodes.Single(n => n.Name == "sample.txt");
        shell.Explorer.SelectedNode = node;
        var beforeDelete = focusCounter.Count;
        shell.Explorer.DeleteCommand.Execute(node);
        await WaitForAsync(() => !File.Exists(filePath)).ConfigureAwait(true);

        // ファイルが消えるのはDeleteNodeAsyncの最初の段階（_treeService.DeleteAsync）に過ぎず、
        // その後のReconcileDirectoryAsync（ツリー再構築でフォーカス喪失）・RequestFocus()
        // （ExplorerView.OnFocusRequestedがFileTreeView.Focus()を呼ぶ）はまだ終わっていない
        // ことがある。ここでCtrl+Zを押すタイミングを急ぐと、フォーカスが戻り切る前に
        // キーが飛んでShellWindow.Keyboard.csのCtrl+Z判定（ExplorerViewControlの子孫に
        // フォーカスがあるか）から外れ、取り消しが発火しないことがある（負荷下のフレーク要因）。
        await WaitForDeleteFocusReadyAsync(shell.Explorer, focusCounter, beforeDelete, window, treeView).ConfigureAwait(true);

        shell.Explorer.HasDeleteUndoNotice.Should().BeTrue("削除直後はステータスバーへ「Ctrl+Zで元に戻せます」の案内を出す必要がある");
        shell.Explorer.DeleteUndoNoticeText.Should().Contain("sample.txt").And.Contain("Ctrl+Z");

        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        await WaitForAsync(() => File.Exists(filePath)).ConfigureAwait(true);

        File.Exists(filePath).Should().BeTrue("Ctrl+Zで元の場所へ復元される必要がある");
        (await File.ReadAllTextAsync(filePath).ConfigureAwait(true)).Should().Be("取り消しの確認用の内容\n2行目", "内容も完全に一致している必要がある");
    }

    [AvaloniaFact(DisplayName = "フォルダ（複数ファイル入り）を削除した場合も、Ctrl+Zで丸ごと元に戻る")]
    public async Task フォルダ削除もCtrlZで丸ごと戻る()
    {
        var folderPath = Path.Combine(_projectDirectory, "folder");
        Directory.CreateDirectory(folderPath);
        await File.WriteAllTextAsync(Path.Combine(folderPath, "a.txt"), "A").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(folderPath, "b.txt"), "B").ConfigureAwait(true);

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await WaitForAsync(() => shell.Explorer.RootNodes.Count > 0).ConfigureAwait(true);

        shell.SelectSideView(SideViewKind.Explorer);
        PumpDispatcher(); // 直前まで非表示だったExplorerViewControlを実際にアタッチする（PumpDispatcher参照）。
        var treeView = window.GetControl<ExplorerView>("ExplorerViewControl").GetControl<TreeView>("FileTreeView");
        treeView.Focus();
        var focusCounter = new FocusRequestCounter(shell.Explorer);

        var node = shell.Explorer.RootNodes.Single(n => n.Name == "folder");
        var beforeDelete = focusCounter.Count;
        shell.Explorer.DeleteCommand.Execute(node);
        await WaitForAsync(() => !Directory.Exists(folderPath)).ConfigureAwait(true);

        // フォルダが消えた直後は、まだツリー再構築（ReconcileDirectoryAsync）とそれに続く
        // RequestFocus()（FileTreeView.Focus()）が終わっていないことがある。フォーカスが
        // 戻り切る前にCtrl+Zを押すと削除の取り消しが届かないため、HasDeleteUndoNoticeが
        // trueになったこと（RequestFocus()の呼び出し直前まで進んだ証拠）と、ツリーへ実際に
        // キーボードフォーカスが入っていることの両方を待ってから押す。
        await WaitForDeleteFocusReadyAsync(shell.Explorer, focusCounter, beforeDelete, window, treeView).ConfigureAwait(true);

        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        await WaitForAsync(() => File.Exists(Path.Combine(folderPath, "a.txt")) && File.Exists(Path.Combine(folderPath, "b.txt"))).ConfigureAwait(true);

        Directory.Exists(folderPath).Should().BeTrue();
        (await File.ReadAllTextAsync(Path.Combine(folderPath, "a.txt")).ConfigureAwait(true)).Should().Be("A");
        (await File.ReadAllTextAsync(Path.Combine(folderPath, "b.txt")).ConfigureAwait(true)).Should().Be("B");
    }

    [AvaloniaFact(DisplayName = "連続で2件削除すると、エクスプローラフォーカス中のCtrl+Zを2回押すことで新しい順に戻る")]
    public async Task 連続削除はCtrlZ2回で新しい順に戻る()
    {
        var first = Path.Combine(_projectDirectory, "first.txt");
        var second = Path.Combine(_projectDirectory, "second.txt");
        await File.WriteAllTextAsync(first, "1件目").ConfigureAwait(true);
        await File.WriteAllTextAsync(second, "2件目").ConfigureAwait(true);

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await WaitForAsync(() => shell.Explorer.RootNodes.Count >= 2).ConfigureAwait(true);

        shell.SelectSideView(SideViewKind.Explorer);
        PumpDispatcher(); // 直前まで非表示だったExplorerViewControlを実際にアタッチする（PumpDispatcher参照）。
        var treeView = window.GetControl<ExplorerView>("ExplorerViewControl").GetControl<TreeView>("FileTreeView");
        treeView.Focus();
        var focusCounter = new FocusRequestCounter(shell.Explorer);

        var beforeDeletes = focusCounter.Count;
        shell.Explorer.DeleteCommand.Execute(shell.Explorer.RootNodes.Single(n => n.Name == "first.txt"));
        await WaitForAsync(() => !File.Exists(first)).ConfigureAwait(true);
        shell.Explorer.DeleteCommand.Execute(shell.Explorer.RootNodes.Single(n => n.Name == "second.txt"));
        await WaitForAsync(() => !File.Exists(second)).ConfigureAwait(true);

        // 2件目の削除処理（ReconcileDirectoryAsync→RequestFocus()）がまだ完了していない
        // ことがあるため、1回目のCtrl+Zを押す前にフォーカスが戻り切るまで待つ（詳細は
        // WaitForDeleteFocusReadyAsyncのコメント参照）。
        await WaitForDeleteFocusReadyAsync(shell.Explorer, focusCounter, beforeDeletes, window, treeView).ConfigureAwait(true);

        var beforeFirstUndo = focusCounter.Count;
        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        await WaitForAsync(() => File.Exists(second)).ConfigureAwait(true);
        File.Exists(second).Should().BeTrue("後から削除した2件目が1回目のCtrl+Zで先に戻るはず");
        File.Exists(first).Should().BeFalse("1件目はまだ戻していない");

        // 取り消し（UndoDeleteAsync）も_undoStore.UndoAsync（ファイル復活）→RefreshAsync
        // （ツリー再構築でフォーカス喪失）→RequestFocus()という同種の非同期の隙間を経て進む。
        // ただしUndoDeleteAsyncは冒頭でHasDeleteUndoNoticeをfalseへ戻し、取り消し対象が
        // まだ残っていても再びtrueにはしないため、ここではツリーへ実際にキーボードフォーカスが
        // 戻ったこと・今回のCtrl+ZでRequestFocus()が新たに発火したこと（focusCounter参照）を
        // 待つ（2回目のCtrl+Zが同じ隙間でフレークするのを防ぐ）。
        await WaitForUndoFocusReadyAsync(focusCounter, beforeFirstUndo, window, treeView).ConfigureAwait(true);

        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        await WaitForAsync(() => File.Exists(first)).ConfigureAwait(true);
        File.Exists(first).Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "テキスト入力欄にフォーカスがある間のCtrl+Zは削除の取り消しを発火しない（エディタのCtrl+Z非干渉）")]
    public async Task テキスト入力中はCtrlZで誤発火しない()
    {
        var filePath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(filePath, "内容").ConfigureAwait(true);

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await WaitForAsync(() => shell.Explorer.RootNodes.Count > 0).ConfigureAwait(true);

        var node = shell.Explorer.RootNodes.Single(n => n.Name == "sample.txt");
        shell.Explorer.DeleteCommand.Execute(node);
        await WaitForAsync(() => !File.Exists(filePath)).ConfigureAwait(true);
        // ファイルが消えた直後は、まだDeleteNodeAsync内のReconcileDirectoryAsync・
        // HasDeleteUndoNotice=trueへの代入が終わっていないことがある（このテストはツリーへの
        // フォーカスは検証対象外のため、通知フラグの確定だけを待てば十分）。
        await WaitForAsync(() => shell.Explorer.HasDeleteUndoNotice).ConfigureAwait(true);
        shell.Explorer.HasDeleteUndoNotice.Should().BeTrue();

        // クイックオープン（Ctrl+P）を開き、検索欄（標準のTextBox）へフォーカスを移す
        // （ShortcutsWindowTestsと同じ手法。テキスト入力欄にフォーカスがある状況の代表）。
        window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control);
        shell.QuickOpen.IsOpen.Should().BeTrue();
        await SettleAsync().ConfigureAwait(true);

        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        await SettleAsync().ConfigureAwait(true);

        File.Exists(filePath).Should().BeFalse(
            "テキスト入力欄にフォーカスがある間のCtrl+Zは、エディタの取り消し（ここではネイティブ処理へ委ねる）に使われるべきで、削除の取り消しを誤発火してはならない");
        shell.Explorer.HasDeleteUndoNotice.Should().BeTrue("取り消しが実行されていないので通知も消えていないはず");
    }

    [AvaloniaFact(DisplayName = "アプリ終了時（ExplorerViewModel.Dispose）に退避ディレクトリ（back/trash/）が空になる")]
    public async Task 終了時に退避が掃除される()
    {
        var filePath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(filePath, "内容").ConfigureAwait(true);

        var appPaths = new AppPaths(_appDirectory);
        var (shell, window) = await OpenShellAsync(appPaths).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await WaitForAsync(() => shell.Explorer.RootNodes.Count > 0).ConfigureAwait(true);

        var node = shell.Explorer.RootNodes.Single(n => n.Name == "sample.txt");
        shell.Explorer.DeleteCommand.Execute(node);
        await WaitForAsync(() => !File.Exists(filePath)).ConfigureAwait(true);
        shell.Explorer.HasDeleteUndoNotice.Should().BeTrue();

        Directory.Exists(appPaths.TrashStagingDirectory).Should().BeTrue("削除直後は退避コピーが残っているはず");
        Directory.EnumerateFileSystemEntries(appPaths.TrashStagingDirectory).Should().NotBeEmpty();

        // StartupCoordinator.DisposeAsyncが実際に呼ぶ経路（ShellViewModel.Dispose→Explorer.Dispose）。
        shell.Dispose();

        Directory.Exists(appPaths.TrashStagingDirectory).Should().BeFalse(
            "セッション内のみ保持する方針のため、アプリ終了時には退避コピーを必ず消す（OSのごみ箱側には残る）");
    }

    /// <summary>
    /// 条件が満たされるかタイムアウトするまで待つ（非同期の確定待ち。他のテストと同じ作法）。
    /// タイムアウトは実時間で約5秒（旧: 200回×10ms≒2秒固定だったが、これでは待ちきれずに
    /// フレークする余地があった）。ループ回数ではなくStopwatchによる実時間で打ち切る。
    /// CPU負荷下ではTask.Delay(10)自体がスケジューリング遅延で10msを大きく超えることがあり、
    /// 単純な回数基準（例: 500回ループ）では実際のタイムアウトが意図した長さから
    /// 大きくずれてしまうため。条件が満たされ次第ループを抜けるため、通常時（負荷がない場合）の
    /// 実行時間が延びることはない。
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < 5000)
        {
            PumpDispatcher();
            await Task.Delay(10).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// レイアウト・レンダリングのジョブを明示的に処理させる。
    /// 調査で判明した点: Avalonia Headlessでは、直前まで非表示だった領域
    /// （サイドビュー切り替えでIsVisible=falseからtrueになった直後のExplorerViewControlなど）が
    /// 実際にレイアウトされ視覚ツリーへアタッチされる（Control.Focus()が成立する前提条件）には、
    /// Dispatcher.UIThread.RunJobs()等でレイアウト・レンダリングのジョブを処理させる必要がある。
    /// await Task.Delay(10)によるポンピングだけでもいずれは処理されるが、それがいつ処理されるかは
    /// CPU負荷に大きく左右され、低負荷では数msで済む一方、高負荷下では実測で秒単位に伸びることを
    /// 確認した。その間はTreeView.Focus()が例外を出さずサイレントに失敗し続ける
    /// （戻り値がfalseになるだけで、テストからは気づきにくい）。WaitForAsyncの待ちループ自身に
    /// これを組み込むことで、この不確実性（＝負荷下のフレーク要因のもう一つの正体）を無くす。
    /// </summary>
    private static void PumpDispatcher()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    /// <summary>
    /// 削除直後、次のCtrl+Zが確実に「削除の取り消し」へ届くまで待つ。
    /// ExplorerViewModel.DeleteNodeAsyncは _treeService.DeleteAsync（ここでファイルが実際に
    /// 消える）→ NotifyDeletedAsync → ReconcileDirectoryAsync（ツリー再構築で選択中だった
    /// コンテナが壊れフォーカスを失う）→ HasDeleteUndoNotice=true → RequestFocus()
    /// （ExplorerView.OnFocusRequestedがFileTreeView.Focus()を呼ぶ）という順で進む非同期処理で、
    /// ファイルが消えるのはその最初の段階に過ぎない。ShellWindow.Keyboard.csのCtrl+Z判定は
    /// ExplorerViewControlの子孫に実際にキーボードフォーカスがあるかどうかで決まるため、
    /// 「ファイルが消えたこと」だけを待ってCtrl+Zを押すと、まだツリー再構築中でフォーカスが
    /// 戻り切っていない隙間を突いてしまい、取り消しが発火しないことがある（CPU負荷下でこの
    /// 隙間が広がりフレークになる）。HasDeleteUndoNoticeがtrueになったこと（RequestFocus()の
    /// 呼び出し直前まで進んだ証拠）・ツリーへ実際にキーボードフォーカスが入っていること・
    /// 今回の操作でRequestFocus()が実際に（新たに）発火したこと（focusCounter参照）の
    /// 3つが揃うまで待つことで、この隙間を塞ぐ（テスト側でFocus()を呼び直す誤魔化しはしない）。
    /// </summary>
    private static Task WaitForDeleteFocusReadyAsync(
        ExplorerViewModel explorer, FocusRequestCounter focusCounter, int since, Window window, TreeView treeView)
        => WaitForAsync(() => focusCounter.Count > since && explorer.HasDeleteUndoNotice && IsFocusWithin(window, treeView));

    /// <summary>
    /// 取り消し（Ctrl+Z）直後、次のCtrl+Zが確実に届くまで待つ。UndoDeleteAsyncも
    /// _undoStore.UndoAsync（ここでファイルが復活する）→ RefreshAsync（ツリー再構築で
    /// フォーカス喪失）→ RequestFocus()という、削除時と同種の非同期の隙間を経て進む。
    /// ただしUndoDeleteAsyncは冒頭でHasDeleteUndoNoticeをfalseへ戻し、取り消し対象がまだ
    /// スタックに残っていても再びtrueにはしないため（次の取り消し用の案内は出し直さない
    /// 仕様）、削除直後の待ちとは異なりHasDeleteUndoNoticeは判定材料に使えない。
    /// </summary>
    private static Task WaitForUndoFocusReadyAsync(FocusRequestCounter focusCounter, int since, Window window, TreeView treeView)
        => WaitForAsync(() => focusCounter.Count > since && IsFocusWithin(window, treeView));

    /// <summary>
    /// ExplorerViewModel.FocusRequestedの発火回数を数える。削除・取り消しのたびに
    /// RequestFocus()経由で1回ずつ増える。「対象へ実際にキーボードフォーカスがあるか」だけを
    /// 見る待ちは、直前の操作で既にフォーカスが有る状態のまま次の操作に入った場合、
    /// 今回のReconcileDirectoryAsync/RefreshAsync→RequestFocus()の一巡がまだ完了していなくても
    /// （＝一時的なフォーカス喪失がまだこれから起きる、または起きている最中でも）
    /// 「今フォーカスがある」というだけの理由で素通りしてしまう恐れがある。操作の直前に
    /// 取得したCountより実際に増えたことも合わせて確認することで、待ちが必ず「今回の」
    /// RequestFocus()完了を捉えるようにする。
    /// </summary>
    private sealed class FocusRequestCounter
    {
        private int _count;
        public FocusRequestCounter(ExplorerViewModel explorer) => explorer.FocusRequested += (_, _) => _count++;
        public int Count => _count;
    }

    /// <summary>
    /// ShellWindow.Keyboard.csのCtrl+Z判定（IsDescendant(focused, ExplorerViewControl)）と
    /// 全く同じ方法で「対象へ実際にキーボードフォーカスがあるか」を判定する。
    /// TreeView.IsKeyboardFocusWithinはツリー再構築直後の更新タイミングが製品側の
    /// フォーカス判定と必ずしも一致しないことがあるため、テスト独自の判定基準を新設せず、
    /// 製品コードと同一のwindow.FocusManager.GetFocusedElement()を起点にした祖先探索を使う。
    /// </summary>
    private static bool IsFocusWithin(Window window, Visual ancestor)
    {
        var focused = window.FocusManager?.GetFocusedElement() as Visual;
        while (focused is not null)
        {
            if (ReferenceEquals(focused, ancestor)) return true;
            focused = focused.GetVisualParent();
        }
        return false;
    }

    /// <summary>ディスパッチャに積まれた非同期継続（フォーカス移動等）が終わるまで待つ。</summary>
    private static async Task SettleAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(10).ConfigureAwait(true);
        }
    }

    private Task<(ShellViewModel Shell, ShellWindow Window)> OpenShellAsync() => OpenShellAsync(new AppPaths(_appDirectory));

    private async Task<(ShellViewModel Shell, ShellWindow Window)> OpenShellAsync(AppPaths appPaths)
    {
        appPaths.EnsureCoreDirectoriesExist();

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Settings(),
            new SettingsStore(appPaths),
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            new AutoConfirmDialogService(),
            new AvaloniaUiServices(),
            openSettings: () => { });

        var window = new ShellWindow(shell) { Width = 1280, Height = 800 };
        window.Show();
        await shell.Graft.InitializeAsync().ConfigureAwait(true);
        return (shell, window);
    }

    /// <summary>削除確認等をすべて許諾するダイアログ。ファイル選択系は使わないためnullを返す。</summary>
    private sealed class AutoConfirmDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(true);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(initial ?? "テスト");

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }
}
