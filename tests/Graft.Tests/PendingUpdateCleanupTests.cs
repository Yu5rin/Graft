using System.IO;
using FluentAssertions;
using Graft.Core.Update;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <see cref="PendingUpdateCleanup"/>: 次回起動時に前回の更新で残った<c>*.old</c>だけを掃除する。
/// 対象は<see cref="UpdateFiles.RequiredFileNames"/>＋".old"の組み合わせに厳密に限定され、
/// それ以外のファイル（利用者データを含む）には一切触れないことを固定する。
/// </summary>
public class PendingUpdateCleanupTests
{
    [Fact(DisplayName = "6ファイル分の.oldがあれば、その6つだけが削除される")]
    public void 対象の6ファイルだけ削除される()
    {
        using var ws = new TempWorkspace();
        var dir = ws.CreateDirectory("app");
        foreach (var fileName in UpdateFiles.RequiredFileNames)
        {
            File.WriteAllText(Path.Combine(dir, fileName + UpdateFiles.OldFileSuffix), "old");
        }

        var removed = PendingUpdateCleanup.Run(dir);

        removed.Should().BeEquivalentTo(UpdateFiles.RequiredFileNames);
        foreach (var fileName in UpdateFiles.RequiredFileNames)
        {
            File.Exists(Path.Combine(dir, fileName + UpdateFiles.OldFileSuffix)).Should().BeFalse();
        }
    }

    [Fact(DisplayName = ".oldが1つも無ければ何も削除しない")]
    public void 何も無ければ何もしない()
    {
        using var ws = new TempWorkspace();
        var dir = ws.CreateDirectory("app");

        var removed = PendingUpdateCleanup.Run(dir);

        removed.Should().BeEmpty();
    }

    [Fact(DisplayName = "利用者データ・関係の無い.oldファイルには一切触れない")]
    public void 利用者データや無関係なoldファイルには触れない()
    {
        using var ws = new TempWorkspace();
        var dir = ws.CreateDirectory("app");
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "settings.json.old"), "利用者が偶然置いたファイル");
        File.WriteAllText(Path.Combine(dir, "myproject.cs.old"), "利用者の作業ファイルかもしれない");
        File.WriteAllText(Path.Combine(dir, "Graft.exe" + UpdateFiles.OldFileSuffix), "old-exe");

        var removed = PendingUpdateCleanup.Run(dir);

        removed.Should().BeEquivalentTo(new[] { "Graft.exe" });
        File.Exists(Path.Combine(dir, "settings.json")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "settings.json.old")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "myproject.cs.old")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "Graft.exe" + UpdateFiles.OldFileSuffix)).Should().BeFalse();
    }
}
