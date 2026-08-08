using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 仕様書3.4（プロジェクト自動判定）の単体テスト。90%以上=自動選択／50〜90%=要確認／
/// 50%未満=ブロック(E303)の3段階しきい値、新規作成前提パスの分母除外、未接続プロジェクトの
/// 除外を、実際のディレクトリ構成を使って検証する。
/// </summary>
public class ProjectMatcherTests
{
    private static Patch MakePatch(params PatchBlock[] blocks) => new() { Blocks = blocks, RawText = string.Empty };
    private static DeleteBlock Existing(string path) => new() { Path = path };
    private static FullContentBlock NewFile(string path) => new() { Path = path, Content = "content" };
    private static MkdirBlock NewDir(string path) => new() { Path = path };
    private static RenameBlock Rename(string from, string to) => new() { Path = from, ToPath = to };

    private static Project MakeProject(string root, string id = "p_test", bool disconnected = false)
        => new() { Id = id, Name = id, Root = root, IsDisconnected = disconnected };

    [Fact(DisplayName = "一致率90%以上は自動選択（AutoSelected）される")]
    public async Task 一致率90パーセント以上は自動選択される()
    {
        using var ws = new TempWorkspace();
        for (var i = 0; i < 9; i++) ws.WriteText($"f{i}.py", "x");
        var project = MakeProject(ws.RootPath);
        var blocks = Enumerable.Range(0, 9).Select(i => (PatchBlock)Existing($"f{i}.py"))
            .Append(Existing("missing.py")).ToArray(); // 9/10 = 90%
        var patch = MakePatch(blocks);

        var outcome = await new ProjectMatcher().MatchAsync(patch, new[] { project });

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Decision.Should().Be(ProjectMatchDecision.AutoSelected);
        outcome.Value.Best!.Ratio.Should().BeApproximately(0.9, 0.0001);
    }

    [Fact(DisplayName = "一致率50〜90%は候補提示のうえ確認を要する（NeedsConfirmation）")]
    public async Task 一致率50から90パーセントは要確認になる()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "x");
        ws.WriteText("b.py", "x");
        var project = MakeProject(ws.RootPath);
        var patch = MakePatch(Existing("a.py"), Existing("b.py"), Existing("c.py"), Existing("d.py")); // 2/4=50%

        var outcome = await new ProjectMatcher().MatchAsync(patch, new[] { project });

        outcome.Value.Decision.Should().Be(ProjectMatchDecision.NeedsConfirmation);
    }

    [Fact(DisplayName = "一致率50%未満は適用をブロックしE303を返す")]
    public async Task 一致率50パーセント未満はE303でブロックされる()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "x");
        var project = MakeProject(ws.RootPath);
        var patch = MakePatch(Existing("a.py"), Existing("b.py"), Existing("c.py"), Existing("d.py")); // 1/4=25%

        var outcome = await new ProjectMatcher().MatchAsync(patch, new[] { project });

        outcome.Value.Decision.Should().Be(ProjectMatchDecision.Blocked);
        outcome.Issues.Should().Contain(i => i.Code == ErrorCode.E303);
    }

    [Fact(DisplayName = "FULL形式・MKDIRなど新規作成前提のパスは一致率の分母から除かれる")]
    public async Task 新規作成前提のパスは分母から除かれる()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "x");
        ws.WriteText("b.py", "x");
        var project = MakeProject(ws.RootPath);
        var patch = MakePatch(
            Existing("a.py"), Existing("b.py"), NewFile("brandnew.py"), NewDir("newdir"));

        var outcome = await new ProjectMatcher().MatchAsync(patch, new[] { project });

        outcome.Value.Best!.Ratio.Should().Be(1.0, "新規作成前提の2件は分母から除かれ、既存2件が両方一致するため100%になるはず");
    }

    [Fact(DisplayName = "RENAMEは移動元パスのみを一致率の判定対象にする")]
    public async Task RENAMEは移動元のみ判定対象になる()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("old.py", "x");
        var project = MakeProject(ws.RootPath);
        var patch = MakePatch(Rename("old.py", "new.py"));

        var outcome = await new ProjectMatcher().MatchAsync(patch, new[] { project });

        outcome.Value.Best!.Ratio.Should().Be(1.0, "移動先(new.py)が存在しなくても移動元が存在すれば一致するはず");
    }

    [Fact(DisplayName = "未接続のプロジェクトは候補から除かれる")]
    public async Task 未接続プロジェクトは候補から除かれる()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "x");
        var disconnected = MakeProject(ws.RootPath, id: "p_gone", disconnected: true);
        var patch = MakePatch(Existing("a.py"));

        var outcome = await new ProjectMatcher().MatchAsync(patch, new[] { disconnected });

        outcome.Value.Decision.Should().Be(ProjectMatchDecision.Blocked);
        outcome.Issues.Should().Contain(i => i.Code == ErrorCode.E303);
        outcome.Value.Candidates.Should().BeEmpty();
    }

    [Fact(DisplayName = "全ブロックが新規作成前提で分母が0の場合は要確認となり判定不能の警告を返す")]
    public async Task 分母が0の場合は判定不能として要確認になる()
    {
        using var ws = new TempWorkspace();
        var project = MakeProject(ws.RootPath);
        var patch = MakePatch(NewFile("brandnew.py"), NewDir("newdir"));

        var outcome = await new ProjectMatcher().MatchAsync(patch, new[] { project });

        outcome.Value.Decision.Should().Be(ProjectMatchDecision.NeedsConfirmation);
        outcome.Value.Best.Should().BeNull();
        outcome.Issues.Should().Contain(i => i.Code == ErrorCode.E303 && i.Severity == Severity.Warning);
    }

    [Fact(DisplayName = "パス一致判定は大文字小文字を無視する")]
    public async Task パス一致は大文字小文字を無視する()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("Src/Main.py", "x");
        var project = MakeProject(ws.RootPath);
        var patch = MakePatch(Existing("src/main.py"));

        var outcome = await new ProjectMatcher().MatchAsync(patch, new[] { project });

        outcome.Value.Best!.Ratio.Should().Be(1.0);
    }

    // ------------------------------------------------------------------
    // 課題1: 新規ファイルのみのパッチを判定から除外する（既存ファイルとFULL形式の食い違い対応）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "接続済みのどのプロジェクトにも実在しないFULL形式パスは、複数プロジェクトがあっても分母から除かれる")]
    public async Task 新規ファイルのみのパッチは複数プロジェクトでも判定不能として要確認になる()
    {
        using var wsA = new TempWorkspace();
        using var wsB = new TempWorkspace();
        wsA.WriteText("a.py", "x");
        wsB.WriteText("b.py", "x");
        var projectA = MakeProject(wsA.RootPath, id: "p_a");
        var projectB = MakeProject(wsB.RootPath, id: "p_b");
        // どちらのプロジェクトにも存在しない新規ファイルのみのパッチ。
        var patch = MakePatch(NewFile("brandnew.py"), NewDir("newdir"));

        var outcome = await new ProjectMatcher().MatchAsync(patch, new[] { projectA, projectB });

        outcome.Value.Decision.Should().Be(ProjectMatchDecision.NeedsConfirmation,
            "新規作成前提のパスしか無い場合は判定不能として扱われ、無警告で通過するはず");
        outcome.Value.Best.Should().BeNull();
    }

    [Fact(DisplayName = "FULL形式の対象が候補プロジェクトの1つに実在する場合は、新規扱いにせず一致率の判定対象に含める")]
    public async Task FULL形式でも実在するプロジェクトがあれば判定対象に含まれる()
    {
        // ApplyEngine（DryRunPlanner.BuildBlockPlan）はファイルが実在すればFULL形式でも
        // Create ではなく Modify（上書き）として扱う。ProjectMatcher側で無条件にFULL形式を
        // 除外すると「判定では新規扱いなのに適用では既存ファイルを上書きする」食い違いが
        // 起きるため、実在するプロジェクトがあるならその情報を判定に使うべき、という回帰テスト。
        using var wsA = new TempWorkspace();
        using var wsB = new TempWorkspace();
        wsA.WriteText("existing.py", "x"); // Aには既にこのファイルがある（FULLは上書きになる）。
        wsB.WriteText("unrelated.py", "x"); // Bには無関係のファイルしか無い。
        var projectA = MakeProject(wsA.RootPath, id: "p_a");
        var projectB = MakeProject(wsB.RootPath, id: "p_b");
        var patch = MakePatch(NewFile("existing.py"));

        var outcome = await new ProjectMatcher().MatchAsync(patch, new[] { projectA, projectB });

        outcome.Value.Best.Should().NotBeNull("Aに実在するファイルを対象にしているため判定不能にはならない");
        outcome.Value.Best!.Project.Id.Should().Be("p_a");
        outcome.Value.Best!.Ratio.Should().Be(1.0);
    }

    [Fact(DisplayName = "新規ファイル作成と既存ファイル変更が混在する場合、既存ファイル分だけで一致率が計算される")]
    public async Task 新規と既存の混在パッチは既存ファイル分だけで判定される()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "x");
        ws.WriteText("b.py", "x");
        ws.WriteText("c.py", "x");
        var project = MakeProject(ws.RootPath);
        // 既存3件は全て一致、新規ファイル2件（どのプロジェクトにも存在しない）は分母から除外されるべき。
        var patch = MakePatch(
            Existing("a.py"), Existing("b.py"), Existing("c.py"),
            NewFile("brandnew1.py"), NewFile("brandnew2.py"));

        var outcome = await new ProjectMatcher().MatchAsync(patch, new[] { project });

        outcome.Value.Best!.Ratio.Should().Be(1.0, "新規ファイル2件は分母から除かれ、既存3件が全て一致するため100%になるはず");
        outcome.Value.Best!.MatchedPaths.Should().HaveCount(3);
    }
}
