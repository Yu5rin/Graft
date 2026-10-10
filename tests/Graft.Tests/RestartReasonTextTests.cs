using FluentAssertions;
using Graft.Infra;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 再起動要求のログ文言（<see cref="RestartReasonText"/>）のテスト。以前は常に
/// 「データ保存先移行完了ダイアログの「再起動」ボタン」と記録していたため、実機では自動更新の
/// 後の再起動でもこの文言が残り、ログを読んだ人が調査を誤った。
/// </summary>
public class RestartReasonTextTests
{
    [Fact(DisplayName = "データ保存先の移行による再起動は、その旨がログ文言に出る")]
    public void データ保存先の移行の文言()
    {
        var message = RestartReasonText.BuildLogMessage(RestartReason.DataDirectoryMigration);

        message.Should().Contain("データ保存先移行");
        message.Should().NotContain("自動更新");
    }

    [Fact(DisplayName = "自動更新のインストール後の再起動は、データ保存先移行の文言にならない")]
    public void 自動更新の文言()
    {
        var message = RestartReasonText.BuildLogMessage(RestartReason.UpdateInstalled);

        message.Should().Contain("自動更新");
        message.Should().NotContain("データ保存先", "実機で調査を誤らせた固定文言が残ってはならない");
    }

    [Fact(DisplayName = "確認なしの自動更新の完了通知からの再起動は、ダイアログ経由の更新とも移行とも区別できる文言になる")]
    public void 完了通知からの再起動の文言()
    {
        var message = RestartReasonText.BuildLogMessage(RestartReason.AutoUpdateNotice);

        message.Should().Contain("自動更新").And.Contain("完了通知");
        message.Should().NotContain("データ保存先");
        message.Should().NotBe(RestartReasonText.BuildLogMessage(RestartReason.UpdateInstalled));
    }

    [Fact(DisplayName = "未知の理由は別の理由にすり替えず、値そのものを出す")]
    public void 未知の理由は値を出す()
    {
        RestartReasonText.BuildLogMessage((RestartReason)999).Should().Contain("999");
    }

    [Fact(DisplayName = "すべての理由に、互いに異なる説明がある")]
    public void すべての理由に別々の説明がある()
    {
        var all = Enum.GetValues<RestartReason>();

        all.Select(RestartReasonText.Describe).Should().OnlyHaveUniqueItems();
        all.Select(RestartReasonText.Describe).Should().NotContain(s => s.StartsWith("不明"));
    }
}
