using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Features;
using Graft.Platform;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// A: 検索結果（SearchView.axaml）の右クリックメニューの回帰テスト。
/// 「開く（該当行へジャンプ）」は既存のJumpCommandをそのまま再利用しているため、その配線と、
/// 新規追加の「パスをコピー」「ファイルマネージャで表示」がヒット行1件ずつの右クリックメニューに
/// 正しく現れること・実際に動くこと・HelpTip.Standardが付いていることを確認する。
/// </summary>
public class SearchContextMenuScenarioTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "検索結果の右クリックメニューに開く・パスをコピー・ファイルマネージャで表示が並ぶ")]
    public void 検索結果の右クリックメニューの3項目が並ぶ()
    {
        var (view, hitBorder) = BuildViewWithHit(out _, out _);
        _ = view;

        var contextMenu = hitBorder.ContextMenu;
        contextMenu.Should().NotBeNull("ヒット行のBorderに右クリックメニューが設定されている必要がある");

        var headers = contextMenu!.GetLogicalDescendants().OfType<MenuItem>()
            .Select(m => m.Header?.ToString()).ToList();

        headers.Should().Contain("開く（該当行へジャンプ） (Enter)");
        headers.Should().Contain("パスをコピー");
        headers.Should().Contain("ファイルマネージャで表示");
    }

    [AvaloniaFact(DisplayName = "検索結果の右クリックメニューの全項目にHelpTip.Standardが付いている")]
    public void 検索結果の右クリックメニュー項目は全てHelpTipを持つ()
    {
        var (_, hitBorder) = BuildViewWithHit(out _, out _);

        var menuItems = hitBorder.ContextMenu!.GetLogicalDescendants().OfType<MenuItem>().ToList();
        menuItems.Should().NotBeEmpty();

        var missing = menuItems.Where(m => HelpTip.GetStandard(m) is null)
            .Select(m => m.Header?.ToString() ?? "(名前無し)").ToList();

        missing.Should().BeEmpty($"次の項目にHelpTip.Standardが付いていません: {string.Join(", ", missing)}");
    }

    [AvaloniaFact(DisplayName = "右クリックメニューの「開く」は既存のJumpCommandを再利用し、該当行へのジャンプ要求が発火する")]
    public void 開くはJumpCommandを再利用してジャンプ要求を発火する()
    {
        var (view, hitBorder) = BuildViewWithHit(out var vm, out var hit);
        _ = view;

        (string FullPath, int Line)? requested = null;
        vm.JumpRequested += (_, target) => requested = target;

        var menuItem = FindMenuItem(hitBorder, "開く（該当行へジャンプ） (Enter)");
        menuItem.Command.Should().BeSameAs(vm.JumpCommand, "既存のジャンプ処理（JumpCommand）を再利用する必要がある");
        vm.JumpCommand.Execute(hit);

        requested.Should().Be((hit.FullPath, hit.LineNumber));
    }

    [AvaloniaFact(DisplayName = "「パスをコピー」を実行すると該当行のフルパスがクリップボードへコピーされる")]
    public void パスをコピーでフルパスがコピーされる()
    {
        var clipboard = new FakeClipboard();
        var (_, hitBorder) = BuildViewWithHit(out var vm, out var hit, clipboard);

        var menuItem = FindMenuItem(hitBorder, "パスをコピー");
        menuItem.Command!.Execute(hit);

        clipboard.Text.Should().Be(hit.FullPath);
        _ = vm;
    }

    [AvaloniaFact(DisplayName = "対象が無いとき（null）はパスをコピー・ファイルマネージャで表示は何もしない（例外にならない）")]
    public void 対象なしでは何もしない()
    {
        var vm = new SearchViewModel(new CrossFileSearchEngine(), new NullDialogService(), new FakeClipboard());

        var act1 = () => vm.CopyPathCommand.Execute(null);
        var act2 = () => vm.RevealCommand.Execute(null);

        act1.Should().NotThrow();
        act2.Should().NotThrow();
    }

    // ------------------------------------------------------------------
    // ヘルパ
    // ------------------------------------------------------------------

    private (SearchView View, Border HitBorder) BuildViewWithHit(
        out SearchViewModel vm, out SearchHitViewModel hit, IClipboardAccess? clipboard = null)
    {
        var localVm = new SearchViewModel(new CrossFileSearchEngine(), new NullDialogService(), clipboard ?? new FakeClipboard());
        var group = new SearchFileGroupViewModel("/proj/a.txt", "a.txt");
        var localHit = new SearchHitViewModel(new SearchHit
        {
            FullPath = "/proj/a.txt",
            RelativePath = "a.txt",
            LineNumber = 3,
            LineText = "hello world",
            ColumnStart = 0,
            MatchLength = 5,
        });
        group.Hits.Add(localHit);
        localVm.Groups.Add(group);

        var view = new SearchView { DataContext = localVm };
        var window = _windows.Track(new Window { Width = 400, Height = 400, Content = view });
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // SearchFileGroupViewModel.IsExpandedは既定でtrueのため、追加の展開操作は不要
        // （TreeDataTemplateのItemsSource=Hitsがそのまま表示される）。
        var hitBorder = window.GetVisualDescendants().OfType<Border>()
            .Single(b => ReferenceEquals(b.DataContext, localHit) && b.ContextMenu is not null);

        vm = localVm;
        hit = localHit;
        return (view, hitBorder);
    }

    private static MenuItem FindMenuItem(Border hitBorder, string header)
        => hitBorder.ContextMenu!.GetLogicalDescendants().OfType<MenuItem>()
            .Single(m => Equals(m.Header?.ToString(), header));

    /// <summary>テストから内容を差し替えられるクリップボード。</summary>
    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; set; }

        public void SetText(string text) => Text = text;

        public Task<string?> GetTextAsync() => Task.FromResult(Text);
    }

    /// <summary>何もしない最小のダイアログ実装。この画面ではダイアログを起動しない操作しか使わない。</summary>
    private sealed class NullDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(true);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult(initial);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null)
            => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }
}
