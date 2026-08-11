using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;

namespace Graft.UiTests;

/// <summary>
/// 不具合3（実機検証）: マッチ失敗（または通常の）差分タブから、画面上の操作だけでは
/// 元のコード編集へ戻れないという指摘への対応。
///
/// 閉じるボタン自体の表示（選択中タブは常時表示にする、EditorPane.axamlのStyle）は
/// ヘッドレステストでは:selected擬似クラスの視覚状態を安定して検証しづらいため、ここでは
/// 差分タブを閉じたときの「戻り先の解決」（EditorPaneViewModel.ResolveReturnTab、
/// EditorPane.axaml.csのOnDiffCloseClickedが最終的に辿り着くCloseTabAsync経由の挙動）を
/// 検証する。優先順位: 1.差分の元ファイルのタブが開いていればそこへ、2.無ければ差分タブを
/// 開く直前にアクティブだったタブへ、3.それも無ければ先頭のタブへ。
/// </summary>
public class DiffTabCloseReturnTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-diff-close-return", Guid.NewGuid().ToString("N"));

    public DiffTabCloseReturnTests()
    {
        Directory.CreateDirectory(_root);
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

    [AvaloniaFact(DisplayName = "不具合3: 元ファイルのタブが開いていれば、差分タブを閉じるとそこへ戻る（直前タブにも先頭タブにも優先）")]
    public async Task 元ファイルのタブが開いていればそこへ戻る()
    {
        // c→a→bの順で開く（先頭タブ=cTab、直前タブ=bTab）ことで、「本来戻るべきa」が
        // 先頭タブ・直前タブのどちらとも一致しない状況を作る。これにより、修正前の
        // 「常にTabs[0]へ戻す」実装ではこのテストが失敗することを確認済み。
        var (editor, cTab, aTab, bTab) = await BuildEditorWithThreeFilesAsync().ConfigureAwait(true);

        // b.txtがアクティブな状態から、a.txt向けの差分タブを開く
        // （ブロック一覧でa.txtのブロックを選んだ状況を模す。直前タブはb.txtになる）。
        editor.ActiveTab = bTab;
        var diff = new DiffViewModel(new Settings(), new AvaloniaUiServices());
        diff.Load(MakePlan("a.txt"));
        editor.ShowDiffTab(diff);

        var diffTab = editor.Tabs.Single(t => t.IsDiffTab);
        (await editor.CloseTabAsync(diffTab).ConfigureAwait(true)).Should().BeTrue();

        editor.ActiveTab.Should().BeSameAs(aTab,
            "差分の元になったa.txtのタブが開いているので、直前タブ（b.txt）にも先頭タブ（c.txt）にも優先してそこへ戻るはず");
    }

    [AvaloniaFact(DisplayName = "不具合3: 元ファイルのタブが開いていなければ、差分タブを開く直前のタブへ戻る（先頭タブより優先）")]
    public async Task 元ファイルが無ければ直前のタブへ戻る()
    {
        // c→aの順で開く（先頭タブ=cTab）ことで、「本来戻るべきa（直前タブ）」が
        // 先頭タブと一致しない状況を作る。
        var (editor, cTab, aTab, _) = await BuildEditorWithThreeFilesAsync(openB: false).ConfigureAwait(true);

        editor.ActiveTab = aTab;
        var diff = new DiffViewModel(new Settings(), new AvaloniaUiServices());
        diff.Load(MakePlan("missing-file-not-open-as-tab.txt")); // どのタブも開いていないファイル
        editor.ShowDiffTab(diff);

        var diffTab = editor.Tabs.Single(t => t.IsDiffTab);
        (await editor.CloseTabAsync(diffTab).ConfigureAwait(true)).Should().BeTrue();

        editor.ActiveTab.Should().BeSameAs(aTab,
            "元ファイルのタブが無い場合は、先頭タブ（c.txt）ではなく差分タブを開く直前にアクティブだったタブ（a.txt）へ戻るはず");
    }

    [AvaloniaFact(DisplayName = "不具合3: 差分タブを閉じてもツリー上のファイルタブ自体は残っている（内容が失われない）")]
    public async Task 差分タブを閉じてもファイルタブは残る()
    {
        var (editor, _, aTab, _) = await BuildEditorWithThreeFilesAsync().ConfigureAwait(true);
        var diff = new DiffViewModel(new Settings(), new AvaloniaUiServices());
        diff.Load(MakePlan("a.txt"));
        editor.ShowDiffTab(diff);

        var diffTab = editor.Tabs.Single(t => t.IsDiffTab);
        (await editor.CloseTabAsync(diffTab).ConfigureAwait(true)).Should().BeTrue();

        editor.Tabs.Should().NotContain(diffTab, "閉じた差分タブ自体はタブ一覧から取り除かれるはず");
        editor.Tabs.Count(t => t.IsDocument).Should().Be(3, "コード編集用のファイルタブは差分タブを閉じても残るはず");
    }

    private static BlockPlan MakePlan(string relativePath) => new()
    {
        Block = new DeleteBlock { Path = relativePath },
        Path = relativePath,
        Operation = EntryOperation.Modify,
        CanApply = false,
        IsSelected = false,
    };

    /// <summary>c.txt→a.txt→(b.txt)の順で開く。戻り値は(editor, cTab, aTab, bTab)。</summary>
    private async Task<(EditorPaneViewModel Editor, EditorTabViewModel CTab, EditorTabViewModel ATab, EditorTabViewModel? BTab)>
        BuildEditorWithThreeFilesAsync(bool openB = true)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "aの内容").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(_root, "b.txt"), "bの内容").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(_root, "c.txt"), "cの内容").ConfigureAwait(true);

        var ui = new AvaloniaUiServices();
        var editor = new EditorPaneViewModel(new Settings(), new NullDialogService(), ui);
        editor.SetProject(_root);

        var cResult = await editor.OpenFileAsync(Path.Combine(_root, "c.txt")).ConfigureAwait(true);
        var aResult = await editor.OpenFileAsync(Path.Combine(_root, "a.txt")).ConfigureAwait(true);
        EditorTabViewModel? bTab = null;
        if (openB)
        {
            var bResult = await editor.OpenFileAsync(Path.Combine(_root, "b.txt")).ConfigureAwait(true);
            bTab = bResult.Value;
        }

        editor.Tabs[0].Should().BeSameAs(cResult.Value, "先頭タブはc.txt（最初に開いたタブ）のはず（テストの前提）");
        return (editor, cResult.Value, aResult.Value, bTab);
    }

    /// <summary>確認ダイアログを常に許諾するIDialogService（保存確認は今回のシナリオでは発生しない）。</summary>
    private sealed class NullDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult((bool?)true);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult(initial);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult((string?)null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null)
            => Task.FromResult((string?)null);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => Task.FromResult((string?)null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }
}
