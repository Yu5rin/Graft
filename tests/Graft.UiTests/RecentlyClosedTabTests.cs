using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Editor;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.ViewModels;

namespace Graft.UiTests;

/// <summary>
/// 製品としての使い勝手3件のうち機能3（Ctrl+Shift+Tで直前に閉じたタブを開き直す）の回帰テスト。
///
/// キー配線（ShellWindow.Keyboard.cs、実際のCtrl+Shift+T押下でのルーティング）はEditorTabの
/// フォーカス判定がheadlessで安定しないため他のキー配線テスト（ShortcutsWindowTests・
/// DeleteUndoTests）と同じ方針で対象外にし、ここではEditorPaneViewModel.RecentlyClosed.cs・
/// EditorTabManagerのロジック（EditorTabReorderTestsと同じくShellWindow抜きでEditorPaneViewModel
/// を直接構築する手法）に絞って検証する。実際のキー押下からの導線はShellWindow.Keyboard.csの
/// switch文（他のCtrl+Shift+*と同じ書き方）そのものであり、実機のXvfb確認でも別途確かめる。
/// </summary>
public class RecentlyClosedTabTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-recently-closed", Guid.NewGuid().ToString("N"));

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

    [AvaloniaFact(DisplayName = "閉じたタブをCtrl+Shift+T相当の操作でカーソル位置ごと開き直せる")]
    public async Task 閉じたタブをカーソル位置ごと開き直せる()
    {
        var dir = Path.Combine(_root, "project1");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "a.txt");
        await File.WriteAllTextAsync(path, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var vm = new EditorPaneViewModel(new Settings(), new NullDialogService(), new AvaloniaUiServices());
        vm.SetProject(dir);
        var tab = (await vm.OpenFileAsync(path).ConfigureAwait(true)).Value;
        tab.CaretLine = 3;
        tab.CaretColumn = 2;

        (await vm.CloseTabAsync(tab).ConfigureAwait(true)).Should().BeTrue();
        vm.Tabs.Should().BeEmpty("閉じた直後はタブが無いはず");

        await vm.ReopenLastClosedTabAsync().ConfigureAwait(true);

        vm.Tabs.Should().ContainSingle("Ctrl+Shift+T相当の操作で閉じたタブが開き直されるはず");
        vm.ActiveTab!.Session.FullPath.Should().Be(path);
        vm.ActiveTab!.CaretLine.Should().Be(3, "閉じた時点のカーソル行が復元されるはず");
        vm.ActiveTab!.CaretColumn.Should().Be(2, "閉じた時点のカーソル桁が復元されるはず");
    }

    [AvaloniaFact(DisplayName = "複数件閉じると新しい順に1件ずつ開き直せる")]
    public async Task 複数件閉じると新しい順に開き直せる()
    {
        var dir = Path.Combine(_root, "project2");
        Directory.CreateDirectory(dir);
        var pathA = Path.Combine(dir, "a.txt");
        var pathB = Path.Combine(dir, "b.txt");
        var pathC = Path.Combine(dir, "c.txt");
        await File.WriteAllTextAsync(pathA, "a").ConfigureAwait(true);
        await File.WriteAllTextAsync(pathB, "b").ConfigureAwait(true);
        await File.WriteAllTextAsync(pathC, "c").ConfigureAwait(true);

        var vm = new EditorPaneViewModel(new Settings(), new NullDialogService(), new AvaloniaUiServices());
        vm.SetProject(dir);
        var a = (await vm.OpenFileAsync(pathA).ConfigureAwait(true)).Value;
        var b = (await vm.OpenFileAsync(pathB).ConfigureAwait(true)).Value;
        var c = (await vm.OpenFileAsync(pathC).ConfigureAwait(true)).Value;

        // a → b → c の順に閉じる（cが最後に閉じた＝最初に復元される）。
        await vm.CloseTabAsync(a).ConfigureAwait(true);
        await vm.CloseTabAsync(b).ConfigureAwait(true);
        await vm.CloseTabAsync(c).ConfigureAwait(true);
        vm.Tabs.Should().BeEmpty();

        await vm.ReopenLastClosedTabAsync().ConfigureAwait(true);
        vm.Tabs.Should().ContainSingle().Which.Session.FullPath.Should().Be(pathC, "最後に閉じたcから復元されるはず");

        await vm.ReopenLastClosedTabAsync().ConfigureAwait(true);
        vm.Tabs.Select(t => t.Session.FullPath).Should().BeEquivalentTo(new[] { pathC, pathB });

        await vm.ReopenLastClosedTabAsync().ConfigureAwait(true);
        vm.Tabs.Select(t => t.Session.FullPath).Should().BeEquivalentTo(new[] { pathC, pathB, pathA });
    }

    [AvaloniaFact(DisplayName = "削除されて存在しなくなったファイルの記録は自動的に読み飛ばされる")]
    public async Task 存在しないファイルは読み飛ばされる()
    {
        var dir = Path.Combine(_root, "project3");
        Directory.CreateDirectory(dir);
        var pathA = Path.Combine(dir, "a.txt");
        var pathB = Path.Combine(dir, "b.txt");
        await File.WriteAllTextAsync(pathA, "a").ConfigureAwait(true);
        await File.WriteAllTextAsync(pathB, "b").ConfigureAwait(true);

        var vm = new EditorPaneViewModel(new Settings(), new NullDialogService(), new AvaloniaUiServices());
        vm.SetProject(dir);
        var a = (await vm.OpenFileAsync(pathA).ConfigureAwait(true)).Value;
        var b = (await vm.OpenFileAsync(pathB).ConfigureAwait(true)).Value;

        await vm.CloseTabAsync(a).ConfigureAwait(true); // 先に閉じる（記録の下側）
        await vm.CloseTabAsync(b).ConfigureAwait(true); // 後に閉じる（記録の先頭）

        // b.txtを閉じたあとで実体を削除する（例: 別プロセス・エクスプローラでの削除を想定）。
        File.Delete(pathB);

        await vm.ReopenLastClosedTabAsync().ConfigureAwait(true);

        vm.Tabs.Should().ContainSingle(
            "b.txtの記録は実体が無いため自動的に読み飛ばされ、a.txtが開き直されるはず");
        vm.Tabs[0].Session.FullPath.Should().Be(pathA);
    }

    [AvaloniaFact(DisplayName = "記録がすべて存在しない場合は、分かりやすいメッセージを出す")]
    public async Task 記録が全て存在しない場合はメッセージを出す()
    {
        var dir = Path.Combine(_root, "project4");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "a.txt");
        await File.WriteAllTextAsync(path, "a").ConfigureAwait(true);

        var dialogs = new RecordingDialogService();
        var vm = new EditorPaneViewModel(new Settings(), dialogs, new AvaloniaUiServices());
        vm.SetProject(dir);
        var a = (await vm.OpenFileAsync(path).ConfigureAwait(true)).Value;
        await vm.CloseTabAsync(a).ConfigureAwait(true);
        File.Delete(path);

        await vm.ReopenLastClosedTabAsync().ConfigureAwait(true);

        vm.Tabs.Should().BeEmpty();
        dialogs.Messages.Should().ContainSingle(
            "記録はあったが実体が無く復元できなかったことが、利用者に分かるように伝わる必要がある");
    }

    [AvaloniaFact(DisplayName = "記録が最初から無い場合も、分かりやすいメッセージを出す")]
    public async Task 記録が最初から無い場合もメッセージを出す()
    {
        var dir = Path.Combine(_root, "project5");
        Directory.CreateDirectory(dir);

        var dialogs = new RecordingDialogService();
        var vm = new EditorPaneViewModel(new Settings(), dialogs, new AvaloniaUiServices());
        vm.SetProject(dir);

        await vm.ReopenLastClosedTabAsync().ConfigureAwait(true);

        vm.Tabs.Should().BeEmpty();
        dialogs.Messages.Should().ContainSingle();
    }

    [AvaloniaFact(DisplayName = "記録は最大10件までで、古いものから押し出される")]
    public async Task 記録は最大10件まで()
    {
        var dir = Path.Combine(_root, "project6");
        Directory.CreateDirectory(dir);

        var vm = new EditorPaneViewModel(new Settings(), new NullDialogService(), new AvaloniaUiServices());
        vm.SetProject(dir);

        // 11個のファイルを開いては閉じ、11件分の「閉じた記録」を積む。
        var paths = new List<string>();
        for (var i = 0; i < 11; i++)
        {
            var path = Path.Combine(dir, $"f{i}.txt");
            await File.WriteAllTextAsync(path, $"{i}").ConfigureAwait(true);
            paths.Add(path);
            var tab = (await vm.OpenFileAsync(path).ConfigureAwait(true)).Value;
            await vm.CloseTabAsync(tab).ConfigureAwait(true);
        }

        // 新しい順に10件（f10〜f1）は復元できるが、最初に閉じたf0はもう記録に残っていないはず。
        for (var i = 0; i < 10; i++)
        {
            await vm.ReopenLastClosedTabAsync().ConfigureAwait(true);
        }
        var reopened = vm.Tabs.Select(t => t.Session.FullPath).ToList();

        reopened.Should().HaveCount(10);
        reopened.Should().NotContain(paths[0], "10件を超えた分は古いものから押し出されるはず（最大10件程度の仕様）");
        reopened.Should().Contain(paths[10], "最後に閉じたものは必ず記録に残っているはず");
    }

    /// <summary>ShowMessageAsyncの呼び出しだけを記録する。他は破壊的操作の安全側（NullDialogServiceと同じ）。</summary>
    private sealed class RecordingDialogService : IDialogService
    {
        public List<(string Title, string Message)> Messages { get; } = new();

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult((bool?)null);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult((string?)null);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult((string?)null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult((string?)null);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => Task.FromResult((string?)null);

        public Task ShowMessageAsync(string title, string message)
        {
            Messages.Add((title, message));
            return Task.CompletedTask;
        }
    }
}
