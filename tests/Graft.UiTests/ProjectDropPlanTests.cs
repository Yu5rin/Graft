using System.IO;
using FluentAssertions;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// プロジェクト一覧への落とし物の振り分け（<see cref="ProjectDropPlan.Create"/>。1.3.1）の単体テスト。
/// <c>ProjectPaneDropTargetTests</c> と同じく、実際のドラッグ操作は合成せず、パスの一覧から結果を返す純粋な関数を確かめる。
/// </summary>
public class ProjectDropPlanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "graft-dropplan-" + Guid.NewGuid().ToString("N"));

    public ProjectDropPlanTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        TempDirectoryCleanup.TryDeleteRecursive(_dir);
        GC.SuppressFinalize(this);
    }

    private string NewFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact(DisplayName = "VBA 連携が使える: ブックは Workbooks、.xlsx は NoMacroWorkbooks に分かれ、親フォルダには入らない。フォルダと通常ファイルは従来どおり")]
    public void 連携が使えるときの振り分け()
    {
        var sub = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(sub);
        var book = NewFile("計画.xlsm");
        var addin = NewFile("a.xlam");
        var plain = NewFile("メモ.txt");
        var xlsx = NewFile("b.xlsx");

        var plan = ProjectDropPlan.Create(new string?[] { sub, book, plain, xlsx, addin, null, "" }, vbaAvailable: true);

        plan.Folders.Should().Equal(sub, _dir);
        plan.Workbooks.Should().Equal(book, addin);
        plan.NoMacroWorkbooks.Should().Equal(xlsx);
    }

    [Fact(DisplayName = "VBA 連携が使えない（Linux・設定オフ）: ブックも .xlsx も通常ファイルと同じで、親フォルダを登録する（従来の挙動）")]
    public void 連携が使えないときは従来どおり()
    {
        var book = NewFile("計画.xlsm");
        var xlsx = NewFile("b.xlsx");

        var plan = ProjectDropPlan.Create(new[] { book, xlsx }, vbaAvailable: false);

        plan.Folders.Should().Equal(_dir, _dir);
        plan.Workbooks.Should().BeEmpty();
        plan.NoMacroWorkbooks.Should().BeEmpty();
    }

    [Fact(DisplayName = "ブックの拡張子で終わる名前のフォルダは、フォルダとして登録する")]
    public void ブックの拡張子のフォルダ()
    {
        var odd = Path.Combine(_dir, "変な名前.xlsm");
        Directory.CreateDirectory(odd);

        var plan = ProjectDropPlan.Create(new[] { odd }, vbaAvailable: true);

        plan.Folders.Should().Equal(odd);
        plan.Workbooks.Should().BeEmpty();
    }
}
