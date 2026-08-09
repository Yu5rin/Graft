using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;

namespace Graft.UiTests;

/// <summary>
/// 不具合2（実機検証）: エクスプローラの「新規ファイル」「新規フォルダ」で作成しても
/// ツリー上で何も起きたように見えない不具合を検証する。
///
/// 真の原因: 折りたたまれた（未展開・未読込）フォルダの直下に作成すると、
/// ReconcileDirectoryAsyncは内部データ（FileNodeViewModel.Children）だけを実列挙結果へ
/// 置き換えており、対象フォルダのIsExpandedはfalseのままだった。ツリーは展開されていない
/// フォルダの子要素を表示しないため、データ上は正しく作成されているのに画面上は
/// 「何も起きていない」ように見えていた。加えて、作成した項目が選択状態にもならないため、
/// 展開後もどれが新規作成分か分からなかった。
///
/// もう一つの原因: PathGuard.Resolveの拡張子ホワイトリスト判定が、拡張子が無いファイル名
/// （Dockerfile等）も「空文字列という未許可の拡張子」として一律に拒否していた。
/// </summary>
public class NewFileRevealTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-new-file-reveal", Guid.NewGuid().ToString("N"));

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

    [AvaloniaFact(DisplayName = "不具合2: 折りたたまれたフォルダの直下に新規ファイルを作ると、フォルダが自動展開されツリーに現れて選択される")]
    public async Task 折りたたまれたフォルダへの新規ファイルは自動展開されて選択される()
    {
        var subDir = Path.Combine(_root, "sub");
        Directory.CreateDirectory(subDir);

        var (explorer, dialogs) = await BuildExplorerWithProjectAsync().ConfigureAwait(true);
        var subNode = explorer.RootNodes.Single(n => n.Name == "sub");
        subNode.IsExpanded.Should().BeFalse("初期状態では折りたたまれているはず");
        subNode.IsLoaded.Should().BeFalse("初期状態では未読込のはず（遅延読み込み）");

        dialogs.NextPromptResult = "created.md";
        explorer.NewFileCommand.Execute(subNode);
        await WaitForAsync(() => File.Exists(Path.Combine(subDir, "created.md"))).ConfigureAwait(true);
        await WaitForAsync(() => subNode.Children.Any(n => n.Name == "created.md")).ConfigureAwait(true);

        subNode.IsExpanded.Should().BeTrue("折りたたまれていたフォルダは、作成後に自動的に展開されるべき");
        var created = subNode.Children.SingleOrDefault(n => n.Name == "created.md");
        created.Should().NotBeNull("実列挙結果がフォルダの子要素へ反映されているはず");
        explorer.SelectedNode.Should().BeSameAs(created, "作成した項目はツリー上で選択状態になるはず");
    }

    [AvaloniaFact(DisplayName = "不具合2: プロジェクトルート直下に新規ファイルを作ると、RootNodesに現れて選択される")]
    public async Task ルート直下への新規ファイルはRootNodesに現れて選択される()
    {
        var (explorer, dialogs) = await BuildExplorerWithProjectAsync().ConfigureAwait(true);

        dialogs.NextPromptResult = "root-created.txt";
        explorer.NewFileCommand.Execute(null);
        await WaitForAsync(() => File.Exists(Path.Combine(_root, "root-created.txt"))).ConfigureAwait(true);
        await WaitForAsync(() => explorer.RootNodes.Any(n => n.Name == "root-created.txt")).ConfigureAwait(true);

        var created = explorer.RootNodes.SingleOrDefault(n => n.Name == "root-created.txt");
        created.Should().NotBeNull();
        explorer.SelectedNode.Should().BeSameAs(created);
    }

    [AvaloniaFact(DisplayName = "不具合2: ファイルを選択した状態で新規ファイルを作ると、その親フォルダ直下に現れて選択される")]
    public async Task ファイル選択中の新規ファイルは親フォルダに現れて選択される()
    {
        var subDir = Path.Combine(_root, "sub");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(subDir, "existing.txt"), "既存").ConfigureAwait(true);

        var (explorer, dialogs) = await BuildExplorerWithProjectAsync().ConfigureAwait(true);
        var subNode = explorer.RootNodes.Single(n => n.Name == "sub");
        subNode.IsExpanded = true; // 既存ファイルを選択できるよう先に展開しておく
        await WaitForAsync(() => subNode.IsLoaded).ConfigureAwait(true);
        var existingFile = subNode.Children.Single(n => n.Name == "existing.txt");

        dialogs.NextPromptResult = "sibling.md";
        explorer.NewFileCommand.Execute(existingFile);
        await WaitForAsync(() => File.Exists(Path.Combine(subDir, "sibling.md"))).ConfigureAwait(true);
        await WaitForAsync(() => subNode.Children.Any(n => n.Name == "sibling.md")).ConfigureAwait(true);

        var created = subNode.Children.SingleOrDefault(n => n.Name == "sibling.md");
        created.Should().NotBeNull("ファイルを選択していた場合は、その親フォルダの直下に作られるはず");
        explorer.SelectedNode.Should().BeSameAs(created);
    }

    [AvaloniaFact(DisplayName = "不具合2: 拡張子なしのファイル名でも作成でき、ツリーに現れて選択される")]
    public async Task 拡張子なしのファイル名でも作成できる()
    {
        var (explorer, dialogs) = await BuildExplorerWithProjectAsync().ConfigureAwait(true);

        dialogs.NextPromptResult = "Dockerfile";
        explorer.NewFileCommand.Execute(null);
        await WaitForAsync(() => File.Exists(Path.Combine(_root, "Dockerfile"))).ConfigureAwait(true);
        await WaitForAsync(() => explorer.RootNodes.Any(n => n.Name == "Dockerfile")).ConfigureAwait(true);

        File.Exists(Path.Combine(_root, "Dockerfile")).Should().BeTrue("拡張子の無いファイル名も作成できるべき");
        var created = explorer.RootNodes.SingleOrDefault(n => n.Name == "Dockerfile");
        created.Should().NotBeNull();
        explorer.SelectedNode.Should().BeSameAs(created);
    }

    [AvaloniaFact(DisplayName = "不具合2: 折りたたまれたフォルダの直下に新規フォルダを作っても同様に自動展開されて選択される")]
    public async Task 折りたたまれたフォルダへの新規フォルダも自動展開されて選択される()
    {
        var subDir = Path.Combine(_root, "sub");
        Directory.CreateDirectory(subDir);

        var (explorer, dialogs) = await BuildExplorerWithProjectAsync().ConfigureAwait(true);
        var subNode = explorer.RootNodes.Single(n => n.Name == "sub");
        subNode.IsExpanded.Should().BeFalse();

        dialogs.NextPromptResult = "inner";
        explorer.NewFolderCommand.Execute(subNode);
        await WaitForAsync(() => Directory.Exists(Path.Combine(subDir, "inner"))).ConfigureAwait(true);
        await WaitForAsync(() => subNode.Children.Any(n => n.Name == "inner")).ConfigureAwait(true);

        subNode.IsExpanded.Should().BeTrue();
        var created = subNode.Children.SingleOrDefault(n => n.Name == "inner");
        created.Should().NotBeNull();
        explorer.SelectedNode.Should().BeSameAs(created);
    }

    /// <summary>条件が満たされるかタイムアウトするまで待つ（他のUiTestsと同じ作法）。</summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10).ConfigureAwait(true);
        }
    }

    private async Task<(ExplorerViewModel Explorer, PromptDialogService Dialogs)> BuildExplorerWithProjectAsync()
    {
        Directory.CreateDirectory(_root);
        var dialogs = new PromptDialogService();
        var ui = new AvaloniaUiServices();
        var editor = new EditorPaneViewModel(new Settings(), dialogs, ui);
        var appPaths = new AppPaths(Path.Combine(_root, "_app"));
        var explorer = new ExplorerViewModel(appPaths, editor, dialogs, new Settings(), ui);

        var project = new Project { Id = "p_test", Name = "テスト用", Root = _root };
        await explorer.SetProjectAsync(project).ConfigureAwait(true);
        return (explorer, dialogs);
    }

    /// <summary>PromptAsyncの戻り値を都度指定できるテスト用IDialogService。確認系は既定で許諾する。</summary>
    private sealed class PromptDialogService : IDialogService
    {
        public string? NextPromptResult { get; set; }

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult((bool?)true);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult(NextPromptResult);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult((string?)null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null)
            => Task.FromResult((string?)null);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => Task.FromResult((string?)null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }
}
