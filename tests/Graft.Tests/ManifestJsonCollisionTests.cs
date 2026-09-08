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
/// 実機不具合の回帰テスト: プロジェクト直下に <c>manifest.json</c>（Chrome/Edge拡張の開発が
/// 典型）があるプロジェクトで、取り消し（<see cref="RevisionRestorer.RestoreAsync"/>）・
/// 「ここまで戻す」（<see cref="RevisionRestorer.RestoreThroughAsync"/>）を行うと、
/// リビジョンのメタデータ（同名の manifest.json）と退避コピーのパスが一致してしまい、
/// 利用者のファイルがGraftの内部データで上書きされていた不具合の修正を検証する。
///
/// 【対応した内容】
/// (1) 新規に作成するバックアップは、退避したプロジェクトファイルを
///     <c>files/</c> サブフォルダ配下へ物理的に分離し、メタデータ（リビジョンフォルダ直下の
///     manifest.json）と同一パスになることが構造的に無くなるようにした
///     （<see cref="Graft.Core.BackupPathUtil.FilesSubfolderName"/>参照）。
/// (2) 復元側（<see cref="BackupSession"/>・<see cref="RevisionRestorer"/>）は新レイアウトを
///     優先しつつ、既存の旧レイアウト（フラット配置）のバックアップも引き続き読めるよう
///     後退する（<see cref="Graft.Core.BackupPathUtil.ResolveBackupFilePathForRead"/>）。
/// (3) 旧レイアウトで既に manifest.json の退避コピーがメタデータに上書きされ壊れている
///     ケースは、書き込む前に検出して拒否する（E216。<see cref="Graft.Core.BackupPathUtil.
///     LooksOverwrittenByManifestMetadata"/>）。この検出は当該ファイルだけを失敗として扱い、
///     同じリビジョンの他のファイルの復元は継続する。
/// </summary>
public class ManifestJsonCollisionTests
{
    private const string OriginalManifestJson = "{\n  \"manifest_version\": 3,\n  \"name\": \"サンプル拡張\",\n  \"version\": \"1.0\"\n}\n";
    private const string ChangedManifestJson = "{\n  \"manifest_version\": 3,\n  \"name\": \"サンプル拡張\",\n  \"version\": \"1.1\"\n}\n";

    /// <summary>SEARCH/REPLACE形式のパッチ本文を組み立てる（仕様書4.1）。</summary>
    private static string BuildSrPatch(string relativePath, string search, string replace)
        => $"<<<< FILE: {relativePath}\n<<<<<<< SEARCH\n{search}\n=======\n{replace}\n>>>>>>> REPLACE\n";

    // ------------------------------------------------------------------
    // 1. 単発復元（RestoreAsync）: 実機不具合そのものの再現
    // ------------------------------------------------------------------

    [Fact(DisplayName = "プロジェクト直下にmanifest.jsonがあるプロジェクトで、それを変更するパッチを適用して取り消すと適用前の内容に戻る（実機不具合の再現）")]
    public async Task プロジェクト直下のmanifestJsonを変更する取り消しで元に戻る()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("manifest.json", OriginalManifestJson);
        harness.WriteProjectText("content.js", "console.log('a');");

        var ctx = harness.MakeContext(1);
        var patchText = BuildSrPatch("manifest.json", "\"version\": \"1.0\"", "\"version\": \"1.1\"");
        var dryRun = await harness.DryRunAsync(patchText, ctx);
        var applied = await harness.ApplyAsync(dryRun, ctx);
        applied.IsSuccess.Should().BeTrue(string.Join(",", applied.Issues.Select(i => i.ToDisplayText())));

        // 適用直後、プロジェクトのmanifest.jsonは変更後の内容になっているはず。
        System.Text.Encoding.UTF8.GetString(harness.ReadProjectBytes("manifest.json")).Should().Be(ChangedManifestJson);

        // 退避先（files/サブフォルダ配下）に元の内容がそのまま残っており、リビジョンフォルダ
        // 直下のメタデータ（manifest.json）を巻き込んで上書きしていないことを確認する
        // （今回の不具合の核心部分）。
        var revisionDir = harness.Paths.GetRevisionDirectory(harness.ProjectId, applied.Value.Revision, applied.Value.AppliedAt);
        var metadataPath = Path.Combine(revisionDir, "manifest.json");
        var backedUpFilePath = Path.Combine(revisionDir, "files", "manifest.json");
        File.Exists(backedUpFilePath).Should().BeTrue("退避コピーはfiles/サブフォルダ配下にあるはず");
        File.ReadAllText(backedUpFilePath).Should().Be(OriginalManifestJson,
            "退避コピーの中身は変更前のプロジェクトのmanifest.jsonであり、リビジョンのメタデータではないはず");
        File.ReadAllText(metadataPath).Should().Contain("\"status\": \"success\"",
            "リビジョンフォルダ直下はメタデータのままで、退避コピーに上書きされていないはず");

        var restorer = new RevisionRestorer(harness.Paths);
        var revisionResult = await harness.Revisions.ReadAsync(harness.ProjectId, applied.Value.Revision);
        revisionResult.IsSuccess.Should().BeTrue();
        revisionResult.Value.IsRestorable.Should().BeTrue();

        var restored = await restorer.RestoreAsync(harness.ProjectId, harness.ProjectRoot, revisionResult.Value, force: false);

        restored.IsSuccess.Should().BeTrue(string.Join(",", restored.Issues.Select(i => i.ToDisplayText())));
        System.Text.Encoding.UTF8.GetString(harness.ReadProjectBytes("manifest.json")).Should().Be(OriginalManifestJson,
            "取り消し後はプロジェクトのmanifest.jsonが適用前の内容に戻っているはず（内部データで上書きされてはならない）");
        System.Text.Encoding.UTF8.GetString(harness.ReadProjectBytes("content.js")).Should().Be("console.log('a');",
            "manifest.jsonと無関係なファイルは変化しないはず");

        // 復元前後でハッシュを比較する（報告者からの依頼事項）。
        var restoredHash = FileTextIO.ComputeHash(System.Text.Encoding.UTF8.GetString(harness.ReadProjectBytes("manifest.json")));
        var entry = revisionResult.Value.Manifest.Entries.Single(e => e.Path == "manifest.json");
        restoredHash.Should().Be(entry.HashBefore, "復元後の内容のハッシュは、適用前に記録されたhashBeforeと一致するはず");
    }

    // ------------------------------------------------------------------
    // 2. 「ここまで戻す」（RestoreThroughAsync）でも同様に戻ること
    // ------------------------------------------------------------------

    [Fact(DisplayName = "「ここまで戻す」でも、プロジェクト直下のmanifest.jsonを変更したリビジョンを正しく取り消せる")]
    public async Task ここまで戻すでもmanifestJsonが元に戻る()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("manifest.json", OriginalManifestJson);

        var ctx1 = harness.MakeContext(1);
        var patch1 = BuildSrPatch("manifest.json", "\"version\": \"1.0\"", "\"version\": \"1.1\"");
        var dryRun1 = await harness.DryRunAsync(patch1, ctx1);
        var applied1 = await harness.ApplyAsync(dryRun1, ctx1);
        applied1.IsSuccess.Should().BeTrue(string.Join(",", applied1.Issues.Select(i => i.ToDisplayText())));

        var ctx2 = harness.MakeContext(2);
        var patch2 = BuildSrPatch("manifest.json", "\"version\": \"1.1\"", "\"version\": \"1.2\"");
        var dryRun2 = await harness.DryRunAsync(patch2, ctx2);
        var applied2 = await harness.ApplyAsync(dryRun2, ctx2);
        applied2.IsSuccess.Should().BeTrue(string.Join(",", applied2.Issues.Select(i => i.ToDisplayText())));

        var list = await harness.Revisions.ListAsync(harness.ProjectId);
        list.IsSuccess.Should().BeTrue();
        var preview = RevisionRestorer.BuildRestoreThroughPreview(list.Value, targetRevision: 1);
        preview.CanExecute.Should().BeTrue();

        var restorer = new RevisionRestorer(harness.Paths);
        var result = await restorer.RestoreThroughAsync(
            harness.ProjectId, harness.ProjectRoot, targetRevision: 1, preview.RevisionsToUndo, newRevisionNumber: 3, force: false);

        result.IsSuccess.Should().BeTrue(string.Join(",", result.Issues.Select(i => i.ToDisplayText())));
        System.Text.Encoding.UTF8.GetString(harness.ReadProjectBytes("manifest.json")).Should().Be(ChangedManifestJson,
            "r1適用直後（version 1.1）の内容まで戻っているはず");

        // このまとめ戻し操作自身の退避先も新レイアウトになっており、メタデータと衝突していない
        // ことを確認する。
        var r3Result = await harness.Revisions.ReadAsync(harness.ProjectId, 3);
        r3Result.IsSuccess.Should().BeTrue();
        var backedUpFilePath = Path.Combine(r3Result.Value.FolderPath, "files", "manifest.json");
        File.Exists(backedUpFilePath).Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // 3. 旧レイアウトからの復元は従来どおり動くこと（後方互換）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "旧レイアウト（フラット配置）で作られたバックアップは、files/サブフォルダが無くても従来どおり復元できる")]
    public async Task 旧レイアウトのバックアップからの復元が動く()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("content.js", "変更後の内容");

        // 旧レイアウト（v1.0.15以前）のバックアップフォルダを手で組み立てる。退避ファイルは
        // files/サブフォルダを経由せず、リビジョンフォルダ直下へ直接フラット配置する。
        var revisionDir = harness.Paths.GetRevisionDirectory(harness.ProjectId, 1, DateTimeOffset.Now);
        Directory.CreateDirectory(revisionDir);
        File.WriteAllText(Path.Combine(revisionDir, "content.js"), "変更前の内容");

        var manifest = new RevisionManifest
        {
            Revision = 1,
            ProjectId = harness.ProjectId,
            Summary = "旧レイアウトのテスト用リビジョン",
            AppliedAt = DateTimeOffset.Now,
            Status = RevisionStatus.Success,
            Entries = new[]
            {
                new RevisionEntry
                {
                    Path = "content.js",
                    Operation = EntryOperation.Modify,
                    HashBefore = FileTextIO.ComputeHash("変更前の内容"),
                    HashAfter = FileTextIO.ComputeHash("変更後の内容"),
                },
            },
        };
        var jsonStore = new JsonFileStore();
        await jsonStore.WriteAsync(Path.Combine(revisionDir, "manifest.json"), manifest, JsonFileStore.DefaultOptions);

        var summary = new RevisionSummary { Manifest = manifest, FolderPath = revisionDir, IsRestorable = true };
        var restorer = new RevisionRestorer(harness.Paths);
        var restored = await restorer.RestoreAsync(harness.ProjectId, harness.ProjectRoot, summary, force: false);

        restored.IsSuccess.Should().BeTrue(string.Join(",", restored.Issues.Select(i => i.ToDisplayText())));
        System.Text.Encoding.UTF8.GetString(harness.ReadProjectBytes("content.js")).Should().Be("変更前の内容",
            "旧レイアウト（直下）の退避ファイルからも正しく復元できるはず（後方互換）");
    }

    // ------------------------------------------------------------------
    // 4. 旧レイアウトで既に壊れているケース（manifest.jsonの退避コピーがメタデータに
    //    上書き済み）を検出し、書き込まずに拒否すること
    // ------------------------------------------------------------------

    [Fact(DisplayName = "旧レイアウトでmanifest.jsonの退避コピーが既にメタデータで上書きされているケースは、書き込まずにE216で失敗し、他のファイルの復元は継続する")]
    public async Task 旧レイアウトで壊れたmanifestJsonは書き込まずに失敗する()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        var currentManifestContent = "利用者のmanifest.json（現在の内容。書き換わってはならない）";
        harness.WriteProjectText("manifest.json", currentManifestContent);
        harness.WriteProjectText("content.js", "現在のcontent.js");

        var revisionDir = harness.Paths.GetRevisionDirectory(harness.ProjectId, 1, DateTimeOffset.Now);
        Directory.CreateDirectory(revisionDir);

        // 旧レイアウトの衝突を実際に再現する: manifest.jsonの退避コピーは、確定処理により
        // メタデータ自身のJSONで上書きされてしまっている（実機不具合そのものの状態）。
        var manifest = new RevisionManifest
        {
            Revision = 1,
            ProjectId = harness.ProjectId,
            Summary = "旧レイアウトの破損再現用リビジョン",
            AppliedAt = DateTimeOffset.Now,
            Status = RevisionStatus.Success,
            Entries = new[]
            {
                new RevisionEntry { Path = "manifest.json", Operation = EntryOperation.Modify },
                new RevisionEntry
                {
                    Path = "content.js",
                    Operation = EntryOperation.Modify,
                    HashBefore = FileTextIO.ComputeHash("以前のcontent.js"),
                    HashAfter = FileTextIO.ComputeHash("現在のcontent.js"),
                },
            },
        };
        var jsonStore = new JsonFileStore();
        // manifest.json（リビジョンのメタデータ）を書く。旧レイアウトではこれが同時に
        // 「manifest.jsonエントリの退避コピー」でもあるため、意図的に同じパスへ書く。
        await jsonStore.WriteAsync(Path.Combine(revisionDir, "manifest.json"), manifest, JsonFileStore.DefaultOptions);
        // content.jsは正しく退避できている（衝突条件はファイル名一致のみのため）。
        File.WriteAllText(Path.Combine(revisionDir, "content.js"), "以前のcontent.js");

        var summary = new RevisionSummary { Manifest = manifest, FolderPath = revisionDir, IsRestorable = true };
        var restorer = new RevisionRestorer(harness.Paths);
        var restored = await restorer.RestoreAsync(harness.ProjectId, harness.ProjectRoot, summary, force: true);

        restored.IsSuccess.Should().BeFalse("manifest.jsonの退避コピーは壊れているため、リビジョン全体としては成功と報告してはならない");
        restored.Issues.Should().Contain(i => i.Code == ErrorCode.E216 && i.Path == "manifest.json",
            "manifest.jsonについてはE216（旧レイアウトの退避データがメタデータで上書き済み）で失敗するはず");

        System.Text.Encoding.UTF8.GetString(harness.ReadProjectBytes("manifest.json")).Should().Be(currentManifestContent,
            "検出した場合は書き込みを行わないため、利用者のmanifest.jsonは一切変化しないはず（最重要）");

        // 衝突と無関係なcontent.jsは正しく復元されているはず（このファイルだけの失敗として扱い、
        // 他のファイルの復元は継続する設計）。
        System.Text.Encoding.UTF8.GetString(harness.ReadProjectBytes("content.js")).Should().Be("以前のcontent.js",
            "manifest.jsonの検出とは独立して、他のファイルは正しく復元されるはず");
    }

    [Fact(DisplayName = "旧レイアウトでmanifest.jsonのパスに来ていても、中身がメタデータの形をしていなければ正常に復元する（誤検出しない）")]
    public async Task 旧レイアウトでも中身がメタデータでなければ正常に復元する()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("manifest.json", "変更後の内容");

        var revisionDir = harness.Paths.GetRevisionDirectory(harness.ProjectId, 1, DateTimeOffset.Now);
        Directory.CreateDirectory(revisionDir);
        // 退避コピーとしてはJSONだが、リビジョンのメタデータのキー（revision/projectId等）を
        // 一切持たない、利用者自身のmanifest.json相当の内容。これはStoreAsync自体は成功したが
        // その後の確定書き込みが走らなかった場合などに相当し、内容としては壊れていない。
        var backupContent = "{\n  \"manifest_version\": 3,\n  \"name\": \"サンプル拡張\"\n}\n";
        File.WriteAllText(Path.Combine(revisionDir, "manifest.json"), backupContent);

        var manifest = new RevisionManifest
        {
            Revision = 1,
            ProjectId = harness.ProjectId,
            AppliedAt = DateTimeOffset.Now,
            Status = RevisionStatus.InProgress,
            Entries = new[] { new RevisionEntry { Path = "manifest.json", Operation = EntryOperation.Modify } },
        };
        var summary = new RevisionSummary { Manifest = manifest, FolderPath = revisionDir, IsRestorable = true };
        var restorer = new RevisionRestorer(harness.Paths);
        var restored = await restorer.RestoreAsync(harness.ProjectId, harness.ProjectRoot, summary, force: true);

        restored.IsSuccess.Should().BeTrue(string.Join(",", restored.Issues.Select(i => i.ToDisplayText())));
        System.Text.Encoding.UTF8.GetString(harness.ReadProjectBytes("manifest.json")).Should().Be(backupContent);
    }
}
