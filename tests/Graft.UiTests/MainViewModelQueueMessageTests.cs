using FluentAssertions;
using Graft.ViewModels;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 仕様書4.10「分割パッチの受け取り」における切断通知の文言（
/// <see cref="MainViewModel.BuildTruncatedPatchMessage"/>）の単体テスト。
/// 解析できたブロックが0件のとき「0件をキューへ追加し」という不自然な表現に
/// ならないことを確認する（実機で発見された不具合）。
/// </summary>
public class MainViewModelQueueMessageTests
{
    [Fact(DisplayName = "解析できたブロックが0件のときは「追加した」という表現を使わない")]
    public void 切断通知文言は0件のとき無かった旨になる()
    {
        var message = MainViewModel.BuildTruncatedPatchMessage(addedCount: 0, duplicateCount: 0);

        message.Should().NotContain("0件をキューへ追加し", "何も追加していないのに「追加した」という表現は不自然");
        message.Should().Contain("解析できたブロックは無かった");
        message.Should().Contain("続きを依頼するプロンプトをクリップボードへコピーしました");
    }

    [Fact(DisplayName = "解析できたブロックが1件以上のときは従来どおり件数を示す")]
    public void 切断通知文言は1件以上のとき件数を示す()
    {
        var message = MainViewModel.BuildTruncatedPatchMessage(addedCount: 2, duplicateCount: 0);

        message.Should().Contain("解析できた2件をキューへ追加し");
    }

    [Fact(DisplayName = "重複件数があるときは0件のときも重複の案内が付く")]
    public void 切断通知文言は重複があると案内が付く()
    {
        var message = MainViewModel.BuildTruncatedPatchMessage(addedCount: 0, duplicateCount: 1);

        message.Should().Contain("同一ファイルへの重複ブロックが1件あります");
    }
}
