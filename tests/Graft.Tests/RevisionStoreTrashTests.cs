using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Infra;
using Graft.Platform;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 10件目の不具合修正: <see cref="RevisionStore.EnforceRetentionAsync"/>（7.4の世代管理）が
/// <see cref="ITrashService"/>経由でごみ箱へ送るように配線されたことの検証。従来は
/// <c>OperatingSystem.IsWindows()</c>判定でWindows専用の<c>RecycleBin</c>を直呼びしており、
/// Linuxでは<c>UseRecycleBin</c>設定に関わらず常に通常削除にフォールバックしていた。
/// 実OSのごみ箱APIには依存せず、フェイクの<see cref="ITrashService"/>で検証する。
/// </summary>
public class RevisionStoreTrashTests
{
    private static async Task<(RevisionManifest Manifest, BackupSession Session)> CreateRevisionAsync(
        ApplyHarness harness, int revision, string patchHash)
    {
        var initial = new RevisionManifest
        {
            Revision = revision,
            ProjectId = harness.ProjectId,
            Summary = $"リビジョン{revision}の変更",
            Type = "fix",
            AppliedAt = DateTimeOffset.Now,
            PatchHash = patchHash,
            Status = RevisionStatus.InProgress,
            Stats = new RevisionStats { Files = 1, Added = 1, Removed = 0 },
        };

        var began = await harness.Backup.BeginAsync(harness.ProjectId, harness.ProjectRoot, initial);
        began.IsSuccess.Should().BeTrue(string.Join(",", began.Issues.Select(i => i.ToDisplayText())));
        var session = began.Value;

        var finalManifest = initial with { Status = RevisionStatus.Success };
        var completed = await session.CompleteAsync(finalManifest);
        completed.IsSuccess.Should().BeTrue();
        return (finalManifest, session);
    }

    [Fact(DisplayName = "UseRecycleBin=trueかつITrashServiceが対応環境なら、世代整理はSendへ渡る（通常削除しない）")]
    public async Task 世代整理はITrashServiceへ渡される()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        for (var i = 1; i <= 3; i++)
        {
            await CreateRevisionAsync(harness, i, $"sha256:rev{i}");
        }

        var trash = new FakeTrashService(isSupported: true, sendSucceeds: true);
        var store = new RevisionStore(harness.Paths, trash);
        var settings = new BackupSettings { MaxRevisions = 1, MaxTotalMB = 0, UseRecycleBin = true };

        var removed = await store.EnforceRetentionAsync(harness.ProjectId, settings);

        removed.IsSuccess.Should().BeTrue();
        removed.Value.Should().Be(2, "3件中、上限1件を超える古い2件が削除対象のはず");
        trash.SentPaths.Should().HaveCount(2, "ごみ箱対応環境ではITrashService.Sendへ渡されるはず");

        var projectBackupDir = harness.Paths.GetProjectBackupDirectory(harness.ProjectId);
        Directory.EnumerateDirectories(projectBackupDir).Should().HaveCount(1, "実体フォルダは新しい1件のみ残るはず");
    }

    [Fact(DisplayName = "ITrashServiceが未対応環境（IsSupported=false）の場合は通常削除にフォールバックする")]
    public async Task 未対応環境では通常削除にフォールバックする()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        await CreateRevisionAsync(harness, 1, "sha256:a");
        await CreateRevisionAsync(harness, 2, "sha256:b");

        var trash = new FakeTrashService(isSupported: false, sendSucceeds: true);
        var store = new RevisionStore(harness.Paths, trash);
        var settings = new BackupSettings { MaxRevisions = 1, MaxTotalMB = 0, UseRecycleBin = true };

        var removed = await store.EnforceRetentionAsync(harness.ProjectId, settings);

        removed.IsSuccess.Should().BeTrue();
        removed.Value.Should().Be(1);
        trash.SentPaths.Should().BeEmpty("未対応環境ではSendを呼ばず、通常削除へ直接フォールバックするはず");

        var projectBackupDir = harness.Paths.GetProjectBackupDirectory(harness.ProjectId);
        Directory.EnumerateDirectories(projectBackupDir).Should().HaveCount(1);
    }

    [Fact(DisplayName = "対応環境でもITrashService.Sendが失敗した場合は、Windows専用実装と同じく通常削除へフォールバックせず失敗として報告する")]
    public async Task Send失敗時は削除失敗として報告される()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        await CreateRevisionAsync(harness, 1, "sha256:a");
        await CreateRevisionAsync(harness, 2, "sha256:b");

        var trash = new FakeTrashService(isSupported: true, sendSucceeds: false);
        var store = new RevisionStore(harness.Paths, trash);
        var settings = new BackupSettings { MaxRevisions = 1, MaxTotalMB = 0, UseRecycleBin = true };

        var removed = await store.EnforceRetentionAsync(harness.ProjectId, settings);

        removed.IsSuccess.Should().BeTrue("EnforceRetentionAsync自体は個々の削除失敗をissueとして返し、Fail全体にはしない");
        removed.Value.Should().Be(0, "ごみ箱へ送れなかった分は通常削除へフォールバックせず、削除失敗のまま残るはず（従来のWindows専用実装と同じ挙動）");
        removed.Issues.Should().Contain(i => i.Code == ErrorCode.E402);
        trash.SentPaths.Should().ContainSingle();

        var projectBackupDir = harness.Paths.GetProjectBackupDirectory(harness.ProjectId);
        Directory.EnumerateDirectories(projectBackupDir).Should().HaveCount(2, "削除に失敗したフォルダはそのまま残るはず");
    }

    [Fact(DisplayName = "UseRecycleBin=falseの場合はITrashServiceが対応環境でもSendを呼ばず通常削除する")]
    public async Task UseRecycleBinがfalseならSendを呼ばない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        await CreateRevisionAsync(harness, 1, "sha256:a");
        await CreateRevisionAsync(harness, 2, "sha256:b");

        var trash = new FakeTrashService(isSupported: true, sendSucceeds: true);
        var store = new RevisionStore(harness.Paths, trash);
        var settings = new BackupSettings { MaxRevisions = 1, MaxTotalMB = 0, UseRecycleBin = false };

        var removed = await store.EnforceRetentionAsync(harness.ProjectId, settings);

        removed.IsSuccess.Should().BeTrue();
        removed.Value.Should().Be(1);
        trash.SentPaths.Should().BeEmpty("UseRecycleBin=falseのときはごみ箱を使わない設定のはず");
    }

    /// <summary>
    /// 実OSのごみ箱APIには依存しないフェイク。<paramref name="sendSucceeds"/>がtrueの場合、
    /// 実際のごみ箱実装（WindowsTrashService・LinuxTrashService）と同じく対象を実体ごと
    /// その場から取り除く（＝呼び出し側が「送った後は通常削除へフォールバックしない」ことを
    /// 実体の消失で確認できるようにする）。falseの場合は何もせず、対象をそのまま残す。
    /// </summary>
    private sealed class FakeTrashService : ITrashService
    {
        private readonly bool _sendSucceeds;

        public FakeTrashService(bool isSupported, bool sendSucceeds)
        {
            IsSupported = isSupported;
            _sendSucceeds = sendSucceeds;
        }

        public List<string> SentPaths { get; } = new();

        public bool IsSupported { get; }

        public string? UnsupportedReason => IsSupported ? null : "テスト用: 未対応環境";

        public bool Send(string path)
        {
            SentPaths.Add(path);
            if (!_sendSucceeds) return false;

            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
            return true;
        }
    }
}
