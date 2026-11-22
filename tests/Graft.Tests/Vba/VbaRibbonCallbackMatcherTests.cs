using FluentAssertions;
using Graft.Core.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaRibbonCallbackMatcher"/>（コールバック名を VBA のミラーと照合する純ロジック）のテスト。
/// 仕様書_ブックの情報と依頼の観点 15.4・15.8。Public／Private、モジュール名つき、大文字小文字。
/// </summary>
public class VbaRibbonCallbackMatcherTests
{
    private static VbaRibbonSourceModule Std(string name, string text) => new(name, VbaModuleKind.Standard, text);

    private static readonly VbaRibbonSourceModule Module1 = Std("Module1",
        "Option Explicit\r\n\r\n"
        + "Public Sub OnRun(control As IRibbonControl)\r\n  MsgBox \"x\"\r\nEnd Sub\r\n\r\n"
        + "Sub NoModifier(control As IRibbonControl)\r\nEnd Sub\r\n\r\n"
        + "Private Sub HiddenOne(control As IRibbonControl)\r\nEnd Sub\r\n\r\n"
        + "Friend Function FriendOne() As Long\r\nEnd Function\r\n\r\n"
        + "Public Function GetEnabled(control As IRibbonControl, ByRef returnedVal)\r\nEnd Function\r\n\r\n"
        + "' Public Sub Commented()\r\n"
        + "Rem Public Sub RemLine()\r\n"
        + "Public Declare Function ApiCall Lib \"kernel32\" () As Long\r\n"
        + "Public Sub 日本語の手続き(control As IRibbonControl)\r\nEnd Sub\r\n");

    private static readonly VbaRibbonSourceModule Module2 = Std("Module2", "Public Sub OnRun()\r\nEnd Sub\r\nPublic Sub OnlyInTwo()\r\nEnd Sub\r\n");

    private static VbaRibbonCallbackStatus Status(string name, params VbaRibbonSourceModule[] modules)
        => VbaRibbonCallbackMatcher.Resolve(name, modules).Status;

    [Theory(DisplayName = "Public／修飾なしの Sub・Function は「あります」（大文字小文字は区別しない）")]
    [InlineData("OnRun")]
    [InlineData("onrun")]
    [InlineData("ONRUN")]
    [InlineData("NoModifier")]
    [InlineData("GetEnabled")]
    [InlineData("日本語の手続き")]
    public void Publicは見つかる(string name)
    {
        Status(name, Module1).Should().Be(VbaRibbonCallbackStatus.Found);
    }

    [Theory(DisplayName = "Private・Friend は「呼べない」（Public ではない）")]
    [InlineData("HiddenOne", "Private")]
    [InlineData("friendone", "Friend")]
    public void Publicでない(string name, string scope)
    {
        var (status, detail) = VbaRibbonCallbackMatcher.Resolve(name, new[] { Module1 });

        status.Should().Be(VbaRibbonCallbackStatus.NotPublic);
        detail.Should().Contain(scope).And.Contain("Module1");
    }

    [Theory(DisplayName = "コメント行・Rem・Declare Function・無い名前は「見つかりません」")]
    [InlineData("Commented")]
    [InlineData("RemLine")]
    [InlineData("ApiCall")]
    [InlineData("Missing")]
    [InlineData("")]
    public void 見つからない(string name)
    {
        var (status, detail) = VbaRibbonCallbackMatcher.Resolve(name, new[] { Module1 });

        status.Should().Be(VbaRibbonCallbackStatus.NotFound);
        detail.Should().Contain("VBA に見つかりません");
    }

    [Fact(DisplayName = "モジュール名つき（Module1.OnRun）: そのモジュールの中だけを探す。別のモジュールにしかなければ見つからない")]
    public void モジュール名つき()
    {
        Status("Module1.OnRun", Module1, Module2).Should().Be(VbaRibbonCallbackStatus.Found);
        Status("module2.onlyintwo", Module1, Module2).Should().Be(VbaRibbonCallbackStatus.Found, "モジュール名も大文字小文字を区別しない");
        Status("Module1.OnlyInTwo", Module1, Module2).Should().Be(VbaRibbonCallbackStatus.NotFound);
        Status("Module2.HiddenOne", Module1, Module2).Should().Be(VbaRibbonCallbackStatus.NotFound);
    }

    [Fact(DisplayName = "モジュール名つきで、そのモジュールが無ければ、モジュールが無いと区別して伝える")]
    public void モジュールが無い()
    {
        var (status, detail) = VbaRibbonCallbackMatcher.Resolve("Nothing.OnRun", new[] { Module1 });

        status.Should().Be(VbaRibbonCallbackStatus.ModuleNotFound);
        detail.Should().Contain("Nothing");
    }

    [Theory(DisplayName = "プロジェクト名・ブック名つきの指定も、最後の 2 つ（モジュール名と手続き名）で照合する")]
    [InlineData("VBAProject.Module1.OnRun")]
    [InlineData("計画.xlsm!Module1.OnRun")]
    [InlineData("計画.xlsm!OnRun")]
    public void 長い修飾(string name)
    {
        Status(name, Module1).Should().Be(VbaRibbonCallbackStatus.Found);
    }

    [Fact(DisplayName = "同名が複数のモジュールにあれば、見つかった上で、その旨を伝える")]
    public void 複数のモジュール()
    {
        var (status, detail) = VbaRibbonCallbackMatcher.Resolve("OnRun", new[] { Module1, Module2 });

        status.Should().Be(VbaRibbonCallbackStatus.Found);
        detail.Should().Contain("Module1").And.Contain("Module2").And.Contain("複数");
    }

    [Fact(DisplayName = "Public が 1 つでもあれば、別のモジュールの Private があっても「あります」")]
    public void Publicが優先()
    {
        var other = Std("Other", "Private Sub OnlyInTwo()\r\nEnd Sub\r\n");

        Status("OnlyInTwo", other, Module2).Should().Be(VbaRibbonCallbackStatus.Found);
    }

    [Fact(DisplayName = "クラスモジュールにある場合は、種類つきで場所を示す")]
    public void クラスモジュール()
    {
        var cls = new VbaRibbonSourceModule("Handler", VbaModuleKind.Class, "Public Sub OnClassOnly()\r\nEnd Sub\r\n");

        var (status, detail) = VbaRibbonCallbackMatcher.Resolve("OnClassOnly", new[] { cls });

        status.Should().Be(VbaRibbonCallbackStatus.Found);
        detail.Should().Contain("クラス").And.Contain("Handler");
    }

    [Fact(DisplayName = "照合: 名前ごとにまとめ、使われている所を集める（大文字小文字違いは同じ名前）。最初に出た順")]
    public void 名前ごとにまとめる()
    {
        var refs = new[]
        {
            new VbaRibbonCallbackRef("onAction", "OnRun", "button a"),
            new VbaRibbonCallbackRef("onAction", "Missing", "button b"),
            new VbaRibbonCallbackRef("onAction", "ONRUN", "button c"),
            new VbaRibbonCallbackRef("onAction", "OnRun", "button a"),
        };

        var checks = VbaRibbonCallbackMatcher.Check(refs, new[] { Module1 });

        checks.Select(c => (c.Name, c.Status)).Should().Equal(
            ("OnRun", VbaRibbonCallbackStatus.Found), ("Missing", VbaRibbonCallbackStatus.NotFound));
        checks[0].Usages.Should().Equal("onAction（button a）", "onAction（button c）");
    }

    [Fact(DisplayName = "ミラーを読めなかった（モジュールが null）ときは、すべて「照合できていません」")]
    public void ミラーが無い()
    {
        var refs = new[] { new VbaRibbonCallbackRef("onAction", "OnRun", "button a") };

        var checks = VbaRibbonCallbackMatcher.Check(refs, null);

        checks.Should().ContainSingle().Which.Status.Should().Be(VbaRibbonCallbackStatus.Unchecked);
    }

    [Fact(DisplayName = "モジュールが 1 つも無いミラーでは、すべて「見つかりません」")]
    public void モジュールが空()
    {
        var refs = new[] { new VbaRibbonCallbackRef("onAction", "OnRun", "button a") };

        VbaRibbonCallbackMatcher.Check(refs, Array.Empty<VbaRibbonSourceModule>())
            .Should().ContainSingle().Which.Status.Should().Be(VbaRibbonCallbackStatus.NotFound);
    }
}
