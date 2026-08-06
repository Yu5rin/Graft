using FluentAssertions;
using Graft.Platform.Null;
using Graft.ViewModels;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 履歴の日付絞り込み（仕様書7.2）。画面は使わないがViewModelがUIプロジェクト側にあるため
/// こちらに置く。入力欄の文字列と実際の絞り込み条件の対応を固定する。
/// 日付選択コントロールを使わない理由は HistoryPaneViewModel のコメントを参照。
/// </summary>
public class HistoryDateFilterTests
{
    [Fact(DisplayName = "yyyy-MM-dd を入力すると絞り込みの日付として反映される")]
    public void 正しい書式は日付として反映される()
    {
        var vm = CreateViewModel();

        vm.DateFromText = "2026-01-15";

        vm.DateFrom.Should().NotBeNull();
        vm.DateFrom!.Value.Year.Should().Be(2026);
        vm.DateFrom.Value.Month.Should().Be(1);
        vm.DateFrom.Value.Day.Should().Be(15);
    }

    [Theory(DisplayName = "解釈できない入力は指定なしとして扱う")]
    [InlineData("")]
    [InlineData("2026")]
    [InlineData("2026-1-5")]
    [InlineData("2026/01/15")]
    [InlineData("ではない")]
    public void 解釈できない入力は指定なしになる(string text)
    {
        var vm = CreateViewModel();

        vm.DateToText = text;

        vm.DateTo.Should().BeNull("入力の途中で一覧が消えないよう、解釈できない値は条件なしとする");
    }

    [Fact(DisplayName = "入力を消すと絞り込みが解除される")]
    public void 入力を消すと解除される()
    {
        var vm = CreateViewModel();
        vm.DateFromText = "2026-01-15";
        vm.DateFrom.Should().NotBeNull();

        vm.DateFromText = string.Empty;

        vm.DateFrom.Should().BeNull();
    }

    private static HistoryPaneViewModel CreateViewModel()
        => new(new Graft.Core.RevisionStore(new Graft.Infra.AppPaths(Path.GetTempPath())),
            new Graft.Core.RevisionRestorer(new Graft.Infra.AppPaths(Path.GetTempPath())),
            new NullDialogService());
}
