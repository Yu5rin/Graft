using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;

namespace Graft.UiTests;

/// <summary>
/// 実機検証で発見した不具合4（起動時のエラーダイアログが複数重なって表示される）のうち、
/// 集約しきれていなかった経路（<see cref="ExplorerViewModel.SetProjectAsync"/> が
/// ファイル監視の開始失敗を自分で即座にダイアログ表示していた）を検証する。
/// <see cref="StartupCoordinator"/> の実際の配線・タイミング（RunStartupValidationAsyncとの
/// 待ち合わせ）は実機検証を要する統合的な処理のため、ここでは
/// <see cref="ExplorerViewModel.WatchStartCompletedHandler"/> という差し替えポイント自体の契約
/// （既定は失敗時のみ即時ダイアログ、設定時は成功・失敗を問わず必ずハンドラへ通知しダイアログは
/// 出さない）を単体で検証する。「成功時もハンドラへ必ず通知する」契約は、この通知を
/// StartupCoordinatorが「初回の監視開始試行が完了した」ことの待ち合わせ信号としても使うために
/// 必須（そうしないと、背景の起動時検証がこの試行より先に完了した場合に監視失敗の警告が
/// 一切表示されないまま失われるレースが起きる。実機検証で実際に踏んだ）。
/// </summary>
public class ExplorerWatchFailureAggregationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-explorer-watchfail", Guid.NewGuid().ToString("N"));

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

    [AvaloniaFact(DisplayName = "WatchStartCompletedHandler未設定時は、監視開始失敗で即座にダイアログを表示する（従来どおりの挙動）")]
    public async Task ハンドラ未設定なら即座にダイアログを出す()
    {
        var dialogs = new RecordingDialogService();
        var explorer = BuildExplorer(dialogs);
        var project = BuildMissingRootProject();

        await explorer.SetProjectAsync(project).ConfigureAwait(true);

        dialogs.ShownMessages.Should().ContainSingle(
            "ハンドラが無い場合はExplorerViewModel自身が従来どおりダイアログを出すはず");
    }

    [AvaloniaFact(DisplayName = "WatchStartCompletedHandlerを設定すると、監視開始失敗はハンドラへ渡りダイアログは出さない（不具合4対応）")]
    public async Task ハンドラ設定時はダイアログを出さずハンドラへ渡す()
    {
        var dialogs = new RecordingDialogService();
        var explorer = BuildExplorer(dialogs);
        var project = BuildMissingRootProject();

        var received = new List<GraftIssue?>();
        explorer.WatchStartCompletedHandler = received.Add;

        await explorer.SetProjectAsync(project).ConfigureAwait(true);

        dialogs.ShownMessages.Should().BeEmpty(
            "起動時レポートへ集約する間は、ExplorerViewModel自身が別ダイアログを出してはならない（不具合4）");
        received.Should().ContainSingle();
        received[0].Should().NotBeNull();
        received[0]!.Code.Should().Be(ErrorCode.E704);
        received[0]!.Detail.Should().Contain("フォルダが見つかりません", "英語の生例外文のままではなく日本語化されているはず（不具合3）");
    }

    [AvaloniaFact(DisplayName = "監視開始が成功した場合も、ハンドラを設定していればnullで必ず1回通知する（待ち合わせ信号として使うため）")]
    public async Task 成功時もハンドラへnullで通知する()
    {
        var dialogs = new RecordingDialogService();
        var explorer = BuildExplorer(dialogs);
        Directory.CreateDirectory(_root);
        var project = new Project { Id = "p_ok", Name = "テスト用", Root = _root };

        var received = new List<GraftIssue?>();
        explorer.WatchStartCompletedHandler = received.Add;

        await explorer.SetProjectAsync(project).ConfigureAwait(true);

        dialogs.ShownMessages.Should().BeEmpty();
        received.Should().ContainSingle("成功・失敗どちらでも必ず1回だけ通知するはず（StartupCoordinatorの待ち合わせに使うため）");
        received[0].Should().BeNull("監視開始が成功した場合はissueなし（null）で通知するはず");
    }

    private ExplorerViewModel BuildExplorer(IDialogService dialogs)
    {
        var ui = new AvaloniaUiServices();
        var editor = new EditorPaneViewModel(new Settings(), dialogs, ui);
        var appPaths = new AppPaths(Path.Combine(_root, "_app"));
        return new ExplorerViewModel(appPaths, editor, dialogs, new Settings(), ui);
    }

    private Project BuildMissingRootProject()
    {
        // ExplorerViewModel.SetProjectAsync はまずツリー列挙（FileTreeService）を行うため、
        // rootディレクトリ自体は実在させつつ、監視開始（FileSystemWatcher）だけを失敗させたい。
        // ……が、FileSystemWatcherが失敗する最も単純な条件はディレクトリ自体が存在しないことなので、
        // 実機の再現手順（存在しないrootを持つプロジェクト）とまったく同じ構成にする。
        // FileTreeService側のツリー列挙は「存在しない」場合エラーを飲み込んで空一覧を返す設計
        // （ExplorerViewModel.ReconcileDirectoryAsyncのコメント参照）のため、後続の監視開始の
        // 失敗だけが表面化する。
        Directory.CreateDirectory(_root);
        var missingRoot = Path.Combine(_root, "does-not-exist");
        return new Project { Id = "p_missing", Name = "テスト用", Root = missingRoot };
    }

    /// <summary>ShowMessageAsyncの呼び出しだけを記録するテスト用IDialogService。確認系は安全側の既定値。</summary>
    private sealed class RecordingDialogService : IDialogService
    {
        public List<string> ShownMessages { get; } = new();

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult((bool?)null);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult((string?)null);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult((string?)null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null)
            => Task.FromResult((string?)null);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => Task.FromResult((string?)null);

        public Task ShowMessageAsync(string title, string message)
        {
            ShownMessages.Add(message);
            return Task.CompletedTask;
        }
    }
}
