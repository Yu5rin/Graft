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
}
