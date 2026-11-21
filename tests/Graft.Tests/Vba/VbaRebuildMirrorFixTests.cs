using System.IO;
using FluentAssertions;
using Graft.Core;
using Graft.UiTests.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 不具合 3（「作り直す」が、ミラーでないフォルダを丸ごとごみ箱へ送る）の回帰テスト。
/// 修正前は、フォルダに何か 1 つでもあれば既存のミラーと見なし、作り直しでフォルダ全体をごみ箱へ送っていた
/// （「家計簿.xlsx」と「写真/」が入ったフォルダで <c>MirrorExists=True</c>、<c>rebuild: true</c> でフォルダごと消えた）。
/// </summary>
public class VbaRebuildMirrorFixTests : VbaServiceFixture
{
    private string PlantForeignFiles()
    {
        Directory.CreateDirectory(Path.Combine(MirrorRoot, "写真"));
        File.WriteAllText(Path.Combine(MirrorRoot, "家計簿.xlsx"), "dummy");
        File.WriteAllText(Path.Combine(MirrorRoot, "写真", "a.jpg"), "dummy");
        return MirrorRoot;
    }

    [Fact(DisplayName = "不具合3: ミラーのものではないファイルだけのフォルダは「既存のミラー」ではなく、作り直しも選べない")]
    public void ミラーでないフォルダは既存のミラーではない()
    {
        var root = PlantForeignFiles();

        Service.MirrorExists(root).Should().BeFalse();
        var info = Service.InspectMirrorFolder(root);
        info.Exists.Should().BeFalse();
        info.HasForeignFiles.Should().BeTrue();
        info.CanRebuild.Should().BeFalse();
    }

    [Fact(DisplayName = "不具合3 再現: ミラーでないフォルダへ rebuild: true で作ろうとしても、何もごみ箱へ送らず失敗する（E402）")]
    public async Task ミラーでないフォルダは作り直さない()
    {
        var root = PlantForeignFiles();
        Bridge.AddWorkbook(Book, FakeVbaBridge.Std("Module1", "x"));

        var result = await Service.CreateMirrorAsync(Book, root, rebuild: true);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Code == ErrorCode.E402);
        Trash.Sent.Should().BeEmpty();
        File.Exists(Path.Combine(root, "家計簿.xlsx")).Should().BeTrue();
        File.Exists(Path.Combine(root, "写真", "a.jpg")).Should().BeTrue();
    }

    [Fact(DisplayName = "不具合3: ミラーにミラーのものではないファイルが混じっていれば、既存のミラーではあるが作り直しは選べない")]
    public async Task 混在したミラーは作り直せない()
    {
        await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "x"));
        File.WriteAllText(Path.Combine(MirrorRoot, "README.md"), "hello");

        var info = Service.InspectMirrorFolder(MirrorRoot);

        info.Exists.Should().BeTrue();
        info.HasForeignFiles.Should().BeTrue();
        info.CanRebuild.Should().BeFalse();
        (await Service.CreateMirrorAsync(Book, MirrorRoot, rebuild: true)).IsSuccess.Should().BeFalse();
        Trash.Sent.Should().BeEmpty();
        File.Exists(MirrorFile("Modules/Module1.bas")).Should().BeTrue();
    }

    [Fact(DisplayName = "不具合3: ミラーだけのフォルダなら作り直せる。送るのは同期対象のファイルだけで、隠しフォルダ（.git など）と同期対象でないファイルは残る")]
    public async Task 作り直しは同期対象のファイルだけを送る()
    {
        await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "old"), FakeVbaBridge.Cls("Cls1", "old cls"));
        Directory.CreateDirectory(Path.Combine(MirrorRoot, ".git"));
        File.WriteAllText(Path.Combine(MirrorRoot, ".git", "HEAD"), "ref");
        File.WriteAllText(MirrorFile("Modules/notes.txt"), "memo");
        Service.InspectMirrorFolder(MirrorRoot).CanRebuild.Should().BeTrue();

        Excel("Module1").Code = "new";
        var rebuilt = await Service.CreateMirrorAsync(Book, MirrorRoot, rebuild: true);

        rebuilt.IsSuccess.Should().BeTrue();
        Trash.Sent.Select(Path.GetFileName).Should().BeEquivalentTo(new[] { "Module1.bas", "Cls1.cls" });
        File.ReadAllText(MirrorFile("Modules/Module1.bas")).Should().Be("new\r\n");
        File.ReadAllText(Path.Combine(MirrorRoot, ".git", "HEAD")).Should().Be("ref");
        File.ReadAllText(MirrorFile("Modules/notes.txt")).Should().Be("memo");
    }

    [Fact(DisplayName = "不具合3: ごみ箱へ送れなかったファイルがあれば、作り直さずに失敗する（同期状態も消さない）")]
    public async Task ごみ箱に送れなければ作り直さない()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "old"));
        var noTrash = new Graft.Features.VbaSyncService(Bridge, States, Paths, trash: null, () => Now);

        var result = await noTrash.CreateMirrorAsync(Book, MirrorRoot, rebuild: true);

        result.IsSuccess.Should().BeFalse();
        (await StateAsync()).Modules.Should().ContainKey("Modules/Module1.bas");
        File.Exists(MirrorFile("Modules/Module1.bas")).Should().BeTrue();
        project.Should().NotBeNull();
    }
}
