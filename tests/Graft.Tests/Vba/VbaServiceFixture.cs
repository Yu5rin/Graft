using System.IO;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Tests.TestSupport;
using Graft.UiTests.Vba;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaSyncService"/> を <see cref="FakeVbaBridge"/> で動かす回帰テスト（<c>Vba*FixTests</c>）の共通の下拵え。
/// 一時フォルダにデータ保存先とミラーを作り、ブックを 1 つ開いてミラーを作るところまでを
/// <see cref="CreatedProjectAsync"/> が行う。ごみ箱は実際には消さずに記録するだけの偽物。
/// </summary>
public abstract class VbaServiceFixture : IDisposable
{
    protected const string Book = @"C:\Work\計画.xlsm";

    protected readonly TempWorkspace Ws = new();
    protected readonly AppPaths Paths;
    protected readonly FakeVbaBridge Bridge = new();
    protected readonly VbaSyncStateStore States;
    protected readonly RecordingTrash Trash = new();
    protected readonly VbaSyncService Service;
    protected readonly DateTimeOffset Now = new(2026, 11, 21, 10, 0, 0, TimeSpan.FromHours(9));

    protected VbaServiceFixture()
    {
        Paths = new AppPaths(Ws.CreateDirectory("app"));
        States = new VbaSyncStateStore(Paths);
        Service = new VbaSyncService(Bridge, States, Paths, Trash, () => Now);
    }

    public void Dispose() => Ws.Dispose();

    /// <summary>ごみ箱の偽物。送られたパスを覚え、実際にフォルダを消す（本物のごみ箱と同じく、元の場所からは無くなる）。</summary>
    protected sealed class RecordingTrash : ITrashService
    {
        public List<string> Sent { get; } = new();

        public bool IsSupported => true;

        public string? UnsupportedReason => null;

        public bool Send(string path)
        {
            Sent.Add(path);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            return true;
        }
    }

    protected string MirrorRoot => Path.Combine(Paths.VbaMirrorsDirectory, VbaMirrorFolderName.Build(Book));

    protected Project NewProject(VbaLink? link = null)
        => new() { Id = ProjectStore.CreateId(MirrorRoot), Name = "計画", Root = MirrorRoot, Vba = link ?? new VbaLink { WorkbookFullName = Book } };

    protected string MirrorFile(string relative) => Path.Combine(MirrorRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    protected FakeVbaBridge.Module Excel(string name) => Bridge.Get(Book)!.Find(name)!;

    protected async Task<Project> CreatedProjectAsync(params FakeVbaBridge.Module[] modules)
    {
        Bridge.AddWorkbook(Book, modules);
        var created = await Service.CreateMirrorAsync(Book, MirrorRoot, rebuild: false);
        created.IsSuccess.Should().BeTrue(string.Join(" / ", created.Issues.Select(i => i.ToDisplayText())));
        return NewProject();
    }

    protected async Task<VbaSyncState> StateAsync()
        => (await States.LoadAsync(NewProject().Id, Book)).Value;

    /// <summary>控えのファイル名の一覧（<c>-auto</c> などの種別つきの名前のまま）。</summary>
    protected IReadOnlyList<string> RescueFileNames()
    {
        var directory = Paths.GetVbaRescueDirectory(NewProject().Id);
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList()!
            : Array.Empty<string>();
    }

    /// <summary>
    /// 控えの本文の一覧。反映前の自動控え（<c>-auto</c>）は通常の反映のたびに残るので、従来の控え（衝突の上書き前・書き戻し失敗）
    /// だけを見たいテストのために、<paramref name="includeAuto"/> が false なら自動控えを除く。
    /// </summary>
    protected IReadOnlyList<string> RescueTexts(bool includeAuto = false)
    {
        var directory = Paths.GetVbaRescueDirectory(NewProject().Id);
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory)
                .Where(f => includeAuto || VbaRescueNaming.Parse(Path.GetFileName(f))?.Kind != VbaRescueKind.Auto)
                .Select(File.ReadAllText).ToList()
            : Array.Empty<string>();
    }
}
