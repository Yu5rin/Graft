using System;
using System.IO;
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
/// プロジェクトペイン改善（利用者からの明示的な要望7項目）のうち、<see cref="ProjectStore"/>
/// 側で新たに追加した操作（削除・ピン留め等の汎用更新・場所の変更）の単体テスト。
/// UI経由の通しシナリオはtests/Graft.UiTests/ProjectPaneOperationsScenarioTests.csを参照。
/// </summary>
public class ProjectStorePaneOperationsTests
{
    // ------------------------------------------------------------------
    // 要望1: 削除（RemoveAsync）— 最重要: 実フォルダには一切触れないこと
    // ------------------------------------------------------------------

    [Fact(DisplayName = "RemoveAsyncはprojects.jsonのエントリだけを削除し、実際のプロジェクトフォルダとその中身は一切削除しない")]
    public async Task 削除は登録情報だけを消しフォルダは残す()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var root = ws.CreateDirectory("myproject");
        var filePath = ws.WriteText("myproject/keep-me.txt", "大事なファイルの中身");

        var registered = await store.RegisterAsync(root, "私のプロジェクト");
        registered.IsSuccess.Should().BeTrue();
        var projectId = registered.Value.Id;

        var result = await store.RemoveAsync(projectId, deleteHistory: false);

        result.IsSuccess.Should().BeTrue();
        (await store.LoadAsync()).Value.Should().BeEmpty("projects.jsonからは削除されているはず");

        Directory.Exists(root).Should().BeTrue("削除するのは登録情報だけで、プロジェクトフォルダ自体は残るはず");
        File.Exists(filePath).Should().BeTrue("フォルダ内のファイルも一切削除されないはず");
        (await File.ReadAllTextAsync(filePath)).Should().Be("大事なファイルの中身", "ファイルの中身も変わらないはず");
    }

    [Fact(DisplayName = "RemoveAsyncはdeleteHistory=falseなら履歴フォルダ（back/<projectId>/）を残す")]
    public async Task 削除は履歴を残す選択ができる()
    {
        using var ws = new TempWorkspace();
        var appDir = ws.CreateDirectory("app");
        var paths = new AppPaths(appDir);
        var store = new ProjectStore(paths);
        var root = ws.CreateDirectory("proj-history");
        var registered = await store.RegisterAsync(root, null);
        var projectId = registered.Value.Id;

        // 実際の適用と同じように、back/<projectId>/ 配下へダミーの履歴フォルダを作る。
        var backupDir = paths.GetProjectBackupDirectory(projectId);
        Directory.CreateDirectory(Path.Combine(backupDir, "r1_20260101_000000"));
        await File.WriteAllTextAsync(Path.Combine(backupDir, "r1_20260101_000000", "manifest.json"), "{}");

        var result = await store.RemoveAsync(projectId, deleteHistory: false);

        result.IsSuccess.Should().BeTrue();
        Directory.Exists(backupDir).Should().BeTrue("「履歴は残す」を選んだので履歴フォルダは残るはず");
    }

    [Fact(DisplayName = "RemoveAsyncはdeleteHistory=trueなら履歴フォルダ（back/<projectId>/）も削除する")]
    public async Task 削除は履歴も削除できる()
    {
        using var ws = new TempWorkspace();
        var appDir = ws.CreateDirectory("app");
        var paths = new AppPaths(appDir);
        var store = new ProjectStore(paths);
        var root = ws.CreateDirectory("proj-history2");
        var registered = await store.RegisterAsync(root, null);
        var projectId = registered.Value.Id;

        var backupDir = paths.GetProjectBackupDirectory(projectId);
        Directory.CreateDirectory(Path.Combine(backupDir, "r1_20260101_000000"));

        var result = await store.RemoveAsync(projectId, deleteHistory: true);

        result.IsSuccess.Should().BeTrue();
        Directory.Exists(backupDir).Should().BeFalse("「履歴も削除する」を選んだので履歴フォルダは削除されるはず");
        Directory.Exists(root).Should().BeTrue("履歴を削除してもプロジェクトフォルダ自体は残るはず（安全性が最優先）");
    }

    [Fact(DisplayName = "履歴を残して削除した後、同じフォルダを再登録すると同じIdになり履歴が復活する")]
    public async Task 履歴を残して削除後に再登録すると履歴が復活する()
    {
        using var ws = new TempWorkspace();
        var appDir = ws.CreateDirectory("app");
        var paths = new AppPaths(appDir);
        var store = new ProjectStore(paths);
        var root = ws.CreateDirectory("proj-revive");
        var registered = await store.RegisterAsync(root, null);
        var originalId = registered.Value.Id;
        var backupDir = paths.GetProjectBackupDirectory(originalId);
        Directory.CreateDirectory(Path.Combine(backupDir, "r1_20260101_000000"));

        await store.RemoveAsync(originalId, deleteHistory: false);

        var reRegistered = await store.RegisterAsync(root, null);

        reRegistered.Value.Id.Should().Be(originalId,
            "CreateIdはパスから決定的に決まるため、同じフォルダを再登録すれば同じIdになるはず（ダイアログ文言の裏付け）");
        Directory.Exists(Path.Combine(backupDir, "r1_20260101_000000")).Should().BeTrue(
            "同じIdに戻るため、履歴フォルダは特に何もしなくても再び結び付くはず");
    }

    [Fact(DisplayName = "RemoveAsyncは存在しないプロジェクトIDに対して失敗を返す")]
    public async Task 削除は未知のプロジェクトIDでは失敗する()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);

        var result = await store.RemoveAsync("p_不存在", deleteHistory: false);

        result.IsSuccess.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // 要望3・4・5: UpdateAsync（ピン留め・表示名・タグの汎用更新）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "UpdateAsyncはピン留めの切替を永続化する")]
    public async Task ピン留めの切替が永続化される()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var registered = await store.RegisterAsync(ws.CreateDirectory("pinme"), null);
        registered.Value.Pinned.Should().BeFalse();

        var pinned = await store.UpdateAsync(registered.Value.Id, p => p with { Pinned = !p.Pinned });
        pinned.IsSuccess.Should().BeTrue();
        pinned.Value.Pinned.Should().BeTrue();

        var reloaded = await store.LoadAsync();
        reloaded.Value.Single().Pinned.Should().BeTrue("再起動を模したLoadAsyncでも保たれているはず");

        var unpinned = await store.UpdateAsync(registered.Value.Id, p => p with { Pinned = !p.Pinned });
        unpinned.Value.Pinned.Should().BeFalse();
    }

    [Fact(DisplayName = "UpdateAsyncで表示名を空にするとDisplayNameはフォルダ名由来の既定へ戻る")]
    public async Task 表示名を空にすると既定へ戻る()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var root = ws.CreateDirectory("folder-name-proj");
        var registered = await store.RegisterAsync(root, "カスタム名");
        registered.Value.DisplayName.Should().Be("カスタム名");

        var reset = await store.UpdateAsync(registered.Value.Id, p => p with { Name = string.Empty });

        reset.IsSuccess.Should().BeTrue();
        reset.Value.Name.Should().BeEmpty();
        reset.Value.DisplayName.Should().Be("folder-name-proj", "Nameが空ならDisplayNameはフォルダ名（Normalize）由来になるはず");

        var reloaded = await store.LoadAsync();
        reloaded.Value.Single().DisplayName.Should().Be("folder-name-proj", "再読込後も既定表示のままのはず");
    }

    [Fact(DisplayName = "UpdateAsyncはタグの一覧を永続化する")]
    public async Task タグが永続化される()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var registered = await store.RegisterAsync(ws.CreateDirectory("tagme"), null);

        var tagged = await store.UpdateAsync(registered.Value.Id, p => p with { Tags = new[] { "web", "backend" } });

        tagged.IsSuccess.Should().BeTrue();
        tagged.Value.Tags.Should().BeEquivalentTo(new[] { "web", "backend" });

        var reloaded = await store.LoadAsync();
        reloaded.Value.Single().Tags.Should().BeEquivalentTo(new[] { "web", "backend" }, "再起動後もタグが保たれているはず");
    }

    [Fact(DisplayName = "UpdateAsyncは存在しないプロジェクトIDに対して失敗を返す")]
    public async Task 汎用更新は未知のプロジェクトIDでは失敗する()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);

        var result = await store.UpdateAsync("p_不存在", p => p with { Pinned = true });

        result.IsSuccess.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // 要望6: RelocateAsync（場所の変更・履歴フォルダの引き継ぎ）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "RelocateAsyncはRootとIdを新しい場所へ差し替え、履歴フォルダ（back/<projectId>/）も一緒に移動する")]
    public async Task 場所の変更は履歴フォルダごと引き継ぐ()
    {
        using var ws = new TempWorkspace();
        var appDir = ws.CreateDirectory("app");
        var paths = new AppPaths(appDir);
        var store = new ProjectStore(paths);

        var oldRoot = ws.CreateDirectory("old-location");
        var registered = await store.RegisterAsync(oldRoot, "移動するプロジェクト");
        var oldId = registered.Value.Id;
        var oldBackupDir = paths.GetProjectBackupDirectory(oldId);
        var revisionDir = Path.Combine(oldBackupDir, "r3_20260101_000000");
        Directory.CreateDirectory(revisionDir);
        await File.WriteAllTextAsync(Path.Combine(revisionDir, "manifest.json"), """{"revision":3}""");

        // フォルダそのものを移動・リネームした状況を再現する（IsDisconnectedになる状況）。
        var newRoot = ws.Combine("new-location");
        Directory.Move(oldRoot, newRoot);

        var relocated = await store.RelocateAsync(oldId, newRoot);

        relocated.IsSuccess.Should().BeTrue();
        relocated.Value.Root.Should().Be(newRoot);
        relocated.Value.Id.Should().NotBe(oldId, "CreateIdはパスから決まるためRootを変えるとIdも変わるはず");
        relocated.Value.IsDisconnected.Should().BeFalse();
        relocated.Value.Name.Should().Be("移動するプロジェクト", "表示名等の他フィールドは維持されるはず");

        var newId = relocated.Value.Id;
        newId.Should().Be(ProjectStore.CreateId(newRoot));

        Directory.Exists(oldBackupDir).Should().BeFalse("旧Idの履歴フォルダは新Idの場所へ移動済みのはず");
        var newBackupDir = paths.GetProjectBackupDirectory(newId);
        File.Exists(Path.Combine(newBackupDir, "r3_20260101_000000", "manifest.json")).Should().BeTrue(
            "履歴（バックアップフォルダ）が新しいIdの場所へ引き継がれているはず（履歴が切り離されないことの直接的な確認）");

        var reloaded = await store.LoadAsync();
        reloaded.Value.Should().ContainSingle(p => p.Id == newId && p.Root == newRoot);
    }

    [Fact(DisplayName = "RelocateAsyncは選んだフォルダが既に別プロジェクトとして登録済みなら重複登録せず失敗を返す")]
    public async Task 場所の変更は重複登録を拒否する()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);

        var rootA = ws.CreateDirectory("dup-a");
        var rootB = ws.CreateDirectory("dup-b");
        var projectA = await store.RegisterAsync(rootA, "A");
        await store.RegisterAsync(rootB, "B");

        // Aの場所をBのフォルダへ変更しようとする（Bは既に別プロジェクトとして登録済み）。
        var result = await store.RelocateAsync(projectA.Value.Id, rootB);

        result.IsSuccess.Should().BeFalse("Bは既に別プロジェクトとして登録済みのため、重複登録を防ぐ必要がある");
        var reloaded = await store.LoadAsync();
        reloaded.Value.Should().HaveCount(2, "拒否された場合はprojects.jsonの内容が変わってはいけない");
    }

    [Fact(DisplayName = "RelocateAsyncは存在しないフォルダを指定すると失敗する")]
    public async Task 場所の変更は存在しないフォルダを拒否する()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var root = ws.CreateDirectory("relocate-missing");
        var registered = await store.RegisterAsync(root, null);

        var result = await store.RelocateAsync(registered.Value.Id, Path.Combine(ws.RootPath, "does-not-exist"));

        result.IsSuccess.Should().BeFalse();
    }

    [Fact(DisplayName = "RelocateAsyncは履歴フォルダがまだ無い（未適用の）プロジェクトでも問題なく場所を変更できる")]
    public async Task 場所の変更は履歴が無くても成功する()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var oldRoot = ws.CreateDirectory("no-history-old");
        var registered = await store.RegisterAsync(oldRoot, null);
        var newRoot = ws.Combine("no-history-new");
        Directory.Move(oldRoot, newRoot);

        var result = await store.RelocateAsync(registered.Value.Id, newRoot);

        result.IsSuccess.Should().BeTrue();
        result.Value.Root.Should().Be(newRoot);
    }
}
