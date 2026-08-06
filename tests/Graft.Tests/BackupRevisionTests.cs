using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 仕様書8章（バックアップ・リビジョン管理）の統合テスト。<see cref="BackupManager"/>・
/// <see cref="BackupSession"/>・<see cref="RevisionStore"/>・<see cref="RevisionRestorer"/> を
/// 一時ディレクトリ上の実ファイルで検証する。<see cref="ApplyEngine"/> を経由せず
/// バックアップ層を直接操作することで、ApplyEngineTests側で確認した
/// GraftResult&lt;T&gt;.Valueの不具合（成功かつ値がnullのケースで例外を投げる）の影響を避け、
/// バックアップ層自体の挙動を独立して検証する。
/// </summary>
public class BackupRevisionTests
{
    /// <summary>BackupManager経由で1件のリビジョンを作成し、in_progress→successまで完了させる。</summary>
    private static async Task<(RevisionManifest Manifest, BackupSession Session)> CreateRevisionAsync(
        ApplyHarness harness, int revision, string patchHash, IReadOnlyList<RevisionEntry>? entries = null,
        RevisionStats? stats = null, string? summary = null)
    {
        var initial = new RevisionManifest
        {
            Revision = revision,
            ProjectId = harness.ProjectId,
            Summary = summary ?? $"リビジョン{revision}の変更",
            Type = "fix",
            AppliedAt = DateTimeOffset.Now,
            PatchHash = patchHash,
            Status = RevisionStatus.InProgress,
            Stats = stats ?? new RevisionStats { Files = 1, Added = 1, Removed = 0 },
        };

        var began = await harness.Backup.BeginAsync(harness.ProjectId, harness.ProjectRoot, initial);
        began.IsSuccess.Should().BeTrue(string.Join(",", began.Issues.Select(i => i.ToDisplayText())));
        var session = began.Value;

        var finalManifest = initial with { Status = RevisionStatus.Success, Entries = entries ?? Array.Empty<RevisionEntry>() };
        var completed = await session.CompleteAsync(finalManifest);
        completed.IsSuccess.Should().BeTrue();
        return (finalManifest, session);
    }

    // ------------------------------------------------------------------
    // 6.3 / 8.1 in_progress → success の遷移
    // ------------------------------------------------------------------

    [Fact(DisplayName = "BeginAsyncはin_progressのmanifestを書き込み、CompleteAsyncでsuccessへ遷移する")]
    public async Task InProgressからSuccessへ遷移する()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        var initial = new RevisionManifest
        {
            Revision = 1, ProjectId = harness.ProjectId, Summary = "テスト", AppliedAt = DateTimeOffset.Now,
            Status = RevisionStatus.InProgress,
        };

        var began = await harness.Backup.BeginAsync(harness.ProjectId, harness.ProjectRoot, initial);
        began.IsSuccess.Should().BeTrue();
        var manifestPath = Path.Combine(began.Value.FolderPath, "manifest.json");
        File.ReadAllText(manifestPath).Should().Contain("\"status\": \"in_progress\"");

        var completed = await began.Value.CompleteAsync(initial with { Status = RevisionStatus.Success });
        completed.IsSuccess.Should().BeTrue();
        File.ReadAllText(manifestPath).Should().Contain("\"status\": \"success\"");
    }

    [Fact(DisplayName = "リビジョンフォルダはr番号_日時の命名規則で作成され、相対パス構造を保ったまま退避する")]
    public async Task フォルダ命名と相対パス構造が保たれる()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("src/sub/file.txt", "退避対象の内容");

        var initial = new RevisionManifest
        {
            Revision = 7, ProjectId = harness.ProjectId, AppliedAt = DateTimeOffset.Now, Status = RevisionStatus.InProgress,
        };
        var began = await harness.Backup.BeginAsync(harness.ProjectId, harness.ProjectRoot, initial);
        began.IsSuccess.Should().BeTrue();

        Path.GetFileName(began.Value.FolderPath).Should().MatchRegex(@"^r7_\d{8}_\d{6}$");

        var stored = await began.Value.StoreAsync("src/sub/file.txt");
        stored.IsSuccess.Should().BeTrue();
        stored.Value.Should().BeTrue();
        var backedUpPath = Path.Combine(began.Value.FolderPath, "src", "sub", "file.txt");
        File.Exists(backedUpPath).Should().BeTrue("相対パス構造を保ったまま退避されているはず");
        File.ReadAllText(backedUpPath).Should().Be("退避対象の内容");
    }

    // ------------------------------------------------------------------
    // 13.1 バックアップフォルダの外部削除
    // ------------------------------------------------------------------

    [Fact(DisplayName = "バックアップフォルダを外部から削除しても履歴にsummaryと統計が残りIsRestorable=falseになる")]
    public async Task 外部削除後もhistoryにsummaryと統計が残る()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        var stats = new RevisionStats { Files = 3, Added = 12, Removed = 4 };
        var (manifest, session) = await CreateRevisionAsync(
            harness, 5, "sha256:abc", stats: stats, summary: "外部削除テスト対象");

        Directory.Delete(session.FolderPath, recursive: true);

        var listResult = await harness.Revisions.ListAsync(harness.ProjectId);
        listResult.IsSuccess.Should().BeTrue();
        var summary = listResult.Value.Single(r => r.Manifest.Revision == 5);
        summary.IsRestorable.Should().BeFalse("実体フォルダが外部から削除されたため復元不可のはず");
        summary.Manifest.Summary.Should().Be("外部削除テスト対象");
        summary.Manifest.Stats.Files.Should().Be(3);
        summary.Manifest.Stats.Added.Should().Be(12);
        summary.Manifest.Stats.Removed.Should().Be(4);
    }

    [Fact(DisplayName = "manifestのentriesが要求する退避ファイルの一部が欠けている場合もIsRestorable=falseになる")]
    public async Task 退避ファイルの一部欠落でも復元不可と判定される()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("a.txt", "内容");
        var (manifest, session) = await CreateRevisionAsync(
            harness, 6, "sha256:def",
            entries: new[] { new RevisionEntry { Path = "a.txt", Operation = EntryOperation.Modify } });
        // entries が要求する a.txt の退避ファイルを一切保存していない（StoreAsyncを呼んでいない）。

        var read = await harness.Revisions.ReadAsync(harness.ProjectId, 6);

        read.IsSuccess.Should().BeTrue();
        read.Value.IsRestorable.Should().BeFalse();
        read.Issues.Should().Contain(i => i.Code == ErrorCode.E405);
    }

    // ------------------------------------------------------------------
    // 7.4 世代管理
    // ------------------------------------------------------------------

    [Fact(DisplayName = "世代管理: プロジェクトあたりの最大リビジョン数を超えた分は古い順に削除される")]
    public async Task 世代管理で古いリビジョンから削除される()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        for (var i = 1; i <= 5; i++)
        {
            await CreateRevisionAsync(harness, i, $"sha256:rev{i}");
        }

        var settings = new BackupSettings { MaxRevisions = 2, MaxTotalMB = 0, UseRecycleBin = false };
        var removed = await harness.Revisions.EnforceRetentionAsync(harness.ProjectId, settings);

        removed.IsSuccess.Should().BeTrue();
        removed.Value.Should().Be(3, "5件中、上限2件を超える古い3件が削除されるはず");
        var remaining = await harness.Revisions.ListAsync(harness.ProjectId);
        remaining.Value.Select(r => r.Manifest.Revision).Should().BeEquivalentTo(new[] { 4, 5 });
    }

    // ------------------------------------------------------------------
    // 6.2 ComputePatchHashの正規化
    // ------------------------------------------------------------------

    [Theory(DisplayName = "ComputePatchHashは改行コード・行末空白の違いを正規化し同じハッシュを返す")]
    [InlineData("line1\nline2\n", "line1\r\nline2\r\n")]
    [InlineData("line1\nline2\n", "line1  \nline2\t\n")]
    [InlineData("line1\r\nline2", "line1\nline2")]
    public void ComputePatchHashは改行と行末空白を正規化する(string a, string b)
    {
        var hashA = RevisionStore.ComputePatchHash(a);
        var hashB = RevisionStore.ComputePatchHash(b);

        hashA.Should().Be(hashB, "改行コード・行末空白の違いのみでは異なるハッシュになってはならない");
    }

    [Fact(DisplayName = "ComputePatchHashは内容が異なれば異なるハッシュを返す")]
    public void ComputePatchHashは内容差を区別する()
    {
        var hashA = RevisionStore.ComputePatchHash("line1\nline2\n");
        var hashB = RevisionStore.ComputePatchHash("line1\nline3\n");

        hashA.Should().NotBe(hashB);
    }

    // ------------------------------------------------------------------
    // 7.3 復元とhashAfter照合
    // ------------------------------------------------------------------

    [Fact(DisplayName = "復元は退避内容を書き戻し、適用後さらに変更されていなければ警告なく完了する")]
    public async Task 復元は退避内容を書き戻す()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("target.txt", "変更前の内容");

        var initial = new RevisionManifest
        {
            Revision = 1, ProjectId = harness.ProjectId, AppliedAt = DateTimeOffset.Now, Status = RevisionStatus.InProgress,
        };
        var began = await harness.Backup.BeginAsync(harness.ProjectId, harness.ProjectRoot, initial);
        await began.Value.StoreAsync("target.txt");

        // 適用後の状態を模してファイルを書き換える。
        var afterText = "変更後の内容";
        File.WriteAllText(Path.Combine(harness.ProjectRoot, "target.txt"), afterText);
        var hashAfter = FileTextIO.ComputeHash(afterText);
        var entries = new[]
        {
            new RevisionEntry { Path = "target.txt", Operation = EntryOperation.Modify, HashAfter = hashAfter },
        };
        var finalManifest = initial with { Status = RevisionStatus.Success, Entries = entries };
        await began.Value.CompleteAsync(finalManifest);

        var summary = new RevisionSummary { Manifest = finalManifest, FolderPath = began.Value.FolderPath, IsRestorable = true };
        var restorer = new RevisionRestorer(harness.Paths);
        var restored = await restorer.RestoreAsync(harness.ProjectId, harness.ProjectRoot, summary, force: false);

        restored.IsSuccess.Should().BeTrue();
        File.ReadAllText(Path.Combine(harness.ProjectRoot, "target.txt")).Should().Be("変更前の内容");
    }

    [Fact(DisplayName = "復元前にhashAfterと現在の内容が食い違う場合は警告となりforce指定がなければ復元しない")]
    public async Task hashAfter不一致は警告となりforce無しでは復元しない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("target.txt", "変更前の内容");

        var initial = new RevisionManifest
        {
            Revision = 1, ProjectId = harness.ProjectId, AppliedAt = DateTimeOffset.Now, Status = RevisionStatus.InProgress,
        };
        var began = await harness.Backup.BeginAsync(harness.ProjectId, harness.ProjectRoot, initial);
        await began.Value.StoreAsync("target.txt");

        // manifestに記録するhashAfterを実際の内容とは異なる値にし、適用後さらに変更された状態を模す。
        var entries = new[]
        {
            new RevisionEntry { Path = "target.txt", Operation = EntryOperation.Modify, HashAfter = "ffffffffffffffff" },
        };
        var finalManifest = initial with { Status = RevisionStatus.Success, Entries = entries };
        await began.Value.CompleteAsync(finalManifest);
        File.WriteAllText(Path.Combine(harness.ProjectRoot, "target.txt"), "さらに変更された内容");

        var summary = new RevisionSummary { Manifest = finalManifest, FolderPath = began.Value.FolderPath, IsRestorable = true };
        var restorer = new RevisionRestorer(harness.Paths);

        var withoutForce = await restorer.RestoreAsync(harness.ProjectId, harness.ProjectRoot, summary, force: false);
        withoutForce.IsSuccess.Should().BeFalse();
        withoutForce.Errors.Should().Contain(i => i.Code == ErrorCode.E301);
        File.ReadAllText(Path.Combine(harness.ProjectRoot, "target.txt")).Should().Be("さらに変更された内容",
            "force指定なしでは警告が出た時点で復元を行わないはず");

        var withForce = await restorer.RestoreAsync(harness.ProjectId, harness.ProjectRoot, summary, force: true);
        withForce.IsSuccess.Should().BeTrue();
        File.ReadAllText(Path.Combine(harness.ProjectRoot, "target.txt")).Should().Be("変更前の内容");
    }

    [Fact(DisplayName = "実体フォルダが存在しないリビジョンは復元できずE405になる")]
    public async Task 実体が無いリビジョンはE405で復元できない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        var (manifest, session) = await CreateRevisionAsync(harness, 9, "sha256:missing");
        Directory.Delete(session.FolderPath, recursive: true);

        var summary = new RevisionSummary { Manifest = manifest, FolderPath = session.FolderPath, IsRestorable = false };
        var restorer = new RevisionRestorer(harness.Paths);

        var result = await restorer.RestoreAsync(harness.ProjectId, harness.ProjectRoot, summary, force: false);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(i => i.Code == ErrorCode.E405);
    }
}
