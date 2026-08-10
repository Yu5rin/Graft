using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;

namespace Graft.UiTests;

/// <summary>
/// 細かいユーザビリティ改善4: エクスプローラのファイル名絞り込み。
/// <see cref="ExplorerViewModel.FilterText"/>の設定から300ms（<c>FilterDebounceMs</c>）の
/// デバウンスを挟んで<see cref="ExplorerFilterService"/>によるディスク走査が走り、一致した
/// ファイルの祖先フォルダが自動展開され、一致しない項目はそもそも
/// <see cref="FileNodeViewModel.Children"/>／<see cref="ExplorerViewModel.RootNodes"/>に
/// 追加されない（当初はIsVisibleによる見た目だけの非表示だったが、実機Xvfb環境で
/// 描画が更新されず残る不具合が見つかったため、ツリーへ載せるノード自体を絞り込む方式に
/// 変更した。経緯はConverters.ExcludedNodeVisibleのコメント参照）。
/// デバウンスは実時間（DispatcherTimer）で動くため、他のデバウンステスト
/// （SettingsAutoSaveTests等）と同じく<see cref="Task.Delay(int)"/>で実際に待つ。
/// </summary>
public class ExplorerFilterTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-explorer-filter", Guid.NewGuid().ToString("N"));

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

    [AvaloniaFact(DisplayName = "一致するファイルだけ表示され、その親フォルダは自動的に展開される")]
    public async Task 一致ファイルだけ表示され親フォルダが自動展開される()
    {
        var (explorer, _) = await BuildExplorerWithProjectAsync().ConfigureAwait(true);

        explorer.FilterText = "match";
        await WaitForFilterSettleAsync(explorer).ConfigureAwait(true);

        var rootNames = explorer.RootNodes.Select(n => n.Name).ToList();
        rootNames.Should().Contain(new[] { "a", "b", "toplevel_match.txt" });
        rootNames.Should().NotContain("unrelated.md", "一致しないファイルは絞り込み中はツリーに載らない");

        var a = explorer.RootNodes.Single(n => n.Name == "a");
        a.IsExpanded.Should().BeTrue("一致ファイルの親フォルダは自動的に展開される必要がある");

        var aChildNames = a.Children.Select(n => n.Name).ToList();
        aChildNames.Should().Contain("match_one.txt");
        aChildNames.Should().NotContain("other.txt", "一致しないファイルは絞り込み中はツリーに載らない");

        var b = explorer.RootNodes.Single(n => n.Name == "b");
        b.IsExpanded.Should().BeTrue();
        var nested = b.Children.Single(n => n.Name == "nested");
        nested.IsExpanded.Should().BeTrue("多段フォルダでも、一致ファイルまでの経路がすべて自動展開される必要がある");
        nested.Children.Select(n => n.Name).Should().Contain("match_two.txt");
    }

    [AvaloniaFact(DisplayName = "絞り込みを消すと展開前の状態へ戻る（絞り込み前から開いていたフォルダは開いたまま、絞り込みで自動的に開いたフォルダは閉じ直す）")]
    public async Task 絞り込み解除で元の展開状態へ戻る()
    {
        var (explorer, _) = await BuildExplorerWithProjectAsync().ConfigureAwait(true);

        // 絞り込み前に "a" だけ手動で開いておく。
        var a = explorer.RootNodes.Single(n => n.Name == "a");
        a.IsExpanded = true;
        await WaitForAsync(() => a.IsLoaded).ConfigureAwait(true);
        var b0 = explorer.RootNodes.Single(n => n.Name == "b");
        b0.IsExpanded.Should().BeFalse("bはまだ開いていないはず（前提条件の確認）");

        explorer.FilterText = "match";
        await WaitForFilterSettleAsync(explorer).ConfigureAwait(true);

        var b = explorer.RootNodes.Single(n => n.Name == "b");
        b.IsExpanded.Should().BeTrue("絞り込み中は一致ファイルの経路として自動的に開く");

        explorer.FilterText = string.Empty;
        await WaitForAsync(() => !b.IsExpanded).ConfigureAwait(true);

        a.IsExpanded.Should().BeTrue("絞り込み前から開いていたフォルダは、解除後も開いたままである必要がある");
        b.IsExpanded.Should().BeFalse("絞り込みで自動的に開いたフォルダは、解除後は閉じ直す必要がある");

        // 絞り込み中はツリーから除外されていた項目も、解除後は全て戻っていること
        // （かつ"a"は同一インスタンスのまま＝展開状態が保たれていること、上のIsExpanded確認で兼ねる）。
        explorer.RootNodes.Select(n => n.Name).Should().Contain(
            new[] { "a", "b", "toplevel_match.txt", "unrelated.md" });
        a.Children.Select(n => n.Name).Should().Contain(new[] { "match_one.txt", "other.txt" });
    }

    [AvaloniaFact(DisplayName = "1件も一致しないとFilterHasNoMatchesが立つ")]
    public async Task 一致0件でFilterHasNoMatchesが立つ()
    {
        var (explorer, _) = await BuildExplorerWithProjectAsync().ConfigureAwait(true);

        explorer.FilterText = "該当なしのはずの文字列xyz";
        await WaitForAsync(() => explorer.FilterHasNoMatches).ConfigureAwait(true);

        explorer.FilterHasNoMatches.Should().BeTrue();
        explorer.RootNodes.Should().BeEmpty("1件も一致しなければツリーには何も表示されない");
    }

    [AvaloniaFact(DisplayName = "実機で見つかった不具合の回帰: 絞り込み中に新しく現れたファイルも、一致しなければツリーに載らない")]
    public async Task 絞り込み中に新規ファイルが現れても正しく隠れる()
    {
        // 実機Xvfb環境で発覚した不具合の再現: 当初はIsVisibleによる見た目だけの非表示だったため、
        // 展開済みフォルダのVirtualizingStackPanel配下で一部の兄弟ノードだけをIsVisible=falseに
        // 切り替えても、実機では描画が更新されず残ってしまう症状があった（ヘッドレス自動テストは
        // ウィンドウを実際に描画しないため再現しなかった。詳しい経緯はConverters.ExcludedNodeVisible・
        // ExplorerViewModel.ApplyFilterToLevelのコメント参照）。ツリーへ載せるノード自体を絞り込む
        // 方式にした今は、RefreshCommand（監視イベント経由の再列挙と同じReconcileDirectoryAsync
        // 経路）で新しく現れたファイルも、一致しなければそもそもChildrenに追加されないことを確認する。
        var (explorer, _) = await BuildExplorerWithProjectAsync().ConfigureAwait(true);

        explorer.FilterText = "match";
        await WaitForFilterSettleAsync(explorer).ConfigureAwait(true);

        var a = explorer.RootNodes.Single(n => n.Name == "a");
        a.IsExpanded.Should().BeTrue();

        // 絞り込みが安定した後で、一致しない新規ファイルをディスク上に追加する。
        // （絞り込み結果自体はFilterTextの変更時にのみ再検索されディスク監視では自動更新されない
        // 仕様のため、ここで新たに追加したファイルが絞り込みの一致集合に入ることはない。
        // 確認したいのは「その新規ファイルがReconcileDirectoryAsyncを経由してもツリーに
        // 漏れ出さないこと」。）
        File.WriteAllText(Path.Combine(_root, "a", "new_unrelated.txt"), "新規・無関係");

        // RefreshCommand（更新ボタン、または監視イベントと同じReconcileDirectoryAsync経路）で
        // 実列挙結果を反映させる（AsyncRelayCommand.Executeはasync voidのため、完了を
        // 短い実時間待ちで確保する。対象が小さなディレクトリのため十分な余裕を見込む）。
        explorer.RefreshCommand.Execute(null);
        await Task.Delay(300).ConfigureAwait(true);

        var aChildNames = a.Children.Select(n => n.Name).ToList();
        aChildNames.Should().NotContain(
            "new_unrelated.txt", "絞り込み中に新しく現れたファイルも、一致しなければツリーに載らない必要がある");
        aChildNames.Should().Contain("match_one.txt", "既存の一致ファイルは引き続き見えている必要がある");
    }

    [AvaloniaFact(DisplayName = "ClearFilterCommandで絞り込み文字列が空になる")]
    public async Task ClearFilterCommandで空になる()
    {
        var (explorer, _) = await BuildExplorerWithProjectAsync().ConfigureAwait(true);
        explorer.FilterText = "match";
        await WaitForFilterSettleAsync(explorer).ConfigureAwait(true);
        explorer.HasFilterText.Should().BeTrue();

        explorer.ClearFilterCommand.Execute(null);

        explorer.HasFilterText.Should().BeFalse();
        explorer.FilterText.Should().BeEmpty();
    }

    /// <summary>デバウンス（300ms）と非同期のディスク走査が完了するのを実時間で待つ。</summary>
    private static async Task WaitForFilterSettleAsync(ExplorerViewModel explorer)
    {
        await Task.Delay(500).ConfigureAwait(true);
        await WaitForAsync(() => explorer.RootNodes.Any(n => n.IsExpanded) || explorer.FilterHasNoMatches)
            .ConfigureAwait(true);
        // フォルダ展開（ExpandPathAsync）自体も非同期のため、末端の子まで読み込まれるのを待つ。
        await Task.Delay(200).ConfigureAwait(true);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10).ConfigureAwait(true);
        }
    }

    private async Task<(ExplorerViewModel Explorer, PromptDialogService Dialogs)> BuildExplorerWithProjectAsync()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "a"));
        Directory.CreateDirectory(Path.Combine(_root, "b", "nested"));
        File.WriteAllText(Path.Combine(_root, "a", "match_one.txt"), "一致1");
        File.WriteAllText(Path.Combine(_root, "a", "other.txt"), "無関係");
        File.WriteAllText(Path.Combine(_root, "b", "nested", "match_two.txt"), "一致2");
        File.WriteAllText(Path.Combine(_root, "toplevel_match.txt"), "一致3");
        File.WriteAllText(Path.Combine(_root, "unrelated.md"), "無関係2");

        var dialogs = new PromptDialogService();
        var ui = new AvaloniaUiServices();
        var editor = new EditorPaneViewModel(new Settings(), dialogs, ui);
        var appPaths = new AppPaths(Path.Combine(_root, "_app"));
        var explorer = new ExplorerViewModel(appPaths, editor, dialogs, new Settings(), ui);

        var project = new Project { Id = "p_test", Name = "テスト用", Root = _root };
        await explorer.SetProjectAsync(project).ConfigureAwait(true);
        return (explorer, dialogs);
    }

    /// <summary>確認系は既定で許諾するだけの最小フェイク。</summary>
    private sealed class PromptDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult((bool?)true);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult((string?)null);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult((string?)null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null)
            => Task.FromResult((string?)null);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => Task.FromResult((string?)null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }
}
