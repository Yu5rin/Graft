using FluentAssertions;
using Graft.Features;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 「gitの変更ファイルだけ」の選択状態の組み立て（<see cref="ChangedFileSelection"/>）の単体テスト。
/// 走査結果（<see cref="ContextFileNode"/>）を直接組み立てて渡し、gitやファイルシステムは使わない。
/// </summary>
public class ChangedFileSelectionTests
{
    private static ContextFileNode File(string path, bool excluded = false, string? reason = null)
        => new() { RelativePath = path, IsExcluded = excluded, ExcludeReason = reason };

    private static ContextFileNode Dir(string path, bool excluded = false, string? reason = null)
        => new() { RelativePath = path, IsDirectory = true, IsExcluded = excluded, ExcludeReason = reason };

    private static readonly IReadOnlyList<ContextFileNode> Scan = new[]
    {
        Dir("src"),
        File("src/a.cs"),
        File("src/b.cs"),
        File("readme.md"),
        File("docs/変更履歴.md"),
        Dir("dist", excluded: true, reason: "既定の除外"),
        Dir("lib"),
        File("lib/huge.bin", excluded: true, reason: "バイナリ"),
        File("lib/x.cs"),
    };

    [Fact(DisplayName = "変更ファイルだけが「内容も出す」になり、それ以外のファイルはすべて「構成だけ」になる")]
    public void 変更ファイルだけがFullになる()
    {
        var selection = ChangedFileSelection.Build(Scan, new[] { "src/a.cs", "readme.md" });

        selection.FullPaths.Should().BeEquivalentTo(new[] { "src/a.cs", "readme.md" });
        selection.States["src/a.cs"].Should().Be(ContextFileState.Full);
        selection.States["readme.md"].Should().Be(ContextFileState.Full);
        selection.States["src/b.cs"].Should().Be(ContextFileState.StructureOnly);
        selection.States["docs/変更履歴.md"].Should().Be(ContextFileState.StructureOnly);
        selection.States["lib/x.cs"].Should().Be(ContextFileState.StructureOnly);
        selection.StructureOnlyCount.Should().Be(3);
        selection.Excluded.Should().BeEmpty();
    }

    [Fact(DisplayName = "状態を持つのは選べるファイルだけ（フォルダ・除外されたファイルは含まない）")]
    public void 状態はフォルダと除外ファイルを含まない()
    {
        var selection = ChangedFileSelection.Build(Scan, new[] { "src/a.cs" });

        selection.States.Keys.Should().BeEquivalentTo(
            new[] { "src/a.cs", "src/b.cs", "readme.md", "docs/変更履歴.md", "lib/x.cs" });
    }

    [Fact(DisplayName = "除外されていて選べない変更ファイルは、理由つきで報告され、Fullにはならない")]
    public void 除外された変更ファイルは理由つきで報告される()
    {
        var selection = ChangedFileSelection.Build(Scan, new[] { "src/a.cs", "lib/huge.bin" });

        selection.FullPaths.Should().Equal("src/a.cs");
        selection.Excluded.Should().ContainSingle();
        selection.Excluded[0].Requested.Should().Be("lib/huge.bin");
        selection.Excluded[0].ExcludeReason.Should().Be("バイナリ");
        selection.States.Should().NotContainKey("lib/huge.bin");
    }

    [Fact(DisplayName = "除外フォルダの配下にある変更ファイルも「除外されている」として報告される（見つからない扱いにしない）")]
    public void 除外フォルダ配下の変更も報告される()
    {
        var selection = ChangedFileSelection.Build(Scan, new[] { "dist/bundle.js", "dist/sub/map.js", "src/a.cs" });

        selection.FullPaths.Should().Equal("src/a.cs");
        selection.Excluded.Select(e => e.Requested).Should().Equal("dist/bundle.js", "dist/sub/map.js");
        selection.Excluded.Should().OnlyContain(e => e.ExcludeReason == "既定の除外");
    }

    [Fact(DisplayName = "走査結果にないパス（走査後に消えたもの等）とフォルダは、その他として数え、選択には影響しない")]
    public void 走査にないパスとフォルダはその他()
    {
        var selection = ChangedFileSelection.Build(Scan, new[] { "ghost.cs", "src", "src/a.cs" });

        selection.FullPaths.Should().Equal("src/a.cs");
        selection.OtherCount.Should().Be(2);
    }

    [Fact(DisplayName = "同じ変更ファイルが重複して渡されても1件にまとめる")]
    public void 重複は1件にまとめる()
    {
        var selection = ChangedFileSelection.Build(Scan, new[] { "src/a.cs", "src/a.cs" });

        selection.FullPaths.Should().Equal("src/a.cs");
    }

    [Fact(DisplayName = "変更ファイルが0件なら、選べるファイルはすべて「構成だけ」になる想定の状態表が返り、Fullは空")]
    public void 変更0件ならFullは空()
    {
        var selection = ChangedFileSelection.Build(Scan, Array.Empty<string>());

        selection.FullPaths.Should().BeEmpty();
        selection.States.Values.Should().OnlyContain(s => s == ContextFileState.StructureOnly);
    }

    [Fact(DisplayName = "サブフォルダのルートで取った変更（接頭辞を外した相対パス）が、走査結果とそのまま一致する")]
    public void サブフォルダのルートの変更が走査結果と一致する()
    {
        // git が返すのはリポジトリルート相対の "app/src/a.cs"。接頭辞を外した結果を渡す。
        var (paths, _) = GitChangedFilesParser.Parse(" M app/src/a.cs\0 M other/z.cs\0", "app/");

        var selection = ChangedFileSelection.Build(Scan, paths);

        selection.FullPaths.Should().Equal("src/a.cs");
        selection.OtherCount.Should().Be(0, "リポジトリの別の場所の変更はそもそも渡されない");
    }

    [Fact(DisplayName = "大文字小文字だけが違う表記でも、1件に定まるなら走査結果のパスで一致する")]
    public void 大文字小文字違いでも一致する()
    {
        var selection = ChangedFileSelection.Build(Scan, new[] { "README.md" });

        selection.FullPaths.Should().Equal("readme.md");
    }
}
