using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
/// 「コード反映」の流れにあった不具合2件と改善1件の回帰テスト。
/// <list type="number">
/// <item><description>一部だけ適用できたあと、失敗したブロックが一覧から消えて、理由も「修正を依頼」も失われていた。</description></item>
/// <item><description>適用済みのパッチ（E302）の再投入が、要約入力と適用確認を済ませた後にしか分からなかった。</description></item>
/// <item><description>適用したあと、変更したファイルを開く導線（履歴差分タブ・ステータスバー通知）が無かった。</description></item>
/// </list>
/// ApplyUndoNoticeTestsと同じ手法（ShellWindowは作らずShellViewModelだけを構築し、
/// MainViewModel.ApplyAsyncを実際に通す）で、画面から見える状態まで確認する。
/// </summary>
public class ApplyFlowFixesTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-apply-flow-fixes", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();
    private readonly RecordingDialogService _dialogs = new();

    public ApplyFlowFixesTests()
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

    // ------------------------------------------------------------------
    // 1. 一部だけ適用できたあと、失敗したブロックが残ること
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "一部適用のあと失敗ブロックだけが接ぎ木パネルに残り、修正を依頼は押せて適用は押せない")]
    public async Task 一部適用のあと失敗ブロックだけが残る()
    {
        var shell = await PartiallyApplyAsync().ConfigureAwait(true);

        shell.Graft.Blocks.Should().ContainSingle("適用できなかったc.txtだけが残り、適用済みのa.txt・b.txtは外れるはず");
        var kept = shell.Graft.Blocks[0];
        kept.Plan.Path.Should().Be("c.txt");
        kept.IsError.Should().BeTrue();
        kept.HasIssue.Should().BeTrue("失敗の理由（赤字）が見えるまま残るはず");
        kept.IssueLines.Should().NotBeEmpty();

        shell.Graft.CopyRecoveryPromptCommand.CanExecute(null).Should().BeTrue("残った失敗ブロックに対して「修正を依頼」が押せるはず");
        shell.Graft.ApplyCommand.CanExecute(null).Should().BeFalse("残っているのは失敗ブロックだけで、適用できるものは無いはず");
        shell.Graft.PreviewCommand.CanExecute(null).Should().BeFalse(
            "元のパッチ全体は保持しない。再ドライランすると適用済みのパッチとしてE302で止まってしまうため");
        shell.Graft.State.Should().Be(CenterPaneState.Content);
        shell.Graft.StatusSummaryText.Should().Contain("1件失敗").And.Contain("0件適用可",
            "ステータスバーも実態（適用できるものは無く、失敗が1件）と合うはず");
        shell.Graft.HasFailedBlocks.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "一部適用のあとで「修正を依頼」を実行すると、残った失敗ブロックの修正依頼文がコピーされる")]
    public async Task 一部適用のあと修正を依頼がコピーできる()
    {
        var shell = await PartiallyApplyAsync().ConfigureAwait(true);
        _clipboard.Text = null;

        await ExecuteAsync(shell.Graft.CopyRecoveryPromptCommand).ConfigureAwait(true);

        _clipboard.Text.Should().NotBeNullOrEmpty();
        _clipboard.Text.Should().Contain("c.txt", "失敗したブロックの対象ファイルが修正依頼文に含まれるはず");
        _clipboard.Text.Should().NotContain("a.txt", "適用済みのブロックは修正依頼に含めないはず");
    }

    [AvaloniaFact(DisplayName = "一部適用のあとに残った失敗ブロックは「破棄」で消せる")]
    public async Task 一部適用のあと残った失敗ブロックを破棄できる()
    {
        var shell = await PartiallyApplyAsync().ConfigureAwait(true);

        shell.Graft.DiscardCommand.CanExecute(null).Should().BeTrue("残った失敗ブロックも破棄できなければならない");
        shell.Graft.DiscardCommand.Execute(null);

        shell.Graft.Blocks.Should().BeEmpty();
        shell.Graft.State.Should().Be(CenterPaneState.Empty);
        shell.Graft.CopyRecoveryPromptCommand.CanExecute(null).Should().BeFalse();
        shell.Graft.DiscardCommand.CanExecute(null).Should().BeFalse("破棄したあとは破棄するものが無い");
        shell.Graft.StatusSummaryText.Should().Be("解析結果はありません");
    }

    [AvaloniaFact(DisplayName = "一部適用のあとに残った失敗ブロックがあっても、新しいパッチを貼れば従来どおり置き換わる")]
    public async Task 一部適用のあと新しいパッチで置き換わる()
    {
        var shell = await PartiallyApplyAsync().ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "d.txt"), "ddd\n").ConfigureAwait(true);

        _clipboard.Text = BuildPatch("d.txt", "ddd", "ddd-changed");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        shell.Graft.Blocks.Should().ContainSingle(b => b.Plan.Path == "d.txt",
            "新しい解析結果へ丸ごと置き換わり、古い失敗ブロックは残らないはず");
        shell.Graft.Blocks.Should().NotContain(b => b.IsError);
        shell.Graft.ApplyCommand.CanExecute(null).Should().BeTrue("新しいパッチは通常どおり適用できるはず");
        shell.Graft.CopyRecoveryPromptCommand.CanExecute(null).Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "全件成功した適用では従来どおりブロック一覧が空になる")]
    public async Task 全件成功なら一覧は空になる()
    {
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "a.txt"), "aaa\n").ConfigureAwait(true);
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text = BuildPatch("a.txt", "aaa", "aaa-changed");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        shell.Graft.Blocks.Should().BeEmpty();
        shell.Graft.State.Should().Be(CenterPaneState.Empty);
        shell.Graft.ApplyCommand.CanExecute(null).Should().BeFalse();
        shell.Graft.DiscardCommand.CanExecute(null).Should().BeFalse();
        _dialogs.Shown.Should().NotContain(s => s.Title == "適用が完了しました", "全件成功ではダイアログを出さない（従来どおり）");
    }

    [AvaloniaFact(DisplayName = "一部適用の完了ダイアログは、失敗ブロックが残っていることと「修正を依頼」「破棄」の使い方を案内する")]
    public async Task 一部適用の完了ダイアログの文言が実態と合う()
    {
        await PartiallyApplyAsync().ConfigureAwait(true);

        var shown = _dialogs.Shown.Should().ContainSingle(s => s.Title == "適用が完了しました").Subject;
        shown.Message.Should().Contain("1件は適用できませんでした");
        shown.Message.Should().Contain("接ぎ木パネルに残しています");
        shown.Message.Should().Contain("修正を依頼");
        shown.Message.Should().Contain("破棄");
    }

    // ------------------------------------------------------------------
    // 2. 適用済みのパッチ（E302）はプレビューの時点で分かること
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "適用済みのパッチをもう一度貼ると、プレビューの時点で「r1で適用済み」が見える")]
    public async Task 適用済みのパッチはプレビューで適用済みと分かる()
    {
        var (shell, patch) = await ApplyOnceAsync().ConfigureAwait(true);

        _clipboard.Text = patch;
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        shell.Graft.AlreadyAppliedRevision.Should().Be(1);
        shell.Graft.HasAlreadyAppliedNotice.Should().BeTrue();
        shell.Graft.AlreadyAppliedNoticeText.Should().Contain("r1").And.Contain("適用済み");
        shell.Graft.StatusSummaryText.Should().Contain("r1で適用済み", "ステータスバーでも押せない理由が分かるはず");

        shell.Graft.Blocks.Should().NotBeEmpty("ブロック一覧は通常どおり見せる");
        shell.Graft.Blocks.Should().NotContain(b => b.IsError,
            "全ブロックを失敗扱いにすると「修正を依頼」が押せてしまう。適用済みのパッチをAIに直してもらう意味は無い");
        shell.Graft.CopyRecoveryPromptCommand.CanExecute(null).Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "適用済みのパッチは、適用できるブロックがあっても適用ボタンが押せない")]
    public async Task 適用済みのパッチは適用ボタンが押せない()
    {
        var (shell, patch) = await ApplyOnceAsync().ConfigureAwait(true);

        _clipboard.Text = patch;
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        shell.Graft.Blocks.Should().NotBeEmpty();
        shell.Graft.Blocks.Should().Contain(b => b.Plan.CanApply, "ブロック自体は適用可能（ファイルは元に戻してあるため）");
        shell.Graft.ApplyCommand.CanExecute(null).Should().BeFalse(
            "押しても要約入力と確認の窓を通った後にApplyEngineで必ず止まる（しかもリビジョン番号を消費する）ため、押せないはず");
    }

    [AvaloniaFact(DisplayName = "適用済みのパッチの再投入では、要約入力や適用確認の窓を出さず、リビジョン番号も消費しない")]
    public async Task 適用済みのパッチは窓を出さず番号も消費しない()
    {
        var (shell, patch) = await ApplyOnceAsync().ConfigureAwait(true);
        var promptsBefore = _dialogs.PromptCount + _dialogs.ConfirmCount;

        _clipboard.Text = patch;
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        // キーボード経路（Ctrl+Enter）は、CanExecuteがfalseでもExecuteを直接呼ぶ。
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        (_dialogs.PromptCount + _dialogs.ConfirmCount).Should().Be(promptsBefore, "要約入力・適用確認の窓は出ないはず");
        var project = shell.Graft.ProjectPane.SelectedItem!.Project;
        var projects = await new ProjectStore(new AppPaths(_appDirectory)).LoadAsync().ConfigureAwait(true);
        projects.Value.Single(p => p.Id == project.Id).NextRevision.Should().Be(2,
            "適用を試みていないので、リビジョン番号は消費されないはず（修正前は止められた時点で3になっていた）");
    }

    [AvaloniaFact(DisplayName = "接ぎ木パネルの上部に「適用済み」のバナーが1つだけ出て、適用ボタンは押せない")]
    public async Task 接ぎ木パネルに適用済みのバナーが出る()
    {
        var (shell, patch) = await ApplyOnceAsync().ConfigureAwait(true);
        shell.IsGraftPanelOpen = true;
        var panel = new GraftPanel { DataContext = shell };
        var window = new Window { Width = 1000, Height = 500, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        string[] VisibleBannerTexts() => panel.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && (t.Text ?? string.Empty).Contains("適用済みです"))
            .Select(t => t.Text!).ToArray();
        VisibleBannerTexts().Should().BeEmpty("適用済みのパッチを解析する前はバナーを出さない");

        _clipboard.Text = patch;
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        Dispatcher.UIThread.RunJobs();

        VisibleBannerTexts().Should().ContainSingle().Which.Should().Contain("r1");
        var apply = panel.GetVisualDescendants().OfType<Button>()
            .Single(b => AutomationProperties.GetName(b) == "適用を実行");
        apply.Command!.CanExecute(null).Should().BeFalse();
        window.Close();
    }

    [AvaloniaFact(DisplayName = "適用済みでないパッチでは「適用済み」の通知は出ない")]
    public async Task 通常のパッチでは適用済みの通知は出ない()
    {
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "a.txt"), "aaa\n").ConfigureAwait(true);
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text = BuildPatch("a.txt", "aaa", "aaa-changed");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        shell.Graft.AlreadyAppliedRevision.Should().BeNull();
        shell.Graft.HasAlreadyAppliedNotice.Should().BeFalse();
        shell.Graft.AlreadyAppliedNoticeText.Should().BeEmpty();
        shell.Graft.ApplyCommand.CanExecute(null).Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "「適用済み」の通知は、破棄しても、別のパッチを貼っても消える")]
    public async Task 適用済みの通知は破棄や別パッチで消える()
    {
        var (shell, patch) = await ApplyOnceAsync().ConfigureAwait(true);
        _clipboard.Text = patch;
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        shell.Graft.HasAlreadyAppliedNotice.Should().BeTrue("前提");

        shell.Graft.DiscardCommand.Execute(null);
        shell.Graft.HasAlreadyAppliedNotice.Should().BeFalse("破棄したら通知も消える");

        _clipboard.Text = patch;
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        shell.Graft.HasAlreadyAppliedNotice.Should().BeTrue("前提");

        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "other.txt"), "xxx\n").ConfigureAwait(true);
        _clipboard.Text = BuildPatch("other.txt", "xxx", "yyy");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        shell.Graft.HasAlreadyAppliedNotice.Should().BeFalse("別のパッチを貼れば通知は消え、適用できるはず");
        shell.Graft.ApplyCommand.CanExecute(null).Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // 3. 適用したあと、変更したファイルを開く導線
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "履歴差分タブの「ファイルを開く」で、そのリビジョンの変更ファイルがエディタで開く")]
    public async Task 履歴差分タブからファイルを開ける()
    {
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await ApplyFullAsync(shell, "sub/new.txt", "内容").ConfigureAwait(true); // r1: 新規作成
        await SelectHistoryRevisionAsync(shell, 1).ConfigureAwait(true);

        var file = shell.Graft.HistoryDiff.Files.Should().ContainSingle().Subject;
        file.CanOpenFile.Should().BeTrue();
        file.OpenFileCommand.CanExecute(null).Should().BeTrue();
        file.OpenFileCommand.Execute(null);

        var expected = Path.Combine(_projectDirectory, "sub", "new.txt");
        await WaitUntilAsync(() => shell.Editor.Tabs.Any(t => t.Kind == EditorTabKind.Document
            && t.Session.FullPath == expected)).ConfigureAwait(true);
        shell.Editor.Tabs.Should().Contain(t => t.Kind == EditorTabKind.Document && t.Session.FullPath == expected,
            "接ぎ木パネルの「対象ファイルを開く」と同じ経路で、プロジェクト内の正しいパスが開くはず");
    }

    [AvaloniaFact(DisplayName = "履歴差分タブの「ファイルを開く」は、相対パスでファイルを開く要求を出す")]
    public async Task 履歴差分タブの開く要求は相対パスを渡す()
    {
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await ApplyFullAsync(shell, "sub/new.txt", "内容").ConfigureAwait(true);
        await SelectHistoryRevisionAsync(shell, 1).ConfigureAwait(true);

        var requested = new List<string>();
        shell.Graft.HistoryDiff.OpenFileRequested += (_, path) => requested.Add(path);
        shell.Graft.HistoryDiff.Files[0].OpenFileCommand.Execute(null);

        requested.Should().Equal(new[] { "sub/new.txt" });
    }

    [AvaloniaFact(DisplayName = "削除されたファイルとフォルダ作成は履歴差分タブから開けない")]
    public async Task 削除とフォルダ作成は開けない()
    {
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "gone.txt"), "bye\n").ConfigureAwait(true);
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text = "<<<< DELETE: gone.txt\n<<<< MKDIR: newdir\n";
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);
        await SelectHistoryRevisionAsync(shell, 1).ConfigureAwait(true);

        shell.Graft.HistoryDiff.Files.Should().HaveCount(2);
        var deleted = shell.Graft.HistoryDiff.Files.Single(f => f.PathText == "gone.txt");
        var mkdir = shell.Graft.HistoryDiff.Files.Single(f => f.PathText == "newdir");
        deleted.CanOpenFile.Should().BeFalse("削除されたファイルは開けない（ボタンも出さない）");
        deleted.OpenFileCommand.CanExecute(null).Should().BeFalse();
        mkdir.CanOpenFile.Should().BeFalse("フォルダは開く対象のファイルではない");
        mkdir.OpenFileCommand.CanExecute(null).Should().BeFalse();

        var requested = new List<string>();
        shell.Graft.HistoryDiff.OpenFileRequested += (_, path) => requested.Add(path);
        deleted.OpenFileCommand.Execute(null);
        requested.Should().BeEmpty("押せないコマンドは要求を出さない");
    }

    [AvaloniaFact(DisplayName = "その後に削除されたファイルを開こうとすると、黙って終わらず理由を案内する")]
    public async Task 後で消えたファイルは案内が出る()
    {
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await ApplyFullAsync(shell, "later-gone.txt", "内容").ConfigureAwait(true);
        await SelectHistoryRevisionAsync(shell, 1).ConfigureAwait(true);
        File.Delete(Path.Combine(_projectDirectory, "later-gone.txt"));

        shell.Graft.HistoryDiff.Files[0].OpenFileCommand.Execute(null);

        await WaitUntilAsync(() => _dialogs.Shown.Any(s => s.Title == "ファイルを開けません")).ConfigureAwait(true);
        _dialogs.Shown.Should().Contain(s => s.Title == "ファイルを開けません" && s.Message.Contains("later-gone.txt"));
        shell.Editor.Tabs.Should().NotContain(t => t.Kind == EditorTabKind.Document);
    }

    [AvaloniaFact(DisplayName = "履歴差分タブの見出しには、開ける変更ファイルにだけ「ファイルを開く」ボタンが出る（削除は出ない）")]
    public async Task 開ける変更ファイルにだけ開くボタンが出る()
    {
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "gone.txt"), "bye\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "keep.txt"), "old\n").ConfigureAwait(true);
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text = "<<<< DELETE: gone.txt\n<<<< FILE: keep.txt MODE=FULL\nnew\n>>>> END\n";
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);
        await SelectHistoryRevisionAsync(shell, 1).ConfigureAwait(true);
        shell.Graft.HistoryDiff.Files.Should().HaveCount(2, "前提");

        var window = new Window { Width = 900, Height = 900, Content = new HistoryDiffView { DataContext = shell.Graft.HistoryDiff } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var buttons = window.GetVisualDescendants().OfType<Button>()
            .Where(b => AutomationProperties.GetName(b) == "このファイルをエディタで開く" && b.IsVisible)
            .ToList();
        buttons.Should().ContainSingle("開けるのは変更したkeep.txtだけで、削除されたgone.txtには出さない");
        buttons[0].IsEffectivelyEnabled.Should().BeTrue();
        window.Close();
    }

    [AvaloniaFact(DisplayName = "適用直後の通知の「変更を見る」で、そのリビジョンが履歴で選ばれ、履歴ビューへ移る")]
    public async Task 変更を見るでリビジョンが選ばれる()
    {
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        shell.ShowAppliedRevisionCommand.CanExecute(null).Should().BeFalse("適用前は押せない");

        await ApplyFullAsync(shell, "a.txt", "a1").ConfigureAwait(true); // r1
        await ApplyFullAsync(shell, "a.txt", "a2").ConfigureAwait(true); // r2
        shell.Graft.HasApplyUndoNotice.Should().BeTrue("前提");
        shell.Graft.LastAppliedRevision.Should().Be(2);
        shell.ShowAppliedRevisionCommand.CanExecute(null).Should().BeTrue();

        var focusRequests = 0;
        shell.Graft.RequestFocusHistory += (_, _) => focusRequests++;
        var changed = WaitForHistoryDiffChangedAsync(shell);
        shell.ShowAppliedRevisionCommand.Execute(null);
        await changed.ConfigureAwait(true);

        shell.Graft.History.SelectedItem.Should().NotBeNull();
        shell.Graft.History.SelectedItem!.RevisionLabel.Should().Be("r2", "たった今適用したリビジョンが選ばれるはず");
        shell.Graft.HistoryDiff.RevisionLabel.Should().Be("r2");
        shell.Graft.HistoryDiff.Files.Should().ContainSingle(f => f.PathText == "a.txt");
        focusRequests.Should().Be(1, "既存のShowHistoryCommand（RequestFocusHistory）で履歴ビューへ移るはず");
    }

    [AvaloniaFact(DisplayName = "「変更を見る」は、履歴の絞り込みでそのリビジョンが隠れていても絞り込みを解除して選ぶ")]
    public async Task 変更を見るは絞り込みで隠れていても選べる()
    {
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await ApplyFullAsync(shell, "a.txt", "a1").ConfigureAwait(true); // r1

        shell.Graft.History.Keyword = "どのリビジョンにも含まれない語";
        shell.Graft.History.Items.Should().BeEmpty("前提: 絞り込みでr1が隠れている");

        var changed = WaitForHistoryDiffChangedAsync(shell);
        shell.ShowAppliedRevisionCommand.Execute(null);
        await changed.ConfigureAwait(true);

        shell.Graft.History.Keyword.Should().BeEmpty();
        shell.Graft.History.SelectedItem!.RevisionLabel.Should().Be("r1");
    }

    // ------------------------------------------------------------------
    // ヘルパ
    // ------------------------------------------------------------------

    /// <summary>3ブロック中2つ成功・1つ失敗（c.txt）の適用を、既定の適用モード（partial）で通す。</summary>
    private async Task<ShellViewModel> PartiallyApplyAsync()
    {
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "a.txt"), "aaa\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "b.txt"), "bbb\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(
            Path.Combine(_projectDirectory, "c.txt"), "存在しない検索対象は含まれていません\n").ConfigureAwait(true);

        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text = BuildPatch("a.txt", "aaa", "aaa-changed")
            + "\n" + BuildPatch("b.txt", "bbb", "bbb-changed")
            + "\n" + BuildPatch("c.txt", "見つからない文字列", "置換後");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        // 前提: 実際にa.txt・b.txtだけが書き換わっている（テストが成り立つ状況であること）。
        (await File.ReadAllTextAsync(Path.Combine(_projectDirectory, "a.txt")).ConfigureAwait(true)).Should().Be("aaa-changed\n");
        (await File.ReadAllTextAsync(Path.Combine(_projectDirectory, "b.txt")).ConfigureAwait(true)).Should().Be("bbb-changed\n");
        return shell;
    }

    /// <summary>
    /// 1件を適用してr1を作り、ファイルを元の内容へ戻したうえで、同じパッチ本文を返す
    /// （再投入してもブロック自体は適用可能な状態にするため。E302は本文のハッシュ一致で判定される）。
    /// </summary>
    private async Task<(ShellViewModel Shell, string Patch)> ApplyOnceAsync()
    {
        var path = Path.Combine(_projectDirectory, "a.txt");
        await File.WriteAllTextAsync(path, "aaa\n").ConfigureAwait(true);
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        var patch = BuildPatch("a.txt", "aaa", "aaa-changed");
        _clipboard.Text = patch;
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);
        shell.Graft.HasApplyUndoNotice.Should().BeTrue("前提: r1が適用されている");

        await File.WriteAllTextAsync(path, "aaa\n").ConfigureAwait(true);
        return (shell, patch);
    }

    private async Task ApplyFullAsync(ShellViewModel shell, string relativePath, string content)
    {
        _clipboard.Text = $"<<<< FILE: {relativePath} MODE=FULL\n{content}\n>>>> END\n";
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);
    }

    /// <summary>履歴からリビジョンを選び、履歴差分タブの内容が整うまで待つ。</summary>
    private static async Task SelectHistoryRevisionAsync(ShellViewModel shell, int revision)
    {
        var changed = WaitForHistoryDiffChangedAsync(shell);
        shell.Graft.History.SelectedItem = shell.Graft.History.Items.Single(i => i.Revision.Manifest.Revision == revision);
        await changed.ConfigureAwait(true);
    }

    private static async Task WaitForHistoryDiffChangedAsync(ShellViewModel shell)
    {
        var tcs = new TaskCompletionSource();
        void OnChanged(object? s, EventArgs e) => tcs.TrySetResult();
        shell.Graft.HistoryDiffChanged += OnChanged;
        try
        {
            await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(true);
        }
        finally
        {
            shell.Graft.HistoryDiffChanged -= OnChanged;
        }
    }

    /// <summary>async voidで動く処理の完了を、UIスレッドのジョブをポンプしながら待つ。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (cts.IsCancellationRequested) break;
            await Task.Delay(10).ConfigureAwait(true);
        }
    }

    private async Task<ShellViewModel> OpenShellAsync()
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        var settings = new Settings { ShowPreview = false };
        await new SettingsStore(appPaths).SaveAsync(settings).ConfigureAwait(true);

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths,
            settings,
            new SettingsStore(appPaths),
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            _dialogs,
            new FakeUiServices(_clipboard),
            openSettings: () => { });

        await shell.Graft.InitializeAsync().ConfigureAwait(true);
        return shell;
    }

    /// <summary>SEARCH/REPLACE形式のパッチ本文を組み立てる（仕様書4.1）。</summary>
    private static string BuildPatch(string relativePath, string search, string replace)
        => $"""
            <<<< FILE: {relativePath}
            summary: テスト用の変更
            <<<<<<< SEARCH
            {search}
            =======
            {replace}
            >>>>>>> REPLACE
            >>>> END

            """;

    private static async Task ExecuteAsync(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        if (command is AsyncRelayCommand async)
        {
            while (async.IsExecuting)
            {
                await Task.Delay(10).ConfigureAwait(true);
            }
        }
    }

    /// <summary>確認をすべて承諾し、表示した案内と窓の出た回数を記録するダイアログ。</summary>
    private sealed class RecordingDialogService : IDialogService
    {
        public List<(string Title, string Message)> Shown { get; } = new();
        public int ConfirmCount { get; private set; }
        public int PromptCount { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message)
        {
            ConfirmCount++;
            return Task.FromResult(true);
        }

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(true);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
        {
            PromptCount++;
            return Task.FromResult<string?>(initial ?? "テスト");
        }

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message)
        {
            Shown.Add((title, message));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; set; }

        public void SetText(string text) => Text = text;

        public Task<string?> GetTextAsync() => Task.FromResult(Text);
    }

    private sealed class FakeUiServices : IUiServices
    {
        private readonly AvaloniaUiServices _inner = new();

        public FakeUiServices(IClipboardAccess clipboard)
        {
            Clipboard = clipboard;
        }

        public IClipboardAccess Clipboard { get; }

        public IScreenInfo Screens => _inner.Screens;

        public IUiTimer CreateTimer(TimeSpan interval, Action onTick) => _inner.CreateTimer(interval, onTick);
    }
}
