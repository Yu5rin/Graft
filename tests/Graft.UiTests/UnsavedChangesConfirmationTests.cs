using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;

namespace Graft.UiTests;

/// <summary>
/// 不具合2の回帰: 実機（Windows）のスクリーンショットで、未保存の変更を閉じる際の確認
/// ダイアログが「キャンセル」「破棄」「保存」の順（左から。Windowsの作法と逆）で、
/// 「破棄」という分かりにくい文言で表示されていた。<see cref="Graft.Editor.EditorTabManager.CloseAsync"/>
/// （<c>EditorPaneViewModel.CloseTabAsync</c>経由で呼ばれる）が渡すラベルを検証する。
///
/// 【文言の経緯】当初の指示は「破棄」→「保存しない」だったが、「「sample.md」には保存されて
/// いない変更があります。保存しますか？」という疑問文には「はい」「いいえ」の方が問いと答えの
/// 形が揃うため、最終的に「はい」「いいえ」を採用した（並び順「肯定→否定→キャンセル」は
/// <see cref="AvaloniaDialogServiceButtonOrderTests"/>で別途検証する）。
/// </summary>
public class UnsavedChangesConfirmationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-unsaved-confirm", Guid.NewGuid().ToString("N"));

    public UnsavedChangesConfirmationTests() => Directory.CreateDirectory(_root);

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

    [AvaloniaFact(DisplayName = "実機不具合の回帰: 未保存の変更を閉じる確認は「保存しますか？」に対して「はい」「いいえ」で問い、既定は「はい」（保存）")]
    public async Task 未保存タブを閉じる確認のラベルが疑問文と揃っている()
    {
        var path = Path.Combine(_root, "sample.md");
        await File.WriteAllTextAsync(path, "元の内容\n");

        var dialogs = new RecordingDialogService();
        var vm = new EditorPaneViewModel(new Settings(), dialogs, new AvaloniaUiServices());
        vm.SetProject(_root);
        var result = await vm.OpenFileAsync(path).ConfigureAwait(true);
        result.IsSuccess.Should().BeTrue();
        var tab = result.Value;

        // 未保存にする（ディスクへは書かず、編集中バッファだけ変える）。
        tab.Session.Document.Insert(tab.Session.Document.TextLength, "追記");
        tab.Session.IsModified.Should().BeTrue("前提: 未保存の変更が無いと確認ダイアログ自体が出ない");

        // 「はい」（既定ボタン、実機不具合の回帰: 保存は非破壊的なので既定にしてよい）を選ぶ。
        dialogs.NextThreeWayResult = true;
        var closed = await vm.CloseTabAsync(tab).ConfigureAwait(true);

        dialogs.LastThreeWayCall.Should().NotBeNull("未保存のタブを閉じようとしたら確認ダイアログが呼ばれるはず");
        var (title, message, yesLabel, noLabel) = dialogs.LastThreeWayCall!.Value;

        title.Should().Be("変更の保存");
        message.Should().Contain("保存しますか？", "問いかけ文は変更しない指示だった");
        yesLabel.Should().Be("はい", "疑問文（保存しますか？）に対しては「保存」「保存しない」より「はい」の方が問いと答えの形が揃う");
        noLabel.Should().Be("いいえ");
        noLabel.Should().NotBe("破棄", "「破棄」は何が起きるか分かりにくいという実機報告があった");

        closed.Should().BeTrue();
        (await File.ReadAllTextAsync(path).ConfigureAwait(true)).Should().Contain("追記", "「はい」＝保存が実行されるはず");
    }

    /// <summary>ConfirmThreeWayAsyncの呼び出し引数を記録するテスト用IDialogService。</summary>
    private sealed class RecordingDialogService : IDialogService
    {
        public bool? NextThreeWayResult { get; set; }
        public (string Title, string Message, string YesLabel, string NoLabel)? LastThreeWayCall { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
        {
            LastThreeWayCall = (title, message, yesLabel, noLabel);
            return Task.FromResult(NextThreeWayResult);
        }

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(null);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null)
            => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }
}
