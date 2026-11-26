using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <see cref="ProjectStore.ClearHistoryAsync"/>（プロジェクトの登録は残したまま、変更履歴だけを削除する）の単体テスト。
/// 通しのシナリオ（右クリックメニュー・確認ダイアログ・履歴ペインの読み直し）は
/// tests/Graft.UiTests/ClearHistoryScenarioTests.cs を参照。
/// </summary>
public class ProjectStoreClearHistoryTests
{
    /// <summary>履歴フォルダ（リビジョンのバックアップと history.jsonl）を、実際の適用と同じ配置で作る。</summary>
    private static string CreateHistory(AppPaths paths, string projectId)
    {
        var backupDir = paths.GetProjectBackupDirectory(projectId);
        var revisionDir = Path.Combine(backupDir, "r1_20260101_000000");
        Directory.CreateDirectory(revisionDir);
        File.WriteAllText(Path.Combine(revisionDir, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(backupDir, "history.jsonl"), "{\"revision\":1}\n");
        return backupDir;
    }

    [Fact(DisplayName = "ClearHistoryAsync: back/<Id>/ を history.jsonl ごと消し、プロジェクトの登録と作業フォルダは残す")]
    public async Task 履歴だけが消え登録とファイルは残る()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var root = ws.CreateDirectory("proj");
        var file = ws.WriteText("proj/keep.txt", "大事な中身");
        var id = (await store.RegisterAsync(root, "P")).Value.Id;
        var backupDir = CreateHistory(paths, id);

        var result = await store.ClearHistoryAsync(id, useRecycleBin: false, trash: null);

        result.IsSuccess.Should().BeTrue();
        result.Issues.Should().BeEmpty();
        Directory.Exists(backupDir).Should().BeFalse("履歴フォルダは history.jsonl ごと消える");
        (await store.LoadAsync()).Value.Should().ContainSingle(p => p.Id == id, "プロジェクトの登録は残る");
        File.ReadAllText(file).Should().Be("大事な中身", "作業フォルダのファイルには触れない");
    }

    [Fact(DisplayName = "ClearHistoryAsync: NextRevision は戻さない（r43 の次は r44。番号を再利用しない）")]
    public async Task 版の番号は戻さない()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var id = (await store.RegisterAsync(ws.CreateDirectory("proj"), null)).Value.Id;
        await store.UpdateAsync(id, p => p with { NextRevision = 44 });
        CreateHistory(paths, id);

        await store.ClearHistoryAsync(id, useRecycleBin: false, trash: null);

        (await store.LoadAsync()).Value.Single(p => p.Id == id).NextRevision.Should().Be(44, "履歴を消しても次に払い出す番号は変わらない");
    }

    [Fact(DisplayName = "ClearHistoryAsync: VBA プロジェクトでも vba-sync（同期状態）と控え（.rescue）には触れない")]
    public async Task VBAの同期状態は残る()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var id = (await store.RegisterAsync(ws.CreateDirectory("mirror"), null)).Value.Id;
        await store.UpdateAsync(id, p => p with { Vba = new VbaLink { WorkbookFullName = @"C:\Work\計画.xlsm" } });
        CreateHistory(paths, id);

        var syncFile = paths.GetVbaSyncFilePath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(syncFile)!);
        File.WriteAllText(syncFile, "{}");
        var rescueDir = paths.GetVbaRescueDirectory(id);
        Directory.CreateDirectory(rescueDir);
        File.WriteAllText(Path.Combine(rescueDir, "a.bas"), "' 控え");

        var result = await store.ClearHistoryAsync(id, useRecycleBin: false, trash: null);

        result.IsSuccess.Should().BeTrue();
        File.Exists(syncFile).Should().BeTrue("同期状態は衝突の判定と未反映の計算の基準そのもの。履歴とは別物");
        File.Exists(Path.Combine(rescueDir, "a.bas")).Should().BeTrue("Excel 側の控えも残る");
        (await store.LoadAsync()).Value.Single(p => p.Id == id).Vba.Should().NotBeNull();
    }

    [Fact(DisplayName = "ClearHistoryAsync: 指定したプロジェクトの履歴だけを消し、ほかのプロジェクトの履歴・登録には影響しない")]
    public async Task ほかのプロジェクトには影響しない()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var a = (await store.RegisterAsync(ws.CreateDirectory("a"), null)).Value.Id;
        var b = (await store.RegisterAsync(ws.CreateDirectory("b"), null)).Value.Id;
        var dirA = CreateHistory(paths, a);
        var dirB = CreateHistory(paths, b);

        await store.ClearHistoryAsync(a, useRecycleBin: false, trash: null);

        Directory.Exists(dirA).Should().BeFalse();
        Directory.Exists(dirB).Should().BeTrue("別のプロジェクトの履歴は残る");
        File.Exists(Path.Combine(dirB, "history.jsonl")).Should().BeTrue();
        (await store.LoadAsync()).Value.Select(p => p.Id).Should().BeEquivalentTo(new[] { a, b });
    }

    [Fact(DisplayName = "ClearHistoryAsync: 履歴フォルダが無くても成功する（空のプロジェクト）")]
    public async Task 履歴が無くても成功する()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var id = (await store.RegisterAsync(ws.CreateDirectory("proj"), null)).Value.Id;

        var result = await store.ClearHistoryAsync(id, useRecycleBin: false, trash: null);

        result.IsSuccess.Should().BeTrue();
        result.Issues.Should().BeEmpty();
    }

    [Fact(DisplayName = "ClearHistoryAsync: 登録の無い Id には失敗（E201）を返し、何も消さない")]
    public async Task 登録の無いIdは失敗する()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var orphan = CreateHistory(paths, "p_000000");

        var result = await store.ClearHistoryAsync("p_000000", useRecycleBin: false, trash: null);

        result.IsSuccess.Should().BeFalse();
        result.HasIssue(ErrorCode.E201).Should().BeTrue();
        Directory.Exists(orphan).Should().BeTrue("登録の無い Id の履歴は、このメソッドでは消さない");
    }

    [Fact(DisplayName = "ClearHistoryAsync: 設定がごみ箱ならごみ箱へ送る。送れなかったときは通常削除へ落とさず、警告にして履歴を残す")]
    public async Task ごみ箱の設定に従う()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var id = (await store.RegisterAsync(ws.CreateDirectory("proj"), null)).Value.Id;
        var backupDir = CreateHistory(paths, id);

        var failing = new FakeTrash(sendSucceeds: false);
        var failed = await store.ClearHistoryAsync(id, useRecycleBin: true, trash: failing);
        failing.Sent.Should().Equal(backupDir);
        failed.Issues.Should().ContainSingle(i => i.Severity == Severity.Warning, "ごみ箱へ送れなかったことを知らせる");
        Directory.Exists(backupDir).Should().BeTrue("ごみ箱を選んだ利用者に、黙って完全削除はしない");

        var working = new FakeTrash(sendSucceeds: true);
        var ok = await store.ClearHistoryAsync(id, useRecycleBin: true, trash: working);
        working.Sent.Should().Equal(backupDir);
        ok.Issues.Should().BeEmpty();

        var off = new FakeTrash(sendSucceeds: true);
        await store.ClearHistoryAsync(id, useRecycleBin: false, trash: off);
        off.Sent.Should().BeEmpty("ごみ箱の設定がオフなら、ごみ箱は使わず通常削除する");
        Directory.Exists(backupDir).Should().BeFalse();
    }

    [Fact(DisplayName = "RemoveAsync: 同じ Id の項目が重ねて登録されていても、すべて外れて一覧に残らない")]
    public async Task 重複した登録もまとめて外れる()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var registered = (await store.RegisterAsync(ws.CreateDirectory("proj"), "P")).Value;
        await store.SaveAsync(new List<Project> { registered, registered });

        var result = await store.RemoveAsync(registered.Id, deleteHistory: false);

        result.IsSuccess.Should().BeTrue();
        (await store.LoadAsync()).Value.Should().BeEmpty("1件だけ外すと、残った重複が「削除したのに一覧に残る」ように見える");
    }

    private sealed class FakeTrash : ITrashService
    {
        private readonly bool _sendSucceeds;

        public FakeTrash(bool sendSucceeds) => _sendSucceeds = sendSucceeds;

        public List<string> Sent { get; } = new();

        public bool IsSupported => true;

        public string? UnsupportedReason => null;

        public bool Send(string path)
        {
            Sent.Add(path);
            if (_sendSucceeds && Directory.Exists(path)) Directory.Delete(path, recursive: true);
            return _sendSucceeds;
        }
    }
}
