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

    [Fact(DisplayName = "「全期間」を選ぶと開始日・終了日ともに指定なしになる")]
    public void 全期間プリセットは絞り込みを解除する()
    {
        var vm = CreateViewModel(FixedNow);
        vm.DateFromText = "2026-01-15";

        vm.DatePreset = HistoryDatePreset.All;

        vm.DateFromText.Should().BeEmpty();
        vm.DateToText.Should().BeEmpty();
        vm.DateFrom.Should().BeNull();
        vm.DateTo.Should().BeNull();
    }

    [Fact(DisplayName = "「今日」を選ぶと開始日が当日0時になる")]
    public void 今日プリセットは当日0時から()
    {
        var vm = CreateViewModel(FixedNow);

        vm.DatePreset = HistoryDatePreset.Today;

        vm.DateFromText.Should().Be("2026-08-08");
        vm.DateToText.Should().BeEmpty("終了日は未指定＝現在までとする");
    }

    [Fact(DisplayName = "「過去7日」を選ぶと当日を含む7日前（6日前0時）になる")]
    public void 過去7日プリセットは当日を含む7日間()
    {
        var vm = CreateViewModel(FixedNow);

        vm.DatePreset = HistoryDatePreset.Last7Days;

        vm.DateFromText.Should().Be("2026-08-02");
        vm.DateToText.Should().BeEmpty();
    }

    [Fact(DisplayName = "「過去30日」を選ぶと当日を含む30日前（29日前0時）になる")]
    public void 過去30日プリセットは当日を含む30日間()
    {
        var vm = CreateViewModel(FixedNow);

        vm.DatePreset = HistoryDatePreset.Last30Days;

        vm.DateFromText.Should().Be("2026-07-10");
        vm.DateToText.Should().BeEmpty();
    }

    [Fact(DisplayName = "プリセットは日付の途中の時刻ではなく日境界（0時）で切り出す")]
    public void プリセットは時刻に関わらず日境界で計算する()
    {
        // 当日の23:59寄りの時刻でも、「今日」の起点は変わらず当日0時であることを確認する。
        var lateNow = new DateTimeOffset(2026, 8, 8, 23, 59, 0, TimeSpan.FromHours(9));
        var vm = CreateViewModel(() => lateNow);

        vm.DatePreset = HistoryDatePreset.Today;

        vm.DateFromText.Should().Be("2026-08-08");
    }

    [Fact(DisplayName = "プリセット選択後に手入力すると表示が「指定期間」に切り替わる")]
    public void 手入力するとプリセット表示が指定期間になる()
    {
        var vm = CreateViewModel(FixedNow);
        vm.DatePreset = HistoryDatePreset.Last7Days;

        vm.DateFromText = "2026-01-01";

        vm.DatePreset.Should().Be(HistoryDatePreset.Custom);
    }

    [Fact(DisplayName = "手入力を両方消すとプリセット表示が「全期間」に戻る")]
    public void 手入力を消すとプリセット表示が全期間に戻る()
    {
        var vm = CreateViewModel(FixedNow);
        vm.DateFromText = "2026-01-01";
        vm.DatePreset.Should().Be(HistoryDatePreset.Custom);

        vm.DateFromText = string.Empty;

        vm.DatePreset.Should().Be(HistoryDatePreset.All);
    }

    [Fact(DisplayName = "プリセットで入力欄を更新しても表示は「指定期間」に切り替わらない")]
    public void プリセット適用中は指定期間にならない()
    {
        var vm = CreateViewModel(FixedNow);

        vm.DatePreset = HistoryDatePreset.Today;

        vm.DatePreset.Should().Be(HistoryDatePreset.Today);
    }

    [Fact(DisplayName = "プリセットを続けて選ぶと、PropertyChanged通知の時点でDateFromTextが新しい値になっている")]
    public void プリセット切り替え直後の通知で最新の値が読める()
    {
        // 実機確認で見つかった不具合の再現テスト。DateFromTextのsetterがDateFromの更新前に
        // 自身のPropertyChangedを発火すると、バインド先（TextBox）がgetterを読み直した際に
        // まだ古いDateFromに基づく値を受け取ってしまい、画面上テキストが更新されなかった。
        var vm = CreateViewModel(FixedNow);
        vm.DatePreset = HistoryDatePreset.Last7Days; // 先に別のプリセットを適用しておく

        string? observedDuringNotification = null;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HistoryPaneViewModel.DateFromText))
            {
                observedDuringNotification = vm.DateFromText;
            }
        };

        vm.DatePreset = HistoryDatePreset.Today;

        observedDuringNotification.Should().Be("2026-08-08",
            "PropertyChanged発火時点でgetterがすでに新しい値を返している必要がある");
    }

    private static readonly Func<DateTimeOffset> FixedNow =
        () => new DateTimeOffset(2026, 8, 8, 10, 30, 0, TimeSpan.FromHours(9));

    private static HistoryPaneViewModel CreateViewModel(Func<DateTimeOffset>? now = null)
        => new(new Graft.Core.RevisionStore(new Graft.Infra.AppPaths(Path.GetTempPath())),
            new Graft.Core.RevisionRestorer(new Graft.Infra.AppPaths(Path.GetTempPath())),
            new Graft.Features.ProjectStore(new Graft.Infra.AppPaths(Path.GetTempPath())),
            new NullDialogService(),
            now);
}
