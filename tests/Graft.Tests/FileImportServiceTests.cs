using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// エクスプローラへの既存ファイル取り込み（依頼「エクスプローラへ既存のファイルを取り込む手段」）の
/// コピー実処理（<see cref="FileImportService"/>）の単体テスト。UIダイアログを伴う衝突確認・
/// 別名採番は<see cref="ExplorerViewModel.BuildImportPlanAsync"/>側の責務のため、ここでは
/// 「計画（<see cref="FileImportPlanItem"/>）どおりに安全・確実にコピーできるか」を検証する。
/// </summary>
public class FileImportServiceTests
{
    [Fact(DisplayName = "ファイルはコピーされ、コピー元は残ったままになる（移動ではない）")]
    public async Task ファイルはコピーされ元は残る()
    {
        using var source = new TempWorkspace();
        using var project = new TempWorkspace();
        var sourceFile = source.WriteText("photo.png", "画像データの代わり");

        var service = new FileImportService();
        var dest = Path.Combine(project.RootPath, "photo.png");
        var items = new[]
        {
            new FileImportPlanItem
            {
                SourceFullPath = sourceFile,
                DestinationFullPath = dest,
                DestinationRelativePath = "photo.png",
                IsDirectory = false,
                Overwrite = false,
            },
        };

        var outcomes = await service.ImportAsync(items, progress: null, CancellationToken.None);

        outcomes.Should().ContainSingle().Which.IsSuccess.Should().BeTrue();
        File.Exists(sourceFile).Should().BeTrue("依頼の必須要件: 常にコピーであり、元のファイルが消えてはならない");
        File.Exists(dest).Should().BeTrue();
        File.ReadAllText(dest).Should().Be("画像データの代わり");
    }

    [Fact(DisplayName = "フォルダは再帰的にコピーされ、空フォルダも含めて複製される")]
    public async Task フォルダは再帰的にコピーされる()
    {
        using var source = new TempWorkspace();
        using var project = new TempWorkspace();
        source.WriteText("assets/a.txt", "A");
        source.WriteText("assets/nested/b.txt", "B");
        source.CreateDirectory("assets/empty");
        var sourceDir = source.Combine("assets");

        var service = new FileImportService();
        var destDir = Path.Combine(project.RootPath, "assets");
        var items = new[]
        {
            new FileImportPlanItem
            {
                SourceFullPath = sourceDir,
                DestinationFullPath = destDir,
                DestinationRelativePath = "assets",
                IsDirectory = true,
                Overwrite = false,
            },
        };

        var outcomes = await service.ImportAsync(items, progress: null, CancellationToken.None);

        outcomes.Should().ContainSingle().Which.IsSuccess.Should().BeTrue();
        File.Exists(Path.Combine(destDir, "a.txt")).Should().BeTrue();
        File.Exists(Path.Combine(destDir, "nested", "b.txt")).Should().BeTrue();
        Directory.Exists(Path.Combine(destDir, "empty")).Should().BeTrue("空フォルダも含めて再帰的に複製されるべき");
        // 元フォルダも消えていないこと（コピーであって移動ではない）。
        File.Exists(Path.Combine(sourceDir, "a.txt")).Should().BeTrue();
    }

    [Fact(DisplayName = "Overwrite=falseで既存ファイルに衝突した場合はその項目だけ失敗し、他の項目は続行される")]
    public async Task 上書きしない場合は衝突した項目だけ失敗する()
    {
        using var source = new TempWorkspace();
        using var project = new TempWorkspace();
        var sourceFile1 = source.WriteText("a.txt", "新しいA");
        var sourceFile2 = source.WriteText("b.txt", "新しいB");
        project.WriteText("a.txt", "既存のA"); // 衝突させる

        var service = new FileImportService();
        var items = new[]
        {
            new FileImportPlanItem
            {
                SourceFullPath = sourceFile1,
                DestinationFullPath = project.Combine("a.txt"),
                DestinationRelativePath = "a.txt",
                IsDirectory = false,
                Overwrite = false,
            },
            new FileImportPlanItem
            {
                SourceFullPath = sourceFile2,
                DestinationFullPath = project.Combine("b.txt"),
                DestinationRelativePath = "b.txt",
                IsDirectory = false,
                Overwrite = false,
            },
        };

        var outcomes = await service.ImportAsync(items, progress: null, CancellationToken.None);

        outcomes.Should().HaveCount(2);
        outcomes[0].IsSuccess.Should().BeFalse("既存のa.txtと衝突し、Overwrite=falseのため失敗するはず");
        outcomes[0].Issue.Should().NotBeNull();
        outcomes[1].IsSuccess.Should().BeTrue("1件の失敗が他の項目の処理を止めてはならない");
        File.ReadAllText(project.Combine("a.txt")).Should().Be("既存のA", "黙って上書きしてはならない");
        File.ReadAllText(project.Combine("b.txt")).Should().Be("新しいB");
    }

    [Fact(DisplayName = "Overwrite=trueなら既存の同名ファイルを上書きする")]
    public async Task 上書き指定なら既存ファイルを置き換える()
    {
        using var source = new TempWorkspace();
        using var project = new TempWorkspace();
        var sourceFile = source.WriteText("a.txt", "新しい内容");
        project.WriteText("a.txt", "古い内容");

        var service = new FileImportService();
        var items = new[]
        {
            new FileImportPlanItem
            {
                SourceFullPath = sourceFile,
                DestinationFullPath = project.Combine("a.txt"),
                DestinationRelativePath = "a.txt",
                IsDirectory = false,
                Overwrite = true,
            },
        };

        var outcomes = await service.ImportAsync(items, progress: null, CancellationToken.None);

        outcomes.Should().ContainSingle().Which.IsSuccess.Should().BeTrue();
        File.ReadAllText(project.Combine("a.txt")).Should().Be("新しい内容");
    }

    [Fact(DisplayName = "コピー元が存在しない場合はその項目だけ失敗として扱われ、アプリは落ちない")]
    public async Task コピー元が消えていた場合は失敗として扱われる()
    {
        using var source = new TempWorkspace();
        using var project = new TempWorkspace();
        var missingSource = source.Combine("gone.txt"); // 作らない

        var service = new FileImportService();
        var items = new[]
        {
            new FileImportPlanItem
            {
                SourceFullPath = missingSource,
                DestinationFullPath = project.Combine("gone.txt"),
                DestinationRelativePath = "gone.txt",
                IsDirectory = false,
                Overwrite = false,
            },
        };

        var outcomes = await service.ImportAsync(items, progress: null, CancellationToken.None);

        outcomes.Should().ContainSingle();
        outcomes[0].IsSuccess.Should().BeFalse();
        outcomes[0].Issue.Should().NotBeNull();
        outcomes[0].Issue!.Code.Should().Be(ErrorCode.E402);
    }

    [Fact(DisplayName = "進捗はコピー済みファイル数／総ファイル数で報告される")]
    public async Task 進捗が報告される()
    {
        using var source = new TempWorkspace();
        using var project = new TempWorkspace();
        source.WriteText("a.txt", "A");
        source.WriteText("b.txt", "B");

        var service = new FileImportService();
        var items = new[]
        {
            new FileImportPlanItem
            {
                SourceFullPath = source.Combine("a.txt"),
                DestinationFullPath = project.Combine("a.txt"),
                DestinationRelativePath = "a.txt",
                IsDirectory = false,
                Overwrite = false,
            },
            new FileImportPlanItem
            {
                SourceFullPath = source.Combine("b.txt"),
                DestinationFullPath = project.Combine("b.txt"),
                DestinationRelativePath = "b.txt",
                IsDirectory = false,
                Overwrite = false,
            },
        };

        var reports = new List<FileImportProgress>();
        var progress = new SynchronousProgress<FileImportProgress>(reports.Add);

        var outcomes = await service.ImportAsync(items, progress, CancellationToken.None);

        outcomes.Should().OnlyContain(o => o.IsSuccess);
        reports.Should().HaveCount(2);
        reports[0].TotalFiles.Should().Be(2);
        reports[^1].CompletedFiles.Should().Be(2);
    }

    [Fact(DisplayName = "中止（キャンセル）した場合、未着手の項目はWasCancelledとして報告され、既にコピー済みの項目は残る")]
    public async Task キャンセルすると未着手項目はキャンセル扱いになる()
    {
        using var source = new TempWorkspace();
        using var project = new TempWorkspace();
        source.WriteText("a.txt", "A");
        source.WriteText("b.txt", "B");

        var service = new FileImportService();
        var cts = new CancellationTokenSource();
        var items = new[]
        {
            new FileImportPlanItem
            {
                SourceFullPath = source.Combine("a.txt"),
                DestinationFullPath = project.Combine("a.txt"),
                DestinationRelativePath = "a.txt",
                IsDirectory = false,
                Overwrite = false,
            },
            new FileImportPlanItem
            {
                SourceFullPath = source.Combine("b.txt"),
                DestinationFullPath = project.Combine("b.txt"),
                DestinationRelativePath = "b.txt",
                IsDirectory = false,
                Overwrite = false,
            },
        };

        // 1件目のコピー完了を合図にキャンセルする（Progress<T>相当だが、テストでは同期コールバックで
        // 即座にキャンセルしたいため、SynchronousProgress<T>を使う）。
        var progress = new SynchronousProgress<FileImportProgress>(p =>
        {
            if (p.CompletedFiles >= 1) cts.Cancel();
        });

        var outcomes = await service.ImportAsync(items, progress, cts.Token);

        outcomes.Should().HaveCount(2);
        outcomes[0].IsSuccess.Should().BeTrue("1件目はキャンセル前にコピーが完了しているはず");
        outcomes[1].WasCancelled.Should().BeTrue("2件目はキャンセル後のため未着手のはず");
        File.Exists(project.Combine("a.txt")).Should().BeTrue("中止しても、既にコピー済みのファイルはロールバックされない仕様");
        File.Exists(project.Combine("b.txt")).Should().BeFalse();
    }

    [Fact(DisplayName = "ResolveDestinationはプロジェクト外へのパスをE201として拒否する")]
    public void ResolveDestinationはルート外を拒否する()
    {
        using var projectWs = new TempWorkspace();
        var project = new Project { Root = projectWs.RootPath };

        var result = FileImportService.ResolveDestination(project, string.Empty, "../outside.txt", PathGuardOptions.Default);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E201);
    }

    [Fact(DisplayName = "ResolveDestinationは拡張子ホワイトリストを適用しない")]
    public void ResolveDestinationは拡張子ホワイトリストを適用しない()
    {
        using var projectWs = new TempWorkspace();
        var project = new Project { Root = projectWs.RootPath };

        var result = FileImportService.ResolveDestination(project, "assets", "photo.png", PathGuardOptions.Default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(Path.Combine(projectWs.RootPath, "assets", "photo.png"));
    }

    [Fact(DisplayName = "DestinationExistsはファイル・フォルダのどちらも検出する")]
    public void DestinationExistsはファイルもフォルダも検出する()
    {
        using var ws = new TempWorkspace();
        var file = ws.WriteText("a.txt", "A");
        var dir = ws.CreateDirectory("dir");
        var missing = ws.Combine("missing.txt");

        FileImportService.DestinationExists(file).Should().BeTrue();
        FileImportService.DestinationExists(dir).Should().BeTrue();
        FileImportService.DestinationExists(missing).Should().BeFalse();
    }

    /// <summary>
    /// <see cref="Progress{T}"/>はコールバックをSynchronizationContext経由で非同期に
    /// マーシャリングするため、テスト（同期的な検証・キャンセル合図）には向かない。
    /// 単体テストではコールバックを即座に同期実行する簡易実装を使う。
    /// </summary>
    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SynchronousProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
