using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// Markdownプレビュー機能の、<see cref="ShellWindow"/>を経由した統合回帰テスト。
///
/// 【実機（Xvfb）検証で発覚した不具合】<see cref="Graft.Views.EditorPane"/>単体で組み立てた
/// ヘッドレステスト（<c>MarkdownPreviewTests.切替ボタンとEscでモードを切り替えられる</c>）は
/// 通っていたにもかかわらず、実際にアプリを起動してEscapeキーを押しても編集モードから
/// プレビューへ戻らないという不具合が実機検証で見つかった。原因は
/// <see cref="ShellWindow.Keyboard.cs"/>の<c>OnTunnelKeyDown</c>が、<see cref="EditorPane"/>の
/// 祖先（トンネリングでより先に実行される）としてEscapeを無条件に「キューの破棄」として
/// 処理し、<c>e.Handled = true</c>にしてしまっていたため、<see cref="EditorPane"/>自身のEscape
/// 処理（検索オーバーレイを閉じる・Markdownプレビューへ戻る）に一切イベントが届いていなかった
/// ことだった。<see cref="EditorPane"/>単体のテストはこの祖先を持たないため検出できなかった。
///
/// 本テストは<see cref="ShellWindow"/>を実際に組み立てて同じ経路を再現し、この不具合が
/// 再発しないことを確認する（<c>ShouldDeferEscapeToEditor</c>の回帰ガード）。
/// </summary>
public class MarkdownPreviewShellIntegrationTests : IDisposable
{
    private readonly string _baseDirectory =
        Path.Combine(Path.GetTempPath(), "graft-md-shell", Guid.NewGuid().ToString("N"));
    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
        try
        {
            if (Directory.Exists(_baseDirectory)) Directory.Delete(_baseDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果に影響しない。
        }
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "実機不具合の回帰: ShellWindow経由でも、Markdown編集モードでのEscapeがキューの破棄に奪われずプレビューへ戻る")]
    public async Task シェル経由でもEscapeでMarkdownプレビューへ戻る()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var (shell, window) = BuildShellAndWindow(appPaths);
        window.Show();
        ShellWindowLoadWaiter.WaitForLayoutApplied(window);

        var filePath = Path.Combine(_baseDirectory, "doc.md");
        await File.WriteAllTextAsync(filePath, "# 見出し\n\n本文です。\n");

        var result = await shell.Editor.OpenFileAsync(filePath);
        result.IsSuccess.Should().BeTrue();
        var tab = result.Value;
        tab.ShowMarkdownPreview.Should().BeTrue("前提: .mdは既定でプレビュー表示のはず");

        // 編集モードへ切り替える（ボタン操作自体はMarkdownPreviewTests側で別途検証済みのため、
        // ここでは状態遷移だけを起こしてEditorPane側の表示切替を動かす）。
        tab.ShowMarkdownPreview = false;
        window.CaptureRenderedFrame().Should().NotBeNull(); // Editor.Focus()の反映も含め描画を進める

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        tab.ShowMarkdownPreview.Should().BeTrue(
            "実機で発覚した不具合の回帰確認: ShellWindowのEscape（キューの破棄）に奪われず、"
            + "EditorPane自身のEscape処理（プレビューへ戻る）まで届く必要がある");

        window.Close();
    }

    [AvaloniaFact(DisplayName = "Markdown編集モードでなければ、ShellWindow経由のEscapeは従来どおりキューの破棄として扱われる")]
    public async Task 通常時はEscapeが従来どおりキューの破棄に使われる()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var (shell, window) = BuildShellAndWindow(appPaths);
        window.Show();
        ShellWindowLoadWaiter.WaitForLayoutApplied(window);

        var filePath = Path.Combine(_baseDirectory, "plain.txt");
        await File.WriteAllTextAsync(filePath, "ただのテキストです。\n");

        var result = await shell.Editor.OpenFileAsync(filePath);
        result.IsSuccess.Should().BeTrue();
        window.CaptureRenderedFrame().Should().NotBeNull();

        // 解析結果が無い状態ではDiscardCommand.CanExecuteはfalseのため実行結果を外部から
        // 直接観測しづらい。ここでは「例外なく実行され、Markdownプレビュー機能側のガード
        // （ShouldDeferEscapeToEditor）が非Markdownファイルで誤って介入しない」ことのみを
        // 確認する（回帰ガードとしての主目的は前のテストの反対側の確認）。
        var act = () => window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        act.Should().NotThrow();

        window.Close();
    }

    private (ShellViewModel Shell, ShellWindow Window) BuildShellAndWindow(AppPaths appPaths)
    {
        IDialogService dialogs = new NullDialogService();
        IUiServices ui = new AvaloniaUiServices();

        var shell = Graft.Views.StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Settings(),
            new SettingsStore(appPaths),
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            dialogs,
            ui,
            openSettings: () => { });

        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        return (shell, window);
    }
}
