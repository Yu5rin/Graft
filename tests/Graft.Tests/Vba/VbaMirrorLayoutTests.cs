using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaMirrorLayout"/>・<see cref="VbaModuleKinds"/> のテスト（仕様書_VBA連携 4.2・6.1・10.1）。
/// ミラーのパスと (種別, コンポーネント名) の対応が崩れると、同じモジュールの二重同期や、
/// 対象外のファイルの Excel への誤反映につながる。
/// </summary>
public class VbaMirrorLayoutTests
{
    [Theory(DisplayName = "全種別で、種別とコンポーネント名から作ったパスを読み解くと元に戻る")]
    [InlineData(VbaModuleKind.Standard, "Module1", "Modules/Module1.bas")]
    [InlineData(VbaModuleKind.Class, "Cls_Order", "Classes/Cls_Order.cls")]
    [InlineData(VbaModuleKind.Form, "UserForm1", "Forms/UserForm1.frm")]
    [InlineData(VbaModuleKind.Document, "ThisWorkbook", "Documents/ThisWorkbook.cls")]
    [InlineData(VbaModuleKind.Document, "Sheet1", "Documents/Sheet1.cls")]
    public void 種別と名前とパスは相互に変換できる(VbaModuleKind kind, string name, string expectedPath)
    {
        VbaMirrorLayout.ToRelativePath(kind, name).Should().Be(expectedPath);

        VbaMirrorLayout.TryParse(expectedPath, out var parsed, out var reason).Should().BeTrue(reason);
        parsed!.Kind.Should().Be(kind);
        parsed.ComponentName.Should().Be(name);
        parsed.RelativePath.Should().Be(expectedPath);
    }

    [Fact(DisplayName = "クラスとドキュメントはどちらも .cls だが、フォルダで種別を区別する")]
    public void クラスとドキュメントはフォルダで区別される()
    {
        VbaMirrorLayout.TryParse("Classes/Foo.cls", out var cls, out _).Should().BeTrue();
        VbaMirrorLayout.TryParse("Documents/Foo.cls", out var doc, out _).Should().BeTrue();

        cls!.Kind.Should().Be(VbaModuleKind.Class);
        doc!.Kind.Should().Be(VbaModuleKind.Document);
    }

    [Theory(DisplayName = "フォルダと拡張子が一致しないパスは同期の対象外になる")]
    [InlineData("Modules/x.cls")]
    [InlineData("Classes/x.bas")]
    [InlineData("Forms/x.cls")]
    [InlineData("Documents/x.bas")]
    [InlineData("Modules/x.txt")]
    [InlineData("Modules/x")]
    public void 不一致は対象外(string path)
    {
        VbaMirrorLayout.TryParse(path, out var parsed, out var reason).Should().BeFalse();
        parsed.Should().BeNull();
        reason.Should().NotBeNullOrWhiteSpace();
    }

    [Theory(DisplayName = "ミラーの外・直下・未知のフォルダ・親フォルダ参照は対象外になる")]
    [InlineData("Foo.bas")]
    [InlineData("README.md")]
    [InlineData("Other/Foo.bas")]
    [InlineData("../Modules/Foo.bas")]
    [InlineData("Modules/../Foo.bas")]
    [InlineData("/Modules/Foo.bas")]
    [InlineData("C:/Modules/Foo.bas")]
    [InlineData("")]
    public void ミラーの外は対象外(string path)
    {
        VbaMirrorLayout.TryParse(path, out _, out var reason).Should().BeFalse();
        reason.Should().NotBeNullOrWhiteSpace();
    }

    [Theory(DisplayName = "入れ子のフォルダは対象外になる")]
    [InlineData("Modules/sub/x.bas")]
    [InlineData("Classes/a/b/x.cls")]
    public void 入れ子は対象外(string path)
    {
        VbaMirrorLayout.TryParse(path, out _, out var reason).Should().BeFalse();
        reason.Should().Contain("入れ子");
    }

    [Theory(DisplayName = "フォルダ名と拡張子の大文字小文字は区別せず、読み解いた結果は正規の綴りになる")]
    [InlineData("modules/Foo.BAS", "Modules/Foo.bas")]
    [InlineData("MODULES\\Foo.bas", "Modules/Foo.bas")]
    [InlineData("documents/Sheet1.CLS", "Documents/Sheet1.cls")]
    [InlineData("./Classes/Foo.cls", "Classes/Foo.cls")]
    public void 大文字小文字を区別せず正規化する(string path, string canonical)
    {
        VbaMirrorLayout.TryParse(path, out var parsed, out var reason).Should().BeTrue(reason);
        parsed!.RelativePath.Should().Be(canonical);
    }

    [Theory(DisplayName = "Windows の予約名のコンポーネントはミラーのファイルにできないため対象外になる")]
    [InlineData("Modules/CON.bas")]
    [InlineData("Modules/con.bas")]
    [InlineData("Classes/PRN.cls")]
    [InlineData("Modules/AUX.bas")]
    [InlineData("Modules/NUL.bas")]
    [InlineData("Modules/COM1.bas")]
    [InlineData("Modules/COM9.bas")]
    [InlineData("Modules/LPT1.bas")]
    [InlineData("Modules/LPT9.bas")]
    public void 予約名は対象外(string path)
    {
        VbaMirrorLayout.TryParse(path, out _, out var reason).Should().BeFalse();
        reason.Should().Contain("予約名");
    }

    [Theory(DisplayName = "予約名に似ているだけの名前は対象になる（COM0・COM10・CONSOLE）")]
    [InlineData("Modules/COM0.bas")]
    [InlineData("Modules/COM10.bas")]
    [InlineData("Modules/CONSOLE.bas")]
    public void 予約名に似た名前は対象(string path)
    {
        VbaMirrorLayout.TryParse(path, out _, out _).Should().BeTrue();
    }

    [Fact(DisplayName = "日本語名や 31 文字を超える既存の名前は、ミラーとしては読める（新規作成の規則とは別）")]
    public void 既存の日本語名は読める()
    {
        VbaMirrorLayout.TryParse("Modules/集計処理.bas", out var parsed, out _).Should().BeTrue();
        parsed!.ComponentName.Should().Be("集計処理");

        VbaMirrorLayout.TryParse("Modules/" + new string('a', 40) + ".bas", out _, out _).Should().BeTrue();
    }

    [Fact(DisplayName = "名前にドットを含む（a.b.bas）ものは対象外になる")]
    public void ドットを含む名前は対象外()
    {
        VbaMirrorLayout.TryParse("Modules/a.b.bas", out _, out var reason).Should().BeFalse();
        reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact(DisplayName = "CollectUnsupported は対象外のファイルだけを E810 の警告にする")]
    public void 対象外を警告として列挙する()
    {
        var issues = VbaMirrorLayout.CollectUnsupported(new[]
        {
            "Modules/Ok.bas", "Modules/x.cls", "Modules/sub/y.bas", "notes.txt", "Modules/CON.bas",
        });

        issues.Select(i => i.Path).Should().BeEquivalentTo("Modules/x.cls", "Modules/sub/y.bas", "notes.txt", "Modules/CON.bas");
        issues.Should().OnlyContain(i => i.Code == ErrorCode.E810 && i.Severity == Severity.Warning);
    }

    [Theory(DisplayName = "COM の Type 値との対応（1/2/3/100 のみ。ActiveX デザイナ 11 などは対象外）")]
    [InlineData(1, VbaModuleKind.Standard)]
    [InlineData(2, VbaModuleKind.Class)]
    [InlineData(3, VbaModuleKind.Form)]
    [InlineData(100, VbaModuleKind.Document)]
    public void COMのType値と種別の対応(int comType, VbaModuleKind kind)
    {
        VbaModuleKinds.FromComType(comType).Should().Be(kind);
        VbaModuleKinds.ToComType(kind).Should().Be(comType);
    }

    [Theory(DisplayName = "未対応の COM の Type 値は null")]
    [InlineData(11)]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(-1)]
    public void 未対応のType値はnull(int comType)
    {
        VbaModuleKinds.FromComType(comType).Should().BeNull();
    }

    [Fact(DisplayName = "Excel 側に新規作成できるのは標準モジュールとクラスだけ")]
    public void 新規作成できる種別()
    {
        VbaModuleKinds.CanCreateOnExcel(VbaModuleKind.Standard).Should().BeTrue();
        VbaModuleKinds.CanCreateOnExcel(VbaModuleKind.Class).Should().BeTrue();
        VbaModuleKinds.CanCreateOnExcel(VbaModuleKind.Document).Should().BeFalse();
        VbaModuleKinds.CanCreateOnExcel(VbaModuleKind.Form).Should().BeFalse();
    }
}
