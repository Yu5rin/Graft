using FluentAssertions;
using Graft.ViewModels;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 仕様書4.10「分割パッチの受け取り」における切断確認ダイアログの文言（
/// <see cref="MainViewModel.BuildTruncatedPatchConfirmMessage"/>）の単体テスト。
/// 解析できたブロックが0件のとき「0件をキューへ追加し」という不自然な表現に
/// ならないこと（実機で発見された不具合）と、コピーにより元のクリップボードの内容が
/// 失われる旨が必ず文言に含まれること（課題2: 確認なしの上書きでパッチが消失した事故対応）
/// を確認する。
/// </summary>
public class MainViewModelQueueMessageTests
{
    [Fact(DisplayName = "解析できたブロックが0件のときは「追加した」という表現を使わない")]
    public void 切断確認文言は0件のとき無かった旨になる()
    {
        var message = MainViewModel.BuildTruncatedPatchConfirmMessage(addedCount: 0, duplicateCount: 0);

        message.Should().NotContain("0件をキューへ追加し", "何も追加していないのに「追加した」という表現は不自然");
        message.Should().Contain("解析できたブロックは無かった");
        message.Should().Contain("続きを依頼するプロンプトをクリップボードへコピーしますか");
    }

    [Fact(DisplayName = "解析できたブロックが1件以上のときは従来どおり件数を示す")]
    public void 切断確認文言は1件以上のとき件数を示す()
    {
        var message = MainViewModel.BuildTruncatedPatchConfirmMessage(addedCount: 2, duplicateCount: 0);

        message.Should().Contain("解析できた2件をキューへ追加しました");
    }

    [Fact(DisplayName = "重複件数があるときは0件のときも重複の案内が付く")]
    public void 切断確認文言は重複があると案内が付く()
    {
        var message = MainViewModel.BuildTruncatedPatchConfirmMessage(addedCount: 0, duplicateCount: 1);

        message.Should().Contain("同一ファイルへの重複ブロックが1件あります");
    }

    [Fact(DisplayName = "0件・1件以上いずれの場合も、コピーで元のクリップボードの内容が失われる旨が伝わる")]
    public void 切断確認文言はクリップボード上書きの注意が両経路で伝わる()
    {
        MainViewModel.BuildTruncatedPatchConfirmMessage(addedCount: 0, duplicateCount: 0)
            .Should().Contain("失われます", "コピーすると元のパッチが失われることが事前に伝わる必要がある");
        MainViewModel.BuildTruncatedPatchConfirmMessage(addedCount: 3, duplicateCount: 0)
            .Should().Contain("失われます");
    }
}
