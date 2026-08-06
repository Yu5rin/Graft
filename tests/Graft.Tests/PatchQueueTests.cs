using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 仕様書4.10（分割パッチの受け取り・パッチキュー）の単体テスト。追加・削除・全削除、
/// 同一ファイル重複時のE007警告、Mergeの出現順維持、保存と復元の往復を検証する。
/// </summary>
public class PatchQueueTests
{
    private static Patch Parse(string patchText) => new PatchParser().Parse(patchText).Value;

    private const string PatchA = """
        <<<< FILE: a.py
        <<<<<<< SEARCH
        value_a = 1
        =======
        value_a = 2
        >>>>>>> REPLACE
        """;

    private const string PatchB = """
        <<<< FILE: b.py
        <<<<<<< SEARCH
        value_b = 1
        =======
        value_b = 2
        >>>>>>> REPLACE
        """;

    private const string PatchADuplicate = """
        <<<< FILE: a.py
        <<<<<<< SEARCH
        value_a = 2
        =======
        value_a = 3
        >>>>>>> REPLACE
        """;

    [Fact(DisplayName = "Addでキューにブロックが追加される")]
    public void Addでブロックが追加される()
    {
        using var ws = new TempWorkspace();
        var queue = new PatchQueue(new AppPaths(ws.CreateDirectory("app")));

        var result = queue.Add(Parse(PatchA));

        result.IsSuccess.Should().BeTrue();
        queue.Items.Should().ContainSingle();
        queue.Items[0].Block.Path.Should().Be("a.py");
    }

    [Fact(DisplayName = "Removeで指定IDのブロックのみが削除される")]
    public void Removeで指定ブロックのみ削除される()
    {
        using var ws = new TempWorkspace();
        var queue = new PatchQueue(new AppPaths(ws.CreateDirectory("app")));
        queue.Add(Parse(PatchA));
        queue.Add(Parse(PatchB));
        var idToRemove = queue.Items[0].Id;

        queue.Remove(idToRemove);

        queue.Items.Should().ContainSingle();
        queue.Items[0].Block.Path.Should().Be("b.py");
    }

    [Fact(DisplayName = "Clearでキューが空になる")]
    public void Clearでキューが空になる()
    {
        using var ws = new TempWorkspace();
        var queue = new PatchQueue(new AppPaths(ws.CreateDirectory("app")));
        queue.Add(Parse(PatchA));
        queue.Add(Parse(PatchB));

        queue.Clear();

        queue.Items.Should().BeEmpty();
    }

    [Fact(DisplayName = "同一ファイルに対する重複ブロックの追加はE007警告になるが追加自体は行われる")]
    public void 重複ブロックはE007警告になる()
    {
        using var ws = new TempWorkspace();
        var queue = new PatchQueue(new AppPaths(ws.CreateDirectory("app")));
        queue.Add(Parse(PatchA));

        var result = queue.Add(Parse(PatchADuplicate));

        result.IsSuccess.Should().BeTrue("重複は警告であり追加自体は妨げられないはず");
        result.Issues.Should().Contain(i => i.Code == ErrorCode.E007 && i.Severity == Severity.Warning);
        queue.Items.Should().HaveCount(2, "警告が出ても両方ともキューには追加されるはず");
    }

    [Fact(DisplayName = "重複していないブロックの追加では警告が出ない")]
    public void 重複がなければ警告が出ない()
    {
        using var ws = new TempWorkspace();
        var queue = new PatchQueue(new AppPaths(ws.CreateDirectory("app")));
        queue.Add(Parse(PatchA));

        var result = queue.Add(Parse(PatchB));

        result.Issues.Should().BeEmpty();
    }

    [Fact(DisplayName = "Mergeはブロックの出現順（追加順）を保ったまま1つのPatchに結合する")]
    public void Mergeは出現順を保つ()
    {
        using var ws = new TempWorkspace();
        var queue = new PatchQueue(new AppPaths(ws.CreateDirectory("app")));
        queue.Add(Parse(PatchB));
        queue.Add(Parse(PatchA));

        var merged = queue.Merge();

        merged.IsSuccess.Should().BeTrue();
        merged.Value.Blocks.Select(b => b.Path).Should().ContainInOrder("b.py", "a.py");
    }

    [Fact(DisplayName = "空のキューでMergeするとE001になる")]
    public void 空のキューでのMergeはE001になる()
    {
        using var ws = new TempWorkspace();
        var queue = new PatchQueue(new AppPaths(ws.CreateDirectory("app")));

        var merged = queue.Merge();

        merged.IsSuccess.Should().BeFalse();
        merged.Errors.Should().Contain(i => i.Code == ErrorCode.E001);
    }

    [Fact(DisplayName = "SaveAsync・LoadAsyncで内容が往復する")]
    public async Task 保存と復元が往復する()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var queue = new PatchQueue(appPaths);
        queue.Add(Parse(PatchA));
        queue.Add(Parse(PatchB));

        var saved = await queue.SaveAsync();
        saved.IsSuccess.Should().BeTrue();

        var restoredQueue = new PatchQueue(appPaths);
        var loaded = await restoredQueue.LoadAsync();

        loaded.IsSuccess.Should().BeTrue();
        restoredQueue.Items.Select(i => i.Block.Path).Should().ContainInOrder("a.py", "b.py");
        var mergedAfterReload = restoredQueue.Merge();
        mergedAfterReload.IsSuccess.Should().BeTrue();
        mergedAfterReload.Value.Blocks.Should().HaveCount(2);
    }

    [Fact(DisplayName = "queue.jsonが存在しない状態でのLoadAsyncは空のキューとして成功する")]
    public async Task ファイル未存在時のLoadAsyncは空で成功する()
    {
        using var ws = new TempWorkspace();
        var queue = new PatchQueue(new AppPaths(ws.CreateDirectory("app")));

        var loaded = await queue.LoadAsync();

        loaded.IsSuccess.Should().BeTrue();
        queue.Items.Should().BeEmpty();
    }
}
