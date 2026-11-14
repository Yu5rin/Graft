using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;

namespace Graft.UiTests;

/// <summary>
/// 右クリックメニューに足した操作（相対パスをコピー・AIに渡すファイルに追加・要約をコピー）と、
/// 取り消しが終わったときの通知（「r2 を取り消しました」）の検証。
/// </summary>
public class MenuActionTests
{
    [AvaloniaFact(DisplayName = "エクスプローラの「相対パスをコピー」はプロジェクトのフォルダからの相対パスをコピーし、余白では無効になる")]
    public async Task エクスプローラで相対パスをコピーできる()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        Directory.CreateDirectory(h.ProjectPath("sub"));
        await File.WriteAllTextAsync(h.ProjectPath("sub/a.txt"), "A\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        await ShellHarness.WaitUntilAsync(() => h.Shell.Explorer.RootNodes.Count > 0).ConfigureAwait(true);

        var folder = h.Shell.Explorer.RootNodes.Single(n => n.Name == "sub");
        folder.IsExpanded = true;
        await ShellHarness.WaitUntilAsync(() => folder.Children.Any(n => n.Name == "a.txt")).ConfigureAwait(true);
        var file = folder.Children.Single(n => n.Name == "a.txt");

        h.Shell.Explorer.CopyRelativePathCommand.CanExecute(file).Should().BeTrue();
        h.Shell.Explorer.CopyRelativePathCommand.Execute(file);
        h.Clipboard.Text.Should().Be("sub/a.txt");

        h.Shell.Explorer.SelectedNode = null; // 余白を右クリックした状態
        h.Shell.Explorer.CopyRelativePathCommand.CanExecute(null).Should().BeFalse("余白には相対パスが無いので、隠さず無効にする");
        h.Shell.Explorer.DeleteCommand.CanExecute(null).Should().BeFalse("余白では、前に選んだファイルへ削除が効いてしまわない");
        h.Shell.Explorer.CopyPathCommand.CanExecute(null).Should().BeTrue("余白ではプロジェクトのフォルダ自体のパスをコピーする");
        h.Shell.Explorer.AddToContextCommand.CanExecute(folder).Should().BeFalse("フォルダは「AIに渡すファイルに追加」の対象外");
        h.Shell.Explorer.AddToContextCommand.CanExecute(file).Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "「AIに渡すファイルに追加」でファイルが「内容も出す」になり、数秒で消える通知が出る")]
    public async Task AIに渡すファイルに追加できる()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        await File.WriteAllTextAsync(h.ProjectPath("a.txt"), "A\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        await ShellHarness.WaitUntilAsync(() => h.Shell.Explorer.RootNodes.Count > 0).ConfigureAwait(true);

        var file = h.Shell.Explorer.RootNodes.Single(n => n.Name == "a.txt");
        h.Shell.Explorer.AddToContextCommand.Execute(file);
        await ShellHarness.WaitUntilAsync(() => h.Shell.Graft.Notice.IsVisible).ConfigureAwait(true);

        h.Shell.Graft.Notice.Text.Should().Contain("a.txt").And.Contain("AIに渡すファイルに追加しました");
        h.Shell.Graft.ContextCollect!.Files.Single(f => f.RelativePath == "a.txt").State
            .Should().Be(Graft.Features.ContextFileState.Full, "追加したファイルは「内容も出す」になる");
    }

    [AvaloniaFact(DisplayName = "取り消しが終わったら、数秒で消える通知で「r2 を取り消しました」と出る。履歴の「要約をコピー」はその履歴の要約をコピーする")]
    public async Task 取り消しの通知と要約のコピー()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        await File.WriteAllTextAsync(h.ProjectPath("a.txt"), "one\ntwo\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        await h.ParseAsync("a.txt", "one", "ONE").ConfigureAwait(true);
        await ShellHarness.ExecuteAsync(h.Shell.Graft.ApplyCommand).ConfigureAwait(true);
        await ShellHarness.WaitUntilAsync(() => h.Shell.Graft.History.Items.Count > 0).ConfigureAwait(true);

        var item = h.Shell.Graft.History.Items[0];
        h.Shell.Graft.History.SelectedItem = item;
        h.Shell.Graft.History.CopySummaryCommand.Execute(null);
        h.Clipboard.Text.Should().Be(item.SummaryText);

        (await h.Shell.Graft.History.UndoLatestAsync().ConfigureAwait(true)).Should().BeTrue();
        h.Shell.Graft.Notice.IsVisible.Should().BeTrue("取り消しが終わったことを、数秒で消える通知で伝える");
        h.Shell.Graft.Notice.Text.Should().Be($"{item.RevisionLabel} を取り消しました");
    }

    [AvaloniaFact(DisplayName = "プロジェクトの「パスをコピー」はフォルダのパスをコピーして通知を出し、フォルダが無くなると「ファイルマネージャーで表示」だけ無効になる")]
    public async Task プロジェクトのパスをコピーできる()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        await File.WriteAllTextAsync(h.ProjectPath("a.txt"), "A\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);

        var pane = h.Shell.Graft.ProjectPane;
        pane.SelectedItem.Should().NotBeNull();
        pane.CopyPathCommand.CanExecute(null).Should().BeTrue();
        pane.RevealCommand.CanExecute(null).Should().BeTrue("フォルダがあれば表示できる");

        pane.CopyPathCommand.Execute(null);
        h.Clipboard.Text.Should().Be(pane.SelectedItem!.Project.Root);
        h.Shell.Graft.Notice.IsVisible.Should().BeTrue("コピーしたことを、数秒で消える通知で伝える");
        h.Shell.Graft.Notice.Text.Should().Be("プロジェクトのパスをコピーしました");

        Directory.Delete(pane.SelectedItem.Project.Root, recursive: true);
        pane.RevealCommand.CanExecute(null).Should().BeFalse("フォルダが見つからないときは開けないので無効");
        pane.CopyPathCommand.CanExecute(null).Should().BeTrue("コピーは、探す手がかりになるので有効のまま");
    }

    [AvaloniaFact(DisplayName = "エクスプローラの「パスをコピー」「相対パスをコピー」は、名前つきの通知を出す。余白ではプロジェクトの文面になる")]
    public async Task エクスプローラのコピーで通知が出る()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        Directory.CreateDirectory(h.ProjectPath("sub"));
        await File.WriteAllTextAsync(h.ProjectPath("sub/a.txt"), "A\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        await ShellHarness.WaitUntilAsync(() => h.Shell.Explorer.RootNodes.Count > 0).ConfigureAwait(true);

        var folder = h.Shell.Explorer.RootNodes.Single(n => n.Name == "sub");
        folder.IsExpanded = true;
        await ShellHarness.WaitUntilAsync(() => folder.Children.Any(n => n.Name == "a.txt")).ConfigureAwait(true);
        var file = folder.Children.Single(n => n.Name == "a.txt");

        h.Shell.Explorer.CopyPathCommand.Execute(file);
        h.Clipboard.Text.Should().Be(file.FullPath);
        h.Shell.Graft.Notice.IsVisible.Should().BeTrue();
        h.Shell.Graft.Notice.Text.Should().Be("a.txt のパスをコピーしました");

        h.Shell.Explorer.CopyRelativePathCommand.Execute(file);
        h.Clipboard.Text.Should().Be("sub/a.txt");
        h.Shell.Graft.Notice.Text.Should().Be("a.txt の相対パスをコピーしました");

        h.Shell.Explorer.CopyPathCommand.Execute(folder);
        h.Shell.Graft.Notice.Text.Should().Be("sub のパスをコピーしました", "フォルダも名前で知らせる");

        h.Shell.Explorer.SelectedNode = null; // 余白を右クリックした状態
        h.Shell.Explorer.CopyPathCommand.Execute(null);
        h.Clipboard.Text.Should().Be(h.ProjectDirectory);
        h.Shell.Graft.Notice.Text.Should().Be("プロジェクトのパスをコピーしました", "余白ではプロジェクト一覧と同じ文面");
    }

    [AvaloniaFact(DisplayName = "エディタのタブの「パスをコピー」「相対パスをコピー」は、名前つきの通知を出す")]
    public async Task エディタのタブのコピーで通知が出る()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        Directory.CreateDirectory(h.ProjectPath("sub"));
        await File.WriteAllTextAsync(h.ProjectPath("sub/a.txt"), "A\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        var opened = await h.Shell.Editor.OpenFileAsync(h.ProjectPath("sub/a.txt")).ConfigureAwait(true);
        opened.IsSuccess.Should().BeTrue();
        var tab = h.Shell.Editor.Tabs.Single(t => t.Kind == EditorTabKind.Document);

        h.Shell.Editor.CopyFullPathCommand.Execute(tab);
        h.Clipboard.Text.Should().Be(tab.Session.FullPath);
        h.Shell.Graft.Notice.Text.Should().Be("a.txt のパスをコピーしました");

        h.Shell.Editor.CopyRelativePathCommand.Execute(tab);
        h.Clipboard.Text.Should().Be("sub/a.txt");
        h.Shell.Graft.Notice.Text.Should().Be("a.txt の相対パスをコピーしました");
    }

    [AvaloniaFact(DisplayName = "検索結果の「パスをコピー」「相対パスをコピー」は、名前つきの通知を出す")]
    public async Task 検索結果のコピーで通知が出る()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        Directory.CreateDirectory(h.ProjectPath("sub"));
        await File.WriteAllTextAsync(h.ProjectPath("sub/a.txt"), "hello\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);

        var fullPath = h.ProjectPath("sub/a.txt");
        var hit = new SearchHitViewModel(new Graft.Features.SearchHit
        {
            FullPath = fullPath,
            RelativePath = "sub/a.txt",
            LineNumber = 1,
            LineText = "hello",
            ColumnStart = 0,
            MatchLength = 5,
        });

        h.Shell.Search.CopyPathCommand.Execute(hit);
        h.Clipboard.Text.Should().Be(fullPath);
        h.Shell.Graft.Notice.Text.Should().Be("a.txt のパスをコピーしました");

        h.Shell.Search.CopyRelativePathCommand.Execute(hit);
        h.Clipboard.Text.Should().Be("sub/a.txt");
        h.Shell.Graft.Notice.Text.Should().Be("a.txt の相対パスをコピーしました");
    }

    [AvaloniaFact(DisplayName = "接ぎ木パネルのブロックの「相対パスをコピー」は、名前つきの通知を出す")]
    public async Task 接ぎ木パネルのコピーで通知が出る()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        Directory.CreateDirectory(h.ProjectPath("sub"));
        await File.WriteAllTextAsync(h.ProjectPath("sub/a.txt"), "A\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        await h.ParseAsync("sub/a.txt", "A", "B").ConfigureAwait(true);

        var block = h.Shell.Graft.Blocks.Single();
        h.Shell.CopyBlockPathCommand.Execute(block);
        h.Clipboard.Text.Should().Be("sub/a.txt");
        h.Shell.Graft.Notice.Text.Should().Be("a.txt の相対パスをコピーしました");
    }
}
