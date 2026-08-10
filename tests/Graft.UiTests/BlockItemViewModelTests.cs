using FluentAssertions;
using Graft.Core;
using Graft.ViewModels;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// <see cref="BlockItemViewModel"/> のエラー表示（不具合1: 接ぎ木パネルでエラー文が
/// 重なって読めなくなる不具合への対処）の単体テスト。
///
/// 背景: PathGuard.Inspect はサイズ超過（E203）・排他ロック（E204）・読み取り専用（E205）を
/// 同時に検出しうるため、1つの BlockPlan に複数件の GraftIssue が付くことがある
/// （DryRunPlanner.FailPlansForFile 経由）。以前は string.Join(Environment.NewLine, ...) で
/// 1本の文字列へ連結し、GraftPanel.axaml側は1つのTextBlockへそのまま表示していた。
/// 件数の上限が無く、際限なく縦へ伸びうる構造だった。
/// 画面ありのheadlessテスト（ScenarioTests.同一ファイルの複数のSEARCH失敗が個別のブロックとして
/// 保持される）と合わせて、こちらは画面を描画せずに検証できるロジック面（上限件数・
/// 「ほかN件」・各行が独立した要素として取り出せること）を担当する。
/// </summary>
public class BlockItemViewModelTests
{
    private static GraftIssue Issue(ErrorCode code, int? line = null) => GraftIssue.Of(code, line: line);

    private static BlockPlan MakePlan(params GraftIssue[] issues) => new()
    {
        Block = new DeleteBlock { Path = "sample.txt" },
        Path = "sample.txt",
        Operation = EntryOperation.Modify,
        CanApply = false,
        IsSelected = false,
        Issues = issues,
    };

    [Fact(DisplayName = "問題が無ければIssueLinesは空、HasIssueもfalse")]
    public void 問題が無ければ空になる()
    {
        var vm = new BlockItemViewModel(MakePlan());

        vm.HasIssue.Should().BeFalse();
        vm.IssueLines.Should().BeEmpty();
        vm.IssueText.Should().BeNull();
    }

    [Fact(DisplayName = "問題が1件なら、その1件だけがIssueLinesに入る")]
    public void 問題が1件ならそのまま1件になる()
    {
        var vm = new BlockItemViewModel(MakePlan(Issue(ErrorCode.E101, line: 7)));

        vm.HasIssue.Should().BeTrue();
        vm.IssueLines.Should().HaveCount(1);
        vm.IssueLines[0].Should().Contain("E101").And.Contain("7行目");
    }

    /// <summary>
    /// 不具合1の核心: 同一ブロックに2件の問題が付いても、両方が別々の要素として
    /// 取り出せる（1本の文字列に潰れて片方が読めなくなったりしない）こと。
    /// </summary>
    [Fact(DisplayName = "問題が複数件でも、それぞれ独立した要素として保持される")]
    public void 問題が複数件でもそれぞれ独立して保持される()
    {
        var vm = new BlockItemViewModel(MakePlan(
            Issue(ErrorCode.E203),
            Issue(ErrorCode.E204)));

        vm.IssueLines.Should().HaveCount(2, "2件の問題は1件に潰れず、それぞれ別の要素になる必要がある");
        vm.IssueLines[0].Should().Contain("E203");
        vm.IssueLines[1].Should().Contain("E204");
        vm.IssueLines.Should().OnlyHaveUniqueItems("互いを上書きしていれば同じ文字列になってしまう");
    }

    /// <summary>
    /// 件数が上限を超える場合は「ほかN件」に集約し、際限なく縦へ伸びないようにする
    /// （ShellViewModel.StatusBarWarning.cs と同じ流儀）。
    /// </summary>
    [Fact(DisplayName = "問題が上限件数を超えると「ほかN件」で集約される")]
    public void 問題が上限を超えるとほかN件に集約される()
    {
        var issues = new[]
        {
            Issue(ErrorCode.E101, line: 1),
            Issue(ErrorCode.E101, line: 2),
            Issue(ErrorCode.E101, line: 3),
            Issue(ErrorCode.E101, line: 4),
            Issue(ErrorCode.E101, line: 5),
        };
        var vm = new BlockItemViewModel(MakePlan(issues));

        // 上限（3件）+「ほかN件」の1行 = 4要素。上限を超えても表示が無制限に伸びない。
        vm.IssueLines.Should().HaveCount(4);
        vm.IssueLines[0].Should().Contain("1行目");
        vm.IssueLines[1].Should().Contain("2行目");
        vm.IssueLines[2].Should().Contain("3行目");
        vm.IssueLines[^1].Should().Be("ほか2件");
    }

    [Fact(DisplayName = "IssueTextはIssueLinesを改行で連結した文字列になる")]
    public void IssueTextはIssueLinesを連結した文字列になる()
    {
        var vm = new BlockItemViewModel(MakePlan(Issue(ErrorCode.E203), Issue(ErrorCode.E204)));

        vm.IssueText.Should().Be(string.Join("\n", vm.IssueLines));
    }

    // ------------------------------------------------------------------
    // B: 接ぎ木パネルの右クリックメニュー「チェックを付ける／外す」の動的ラベル
    // ------------------------------------------------------------------

    private static BlockPlan MakeApplicablePlan(bool isSelected) => new()
    {
        Block = new DeleteBlock { Path = "sample.txt" },
        Path = "sample.txt",
        Operation = EntryOperation.Modify,
        CanApply = true,
        IsSelected = isSelected,
    };

    [Fact(DisplayName = "未チェックのブロックはToggleLabelが「チェックを付ける (Space)」になる")]
    public void 未チェックはチェックを付けるになる()
    {
        var vm = new BlockItemViewModel(MakeApplicablePlan(isSelected: false));

        vm.ToggleLabel.Should().Be("チェックを付ける (Space)");
    }

    [Fact(DisplayName = "チェック済みのブロックはToggleLabelが「チェックを外す (Space)」になる")]
    public void チェック済みはチェックを外すになる()
    {
        var vm = new BlockItemViewModel(MakeApplicablePlan(isSelected: true));

        vm.ToggleLabel.Should().Be("チェックを外す (Space)");
    }

    [Fact(DisplayName = "IsSelectedを切り替えるとToggleLabelも追従して変化通知が飛ぶ")]
    public void IsSelectedを切り替えるとToggleLabelが追従する()
    {
        var vm = new BlockItemViewModel(MakeApplicablePlan(isSelected: false));
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is not null) changed.Add(e.PropertyName); };

        vm.IsSelected = true;

        vm.ToggleLabel.Should().Be("チェックを外す (Space)");
        changed.Should().Contain(nameof(BlockItemViewModel.ToggleLabel),
            "ToggleLabelのバインディングが更新されるにはPropertyChangedが必要");
    }

    [Fact(DisplayName = "Toggle()は失敗ブロック（CanApply=false）には効かない")]
    public void 失敗ブロックはToggleが効かない()
    {
        var vm = new BlockItemViewModel(MakePlan()); // CanApply=false

        vm.CanToggle.Should().BeFalse();
        vm.Toggle();

        vm.IsSelected.Should().BeFalse("失敗ブロックはトグル操作の対象外のため変化しない");
    }
}
