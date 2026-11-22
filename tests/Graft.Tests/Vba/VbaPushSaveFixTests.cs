using System.IO;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.UiTests.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 不具合 4（書き戻しに失敗したモジュール（E809）や状態不明（E805）があっても、ブックが保存される）の回帰テスト。
/// 修正前は保存条件が <c>SaveWorkbookAfterPush &amp;&amp; 書けた件数 &gt; 0</c> だけで、壊れたまま
/// （<c>…End Sub'???</c>）のモジュールがあっても <c>SaveCount=1</c> になり、「保存せずに閉じれば元に戻る」
/// という最後の逃げ道が無くなっていた。
/// </summary>
public class VbaPushSaveFixTests : VbaServiceFixture
{
    private static VbaLink SaveLink() => new() { WorkbookFullName = Book, SaveWorkbookAfterPush = true };

    private async Task<Project> ProjectWithTwoEditedModulesAsync()
    {
        await CreatedProjectAsync(
            FakeVbaBridge.Std("Module1", "Sub A()\r\nEnd Sub"),
            FakeVbaBridge.Cls("Cls1", "Option Explicit"));
        File.WriteAllText(MirrorFile("Modules/Module1.bas"), "Sub A()\r\n  Debug.Print 1\r\nEnd Sub\r\n");
        File.WriteAllText(MirrorFile("Classes/Cls1.cls"), "Option Explicit\r\n' edited\r\n");
        return NewProject(SaveLink());
    }

    [Fact(DisplayName = "不具合4 再現: 書き込み不一致かつ書き戻しにも失敗（E809）したモジュールがあれば、他が成功してもブックを保存しない。警告で知らせ、控えは .rescue/ に残る")]
    public async Task 書き戻し失敗があれば保存しない()
    {
        var project = await ProjectWithTwoEditedModulesAsync();
        Bridge.CorruptOnWrite.Add("Module1");
        Bridge.RollbackFails.Add("Module1");

        var push = await Service.PushAsync(project);

        push.IsSuccess.Should().BeTrue();
        push.Value.Pushed.Should().Equal("Classes/Cls1.cls");
        push.Value.Failed.Should().Equal("Modules/Module1.bas");
        push.Value.WorkbookSaved.Should().BeFalse();
        Bridge.Get(Book)!.SaveCount.Should().Be(0, "壊れたまま保存すると、保存せずに閉じる逃げ道が無くなる");
        Bridge.Calls.Should().NotContain("Save");
        push.Issues.Should().Contain(i => i.Code == ErrorCode.E809 && i.Severity == Severity.Warning && i.Detail!.Contains("保存しませんでした"));
        RescueTexts().Should().ContainSingle().Which.Should().Be("Sub A()\r\nEnd Sub", "元の本文は .rescue/ にある（従来の控え。自動控えとは別に残り、自動では消えない）");
    }

    [Fact(DisplayName = "不具合4: 書き戻しに成功した（RolledBack）モジュールがあるだけなら、成功したものがある限り従来どおりブックを保存する")]
    public async Task 書き戻せたなら保存する()
    {
        var project = await ProjectWithTwoEditedModulesAsync();
        Bridge.CorruptOnWrite.Add("Module1");

        var push = await Service.PushAsync(project);

        push.Value.Pushed.Should().Equal("Classes/Cls1.cls");
        push.Value.Failed.Should().Equal("Modules/Module1.bas");
        push.Value.WorkbookSaved.Should().BeTrue();
        Bridge.Get(Book)!.SaveCount.Should().Be(1);
        Excel("Module1").Code.Should().Be("Sub A()\r\nEnd Sub", "書き戻されて元の本文のまま");
    }

    [Fact(DisplayName = "不具合4: 書く前に止めた失敗（E811: Shift_JIS で表せない文字）は Excel 側を触っていないので、他が成功すれば保存する")]
    public async Task 書く前に止めた失敗は保存を妨げない()
    {
        var project = await ProjectWithTwoEditedModulesAsync();
        File.WriteAllText(MirrorFile("Modules/Module1.bas"), "Sub A()\r\n  Debug.Print \"😀\"\r\nEnd Sub\r\n");

        var push = await Service.PushAsync(project);

        push.Value.Failed.Should().Equal("Modules/Module1.bas");
        push.Value.Pushed.Should().Equal("Classes/Cls1.cls");
        push.Value.WorkbookSaved.Should().BeTrue();
    }
}
