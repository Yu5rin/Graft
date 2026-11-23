using System.IO;
using System.IO.Compression;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Graft.UiTests.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// リボンの書き換え（仕様書_ブックの情報と依頼の観点 16.5。段階 F3: Excel で開いていないブック）を、一時フォルダのブックで確かめる。
/// 失敗の注入（控えを書けない・ディスクがいっぱい・置き換えの失敗・読み直しの不一致・戻せない・途中で落ちる）では、
/// <b>ブックのファイルが元のまま（または控えと同じ）</b>であることを、バイト列で比べて固定する。
/// <para>
/// このテストのブックは、<see cref="VbaServiceFixture"/> の Fake のパス（<c>C:\Work\計画.xlsm</c>）ではなく、一時フォルダの実ファイル。
/// ブックを Excel の代役（<see cref="FakeVbaBridge"/>）で開いてミラーを作り、取り込んだあと、代役から閉じて「開いていないブック」にする。
/// </para>
/// </summary>
public sealed partial class VbaRibbonRewriteServiceTests : IDisposable
{
    private const string Ribbon14 = "CustomUI/customUI14.xml";
    private const string Ribbon12 = "CustomUI/customUI.xml";

    private static readonly string XmlA = RibbonPackageBuilder.SampleRibbon;
    private static readonly string XmlB = RibbonPackageBuilder.SampleRibbon.Replace("label=\"業務\"", "label=\"業務B\"", StringComparison.Ordinal);

    private static readonly string XmlOld =
        "<customUI xmlns=\"http://schemas.microsoft.com/office/2006/01/customui\"><ribbon><tabs><tab id=\"tOld\" label=\"旧\"/></tabs></ribbon></customUI>";

    private readonly TempWorkspace _ws = new();
    private readonly AppPaths _paths;
    private readonly FakeVbaBridge _bridge = new();
    private readonly VbaSyncStateStore _states;
    private readonly DateTimeOffset _now = new(2026, 11, 23, 10, 0, 0, TimeSpan.FromHours(9));
    private readonly string _book;
    private readonly string _mirror;
    private VbaSyncService _service;

    public VbaRibbonRewriteServiceTests()
    {
        _paths = new AppPaths(_ws.CreateDirectory("app"));
        _states = new VbaSyncStateStore(_paths);
        _book = Path.Combine(_ws.CreateDirectory("books"), "計画.xlsm");
        _mirror = Path.Combine(_paths.VbaMirrorsDirectory, VbaMirrorFolderName.Build(_book));
        _service = NewService();
    }

    public void Dispose() => _ws.Dispose();

    private VbaSyncService NewService()
    {
        var service = new VbaSyncService(_bridge, _states, _paths, null, () => _now);
        service.Ribbon.PackageReader = path => VbaRibbonPackageReader.Read(path);
        // 排他で開く待ちは、テストでは短くする（本番は 10 秒）。
        service.Ribbon.Io.ExclusiveTimeout = TimeSpan.FromMilliseconds(150);
        service.Ribbon.Io.ExclusiveTimeoutAfterExcelClose = TimeSpan.FromMilliseconds(150);
        service.Ribbon.Io.ExclusiveInterval = TimeSpan.FromMilliseconds(20);
        return service;
    }

    private Project Project => new()
    {
        Id = ProjectStore.CreateId(_mirror), Name = "計画", Root = _mirror, Vba = new VbaLink { WorkbookFullName = _book },
    };

    private VbaRibbonSyncService Ribbon => _service.Ribbon;

    private string MirrorFile(string relative) => Path.Combine(_mirror, relative.Replace('/', Path.DirectorySeparatorChar));

    private string BackupDir => _paths.GetVbaWorkbookBackupDirectory(Project.Id);

    private string JobFile => _paths.GetVbaRibbonJobFilePath(Project.Id);

    private string[] BackupFiles() => Directory.Exists(BackupDir) ? Directory.GetFiles(BackupDir) : Array.Empty<string>();

    private string[] TempFilesNextToBook() => Directory.GetFiles(Path.GetDirectoryName(_book)!, "~graft-*");

    /// <summary>ブックを作ってミラーを作り、ブックのリボンを取り込んでから、Excel の代役から閉じる（開いていないブック）。</summary>
    private async Task<byte[]> ArrangeAsync(string? book14 = null, string? bookOld = null)
    {
        File.WriteAllBytes(_book, RibbonPackageBuilder.ExcelWorkbook(book14, bookOld));
        _bridge.AddWorkbook(_book, FakeVbaBridge.Std("Module1", "Public Sub OnRun(control As IRibbonControl)\r\nEnd Sub"));
        var created = await _service.CreateMirrorAsync(_book, _mirror, rebuild: false);
        created.IsSuccess.Should().BeTrue(string.Join(" / ", created.Issues.Select(i => i.ToDisplayText())));
        var pulled = await _service.PullAsync(Project);
        pulled.IsSuccess.Should().BeTrue(string.Join(" / ", pulled.Issues.Select(i => i.ToDisplayText())));
        _bridge.CloseWorkbook(_book);
        return File.ReadAllBytes(_book);
    }

    private void WriteMirror(string relative, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MirrorFile(relative))!);
        File.WriteAllText(MirrorFile(relative), text);
    }

    private async Task<VbaRibbonRewritePlan> PlanAsync()
    {
        var prepared = await Ribbon.PrepareRewriteAsync(Project);
        prepared.IsSuccess.Should().BeTrue(string.Join(" / ", prepared.Issues.Select(i => i.ToDisplayText())));
        prepared.Value.Kind.Should().Be(VbaRibbonRewritePreparationKind.Ready);
        return prepared.Value.Plan!;
    }

    private static string? PartText(byte[] zip, string name)
    {
        var entry = RibbonPackageBuilder.ReadEntries(zip).FirstOrDefault(e => e.Name == name);
        return entry.Data is null ? null : new System.Text.UTF8Encoding(false).GetString(entry.Data);
    }

    private void BookShouldBeUnchanged(byte[] original, string because)
        => File.ReadAllBytes(_book).Should().Equal(original, because);

    private void NothingLeftBehind()
    {
        File.Exists(JobFile).Should().BeFalse("作業の記録は終わったら消す");
        TempFilesNextToBook().Should().BeEmpty("一時ファイルはどの段階で止まっても消す");
    }

    // ------------------------------------------------------------------
    // 成功
    // ------------------------------------------------------------------

    [Fact(DisplayName = "開いていないブックの差し替え: 控え（元と同じバイト列）→ 書き換え → 読み直して照合 → 基準の更新。ほかの部品は 1 バイトも変わらない")]
    public async Task 差し替えの全段階()
    {
        var original = await ArrangeAsync(book14: XmlA);
        WriteMirror(Ribbon14, XmlB);
        (await _service.GetLocalStatusAsync(Project)).RibbonUnpushed.Should().BeTrue();

        var plan = await PlanAsync();
        plan.Targets.Should().ContainSingle(t => t.MirrorPath == Ribbon14 && !t.IsNewPart && t.BookXml == XmlA);
        plan.BackupDirectory.Should().Be(BackupDir);

        var writingSeen = false;
        Ribbon.Io.Replace = (temp, dest) =>
        {
            writingSeen = _service.IsWritingToExcel;
            VbaRibbonPackageWriter.ReplaceFile(temp, dest);
        };
        var result = await Ribbon.RewriteAsync(Project, plan);

        result.IsSuccess.Should().BeTrue(string.Join(" / ", result.Issues.Select(i => i.ToDisplayText())));
        result.Issues.Should().BeEmpty();
        writingSeen.Should().BeTrue("書き換えの間は「書いている最中」の旗を立てる（ウィンドウを閉じる確認などを待たせる）");
        _service.IsWritingToExcel.Should().BeFalse();

        var after = File.ReadAllBytes(_book);
        PartText(after, "customUI/customUI14.xml").Should().Be(XmlB);
        var before = RibbonPackageBuilder.ReadEntries(original);
        var now = RibbonPackageBuilder.ReadEntries(after);
        now.Select(e => e.Name).Should().Equal(before.Select(e => e.Name), "項目の名前と順序を保つ");
        foreach (var (name, data) in before.Where(e => e.Name != "customUI/customUI14.xml"))
        {
            now.First(e => e.Name == name).Data.Should().Equal(data, $"触っていない部品（{name}）は 1 バイトも変えない");
        }

        BackupFiles().Should().ContainSingle();
        File.ReadAllBytes(BackupFiles()[0]).Should().Equal(original, "控えは書き換える前のブックそのもの");
        Path.GetFileName(BackupFiles()[0]).Should().Be("計画-20261123-100000-ribbon.xlsm");
        result.Value.BackupPath.Should().Be(BackupFiles()[0]);

        var state = (await _states.LoadAsync(Project.Id, _book)).Value;
        var record = state.Ribbon![Ribbon14];
        record.MirrorHash.Should().Be(VbaTextNormalizer.Hash(XmlB)).And.Be(record.FileHash);
        record.PartName.Should().Be("customUI/customUI14.xml");
        (await _service.GetLocalStatusAsync(Project)).RibbonUnpushed.Should().BeFalse("反映したので「リボン未反映」が消える");
        NothingLeftBehind();

        var again = await Ribbon.PrepareRewriteAsync(Project);
        again.Value.Kind.Should().Be(VbaRibbonRewritePreparationKind.NothingToDo, "反映済みなら、次は何もしない");
    }

    [Fact(DisplayName = "customUI の無いブックへの新規の追加: 関係と部品を足し、取込で読める。確認の手順は「新しく足す」")]
    public async Task 新規の追加()
    {
        await ArrangeAsync();
        Directory.Exists(MirrorFile("CustomUI")).Should().BeFalse();
        WriteMirror(Ribbon14, XmlA);

        var plan = await PlanAsync();
        plan.AddsNewPart.Should().BeTrue();
        plan.Targets.Single().BookXml.Should().BeNull();
        VbaRibbonRewriteText.Steps(plan).Should().Equal(
            "1. 書き換える前のブックを、控えとして丸ごと残す", "2. リボンの XML を新しく足す（CustomUI/customUI14.xml）");

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.IsSuccess.Should().BeTrue(string.Join(" / ", result.Issues.Select(i => i.ToDisplayText())));
        result.Value.AddedNew.Should().BeTrue();
        var read = VbaRibbonPackageReader.Read(_book);
        read.Status.Should().Be(VbaRibbonReadStatus.Ok);
        read.Parts.Should().ContainSingle(p => p.PartName == "customUI/customUI14.xml" && p.Xml == XmlA);
        NothingLeftBehind();
    }

    [Fact(DisplayName = "新旧の両方を 1 回の書き換えにまとめる（控えは 1 つ）")]
    public async Task 両形式を一度に()
    {
        await ArrangeAsync(book14: XmlA, bookOld: XmlOld);
        WriteMirror(Ribbon14, XmlB);
        WriteMirror(Ribbon12, XmlOld.Replace("label=\"旧\"", "label=\"旧2\"", StringComparison.Ordinal));

        var plan = await PlanAsync();
        plan.Targets.Select(t => t.MirrorPath).Should().Equal(Ribbon14, Ribbon12);
        var result = await Ribbon.RewriteAsync(Project, plan);

        result.IsSuccess.Should().BeTrue();
        BackupFiles().Should().ContainSingle("閉じ開き・控えは 1 回にまとめる");
        PartText(File.ReadAllBytes(_book), "customUI/customUI.xml").Should().Contain("旧2");
    }

    [Fact(DisplayName = "反映するものが無い・衝突だけ: 何もせず NothingToDo（衝突は E812 の知らせ）")]
    public async Task 反映するものが無い()
    {
        var original = await ArrangeAsync(book14: XmlA);
        var nothing = await Ribbon.PrepareRewriteAsync(Project);
        nothing.Value.Kind.Should().Be(VbaRibbonRewritePreparationKind.NothingToDo);
        nothing.Value.Notes.Should().BeEmpty();

        // ブック側も変わっている（衝突）。F3 では衝突は書かない（Graft 側で上書きは F5 の衝突ダイアログ）。
        WriteMirror(Ribbon14, XmlB);
        File.WriteAllBytes(_book, RibbonPackageBuilder.ExcelWorkbook(XmlA.Replace("業務", "業務Z", StringComparison.Ordinal)));
        var conflict = await Ribbon.PrepareRewriteAsync(Project);
        conflict.Value.Kind.Should().Be(VbaRibbonRewritePreparationKind.NothingToDo);
        conflict.Value.Notes.Should().ContainSingle(i => i.Code == ErrorCode.E812 && i.Path == Ribbon14);
        BackupFiles().Should().BeEmpty();
        _ = original;
    }

    [Fact(DisplayName = "未保存のリボンのファイルの保存を断られたら中止（何も変えない）")]
    public async Task 未保存を断られたら中止()
    {
        var original = await ArrangeAsync(book14: XmlA);
        WriteMirror(Ribbon14, XmlB);
        IReadOnlyList<string>? asked = null;
        Ribbon.BeforeRewriteAsync = paths =>
        {
            asked = paths;
            return Task.FromResult(new VbaPullSaveOutcome(paths, false));
        };

        var prepared = await Ribbon.PrepareRewriteAsync(Project);

        prepared.Value.Kind.Should().Be(VbaRibbonRewritePreparationKind.Declined);
        asked.Should().ContainSingle(p => p.EndsWith("customUI14.xml", StringComparison.Ordinal));
        BookShouldBeUnchanged(original, "断られたら何も変えない");
    }

    // ------------------------------------------------------------------
    // 書く前に止まる（何も変えない）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "XML のエラー: E818 で止め、控えも作業の記録も作らない")]
    public async Task XMLのエラーは止める()
    {
        var original = await ArrangeAsync(book14: XmlA);
        WriteMirror(Ribbon14, XmlA.Replace("<button id=\"btnRun\"", "<buton id=\"btnRun\"", StringComparison.Ordinal)
            .Replace("onAction=\"OnRun\" getEnabled=\"GetEnabled\"/>", "onAction=\"OnRun\" getEnabled=\"GetEnabled\"></buton>", StringComparison.Ordinal));

        var prepared = await Ribbon.PrepareRewriteAsync(Project);

        prepared.IsSuccess.Should().BeFalse();
        prepared.Issues.Should().OnlyContain(i => i.Code == ErrorCode.E818 && i.Severity == Severity.Error && i.Path == Ribbon14);
        prepared.Issues.Should().Contain(i => i.Detail!.Contains("buton", StringComparison.Ordinal));
        BookShouldBeUnchanged(original, "検査のエラーでは書かない");
        BackupFiles().Should().BeEmpty();
        File.Exists(JobFile).Should().BeFalse();
    }

    [Fact(DisplayName = "検査の警告は止めず、計画に載る（確認ダイアログに出す）")]
    public async Task 警告は計画に載る()
    {
        await ArrangeAsync(book14: XmlA);
        WriteMirror(Ribbon14, XmlB.Replace("onAction=\"OnPick\"", "onAction=\"OnPickMissing\"", StringComparison.Ordinal));

        var plan = await PlanAsync();

        plan.Warnings.Should().Contain(w => w.Finding.Check == 12 && w.Finding.Message.Contains("OnPickMissing", StringComparison.Ordinal));
        plan.Warnings.Should().OnlyContain(w => w.Finding.Severity == Severity.Warning);
    }

    [Fact(DisplayName = "確認のあいだに Excel で開かれた: 確認した経路（開いていない）と違うので、書き換えの直前に止める（E821）")]
    public async Task 確認のあいだに開かれた()
    {
        var original = await ArrangeAsync(book14: XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        _bridge.AddWorkbook(_book);

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E821 && i.Detail!.Contains("開かれた", StringComparison.Ordinal));
        BookShouldBeUnchanged(original, "経路が変わったら書かない");
        _bridge.Calls.Should().NotContain(c => c.StartsWith("SaveClose", StringComparison.Ordinal), "閉じない");
        BackupFiles().Should().BeEmpty();
        NothingLeftBehind();
    }

    [Fact(DisplayName = "Excel で開いているアドイン（.xlam）: アドインを閉じてからの案内で止める（16.16-2）")]
    public async Task 開いているアドインは止める()
    {
        var addin = Path.Combine(Path.GetDirectoryName(_book)!, "道具.xlam");
        // customUI の無いアドインへ足す（記録なし・ミラーにだけある＝反映の対象）。
        File.WriteAllBytes(addin, RibbonPackageBuilder.ExcelWorkbook());
        var project = Project with { Vba = new VbaLink { WorkbookFullName = addin } };
        Directory.CreateDirectory(MirrorFile("CustomUI"));
        File.WriteAllText(MirrorFile(Ribbon14), XmlB);
        _bridge.AddWorkbook(addin);

        var prepared = await Ribbon.PrepareRewriteAsync(project);

        prepared.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E819 && i.Detail!.Contains("アドイン", StringComparison.Ordinal));
        File.ReadAllBytes(addin).Should().Equal(RibbonPackageBuilder.ExcelWorkbook());
    }

    [Fact(DisplayName = "電子署名のあるブック: E819（書き換えると署名が無効になる）")]
    public async Task 電子署名は止める()
    {
        await ArrangeAsync(book14: XmlA);
        var entries = RibbonPackageBuilder.ReadEntries(File.ReadAllBytes(_book));
        entries.Add(("_xmlsignatures/origin.sigs", Array.Empty<byte>()));
        File.WriteAllBytes(_book, RibbonPackageBuilder.Zip(entries.ToArray()));
        var signed = File.ReadAllBytes(_book);
        WriteMirror(Ribbon14, XmlB);

        var prepared = await Ribbon.PrepareRewriteAsync(Project);

        prepared.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E819 && i.Detail!.Contains("電子署名", StringComparison.Ordinal));
        BookShouldBeUnchanged(signed, "署名つきは書かない");
    }

    [Fact(DisplayName = "空き容量の予行: ブックのフォルダが足りなければ E821、控えの保存先が足りなければ E816（どちらも何も書かない）")]
    public async Task 空き容量の予行()
    {
        var original = await ArrangeAsync(book14: XmlA);
        WriteMirror(Ribbon14, XmlB);
        var bookFolder = Path.GetDirectoryName(Path.GetFullPath(_book))!;

        Ribbon.Io.FreeSpace = dir => dir == bookFolder ? 10 : long.MaxValue;
        var here = await Ribbon.PrepareRewriteAsync(Project);
        here.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E821 && i.Detail!.Contains("空き容量", StringComparison.Ordinal));

        Ribbon.Io.FreeSpace = dir => dir == BackupDir ? 10 : long.MaxValue;
        var backup = await Ribbon.PrepareRewriteAsync(Project);
        backup.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E816 && i.Detail!.Contains("控えの保存先の空き容量", StringComparison.Ordinal));

        BookShouldBeUnchanged(original, "予行で止まれば何も書かない");
        BackupFiles().Should().BeEmpty();
    }

    [Fact(DisplayName = "確認のあいだにミラー・ブックが変わった: 書き換えずに止める（確認したのと違う内容を黙って書かない）")]
    public async Task 確認のあいだの変化()
    {
        var original = await ArrangeAsync(book14: XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();

        WriteMirror(Ribbon14, XmlB.Replace("業務B", "業務C", StringComparison.Ordinal));
        var mirrorChanged = await Ribbon.RewriteAsync(Project, plan);
        mirrorChanged.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E821 && i.Detail!.Contains("確認のあいだに", StringComparison.Ordinal));
        BookShouldBeUnchanged(original, "ミラーが変わったら書かない");

        WriteMirror(Ribbon14, XmlB);
        File.SetLastWriteTimeUtc(_book, DateTime.UtcNow.AddMinutes(5));
        var bookChanged = await Ribbon.RewriteAsync(Project, plan);
        bookChanged.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E821 && i.Detail!.Contains("保存された見込み", StringComparison.Ordinal));
        File.ReadAllBytes(_book).Should().Equal(original);
        BackupFiles().Should().BeEmpty();
        NothingLeftBehind();
    }

    // ------------------------------------------------------------------
    // 失敗の注入（書き換えの途中）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "使用中（排他で開けない）: 待ってから E821。所有者ファイル ~$ があれば添える。何も変えない")]
    public async Task 使用中()
    {
        var original = await ArrangeAsync(book14: XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_book)!, "~$計画.xlsm"), "owner");
        var attempts = 0;
        Ribbon.Io.OpenExclusive = _ =>
        {
            attempts++;
            throw new IOException("The process cannot access the file because it is being used by another process.");
        };

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E821
                                                  && i.Detail!.Contains("ほかのプログラムが使っている", StringComparison.Ordinal)
                                                  && i.Detail.Contains("~$計画.xlsm", StringComparison.Ordinal));
        attempts.Should().BeGreaterThan(1, "閉じた直後のハンドルの残りを待って、何度か開き直す");
        BookShouldBeUnchanged(original, "排他で開けなければ何もしない");
        BackupFiles().Should().BeEmpty();
        File.Exists(JobFile).Should().BeFalse();
    }

    [Fact(DisplayName = "控えを書けない: E816 で止め、書き換えない（作りかけの控えも残さない）")]
    public async Task 控えを書けない()
    {
        var original = await ArrangeAsync(book14: XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        var defaultCreate = Ribbon.Io.CreateNew;
        Ribbon.Io.CreateNew = path => path.StartsWith(BackupDir, StringComparison.Ordinal)
            ? throw new UnauthorizedAccessException("Access to the path is denied.")
            : defaultCreate(path);

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E816 && i.Severity == Severity.Error && i.Detail!.Contains("書き換えませんでした", StringComparison.Ordinal));
        BookShouldBeUnchanged(original, "控えを書けなければ書き換えない");
        BackupFiles().Should().BeEmpty();
        NothingLeftBehind();
        (await _service.GetLocalStatusAsync(Project)).RibbonUnpushed.Should().BeTrue("未反映のまま残る");
    }

    [Fact(DisplayName = "ディスクがいっぱい（控えの途中）: 作りかけの控えを消し、E816（「ディスクの空きが足りません」）。ブックは元のまま")]
    public async Task ディスクがいっぱい()
    {
        var original = await ArrangeAsync(book14: XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        var defaultCreate = Ribbon.Io.CreateNew;
        Ribbon.Io.CreateNew = path => path.StartsWith(BackupDir, StringComparison.Ordinal) ? new DiskFullStream(path) : defaultCreate(path);

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E816 && i.Detail!.Contains("ディスクの空きが足りません", StringComparison.Ordinal));
        BookShouldBeUnchanged(original, "控えの途中で止まればブックには触れていない");
        BackupFiles().Should().BeEmpty("作りかけの控えは消す");
        NothingLeftBehind();
    }

    [Fact(DisplayName = "置き換えの失敗: E821。ブックは元のまま、いま作った控えも一時ファイルも消す")]
    public async Task 置き換えの失敗()
    {
        var original = await ArrangeAsync(book14: XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        Ribbon.Io.Replace = (_, _) => throw new IOException("The process cannot access the file.");

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E821 && i.Detail!.Contains("変えていません", StringComparison.Ordinal));
        BookShouldBeUnchanged(original, "File.Replace は成功か失敗のどちらかで、失敗なら元のまま");
        BackupFiles().Should().BeEmpty("何も変えていないのに世代だけが進むのを避ける（16.11）");
        NothingLeftBehind();
        (await _states.LoadAsync(Project.Id, _book)).Value.Ribbon![Ribbon14].MirrorHash.Should().Be(VbaTextNormalizer.Hash(XmlA), "基準は進めない");
    }

    [Fact(DisplayName = "読み直しの不一致: 控えから戻して E821（ブックは元のバイト列）。基準は進めない")]
    public async Task 読み直しの不一致は戻す()
    {
        var original = await ArrangeAsync(book14: XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        var replaced = false;
        Ribbon.Io.Replace = (temp, dest) =>
        {
            VbaRibbonPackageWriter.ReplaceFile(temp, dest);
            replaced = true;
        };
        Ribbon.PackageReader = path =>
        {
            var read = VbaRibbonPackageReader.Read(path);
            // 置き換えたあとの読み直しだけ、期待と違う本文を返す（Excel 以外の何かが書き換えた、という状況の代わり）。
            return replaced && File.ReadAllBytes(path) is var bytes && PartText(bytes, "customUI/customUI14.xml") == XmlB
                ? read with { Parts = read.Parts.Select(p => p with { Xml = p.Xml + "<!-- x -->" }).ToList() }
                : read;
        };

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E821 && i.Detail!.Contains("控えから元の内容に戻しました", StringComparison.Ordinal));
        BookShouldBeUnchanged(original, "控えから戻したので、元のバイト列");
        BackupFiles().Should().ContainSingle("戻したときの控えは残す");
        NothingLeftBehind();
        (await _states.LoadAsync(Project.Id, _book)).Value.Ribbon![Ribbon14].NeedsReview.Should().BeFalse();
    }

    [Fact(DisplayName = "読み直しの不一致で、控えからも戻せない: E823。要確認の印を立て、控えの場所を示す（控えは残る）")]
    public async Task 戻せなければ要確認()
    {
        var original = await ArrangeAsync(book14: XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        var calls = 0;
        Ribbon.Io.Replace = (temp, dest) =>
        {
            calls++;
            if (calls > 1) throw new IOException("復元の置き換えに失敗");
            VbaRibbonPackageWriter.ReplaceFile(temp, dest);
        };
        Ribbon.PackageReader = path =>
        {
            var read = VbaRibbonPackageReader.Read(path);
            return calls > 0 ? read with { Parts = read.Parts.Select(p => p with { Xml = "<customUI/>" }).ToList() } : read;
        };

        var result = await Ribbon.RewriteAsync(Project, plan);

        var issue = result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E823).Subject;
        issue.Detail.Should().Contain(BackupDir);
        BackupFiles().Should().ContainSingle();
        File.ReadAllBytes(BackupFiles()[0]).Should().Equal(original, "控えは元のブックのまま残っている（利用者が手で戻せる）");
        (await _states.LoadAsync(Project.Id, _book)).Value.Ribbon![Ribbon14].NeedsReview.Should().BeTrue("次の同期で衝突として扱い、黙って上書きしない");
        NothingLeftBehind();

        // 要確認のあいだは反映しない（衝突として知らせる）。
        var next = await Ribbon.PrepareRewriteAsync(Project);
        next.Value.Kind.Should().Be(VbaRibbonRewritePreparationKind.NothingToDo);
        next.Value.Notes.Should().Contain(i => i.Code == ErrorCode.E812);
    }

    // ------------------------------------------------------------------
    // 途中で落ちた後の後始末（16.11）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "置き換えのあとで落ちた: 記録が残り、次の起動の後始末で「書き換え済み」と知らせ、記録と一時ファイルを片付ける")]
    public async Task 置き換えのあとで落ちた()
    {
        await ArrangeAsync(book14: XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        Ribbon.Io.Replace = (temp, dest) =>
        {
            VbaRibbonPackageWriter.ReplaceFile(temp, dest);
            throw new SimulatedCrash();
        };

        var crash = async () => await Ribbon.RewriteAsync(Project, plan);
        await crash.Should().ThrowAsync<SimulatedCrash>();
        File.Exists(JobFile).Should().BeTrue("落ちたら記録が残る");
        _service.IsWritingToExcel.Should().BeFalse();

        // 残った一時ファイル（落ちたときに消せなかったもの）を置いておく。
        var job = System.Text.Json.JsonSerializer.Deserialize<VbaRibbonRewriteJob>(File.ReadAllText(JobFile), JsonFileStore.DefaultOptions)!;
        File.WriteAllText(job.TempPath!, "half");

        // 次の起動（作り直したサービス）。
        _service = NewService();
        Ribbon.HasInterruptedRewrite(Project).Should().BeTrue();
        var recovery = await Ribbon.RecoverInterruptedAsync(Project);

        recovery!.State.Should().Be(VbaRibbonInterruptedState.Rewritten);
        recovery.Message.Should().Contain("途中で終わりました").And.Contain("書き換え済み").And.NotContain("Excel では閉じたまま");
        recovery.RemovedTempFiles.Should().Be(1);
        NothingLeftBehind();
        PartText(File.ReadAllBytes(_book), "customUI/customUI14.xml").Should().Be(XmlB);
        (await Ribbon.RecoverInterruptedAsync(Project)).Should().BeNull("片付けたら、もう知らせない");

        // 基準は更新されていないが、次の取込で両側が同じ本文なので基準を作り直す（16.4）。未反映の印は、その取込で消える。
        _bridge.AddWorkbook(_book);
        var pulled = await _service.PullAsync(Project);
        pulled.IsSuccess.Should().BeTrue();
        (await _service.GetLocalStatusAsync(Project)).RibbonUnpushed.Should().BeFalse();
    }

    [Fact(DisplayName = "控えの途中で落ちた: 次のリボンの操作（準備）で後始末し、「元のまま」と知らせる。作りかけの控えは消す")]
    public async Task 控えの途中で落ちた()
    {
        var original = await ArrangeAsync(book14: XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        var defaultCreate = Ribbon.Io.CreateNew;
        Ribbon.Io.CreateNew = path =>
        {
            if (!path.StartsWith(BackupDir, StringComparison.Ordinal)) return defaultCreate(path);
            File.WriteAllText(path, "half"); // 作りかけの控え
            throw new SimulatedCrash();
        };

        var crash = async () => await Ribbon.RewriteAsync(Project, plan);
        await crash.Should().ThrowAsync<SimulatedCrash>();
        BackupFiles().Should().ContainSingle();

        _service = NewService();
        var prepared = await Ribbon.PrepareRewriteAsync(Project);

        prepared.Value.Recovery.Should().NotBeNull();
        prepared.Value.Recovery!.State.Should().Be(VbaRibbonInterruptedState.Original);
        prepared.Value.Recovery.Message.Should().Contain("元のまま");
        prepared.Value.Kind.Should().Be(VbaRibbonRewritePreparationKind.Ready, "片付けたあと、今回の準備は続ける");
        BackupFiles().Should().BeEmpty("作りかけの控えは「控え」として残さない");
        BookShouldBeUnchanged(original, "控えの途中ではブックに触れていない");
        File.Exists(JobFile).Should().BeFalse();
    }

    [Fact(DisplayName = "読めない作業の記録: 片付けて、ブックは変えていない旨を知らせる")]
    public async Task 読めない記録()
    {
        await ArrangeAsync(book14: XmlA);
        Directory.CreateDirectory(Path.GetDirectoryName(JobFile)!);
        File.WriteAllText(JobFile, "{ 壊れた");

        var recovery = await Ribbon.RecoverInterruptedAsync(Project);

        recovery!.Job.Should().BeNull();
        recovery.Message.Should().Contain("読めませんでした");
        File.Exists(JobFile).Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // 控えの掃除・直列化・手動の検査
    // ------------------------------------------------------------------

    [Fact(DisplayName = "控えの掃除: 成功のあとに世代数を超えた古いものを消す（同じ秒は連番）")]
    public async Task 控えの掃除()
    {
        await ArrangeAsync(book14: XmlA);
        Ribbon.MaxBackupGenerations = () => 2;
        foreach (var label in new[] { "B1", "B2", "B3" })
        {
            WriteMirror(Ribbon14, XmlA.Replace("label=\"業務\"", $"label=\"{label}\"", StringComparison.Ordinal));
            var plan = await PlanAsync();
            (await Ribbon.RewriteAsync(Project, plan)).IsSuccess.Should().BeTrue();
        }

        BackupFiles().Select(Path.GetFileName).Should().BeEquivalentTo(
            "計画-20261123-100000-ribbon_2.xlsm", "計画-20261123-100000-ribbon_3.xlsm");
    }

    [Fact(DisplayName = "VBA の反映と同時に走らない（同じゲート）: 反映が終わるまで、書き換えは始まらない")]
    public async Task 反映と直列()
    {
        await ArrangeAsync(book14: XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        _bridge.AddWorkbook(_book, FakeVbaBridge.Std("Module1", "Public Sub OnRun(control As IRibbonControl)\r\nEnd Sub"));
        File.WriteAllText(MirrorFile("Modules/Module1.bas"), "Sub Y()\r\nEnd Sub");
        var release = new TaskCompletionSource();
        _bridge.BeforeWrite = () => release.Task;

        var push = _service.PushAsync(Project);
        await Task.Delay(50);
        var rewrite = Ribbon.RewriteAsync(Project, plan);
        await Task.Delay(150);
        rewrite.IsCompleted.Should().BeFalse("反映がゲートを持っているあいだは待つ");

        release.SetResult();
        await push;
        var result = await rewrite;
        // 反映のあいだブックは Excel で開いているので、計画（開いていない）とは違う経路になり、止まる（順番が守られた証拠でもある）。
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E821 && i.Detail!.Contains("開かれた", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "手動の「リボンの XML を検査」: エラー・警告を一覧にするだけで、何も書かない")]
    public async Task 手動の検査()
    {
        var original = await ArrangeAsync(book14: XmlA);
        WriteMirror(Ribbon14, "<customUI xmlns=\"http://schemas.microsoft.com/office/2009/07/customui\"><ribbon><tabs><tab id=\"1st\"/></tabs></ribbon></customUI>");

        var inspected = await Ribbon.InspectMirrorAsync(Project);

        var one = inspected.Should().ContainSingle().Subject;
        one.MirrorPath.Should().Be(Ribbon14);
        one.Validation!.HasErrors.Should().BeTrue();
        BookShouldBeUnchanged(original, "検査は読むだけ");
        BackupFiles().Should().BeEmpty();
    }

    /// <summary>Graft が落ちた、の代わり（書き換えのどの catch にも捕まらない例外）。</summary>
    private sealed class SimulatedCrash : Exception
    {
    }

    /// <summary>書き込むとディスクがいっぱいで失敗するファイル（Windows の ERROR_DISK_FULL の HResult）。</summary>
    private sealed class DiskFullStream : FileStream
    {
        public DiskFullStream(string path)
            : base(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)
        {
        }

        public override void Write(byte[] buffer, int offset, int count) => throw Full();

        public override void Write(ReadOnlySpan<byte> buffer) => throw Full();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw Full();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw Full();

        private static IOException Full() => new("There is not enough space on the disk.", unchecked((int)0x80070070));
    }
}
