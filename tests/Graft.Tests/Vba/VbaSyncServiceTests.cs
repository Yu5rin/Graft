using System.IO;
using System.Text;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Tests.TestSupport;
using Graft.UiTests.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaSyncService"/>（Pull／Push／ミラー作成のつなぎ）のテスト（仕様書_VBA連携 7.1・7.2・7.2b・10.2）。
/// 三方比較そのものは <see cref="VbaSyncPlanner"/> のテストで固めてあるので、ここでは Planner と Bridge と
/// StateStore とミラーのファイル I/O の「つなぎ」――書く形（UTF-8・CRLF・既存の形を保つ）、同期基準の更新、
/// ミラーを Push 後に書き換えないこと、未保存の確認、失敗時の控え――を <see cref="FakeVbaBridge"/> で確かめる。
/// </summary>
public class VbaSyncServiceTests : IDisposable
{
    private const string Book = @"C:\Work\計画.xlsm";

    private readonly TempWorkspace _ws = new();
    private readonly AppPaths _paths;
    private readonly FakeVbaBridge _bridge = new();
    private readonly VbaSyncStateStore _states;
    private readonly FakeTrash _trash = new();
    private readonly VbaSyncService _service;
    private readonly DateTimeOffset _now = new(2026, 11, 21, 10, 0, 0, TimeSpan.FromHours(9));

    public VbaSyncServiceTests()
    {
        _paths = new AppPaths(_ws.CreateDirectory("app"));
        _states = new VbaSyncStateStore(_paths);
        _service = new VbaSyncService(_bridge, _states, _paths, _trash, () => _now);
    }

    public void Dispose() => _ws.Dispose();

    private sealed class FakeTrash : ITrashService
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

    private string MirrorRoot => Path.Combine(_paths.VbaMirrorsDirectory, VbaMirrorFolderName.Build(Book));

    private Project NewProject(VbaLink? link = null)
        => new() { Id = ProjectStore.CreateId(MirrorRoot), Name = "計画", Root = MirrorRoot, Vba = link ?? new VbaLink { WorkbookFullName = Book } };

    private string MirrorFile(string relative) => Path.Combine(MirrorRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    private async Task<Project> CreatedProjectAsync(params FakeVbaBridge.Module[] modules)
    {
        _bridge.AddWorkbook(Book, modules);
        var created = await _service.CreateMirrorAsync(Book, MirrorRoot, rebuild: false);
        created.IsSuccess.Should().BeTrue(string.Join(" / ", created.Issues.Select(i => i.ToDisplayText())));
        return NewProject();
    }

    private async Task<VbaSyncState> StateAsync()
        => (await _states.LoadAsync(NewProject().Id, Book)).Value;

    // ------------------------------------------------------------------
    // ミラー作成（7.1）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "ミラー作成: 全種別が所定のフォルダに、UTF-8（BOMなし）・CRLF・末尾に CRLF 1つで書かれ、ファイル名はコンポーネント名")]
    public async Task ミラー作成の形()
    {
        var project = await CreatedProjectAsync(
            FakeVbaBridge.Std("Module1", "Sub A()\r\n  MsgBox \"こんにちは\"\r\nEnd Sub"),
            FakeVbaBridge.Cls("Cls1", "Option Explicit"),
            FakeVbaBridge.Form("UserForm1", "Private Sub x()\r\nEnd Sub"),
            FakeVbaBridge.Doc("Sheet1", "' sheet", caption: "集計"),
            FakeVbaBridge.Doc("ThisWorkbook", string.Empty));

        var bytes = File.ReadAllBytes(MirrorFile("Modules/Module1.bas"));
        bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "BOM なし");
        new UTF8Encoding(false).GetString(bytes).Should().Be("Sub A()\r\n  MsgBox \"こんにちは\"\r\nEnd Sub\r\n");
        File.ReadAllText(MirrorFile("Classes/Cls1.cls")).Should().Be("Option Explicit\r\n");
        File.Exists(MirrorFile("Forms/UserForm1.frm")).Should().BeTrue();
        File.Exists(MirrorFile("Documents/Sheet1.cls")).Should().BeTrue("シートの表示名ではなくコンポーネント名");
        File.ReadAllText(MirrorFile("Documents/ThisWorkbook.cls")).Should().BeEmpty("空のモジュールは空のファイル");
        project.Root.Should().Be(MirrorRoot);
    }

    [Fact(DisplayName = "ミラー作成: 同期基準が作られ、2本のハッシュ・種別・シートの表示名が入る。作った直後は何も未反映")]
    public async Task ミラー作成で同期基準ができる()
    {
        var project = await CreatedProjectAsync(
            FakeVbaBridge.Std("Module1", "Sub A()\r\nEnd Sub"), FakeVbaBridge.Doc("Sheet1", "' s", "集計"));

        var state = await StateAsync();

        state.WorkbookFullName.Should().Be(Book);
        state.Modules.Should().HaveCount(2);
        var module = state.Modules["Modules/Module1.bas"];
        module.MirrorHash.Should().Be(VbaTextNormalizer.Hash("Sub A()\r\nEnd Sub")).And.Be(module.ExcelHash);
        module.ExcelLooseHash.Should().Be(VbaTextNormalizer.LooseHash("Sub A()\r\nEnd Sub"));
        state.Modules["Documents/Sheet1.cls"].Caption.Should().Be("集計");

        (await _service.GetLocalStatusAsync(project)).Should().Be(new VbaLocalStatus(0, 0));
        var push = await _service.PushAsync(project);
        push.Value.WrittenCount.Should().Be(0);
        _bridge.Calls.Should().NotContain(c => c.StartsWith("Write:"), "同期済みなら Excel へは何も書かない");
    }

    [Fact(DisplayName = "ミラー作成: 予約名（CON など）のコンポーネントはミラーに写さず、E810 の警告にする")]
    public async Task 予約名はスキップされる()
    {
        _bridge.AddWorkbook(Book, FakeVbaBridge.Std("Module1", "x"), FakeVbaBridge.Std("CON", "y"));

        var result = await _service.CreateMirrorAsync(Book, MirrorRoot, false);

        result.IsSuccess.Should().BeTrue();
        result.Value.ModuleCount.Should().Be(1);
        result.Issues.Should().Contain(i => i.Code == ErrorCode.E810);
        File.Exists(MirrorFile("Modules/CON.bas")).Should().BeFalse();
    }

    [Fact(DisplayName = "ミラー作成: ブックが読めなければ（E802 など）1つも書かず、同期状態も作らない")]
    public async Task 作成の失敗はミラーを残さない()
    {
        _bridge.AddWorkbook(Book, FakeVbaBridge.Std("Module1", "x"));
        _bridge.FailNext("Read", GraftIssue.Of(ErrorCode.E802));

        var result = await _service.CreateMirrorAsync(Book, MirrorRoot, false);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Code == ErrorCode.E802);
        Directory.Exists(MirrorRoot).Should().BeFalse();
        File.Exists(_paths.GetVbaSyncFilePath(NewProject().Id)).Should().BeFalse();
    }

    [Fact(DisplayName = "ミラー作成: 既存のミラーがあれば MirrorExists が true。作り直すと既存はごみ箱へ送られ、新しい内容になる")]
    public async Task 作り直しは既存をごみ箱へ送る()
    {
        await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "old"));
        _service.MirrorExists(MirrorRoot).Should().BeTrue();
        _service.MirrorExists(Path.Combine(_ws.RootPath, "nothing")).Should().BeFalse();

        _bridge.Get(Book)!.Find("Module1")!.Code = "new";
        var rebuilt = await _service.CreateMirrorAsync(Book, MirrorRoot, rebuild: true);

        rebuilt.IsSuccess.Should().BeTrue();
        // フォルダごとではなく、同期対象のファイルだけを送る（不具合 3）。
        _trash.Sent.Should().ContainSingle().Which.Should().Be(Path.GetFullPath(MirrorFile("Modules/Module1.bas")));
        File.ReadAllText(MirrorFile("Modules/Module1.bas")).Should().Be("new\r\n");
    }

    // ------------------------------------------------------------------
    // Pull（7.2）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "Pull: Excel 側だけが変わったモジュールをミラーへ取り込み、基準（2本とも）を更新する。履歴フォルダは作らない")]
    public async Task Excelだけの変更を取り込む()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "Sub A()\r\nEnd Sub"), FakeVbaBridge.Std("Module2", "x"));
        _bridge.Get(Book)!.Find("Module1")!.Code = "Sub A()\r\n  Debug.Print 1\r\nEnd Sub";

        var pull = await _service.PullAsync(project);

        pull.IsSuccess.Should().BeTrue();
        pull.Value.Pulled.Should().Equal("Modules/Module1.bas");
        File.ReadAllText(MirrorFile("Modules/Module1.bas")).Should().Be("Sub A()\r\n  Debug.Print 1\r\nEnd Sub\r\n");
        var module = (await StateAsync()).Modules["Modules/Module1.bas"];
        module.MirrorHash.Should().Be(module.ExcelHash).And.Be(VbaTextNormalizer.Hash("Sub A()\r\n  Debug.Print 1\r\nEnd Sub"));
        Directory.Exists(_paths.BackupRootDirectory).Should().BeFalse("Pull は履歴（リビジョン）に残さない");

        (await _service.PullAsync(project)).Value.IsEmpty.Should().BeTrue("取り込んだ後は何もない");
    }

    [Fact(DisplayName = "Pull: 既存のファイルの文字コード・改行・末尾改行の有無を保つ（Shift_JIS・LF・末尾改行なし）")]
    public async Task 既存の形を保つ()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var sjis = Encoding.GetEncoding(932);
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "Sub A()\r\n' 説明\r\nEnd Sub"));
        // ミラーのファイルを Shift_JIS・LF・末尾改行なしに差し替える（本文は同じなので、ハッシュは変わらない）。
        // 日本語を含めるのは、ASCII だけのファイルは UTF-8 と判定されるため。
        File.WriteAllBytes(MirrorFile("Modules/Module1.bas"), sjis.GetBytes("Sub A()\n' 説明\nEnd Sub"));
        _bridge.Get(Book)!.Find("Module1")!.Code = "Sub A()\r\n  MsgBox \"日本語\"\r\nEnd Sub";

        (await _service.PullAsync(project)).Value.Pulled.Should().HaveCount(1);

        var bytes = File.ReadAllBytes(MirrorFile("Modules/Module1.bas"));
        sjis.GetString(bytes).Should().Be("Sub A()\n  MsgBox \"日本語\"\nEnd Sub", "Shift_JIS・LF・末尾改行なしのまま");
        bytes.Should().NotContain((byte)'\r');
    }

    [Fact(DisplayName = "Pull: 記録のない Excel だけのモジュールは新規ファイルとして取り込まれる")]
    public async Task 新しいモジュールを取り込む()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "x"));
        _bridge.Get(Book)!.Modules.Add(FakeVbaBridge.Cls("Added", "Option Explicit"));

        var pull = await _service.PullAsync(project);

        pull.Value.Pulled.Should().Equal("Classes/Added.cls");
        File.ReadAllText(MirrorFile("Classes/Added.cls")).Should().Be("Option Explicit\r\n");
        (await StateAsync()).Modules.Should().ContainKey("Classes/Added.cls");
    }

    [Fact(DisplayName = "Pull: 両側が変わって本文が違うと衝突。取り込まずに結果へ載せ、E812 の警告を出す（P3 でダイアログへ差し替え）")]
    public async Task 衝突は取り込まない()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "base"));
        File.WriteAllText(MirrorFile("Modules/Module1.bas"), "graft side\r\n");
        _bridge.Get(Book)!.Find("Module1")!.Code = "excel side";

        var pull = await _service.PullAsync(project);

        pull.Value.Conflicts.Should().Equal("Modules/Module1.bas");
        pull.Value.Pulled.Should().BeEmpty();
        pull.Issues.Should().Contain(i => i.Code == ErrorCode.E812 && i.Severity == Severity.Warning);
        File.ReadAllText(MirrorFile("Modules/Module1.bas")).Should().Be("graft side\r\n", "ミラーは書き換えない");
    }

    [Fact(DisplayName = "Pull: ミラーから消えたモジュールは削除保留になり、自動 Pull では復活しない。「Excel から復元」で戻る")]
    public async Task 削除保留は復元でだけ戻る()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "keep"), FakeVbaBridge.Std("Gone", "will be deleted in mirror"));
        File.Delete(MirrorFile("Modules/Gone.bas"));

        var pull = await _service.PullAsync(project);

        pull.Value.Pulled.Should().BeEmpty();
        File.Exists(MirrorFile("Modules/Gone.bas")).Should().BeFalse("自動 Pull では復活させない");
        (await StateAsync()).Modules["Modules/Gone.bas"].DeletePending.Should().BeTrue();
        (await _service.GetLocalStatusAsync(project)).DeletePendingCount.Should().Be(1);

        var restored = await _service.PullAsync(project, new[] { "Modules/Gone.bas" }, restoreDeletePending: true);

        restored.Value.Pulled.Should().Equal("Modules/Gone.bas");
        File.ReadAllText(MirrorFile("Modules/Gone.bas")).Should().Be("will be deleted in mirror\r\n");
        (await StateAsync()).Modules["Modules/Gone.bas"].DeletePending.Should().BeFalse();
    }

    [Fact(DisplayName = "Pull: 大文字小文字・空白だけの差（VBE の整形）は Excel 側の変更とみなさず、取り込まない")]
    public async Task 整形差は取り込まない()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "Sub A()\r\n  x = 1\r\nEnd Sub"));
        _bridge.Get(Book)!.Find("Module1")!.Code = "Sub A()\r\n  X  =  1\r\nEnd Sub";

        var pull = await _service.PullAsync(project);

        pull.Value.IsEmpty.Should().BeTrue();
        File.ReadAllText(MirrorFile("Modules/Module1.bas")).Should().Be("Sub A()\r\n  x = 1\r\nEnd Sub\r\n");
        (await StateAsync()).Modules["Modules/Module1.bas"].ExcelHash
            .Should().Be(VbaTextNormalizer.Hash("Sub A()\r\n  X  =  1\r\nEnd Sub"), "excelHash だけ更新する");
    }

    [Fact(DisplayName = "Pull: 取り込み先に未保存の編集があれば確認が呼ばれ、断られたモジュールは取り込まない（警告）。他は取り込む")]
    public async Task 未保存を断ると取り込まない()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "a"), FakeVbaBridge.Std("Module2", "b"));
        _bridge.Get(Book)!.Find("Module1")!.Code = "a2";
        _bridge.Get(Book)!.Find("Module2")!.Code = "b2";
        IReadOnlyList<string>? asked = null;
        _service.BeforePullAsync = paths =>
        {
            asked = paths;
            return Task.FromResult(new VbaPullSaveOutcome(new[] { MirrorFile("Modules/Module1.bas") }, false));
        };

        var pull = await _service.PullAsync(project);

        asked.Should().BeEquivalentTo(new[] { MirrorFile("Modules/Module1.bas"), MirrorFile("Modules/Module2.bas") });
        pull.Value.Pulled.Should().Equal("Modules/Module2.bas");
        pull.Value.Skipped.Should().Equal("Modules/Module1.bas");
        pull.Issues.Should().Contain(i => i.Code == ErrorCode.E702);
        File.ReadAllText(MirrorFile("Modules/Module1.bas")).Should().Be("a\r\n");
        File.ReadAllText(MirrorFile("Modules/Module2.bas")).Should().Be("b2\r\n");
    }

    [Fact(DisplayName = "Pull: 未保存の編集を保存してもらうとミラーが変わるので、計画を作り直す（両側が変われば衝突になる）")]
    public async Task 保存したら計画を作り直す()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "a"));
        _bridge.Get(Book)!.Find("Module1")!.Code = "excel edit";
        _service.BeforePullAsync = _ =>
        {
            File.WriteAllText(MirrorFile("Modules/Module1.bas"), "unsaved edit now saved\r\n"); // エディタの保存の代わり。
            return Task.FromResult(new VbaPullSaveOutcome(Array.Empty<string>(), true));
        };

        var pull = await _service.PullAsync(project);

        pull.Value.Conflicts.Should().Equal("Modules/Module1.bas");
        File.ReadAllText(MirrorFile("Modules/Module1.bas")).Should().Be("unsaved edit now saved\r\n");
    }

    [Fact(DisplayName = "Pull: Excel に接続できなければ（E804）何もせず失敗を返す")]
    public async Task 接続できなければ失敗()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "a"));
        _bridge.CloseWorkbook(Book);

        var pull = await _service.PullAsync(project);

        pull.IsSuccess.Should().BeFalse();
        pull.Issues.Should().Contain(i => i.Code == ErrorCode.E804);
    }

    // ------------------------------------------------------------------
    // Push（7.2b）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "Push: ミラー側だけが変わったモジュールを Excel へ書き、基準を更新する。ミラーのファイルは書き換えない")]
    public async Task ミラーの変更を反映する()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "Sub A()\r\nEnd Sub"), FakeVbaBridge.Std("Module2", "same"));
        File.WriteAllText(MirrorFile("Modules/Module1.bas"), "Sub A()\n  Debug.Print 2\nEnd Sub\n"); // LF のミラー。
        var mirrorBefore = File.ReadAllBytes(MirrorFile("Modules/Module1.bas"));
        (await _service.GetLocalStatusAsync(project)).UnpushedCount.Should().Be(1);

        var push = await _service.PushAsync(project);

        push.IsSuccess.Should().BeTrue();
        push.Value.Pushed.Should().Equal("Modules/Module1.bas");
        _bridge.Get(Book)!.Find("Module1")!.Code.Should().Be("Sub A()\r\n  Debug.Print 2\r\nEnd Sub", "VBE へは CRLF にそろえ、末尾の改行を除いた本文を渡す");
        File.ReadAllBytes(MirrorFile("Modules/Module1.bas")).Should().Equal(mirrorBefore);
        _bridge.WriteRequests.Single().Select(w => w.Name).Should().Equal("Module1");
        (await _service.GetLocalStatusAsync(project)).UnpushedCount.Should().Be(0);
    }

    [Fact(DisplayName = "Push: VBE が整形しても、ミラーは書き換えず、E809 は警告。基準は書いたミラーと読み直した Excel の2本になり、次は何も要らない")]
    public async Task 整形差を基準で吸収する()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "Sub A()\r\nEnd Sub"));
        _bridge.FormatOnWrite = code => code.Replace("x=1", "x = 1").Replace("sub ", "Sub ");
        File.WriteAllText(MirrorFile("Modules/Module1.bas"), "Sub A()\r\n  x=1\r\nEnd Sub\r\n");

        var push = await _service.PushAsync(project);

        push.IsSuccess.Should().BeTrue();
        push.Issues.Should().Contain(i => i.Code == ErrorCode.E809 && i.Severity == Severity.Warning);
        File.ReadAllText(MirrorFile("Modules/Module1.bas")).Should().Be("Sub A()\r\n  x=1\r\nEnd Sub\r\n", "ミラーは整形差を取り込まない");
        var module = (await StateAsync()).Modules["Modules/Module1.bas"];
        module.MirrorHash.Should().Be(VbaTextNormalizer.Hash("Sub A()\r\n  x=1\r\nEnd Sub"));
        module.ExcelHash.Should().Be(VbaTextNormalizer.Hash("Sub A()\r\n  x = 1\r\nEnd Sub"));

        // 整形差は変更に見えない: 次の Pull／Push は何もしない。
        (await _service.PullAsync(project)).Value.IsEmpty.Should().BeTrue();
        (await _service.PushAsync(project)).Value.WrittenCount.Should().Be(0);
    }

    [Fact(DisplayName = "Push: ミラーにだけある標準／クラスは Excel に新規作成される。Documents／Forms の新規は作成不可（E808 の警告）で書かない")]
    public async Task 新規作成と作成不可()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "x"));
        File.WriteAllText(MirrorFile("Modules/NewMod.bas"), "Sub N()\r\nEnd Sub\r\n");
        Directory.CreateDirectory(Path.GetDirectoryName(MirrorFile("Classes/NewCls.cls"))!);
        File.WriteAllText(MirrorFile("Classes/NewCls.cls"), "Option Explicit\r\n");
        Directory.CreateDirectory(Path.GetDirectoryName(MirrorFile("Documents/Sheet9.cls"))!);
        File.WriteAllText(MirrorFile("Documents/Sheet9.cls"), "' no such sheet\r\n");

        var push = await _service.PushAsync(project);

        push.Value.Created.Should().BeEquivalentTo(new[] { "Modules/NewMod.bas", "Classes/NewCls.cls" });
        _bridge.Get(Book)!.Find("NewMod")!.Kind.Should().Be(VbaModuleKind.Standard);
        _bridge.Get(Book)!.Find("Sheet9").Should().BeNull();
        push.Issues.Should().Contain(i => i.Code == ErrorCode.E808 && i.Severity == Severity.Warning);
        (await StateAsync()).Modules.Should().ContainKey("Modules/NewMod.bas");
    }

    [Fact(DisplayName = "Push: CP932 で表せない文字を含むモジュールは反映しない（E811）。他のモジュールは反映する")]
    public async Task CP932で表せない文字は反映しない()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Good", "a"), FakeVbaBridge.Std("Bad", "b"));
        File.WriteAllText(MirrorFile("Modules/Good.bas"), "a2\r\n");
        File.WriteAllText(MirrorFile("Modules/Bad.bas"), "' emダッシュ — と絵文字 😀\r\n");

        var push = await _service.PushAsync(project);

        push.Value.Pushed.Should().Equal("Modules/Good.bas");
        push.Value.Failed.Should().Equal("Modules/Bad.bas");
        var e811 = push.Issues.Single(i => i.Code == ErrorCode.E811);
        e811.Detail.Should().Contain("U+2014").And.Contain("Modules/Bad.bas");
        _bridge.Get(Book)!.Find("Bad")!.Code.Should().Be("b");
    }

    [Fact(DisplayName = "Push: 衝突したモジュールは反映せず結果へ載せる。衝突でないモジュールは反映する")]
    public async Task 衝突は反映しない()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Conf", "base"), FakeVbaBridge.Std("Fine", "base2"));
        File.WriteAllText(MirrorFile("Modules/Conf.bas"), "graft\r\n");
        _bridge.Get(Book)!.Find("Conf")!.Code = "excel";
        File.WriteAllText(MirrorFile("Modules/Fine.bas"), "graft2\r\n");

        var push = await _service.PushAsync(project);

        push.Value.Conflicts.Should().Equal("Modules/Conf.bas");
        push.Value.Pushed.Should().Equal("Modules/Fine.bas");
        _bridge.Get(Book)!.Find("Conf")!.Code.Should().Be("excel");
        push.Issues.Should().Contain(i => i.Code == ErrorCode.E812);
    }

    [Theory(DisplayName = "Push: 前提条件に違反すると 1件も書かずに全体が失敗し、理由のコードを返す（ロック E803・マクロ実行中 E813・未許可 E802）")]
    [InlineData("locked", ErrorCode.E803)]
    [InlineData("running", ErrorCode.E813)]
    [InlineData("untrusted", ErrorCode.E802)]
    public async Task 前提条件の違反は全体の失敗(string kind, ErrorCode expected)
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "a"));
        File.WriteAllText(MirrorFile("Modules/Module1.bas"), "a2\r\n");
        var workbook = _bridge.Get(Book)!;
        switch (kind)
        {
            case "locked": workbook.Locked = true; break;
            case "running": workbook.DesignMode = false; break;
            default: workbook.Availability = VbaWorkbookAvailability.AccessNotTrusted; break;
        }

        var push = await _service.PushAsync(project);

        push.IsSuccess.Should().BeFalse();
        push.Issues.Should().Contain(i => i.Code == expected);
        workbook.Find("Module1")!.Code.Should().Be("a");
        (await _service.GetLocalStatusAsync(project)).UnpushedCount.Should().Be(1, "未反映の印が残る");
    }

    [Fact(DisplayName = "Push: 書き込み後の読み直しが別内容なら、控えへ書き戻される（RolledBack）。基準は更新せず、未反映のまま残る")]
    public async Task 別内容なら書き戻される()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "orig"));
        File.WriteAllText(MirrorFile("Modules/Module1.bas"), "changed\r\n");
        _bridge.CorruptOnWrite.Add("Module1");

        var push = await _service.PushAsync(project);

        push.IsSuccess.Should().BeTrue();
        push.Value.Failed.Should().Equal("Modules/Module1.bas");
        push.Issues.Should().Contain(i => i.Code == ErrorCode.E809 && i.Severity == Severity.Error);
        _bridge.Get(Book)!.Find("Module1")!.Code.Should().Be("orig", "控えが書き戻されている");
        (await _service.GetLocalStatusAsync(project)).UnpushedCount.Should().Be(1);
    }

    [Fact(DisplayName = "Push: 控えの書き戻しにも失敗したら E809 で、控えの本文を .rescue/ に書き出し、パスをエラーの詳細に載せる")]
    public async Task 戻せなければ控えをrescueへ書く()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "orig body"));
        File.WriteAllText(MirrorFile("Modules/Module1.bas"), "changed\r\n");
        _bridge.CorruptOnWrite.Add("Module1");
        _bridge.RollbackFails.Add("Module1");

        var push = await _service.PushAsync(project);

        var issue = push.Issues.Single(i => i.Code == ErrorCode.E809);
        var rescue = Directory.GetFiles(_paths.GetVbaRescueDirectory(project.Id));
        rescue.Should().ContainSingle().Which.Should().Contain("Module1-20261121-100000");
        File.ReadAllText(rescue[0]).Should().Be("orig body");
        issue.Detail.Should().Contain(rescue[0]);
    }

    [Fact(DisplayName = "Push: SaveWorkbookAfterPush ならブックを保存する。読み取り専用なら E807 を返すが、反映は済んでいる")]
    public async Task 反映後にブックを保存する()
    {
        var link = new VbaLink { WorkbookFullName = Book, SaveWorkbookAfterPush = true };
        await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "a"));
        var project = NewProject(link);
        File.WriteAllText(MirrorFile("Modules/Module1.bas"), "a2\r\n");

        var push = await _service.PushAsync(project);

        push.Value.WorkbookSaved.Should().BeTrue();
        _bridge.Get(Book)!.SaveCount.Should().Be(1);

        _bridge.Get(Book)!.IsReadOnly = true;
        File.WriteAllText(MirrorFile("Modules/Module1.bas"), "a3\r\n");
        var readOnly = await _service.PushAsync(project);

        readOnly.Value.Pushed.Should().Equal("Modules/Module1.bas");
        readOnly.Value.WorkbookSaved.Should().BeFalse();
        readOnly.Issues.Should().Contain(i => i.Code == ErrorCode.E807);
    }

    [Fact(DisplayName = "Push: 保存しない設定（既定）ではブックを保存しない")]
    public async Task 既定では保存しない()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "a"));
        File.WriteAllText(MirrorFile("Modules/Module1.bas"), "a2\r\n");

        await _service.PushAsync(project);

        _bridge.Get(Book)!.SaveCount.Should().Be(0);
        _bridge.Calls.Should().NotContain("Save");
    }

    [Fact(DisplayName = "Push: 対象を名指ししたときは、そのモジュールだけを反映する")]
    public async Task 名指しで反映する()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("A", "a"), FakeVbaBridge.Std("B", "b"));
        File.WriteAllText(MirrorFile("Modules/A.bas"), "a2\r\n");
        File.WriteAllText(MirrorFile("Modules/B.bas"), "b2\r\n");

        var push = await _service.PushAsync(project, new[] { "Modules/B.bas" });

        push.Value.Pushed.Should().Equal("Modules/B.bas");
        _bridge.Get(Book)!.Find("A")!.Code.Should().Be("a");
        (await _service.GetLocalStatusAsync(project)).UnpushedCount.Should().Be(1);
    }

    [Fact(DisplayName = "対象外のファイル（入れ子・フォルダと拡張子の不一致）は同期せず、E810 の警告になる")]
    public async Task 対象外のファイルは警告()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "a"));
        Directory.CreateDirectory(Path.Combine(MirrorRoot, "Modules", "sub"));
        File.WriteAllText(MirrorFile("Modules/sub/x.bas"), "x");
        File.WriteAllText(MirrorFile("Modules/Wrong.cls"), "x");
        File.WriteAllText(MirrorFile("notes.txt"), "x");

        var pull = await _service.PullAsync(project);

        pull.Issues.Count(i => i.Code == ErrorCode.E810).Should().Be(3);
        _bridge.Get(Book)!.Modules.Should().HaveCount(1);
    }
}
