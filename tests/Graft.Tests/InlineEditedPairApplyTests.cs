using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 差分画面のインライン編集で書き換えたSEARCH部を、メモリ上の現在のパッチへ差し替えて適用する経路
/// （<c>MainViewModel.AdoptInlineEditAsync</c> がやること）の、Core側の保証を検証する。
/// 差し替えは <c>Patch</c>/<c>SearchReplaceBlock</c> の <c>with</c> 式だけで行うため、
/// ここでは同じ手順（ペアを差し替えた新しいパッチ）をテスト側で再現し、
/// 通常のドライラン・本適用・履歴の記録がそのまま働くことを確かめる。
/// </summary>
public class InlineEditedPairApplyTests
{
    private const string FailingPatch =
        "<<<< FILE: sample.txt\n" +
        "<<<<<<< SEARCH  # 2行目を直す\n" +
        "two (AIが思い込んだ内容)\n" +
        "=======\n" +
        "TWO\n" +
        ">>>>>>> REPLACE\n";

    private static (Patch Patch, SearchReplacePair Edited) ReplaceSearch(Patch patch, string newSearch)
    {
        var block = (SearchReplaceBlock)patch.Blocks[0];
        var original = block.Pairs[0];
        // InlineEditViewModel.BuildEditedPair と同じ作り（SEARCH部だけ差し替え、印を立てる）。
        var edited = original with { SearchText = newSearch, IsSearchEdited = true };
        var newBlock = block with { Pairs = new[] { edited } };
        return (patch with { Blocks = new PatchBlock[] { newBlock } }, edited);
    }

    [Fact(DisplayName = "SEARCH差し替え: 元のSEARCHは失敗するが、書き換えたSEARCHに差し替えると適用可能になり、REPLACEは変わらない")]
    public async Task 差し替えると適用可能になる()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("sample.txt", "one\ntwo\nthree\n");
        var ctx = harness.MakeContext(1);

        var original = ApplyHarness.Parse(FailingPatch);
        var before = (await harness.Engine.DryRunAsync(original, ctx)).Value;
        before.Plans.Should().ContainSingle().Which.CanApply.Should().BeFalse("元のSEARCHは現在のファイルと一致しない");

        var (fixedPatch, edited) = ReplaceSearch(original, "two");
        var after = (await harness.Engine.DryRunAsync(fixedPatch, ctx)).Value;

        var plan = after.Plans.Should().ContainSingle().Subject;
        plan.CanApply.Should().BeTrue("書き換えたSEARCHは一致するので適用可能になるはず");
        plan.IsSelected.Should().BeTrue();
        plan.Pair.Should().BeSameAs(edited);
        plan.Pair!.ReplaceText.Should().Be("TWO", "REPLACE部は差し替えの前後で変わってはならない");
        plan.Pair.Description.Should().Be("2行目を直す", "説明など、SEARCH部以外も元のまま引き継ぐ");
        plan.AfterText.Should().Be("one\nTWO\nthree");
    }

    [Fact(DisplayName = "SEARCH差し替え: 差し替えたパッチを本適用でき、履歴のエントリに書き換えた件数が残る")]
    public async Task 履歴に差し替えが残る()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("sample.txt", "one\ntwo\nthree\n");
        var ctx = harness.MakeContext(1);

        var (fixedPatch, _) = ReplaceSearch(ApplyHarness.Parse(FailingPatch), "two");
        var dryRun = (await harness.Engine.DryRunAsync(fixedPatch, ctx)).Value;
        var apply = await harness.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeTrue();
        Encoding.UTF8.GetString(harness.ReadProjectBytes("sample.txt")).Should().Be("one\nTWO\nthree\n");
        apply.Value.Entries.Should().ContainSingle().Which.InlineEditedPairs.Should().Be(1,
            "AIの出力と違うSEARCHが適用に含まれたことを、後から履歴で気づけるようにする");

        var json = JsonSerializer.Serialize(apply.Value.Entries[0], JsonFileStore.DefaultOptions);
        json.Should().Contain("\"inlineEditedPairs\": 1");
    }

    [Fact(DisplayName = "SEARCH差し替え: 書き換えていない通常のパッチは、履歴に件数を出さない（JSONの形が変わらない）")]
    public async Task 通常の適用は件数を出さない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("sample.txt", "one\ntwo\nthree\n");
        var ctx = harness.MakeContext(1);

        var patch = ApplyHarness.Parse(FailingPatch.Replace("two (AIが思い込んだ内容)", "two"));
        var dryRun = (await harness.Engine.DryRunAsync(patch, ctx)).Value;
        var apply = await harness.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeTrue();
        apply.Value.Entries.Single().InlineEditedPairs.Should().Be(0);
        JsonSerializer.Serialize(apply.Value.Entries[0], JsonFileStore.DefaultOptions)
            .Should().NotContain("inlineEditedPairs", "既存のmanifest.jsonの形を変えない");
    }

    [Fact(DisplayName = "SEARCH差し替え: 差し替え後も通常の安全検査を通る（インデント差は相対インデント補正で吸収され、REPLACEの内容は変わらない）")]
    public async Task 差し替えても安全検査を迂回しない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("sample.cs", "class A\n{\n    void F()\n    {\n        Run();\n    }\n}\n");
        var ctx = harness.MakeContext(1);

        var original = ApplyHarness.Parse(
            "<<<< FILE: sample.cs\n<<<<<<< SEARCH\nvoid F() { Run(); }\n=======\nvoid F()\n{\n    Run2();\n}\n>>>>>>> REPLACE\n");
        // 利用者が実ファイルを見ながら、インデントがずれた状態で書き換える（先頭インデントだけ違う）。
        var (fixedPatch, _) = ReplaceSearch(original, "void F()\n{\n    Run();\n}");
        var dryRun = (await harness.Engine.DryRunAsync(fixedPatch, ctx)).Value;

        var plan = dryRun.Plans.Should().ContainSingle().Subject;
        plan.CanApply.Should().BeTrue();
        plan.Stage.Should().Be(MatchStage.RelativeIndent, "同じMatchEngineの段階判定を通っている");
        plan.AfterText.Should().Contain("    void F()\n    {\n        Run2();\n    }",
            "REPLACE部はインデント補正を受けるだけで、内容は1文字も変わらない（E217の不変条件）");
    }
}
