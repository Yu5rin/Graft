using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 一部適用の後に残りの失敗ブロックを直して適用し直すパッチ（<see cref="Patch.PatchHashSource"/>）の
/// 二重適用検知（E302）の扱いを検証する。残りのパッチは直前のリビジョンと同じパッチではないので
/// E302にしない。一方、元のAI出力をそのまま貼り直したときは従来どおりE302になる。
/// </summary>
public class RemainderPatchIdentityTests
{
    private const string OriginalPatch =
        "<<<< FILE: ok.txt\n<<<<<<< SEARCH\nalpha\n=======\nALPHA\n>>>>>>> REPLACE\n" +
        "<<<< FILE: bad.txt\n<<<<<<< SEARCH  # 直す\ntwo (思い込み)\n=======\nTWO\n>>>>>>> REPLACE\n";

    private static Patch Remainder(Patch original, string newSearch)
    {
        var bad = (SearchReplaceBlock)original.Blocks[1];
        var edited = bad.Pairs[0] with { SearchText = newSearch, IsSearchEdited = true };
        var blocks = new PatchBlock[] { bad with { Pairs = new[] { edited } } };
        return original with { Blocks = blocks, PatchHashSource = PatchIdentity.ForRemainder(original, blocks) };
    }

    [Fact(DisplayName = "残りのパッチ: 一部適用の直後でも、残りの修正パッチはE302にならず、元の出力の貼り直しはE302になる")]
    public async Task 残りはE302にならず元の出力はE302になる()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("ok.txt", "alpha\nbeta\n");
        harness.WriteProjectText("bad.txt", "one\ntwo\nthree\n");

        // 1回目: 元の出力を適用（ok.txt だけ成功し、r1 が記録される）。
        var original = ApplyHarness.Parse(OriginalPatch);
        var ctx1 = harness.MakeContext(1);
        var first = (await harness.Engine.DryRunAsync(original, ctx1)).Value;
        first.Plans.Single(p => p.Path == "bad.txt").CanApply.Should().BeFalse();
        (await harness.ApplyAsync(first, ctx1)).IsSuccess.Should().BeTrue();

        // 残りのパッチ（bad.txt のSEARCHを直したもの）: r1 と同じパッチ扱いにならない。
        var remainder = Remainder(original, "two");
        var ctx2 = harness.MakeContext(2);
        var second = (await harness.Engine.DryRunAsync(remainder, ctx2)).Value;
        second.AlreadyAppliedRevision.Should().BeNull("残りのパッチは r1 と同じパッチではない");
        second.PatchHash.Should().NotBe(first.PatchHash);
        second.Plans.Single().CanApply.Should().BeTrue();

        var applied = await harness.ApplyAsync(second, ctx2);
        applied.IsSuccess.Should().BeTrue("適用時の再判定（ApplyEngine）もE302で止めない");
        applied.Value.PatchHash.Should().Be(second.PatchHash);

        // 元の出力をそのまま貼り直すと、従来どおり r1 で適用済み（E302）。
        var again = (await harness.Engine.DryRunAsync(ApplyHarness.Parse(OriginalPatch), harness.MakeContext(3))).Value;
        again.AlreadyAppliedRevision.Should().Be(1);

        // 同じ修正をもう一度適用しようとすると、r2 で適用済み（E302）。二重適用の検知は壊れていない。
        var sameFix = (await harness.Engine.DryRunAsync(Remainder(original, "two"), harness.MakeContext(3))).Value;
        sameFix.AlreadyAppliedRevision.Should().Be(2);
    }

    [Fact(DisplayName = "残りのパッチ: 修正の中身が違えば別のパッチとして扱われ、同じなら同じ同一性になる")]
    public void 同一性は残りの中身で決まる()
    {
        var original = ApplyHarness.Parse(OriginalPatch);

        Remainder(original, "two").PatchHashSource.Should().Be(Remainder(original, "two").PatchHashSource);
        Remainder(original, "two").PatchHashSource.Should().NotBe(Remainder(original, "three").PatchHashSource);
        Remainder(original, "two").PatchHashSource.Should().NotBe(original.RawText);
        original.PatchHashSource.Should().BeNull("通常のパッチはRawTextのハッシュのまま");
        Remainder(original, "two").RawText.Should().Be(original.RawText, "RawTextは元のAI出力のまま");
    }
}
