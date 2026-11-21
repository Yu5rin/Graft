using System.IO;
using System.Text.Json.Nodes;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// データ保存先の移行後の Root／Id の付け替え（<see cref="VbaDataRelinker"/>）と、
/// <see cref="ProjectStore"/> の場所の変更・削除での同期状態の扱い（仕様書_VBA連携 4.4・10.1）。
/// </summary>
public class VbaRelinkAndStoreTests
{
    private sealed class FakeTrash : ITrashService
    {
        public List<string> Sent { get; } = new();

        public bool Succeed { get; set; } = true;

        public bool IsSupported => true;

        public string? UnsupportedReason => null;

        public bool Send(string path)
        {
            if (!Succeed) return false;
            Sent.Add(path);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            return true;
        }
    }

    private static string Mirror(AppPaths paths, string name)
    {
        var dir = Path.Combine(paths.VbaMirrorsDirectory, name);
        Directory.CreateDirectory(Path.Combine(dir, "Modules"));
        File.WriteAllText(Path.Combine(dir, "Modules", "Module1.bas"), "Sub A()\r\nEnd Sub\r\n");
        return dir;
    }

    private static async Task<Project> RegisterVbaAsync(ProjectStore store, string root)
    {
        var registered = await store.RegisterAsync(root, "計画");
        registered.IsSuccess.Should().BeTrue();
        var updated = await store.UpdateAsync(registered.Value.Id, p => p with
        {
            Vba = new VbaLink { WorkbookFullName = @"C:\Work\計画.xlsm" },
        });
        return updated.Value;
    }

    [Fact(DisplayName = "移行: 旧保存先配下のミラーを持つ VBA プロジェクトの Root と Id が新保存先へ付け替わり、back／vba-sync／queue／layout も追随する")]
    public async Task 移行でRootとIdが付け替わる()
    {
        using var ws = new TempWorkspace();
        var oldPaths = new AppPaths(ws.CreateDirectory("old"));
        var newPaths = new AppPaths(ws.Combine("new"));
        var mirror = Mirror(oldPaths, "計画-1a2b3c4d");
        var oldProject = await RegisterVbaAsync(new ProjectStore(oldPaths), mirror);

        // 旧 Id をキーに持つ各データ。
        Directory.CreateDirectory(Path.Combine(oldPaths.GetProjectBackupDirectory(oldProject.Id), "r1"));
        File.WriteAllText(Path.Combine(oldPaths.GetProjectBackupDirectory(oldProject.Id), "r1", "x.txt"), "backup");
        await new VbaSyncStateStore(oldPaths).SaveAsync(oldProject.Id, new VbaSyncState { WorkbookFullName = @"C:\Work\計画.xlsm" });
        Directory.CreateDirectory(oldPaths.GetVbaRescueDirectory(oldProject.Id));
        File.WriteAllText(Path.Combine(oldPaths.GetVbaRescueDirectory(oldProject.Id), "a.txt"), "rescue");
        File.WriteAllText(oldPaths.QueueFilePath,
            $"{{\"items\":[{{\"id\":\"q1\",\"projectId\":\"{oldProject.Id}\"}},{{\"id\":\"q2\",\"projectId\":\"p_other\"}}]}}");
        File.WriteAllText(oldPaths.WindowLayoutFilePath,
            $"{{\"projectPaneWidths\":{{\"{oldProject.Id}\":{{\"projectColumnWidth\":300}},\"p_other\":{{}}}}}}");

        var result = DataDirectoryMigrator.MigrateAndSwitchPointer(ws.RootPath, oldPaths.BaseDirectory, newPaths.BaseDirectory, switchToPortable: false);

        result.IsSuccess.Should().BeTrue(string.Join(" / ", result.Issues.Select(i => i.ToDisplayText())));
        var loaded = (await new ProjectStore(newPaths).LoadAsync()).Value.Single();
        var expectedRoot = Path.Combine(newPaths.VbaMirrorsDirectory, "計画-1a2b3c4d");
        loaded.Root.Should().Be(expectedRoot);
        loaded.Id.Should().Be(ProjectStore.CreateId(expectedRoot)).And.NotBe(oldProject.Id);
        loaded.Vba.Should().NotBeNull("VBA の紐付けは保たれる");
        File.Exists(Path.Combine(expectedRoot, "Modules", "Module1.bas")).Should().BeTrue();

        Directory.Exists(newPaths.GetProjectBackupDirectory(oldProject.Id)).Should().BeFalse();
        File.ReadAllText(Path.Combine(newPaths.GetProjectBackupDirectory(loaded.Id), "r1", "x.txt")).Should().Be("backup");
        File.Exists(newPaths.GetVbaSyncFilePath(oldProject.Id)).Should().BeFalse();
        File.Exists(newPaths.GetVbaSyncFilePath(loaded.Id)).Should().BeTrue();
        File.ReadAllText(Path.Combine(newPaths.GetVbaRescueDirectory(loaded.Id), "a.txt")).Should().Be("rescue");

        var queue = File.ReadAllText(newPaths.QueueFilePath);
        queue.Should().Contain(loaded.Id).And.NotContain(oldProject.Id).And.Contain("p_other");
        var layout = JsonNode.Parse(File.ReadAllText(newPaths.WindowLayoutFilePath))!["projectPaneWidths"]!.AsObject();
        layout.ContainsKey(loaded.Id).Should().BeTrue();
        layout.ContainsKey(oldProject.Id).Should().BeFalse();
        layout.ContainsKey("p_other").Should().BeTrue();

        File.Exists(oldPaths.ProjectsFilePath).Should().BeTrue("旧保存先には触れない");
    }

    [Fact(DisplayName = "移行: 利用者が選んだ場所（旧 vba-mirrors の外）のミラーと、VBA でないプロジェクトは付け替えない")]
    public async Task 保存先の外のミラーは動かさない()
    {
        using var ws = new TempWorkspace();
        var oldPaths = new AppPaths(ws.CreateDirectory("old"));
        var newPaths = new AppPaths(ws.Combine("new"));
        var external = ws.CreateDirectory("mine/book");
        var plain = ws.CreateDirectory("plain");
        var store = new ProjectStore(oldPaths);
        var vba = await RegisterVbaAsync(store, external);
        var normal = (await store.RegisterAsync(plain, null)).Value;

        DataDirectoryMigrator.MigrateAndSwitchPointer(ws.RootPath, oldPaths.BaseDirectory, newPaths.BaseDirectory, false)
            .IsSuccess.Should().BeTrue();

        var loaded = (await new ProjectStore(newPaths).LoadAsync()).Value;
        loaded.Select(p => p.Id).Should().BeEquivalentTo(new[] { vba.Id, normal.Id });
        loaded.Single(p => p.Id == vba.Id).Root.Should().Be(Path.GetFullPath(external));
    }

    [Fact(DisplayName = "移行: 付け替え先の back/<新Id> が既にあると失敗し、コピー先は片付けられ、旧保存先は無傷")]
    public async Task 付け替えに失敗したら移行全体が失敗する()
    {
        using var ws = new TempWorkspace();
        var oldPaths = new AppPaths(ws.CreateDirectory("old"));
        var newPaths = new AppPaths(ws.Combine("new"));
        var mirror = Mirror(oldPaths, "b-1");
        var oldProject = await RegisterVbaAsync(new ProjectStore(oldPaths), mirror);
        Directory.CreateDirectory(oldPaths.GetProjectBackupDirectory(oldProject.Id));
        var newId = ProjectStore.CreateId(Path.Combine(newPaths.VbaMirrorsDirectory, "b-1"));
        // 先に新 Id のフォルダがある状況を、コピー先へ細工して作る（移行の途中で既存データと衝突）。
        Directory.CreateDirectory(Path.Combine(newPaths.GetProjectBackupDirectory(newId), "stale"));

        var result = DataDirectoryMigrator.MigrateAndSwitchPointer(ws.RootPath, oldPaths.BaseDirectory, newPaths.BaseDirectory, false);

        result.IsSuccess.Should().BeFalse();
        Directory.Exists(newPaths.BaseDirectory).Should().BeFalse("失敗時は中途半端なコピー先を片付ける");
        File.Exists(oldPaths.ProjectsFilePath).Should().BeTrue();
        Directory.Exists(oldPaths.GetProjectBackupDirectory(oldProject.Id)).Should().BeTrue();
    }

    [Fact(DisplayName = "起動時の取り込み直し: 旧保存先の最新データで上書きされた Root と Id も、旧保存先の削除の前に付け替え直される")]
    public async Task 取り込み直しの後でも付け替わる()
    {
        using var ws = new TempWorkspace();
        var oldPaths = new AppPaths(ws.CreateDirectory("old"));
        var newPaths = new AppPaths(ws.Combine("new"));
        var mirror = Mirror(oldPaths, "b-2");
        var oldProject = await RegisterVbaAsync(new ProjectStore(oldPaths), mirror);
        Directory.CreateDirectory(Path.Combine(oldPaths.GetProjectBackupDirectory(oldProject.Id), "r1"));
        File.WriteAllText(Path.Combine(oldPaths.GetProjectBackupDirectory(oldProject.Id), "r1", "x.txt"), "v1");

        DataDirectoryMigrator.MigrateAndSwitchPointer(ws.RootPath, oldPaths.BaseDirectory, newPaths.BaseDirectory, false)
            .IsSuccess.Should().BeTrue();
        // 再起動を後回しにしている間に、旧保存先へ履歴が増える。
        File.WriteAllText(Path.Combine(oldPaths.GetProjectBackupDirectory(oldProject.Id), "r1", "x.txt"), "v2-newer");

        var outcome = DataDirectoryMigrator.RunPendingCleanup(newPaths.BaseDirectory);

        outcome.Result.Should().Be(DataDirectoryMigrator.PendingCleanupResult.Completed, outcome.Detail);
        var loaded = (await new ProjectStore(newPaths).LoadAsync()).Value.Single();
        loaded.Root.Should().StartWith(newPaths.VbaMirrorsDirectory);
        Directory.Exists(newPaths.GetProjectBackupDirectory(oldProject.Id)).Should().BeFalse("旧 Id の履歴フォルダが取り残されない");
        File.ReadAllText(Path.Combine(newPaths.GetProjectBackupDirectory(loaded.Id), "r1", "x.txt")).Should().Be("v2-newer");
        Directory.Exists(oldPaths.VbaMirrorsDirectory).Should().BeFalse("旧保存先は掃除された");
    }

    [Fact(DisplayName = "RelocateAsync: Root が変わって Id が変わると、vba-sync/<旧Id>.json と控えが新しい Id へ付け替わる")]
    public async Task 場所の変更で同期状態が付け替わる()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var project = await RegisterVbaAsync(store, ws.CreateDirectory("m1"));
        await new VbaSyncStateStore(paths).SaveAsync(project.Id, new VbaSyncState { WorkbookFullName = @"C:\Work\計画.xlsm" });
        Directory.CreateDirectory(paths.GetVbaRescueDirectory(project.Id));

        var moved = await store.RelocateAsync(project.Id, ws.CreateDirectory("m2"));

        moved.IsSuccess.Should().BeTrue();
        moved.Value.Id.Should().NotBe(project.Id);
        File.Exists(paths.GetVbaSyncFilePath(project.Id)).Should().BeFalse();
        File.Exists(paths.GetVbaSyncFilePath(moved.Value.Id)).Should().BeTrue();
        Directory.Exists(paths.GetVbaRescueDirectory(moved.Value.Id)).Should().BeTrue();
    }

    [Fact(DisplayName = "RemoveAsync(VBA): 既定は登録だけ外す。ミラーも同期状態も残り、同じブックを再追加すると同じ Id に戻る")]
    public async Task 登録だけ外すと残る()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var mirror = Mirror(paths, "b-3");
        var project = await RegisterVbaAsync(store, mirror);
        await new VbaSyncStateStore(paths).SaveAsync(project.Id, new VbaSyncState());
        var trash = new FakeTrash();

        var result = await store.RemoveAsync(project.Id, deleteHistory: false, VbaRemoveOptions.KeepAll, trash);

        result.IsSuccess.Should().BeTrue();
        Directory.Exists(mirror).Should().BeTrue();
        File.Exists(paths.GetVbaSyncFilePath(project.Id)).Should().BeTrue();
        trash.Sent.Should().BeEmpty();
        (await store.RegisterAsync(mirror, null)).Value.Id.Should().Be(project.Id);
    }

    [Fact(DisplayName = "RemoveAsync(VBA): ミラーと同期状態も削除を選ぶと、ミラーはごみ箱へ送られ、同期状態ファイルは消える（控えは残る）")]
    public async Task ミラーと同期状態を削除できる()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var mirror = Mirror(paths, "b-4");
        var project = await RegisterVbaAsync(store, mirror);
        await new VbaSyncStateStore(paths).SaveAsync(project.Id, new VbaSyncState());
        Directory.CreateDirectory(paths.GetVbaRescueDirectory(project.Id));
        var trash = new FakeTrash();

        var result = await store.RemoveAsync(project.Id, false, new VbaRemoveOptions(true, true), trash);

        result.IsSuccess.Should().BeTrue();
        trash.Sent.Should().ContainSingle().Which.Should().Be(mirror);
        File.Exists(paths.GetVbaSyncFilePath(project.Id)).Should().BeFalse();
        Directory.Exists(paths.GetVbaRescueDirectory(project.Id)).Should().BeTrue();
    }

    [Fact(DisplayName = "RemoveAsync(VBA): 利用者が選んだ場所のミラーは、削除を選んでもフォルダを消さない（同期状態だけ対象）")]
    public async Task 保存先の外のミラーは消さない()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var external = ws.CreateDirectory("mine/book");
        var project = await RegisterVbaAsync(store, external);
        await new VbaSyncStateStore(paths).SaveAsync(project.Id, new VbaSyncState());
        var trash = new FakeTrash();

        (await store.RemoveAsync(project.Id, false, new VbaRemoveOptions(true, true), trash)).IsSuccess.Should().BeTrue();

        Directory.Exists(external).Should().BeTrue();
        trash.Sent.Should().BeEmpty();
        File.Exists(paths.GetVbaSyncFilePath(project.Id)).Should().BeFalse();
    }

    [Fact(DisplayName = "RemoveAsync(VBA): ごみ箱へ送れなければ、ミラーは残して警告にする（登録の削除自体は成功）")]
    public async Task ごみ箱へ送れなければ警告()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var mirror = Mirror(paths, "b-5");
        var project = await RegisterVbaAsync(store, mirror);

        var result = await store.RemoveAsync(project.Id, false, new VbaRemoveOptions(false, true), new FakeTrash { Succeed = false });

        result.IsSuccess.Should().BeTrue();
        result.Issues.Should().Contain(i => i.Severity == Severity.Warning);
        Directory.Exists(mirror).Should().BeTrue();
    }
}
