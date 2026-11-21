using System.IO;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Platform;
using Graft.UiTests.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 不具合 1（フォルダ違いで同名のファイルを新規作成すると、既存のモジュールが確認なしに上書きされる）と、
/// 同名で種別違いの新規ファイルを E808 で止める修正の回帰テスト。
/// 修正前は、Excel の Document の <c>ThisWorkbook</c> に対して <c>Classes/ThisWorkbook.cls</c> を新規作成すると
/// ブリッジが名前だけで既存を引いて本文を置き換えていた（衝突 0・控え 0・通知は「反映済み」）。
/// </summary>
public class VbaCrossKindFixTests : VbaServiceFixture
{
    private static readonly DateTimeOffset At = new(2026, 11, 21, 10, 0, 0, TimeSpan.FromHours(9));

    private static VbaMirrorModule Mirror(VbaModuleKind kind, string name, string text)
        => new(VbaMirrorLayout.ToRelativePath(kind, name), kind, name, VbaTextNormalizer.Hash(text), VbaTextNormalizer.LooseHash(text));

    private static VbaExcelModule ExcelModule(VbaModuleKind kind, string name, string text)
        => new(name, kind, VbaTextNormalizer.Hash(text), VbaTextNormalizer.LooseHash(text));

    // ------------------------------------------------------------------
    // Planner（純ロジック）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "不具合1 Planner: Excel に Document の ThisWorkbook があるとき、Classes/ThisWorkbook.cls の新規作成は NotCreatable（E808）で止める")]
    public void 別種別の同名の新規作成は止める()
    {
        var plan = VbaSyncPlanner.Plan(
            new[] { Mirror(VbaModuleKind.Class, "ThisWorkbook", "Option Explicit\r\n' new") },
            new[] { ExcelModule(VbaModuleKind.Document, "ThisWorkbook", "Option Explicit") },
            state: null, At);

        var created = plan.Modules.Single(m => m.RelativePath == "Classes/ThisWorkbook.cls");
        created.Action.Should().Be(VbaSyncAction.NotCreatable);
        created.Issue!.Code.Should().Be(ErrorCode.E808);
        created.Issue.Detail.Should().Contain("Documents/ThisWorkbook.cls", "書くべきファイルを案内する");
    }

    [Fact(DisplayName = "不具合1 Planner: ミラーに Modules/Foo.bas と Classes/Foo.cls が両方新規で現れたら、どちらも作成しない（E808）")]
    public void 同名で種別違いの新規が2つなら両方止める()
    {
        var plan = VbaSyncPlanner.Plan(
            new[] { Mirror(VbaModuleKind.Standard, "Foo", "a"), Mirror(VbaModuleKind.Class, "Foo", "b") },
            Array.Empty<VbaExcelModule>(), state: null, At);

        plan.Modules.Should().OnlyContain(m => m.Action == VbaSyncAction.NotCreatable && m.Issue!.Code == ErrorCode.E808);
        plan.Modules.Should().HaveCount(2);
    }

    [Fact(DisplayName = "不具合1 Planner: 名前が違えば、または同じ種別なら、新規作成はこれまでどおり PushCreate")]
    public void 衝突しない新規作成は止めない()
    {
        var plan = VbaSyncPlanner.Plan(
            new[] { Mirror(VbaModuleKind.Class, "Brand", "x"), Mirror(VbaModuleKind.Standard, "Other", "y") },
            new[] { ExcelModule(VbaModuleKind.Document, "ThisWorkbook", "z") }, state: null, At);

        plan.Modules.Where(m => m.RelativePath is "Classes/Brand.cls" or "Modules/Other.bas")
            .Should().OnlyContain(m => m.Action == VbaSyncAction.PushCreate);
    }

    // ------------------------------------------------------------------
    // VbaSyncService + FakeVbaBridge（実証した再現そのもの）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "不具合1 再現: Classes/ThisWorkbook.cls を新規に作って Push しても、Document の ThisWorkbook は上書きされず E808 になる")]
    public async Task ThisWorkbookは上書きされない()
    {
        const string original = "Option Explicit\r\nPrivate Sub Workbook_Open()\r\n  ImportantInit\r\nEnd Sub";
        var project = await CreatedProjectAsync(FakeVbaBridge.Doc("ThisWorkbook", original), FakeVbaBridge.Std("Module1", "x"));
        Directory.CreateDirectory(Path.GetDirectoryName(MirrorFile("Classes/ThisWorkbook.cls"))!);
        File.WriteAllText(MirrorFile("Classes/ThisWorkbook.cls"), "Option Explicit\r\n' new\r\n");

        var push = await Service.PushAsync(project);

        push.IsSuccess.Should().BeTrue();
        Excel("ThisWorkbook").Code.Should().Be(original, "既存のドキュメントモジュールの本文は 1 文字も変わらない");
        Excel("ThisWorkbook").Kind.Should().Be(VbaModuleKind.Document);
        push.Value.Created.Should().BeEmpty();
        push.Value.Pushed.Should().BeEmpty();
        push.Issues.Should().Contain(i => i.Code == ErrorCode.E808 && i.Path == "Classes/ThisWorkbook.cls");
        Bridge.Calls.Should().NotContain(c => c.StartsWith("Write:"), "書き込みの要求そのものを出さない");
        File.ReadAllText(MirrorFile("Classes/ThisWorkbook.cls")).Should().Be("Option Explicit\r\n' new\r\n", "ミラーのファイルは触らない");
    }

    [Fact(DisplayName = "不具合1 再現: 止めたあとの「Excel から取込」でも、ミラーの Documents/ThisWorkbook.cls は元の本文のまま")]
    public async Task 取込でも元の本文が残る()
    {
        const string original = "Option Explicit\r\n' original";
        var project = await CreatedProjectAsync(FakeVbaBridge.Doc("ThisWorkbook", original));
        Directory.CreateDirectory(Path.GetDirectoryName(MirrorFile("Classes/ThisWorkbook.cls"))!);
        File.WriteAllText(MirrorFile("Classes/ThisWorkbook.cls"), "' new\r\n");

        await Service.PushAsync(project);
        var pull = await Service.PullAsync(project);

        pull.IsSuccess.Should().BeTrue();
        File.ReadAllText(MirrorFile("Documents/ThisWorkbook.cls")).Should().Be(original + "\r\n");
        Excel("ThisWorkbook").Code.Should().Be(original);
    }

    [Fact(DisplayName = "同名で種別違いの新規ファイル: Modules/Foo.bas と Classes/Foo.cls を同時に作っても、どちらも書かず E808（結果の対応もずれない）")]
    public async Task 同名の2つの新規は書かない()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "x"));
        File.WriteAllText(MirrorFile("Modules/Foo.bas"), "Sub A()\r\nEnd Sub\r\n");
        Directory.CreateDirectory(Path.GetDirectoryName(MirrorFile("Classes/Foo.cls"))!);
        File.WriteAllText(MirrorFile("Classes/Foo.cls"), "Option Explicit\r\n");

        var push = await Service.PushAsync(project);

        push.Value.Created.Should().BeEmpty();
        push.Issues.Count(i => i.Code == ErrorCode.E808).Should().Be(2);
        Bridge.Get(Book)!.Find("Foo").Should().BeNull("どちらも作らない");
    }

    [Fact(DisplayName = "不具合1 最後の砦: ブリッジは、引いたコンポーネントの種別が書こうとしている種別と違えば 1 文字も書かない")]
    public async Task ブリッジは種別違いを書かない()
    {
        Bridge.AddWorkbook(Book, FakeVbaBridge.Doc("Sheet1", "' sheet"));

        var written = await Bridge.WriteComponentsAsync(Book,
            new[] { new VbaComponentWrite("Sheet1", VbaModuleKind.Class, "' overwritten", CreateIfMissing: true) });

        written.Value.Single().Status.Should().Be(VbaWriteStatus.Failed);
        written.Value.Single().Issue!.Code.Should().Be(ErrorCode.E808);
        Excel("Sheet1").Code.Should().Be("' sheet");
    }

    // ------------------------------------------------------------------
    // VbaWriteTargetGuard（Windows のブリッジが使う純ロジック）
    // ------------------------------------------------------------------

    [Theory(DisplayName = "VbaWriteTargetGuard: 名前が違う（並びが変わって別のモジュールを引いた）・種別が違うときだけ E808、一致なら null")]
    [InlineData("Module1", VbaModuleKind.Standard, "Module1", VbaModuleKind.Standard, false)]
    [InlineData("module1", VbaModuleKind.Standard, "Module1", VbaModuleKind.Standard, false)]
    [InlineData("Module1", VbaModuleKind.Standard, "Module2", VbaModuleKind.Standard, true)]
    [InlineData("ThisWorkbook", VbaModuleKind.Class, "ThisWorkbook", VbaModuleKind.Document, true)]
    [InlineData("Foo", VbaModuleKind.Standard, "Foo", null, true)]
    public void 書き込み対象の照合(string name, VbaModuleKind kind, string actualName, VbaModuleKind? actualKind, bool blocked)
    {
        var issue = VbaWriteTargetGuard.Check(name, kind, actualName, actualKind);

        if (blocked) issue!.Code.Should().Be(ErrorCode.E808);
        else issue.Should().BeNull();
    }

    // ------------------------------------------------------------------
    // 適用の前の検査（VbaApplyGuard）と既存コンポーネントの一覧
    // ------------------------------------------------------------------

    private static BlockPlan NewFile(string path, string text, bool selected = true)
        => new()
        {
            Block = new FullContentBlock { Path = path, Content = text },
            Path = path,
            Operation = "create",
            CanApply = true,
            IsSelected = selected,
            BeforeText = null,
            AfterText = text,
        };

    [Fact(DisplayName = "不具合1 VbaApplyGuard: 既存の Document と同名で種別違いの新規ファイルは E808、同じ種別の新規ファイルは止めない")]
    public void 適用前の検査は同名種別違いを止める()
    {
        var existing = new[] { new VbaComponentRef("ThisWorkbook", VbaModuleKind.Document), new VbaComponentRef("Module1", VbaModuleKind.Standard) };

        var findings = VbaApplyGuard.CheckNameCollisions(
            new[] { NewFile("Classes/ThisWorkbook.cls", "x"), NewFile("Modules/Fresh.bas", "y") }, existing);

        findings.Should().ContainSingle().Which.Issue.Code.Should().Be(ErrorCode.E808);
        findings.Single().Plan.Path.Should().Be("Classes/ThisWorkbook.cls");
    }

    [Fact(DisplayName = "不具合1 VbaApplyGuard: チェックを外したブロックは検査しない。同時に作る Modules/Foo.bas と Classes/Foo.cls は両方止める")]
    public void 適用前の検査は外したブロックを見ず同時作成を止める()
    {
        var existing = Array.Empty<VbaComponentRef>();

        VbaApplyGuard.CheckNameCollisions(
            new[] { NewFile("Classes/ThisWorkbook.cls", "x", selected: false) },
            new[] { new VbaComponentRef("ThisWorkbook", VbaModuleKind.Document) }).Should().BeEmpty();

        VbaApplyGuard.CheckNameCollisions(
                new[] { NewFile("Modules/Foo.bas", "a"), NewFile("Classes/Foo.cls", "b") }, existing)
            .Should().HaveCount(2);
    }

    [Fact(DisplayName = "不具合1 ListKnownComponentsAsync: ミラーのファイルと Excel のコンポーネントを名前と種別で返す")]
    public async Task 既存のコンポーネントの一覧()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Doc("ThisWorkbook", "a"), FakeVbaBridge.Std("Module1", "b"));
        File.WriteAllText(MirrorFile("Modules/OnlyMirror.bas"), "c");

        var known = await Service.ListKnownComponentsAsync(project);

        known.Should().Contain(new VbaComponentRef("ThisWorkbook", VbaModuleKind.Document));
        known.Should().Contain(new VbaComponentRef("OnlyMirror", VbaModuleKind.Standard));
        known.Should().Contain(new VbaComponentRef("Module1", VbaModuleKind.Standard));
    }
}
