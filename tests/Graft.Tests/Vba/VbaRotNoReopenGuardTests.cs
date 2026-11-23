using System.Text.RegularExpressions;
using FluentAssertions;
using Graft.Core.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 実行中オブジェクトテーブル（ROT）の古い項目と、閉じたブックの扱い（実機検証 6-34 の 5-b。仕様書_ブックの情報と依頼の観点 16.6・付録A-84）。
/// <para>
/// 実機（2026-11-24）で、<c>Workbook.Close</c> のあと 10 秒たってもブックが ROT に残ることを 5 回とも確かめた。そこで、ROT の項目は
/// 「候補」にすぎず、開いているかは項目の Excel の <c>Workbooks</c> に実在するかで決める（<see cref="VbaRotNames.IsLiveEntry"/>）。
/// 古い項目を扱うとき、<b>閉じたブックを開き直してしまう操作</b>（<c>Marshal.BindToMoniker</c>・<c>IMoniker.BindToObject</c>）が混ざらないことは、
/// COM の無い Linux の CI でもソースを読んで固定する。
/// </para>
/// </summary>
public class VbaRotNoReopenGuardTests
{
    [Fact(DisplayName = "IsLiveEntry: 項目の Excel の Workbooks に同じパスがあれば「開いている」（大文字小文字・区切りの違いは無視）")]
    public void 実在するブックは開いている()
    {
        VbaRotNames.IsLiveEntry(new[] { @"C:\Work\a.xlsm", @"C:\Work\計画.xlsm" }, @"c:\work\.\計画.XLSM").Should().BeTrue();
    }

    [Fact(DisplayName = "IsLiveEntry: Workbooks に無ければ、ROT に残っていても「開いていない」")]
    public void 実在しない古い項目は開いていない()
    {
        VbaRotNames.IsLiveEntry(new[] { @"C:\Work\a.xlsm" }, @"C:\Work\計画.xlsm").Should().BeFalse();
        VbaRotNames.IsLiveEntry(Array.Empty<string>(), @"C:\Work\計画.xlsm").Should().BeFalse("最後のブックを閉じた Excel は、Workbooks が空のまま生きている");
    }

    [Fact(DisplayName = "IsLiveEntry: 項目のオブジェクトが死んでいて辿れない（null）ときも「開いていない」。別のブックと取り違えない")]
    public void 死んだ項目は開いていない()
    {
        VbaRotNames.IsLiveEntry(null, @"C:\Work\計画.xlsm").Should().BeFalse();
        VbaRotNames.IsLiveEntry(new string?[] { null, "" }, @"C:\Work\計画.xlsm").Should().BeFalse();
        VbaRotNames.IsLiveEntry(new[] { @"C:\Work\計画.xlsm.bak" }, @"C:\Work\計画.xlsm").Should().BeFalse("前方一致では同じブックと見なさない");
    }

    [Fact(DisplayName = "ROT を扱うコードに、閉じたブックを開き直す操作（BindToMoniker・BindToObject・Process.Start）が無い")]
    public void ブックを開き直す操作が混ざらない()
    {
        var windows = Path.Combine(FindSrcGraft(), "Platform", "Windows");
        var files = Directory.EnumerateFiles(windows, "*.cs")
            .Where(f => Path.GetFileName(f).StartsWith("WindowsVbaBridge", StringComparison.Ordinal)
                        || Path.GetFileName(f) == "ComRunningObjectTable.cs")
            .ToList();
        files.Should().NotBeEmpty();

        var forbidden = new Regex(@"\b(BindToMoniker|BindToObject|Process\.Start|GetActiveObject)\s*\(", RegexOptions.CultureInvariant);
        var found = new List<string>();
        foreach (var file in files)
        {
            var lineNumber = 0;
            foreach (var line in File.ReadAllLines(file))
            {
                lineNumber++;
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*')) continue; // コメントでの説明は構わない。
                if (forbidden.IsMatch(line)) found.Add($"{Path.GetFileName(file)}:{lineNumber}: {trimmed}");
            }
        }

        found.Should().BeEmpty("ROT の古い項目を扱うコードが、閉じたブックを（別の Excel で）開き直してはいけない");
    }

    private static string FindSrcGraft()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Graft.sln"))) dir = dir.Parent;
        dir.Should().NotBeNull("リポジトリのルート（Graft.sln）が見つかること");
        return Path.Combine(dir!.FullName, "src", "Graft");
    }
}
