using FluentAssertions;
using Graft.Core.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// プロジェクト一覧へのブックのドラッグ＆ドロップ（1.3.1）で、落とされたファイルを VBA のプロジェクトにできるブックとして
/// 扱うかの判定。拡張子の候補は「Excel ブックから追加」の一覧と同じ（<see cref="VbaRotNames.IsWorkbookDisplayName"/>）で、
/// マクロを保存できない .xlsx だけを分ける。
/// </summary>
public class VbaDropWorkbookNamesTests
{
    [Theory(DisplayName = "IsVbaWorkbookFile: .xlsm／.xlsb／.xlam／.xls（大文字小文字は問わない）は VBA のプロジェクトにできるブック")]
    [InlineData(@"C:\Work\計画.xlsm")]
    [InlineData(@"C:\Work\計画.XLSB")]
    [InlineData("/home/u/addin.xlam")]
    [InlineData(@"C:\Work\旧.xls")]
    public void 対象のブック(string path)
    {
        VbaRotNames.IsVbaWorkbookFile(path).Should().BeTrue();
        VbaRotNames.IsNoMacroWorkbookFile(path).Should().BeFalse();
    }

    [Theory(DisplayName = "IsNoMacroWorkbookFile: .xlsx はマクロを保存できない。VBA のプロジェクトの対象ではない")]
    [InlineData(@"C:\Work\計画.xlsx")]
    [InlineData("/home/u/a.XLSX")]
    public void マクロ不可のブック(string path)
    {
        VbaRotNames.IsNoMacroWorkbookFile(path).Should().BeTrue();
        VbaRotNames.IsVbaWorkbookFile(path).Should().BeFalse();
    }

    [Theory(DisplayName = "ブックでないファイル（txt・csv・.xltm・.xla・null・空）は、どちらでもない")]
    [InlineData(@"C:\Work\メモ.txt")]
    [InlineData(@"C:\Work\a.csv")]
    [InlineData(@"C:\Work\a.xltm")]
    [InlineData(@"C:\Work\a.xla")]
    [InlineData("")]
    [InlineData(null)]
    public void ブックでない(string? path)
    {
        VbaRotNames.IsVbaWorkbookFile(path).Should().BeFalse();
        VbaRotNames.IsNoMacroWorkbookFile(path).Should().BeFalse();
    }
}
