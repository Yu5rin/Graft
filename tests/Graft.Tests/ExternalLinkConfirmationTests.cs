using FluentAssertions;
using Graft.Platform;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <see cref="ExternalLinkConfirmation"/>: セキュリティ点検の指摘事項
/// 「release.HtmlUrlをスキーム検査なしでShellExecuteに渡している」への対応を検証する。
/// GitHub Releases APIの応答に含まれる<c>html_url</c>は通信経路や配布元設定次第で
/// 信頼できない値になりうるため、(1) 絶対URIかつhttpsのときだけ開く、(2) 開く前に
/// URL全文を確認ダイアログへ表示する、の2点を実際のUI・ブラウザ起動を経由せずに固定する。
/// </summary>
public class ExternalLinkConfirmationTests
{
    [Fact(DisplayName = "https絶対URIなら確認ダイアログにURL全文を表示し、OKなら実際に開く")]
    public async Task httpsなら確認のうえ開く()
    {
        var dialogs = new RecordingDialogService(confirmResult: true);
        var launcher = new RecordingExternalLinkLauncher();

        var handled = await ExternalLinkConfirmation.TryConfirmAndOpenHttpsAsync(
            dialogs, launcher, "https://github.com/Yu5rin/Graft/releases/tag/v1.0.0", "リリースページを開きますか？");

        handled.Should().BeTrue();
        dialogs.LastMessage.Should().Contain("https://github.com/Yu5rin/Graft/releases/tag/v1.0.0",
            "利用者が開く前に実際のURLを確認できること");
        launcher.OpenedUrl.Should().Be("https://github.com/Yu5rin/Graft/releases/tag/v1.0.0");
    }

    [Fact(DisplayName = "確認ダイアログでキャンセルした場合は開かない")]
    public async Task キャンセルすれば開かない()
    {
        var dialogs = new RecordingDialogService(confirmResult: false);
        var launcher = new RecordingExternalLinkLauncher();

        var handled = await ExternalLinkConfirmation.TryConfirmAndOpenHttpsAsync(
            dialogs, launcher, "https://github.com/Yu5rin/Graft/releases/tag/v1.0.0", "リリースページを開きますか？");

        // ダイアログ自体は表示できた（URLは正当）ので戻り値はtrueだが、実際には開かない。
        handled.Should().BeTrue();
        launcher.OpenedUrl.Should().BeNull();
    }

    [Theory(DisplayName = "https以外のスキームは確認ダイアログすら出さず拒否する")]
    [InlineData("http://github.com/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-appx-web:evil")]
    public async Task https以外は拒否する(string url)
    {
        var dialogs = new RecordingDialogService(confirmResult: true);
        var launcher = new RecordingExternalLinkLauncher();

        var handled = await ExternalLinkConfirmation.TryConfirmAndOpenHttpsAsync(dialogs, launcher, url, "タイトル");

        handled.Should().BeFalse();
        dialogs.ConfirmCallCount.Should().Be(0, "信頼できないスキームでは確認ダイアログすら出さない");
        launcher.OpenedUrl.Should().BeNull();
    }

    [Theory(DisplayName = "絶対URIとして解釈できない・空・nullの場合も拒否する")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/path")]
    [InlineData("not a url")]
    public async Task 不正なURLは拒否する(string? url)
    {
        var dialogs = new RecordingDialogService(confirmResult: true);
        var launcher = new RecordingExternalLinkLauncher();

        var handled = await ExternalLinkConfirmation.TryConfirmAndOpenHttpsAsync(dialogs, launcher, url, "タイトル");

        handled.Should().BeFalse();
        launcher.OpenedUrl.Should().BeNull();
    }

    private sealed class RecordingExternalLinkLauncher : IExternalLinkLauncher
    {
        public bool IsSupported => true;
        public string? UnsupportedReason => null;
        public string? OpenedUrl { get; private set; }
        public void Open(string url) => OpenedUrl = url;
    }

    /// <summary>
    /// ConfirmAsyncだけを記録・制御し、それ以外は呼ばれない前提でNotSupportedExceptionにする
    /// 最小限のIDialogServiceフェイク（ExternalLinkConfirmationはConfirmAsyncしか呼ばないため）。
    /// </summary>
    private sealed class RecordingDialogService : IDialogService
    {
        private readonly bool _confirmResult;

        public RecordingDialogService(bool confirmResult) => _confirmResult = confirmResult;

        public int ConfirmCallCount { get; private set; }
        public string? LastTitle { get; private set; }
        public string? LastMessage { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message)
        {
            ConfirmCallCount++;
            LastTitle = title;
            LastMessage = message;
            return Task.FromResult(_confirmResult);
        }

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => throw new NotSupportedException();
        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => throw new NotSupportedException();
        public Task<string?> PickFolderAsync(string title) => throw new NotSupportedException();
        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null)
            => throw new NotSupportedException();
        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => throw new NotSupportedException();
        public Task ShowMessageAsync(string title, string message) => throw new NotSupportedException();
    }
}
