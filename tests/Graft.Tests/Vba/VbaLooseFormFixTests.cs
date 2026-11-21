using System.IO;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.UiTests.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 不具合 2（文字列リテラル・コメント・字下げだけの Excel 側の編集が取り込まれず、次の反映で消える）の回帰テスト。
/// 修正前の <c>ToLooseForm</c> は文字列・コメントの中も含めて空白を全部除いて大文字化していたので、
/// <c>MsgBox "hi"</c> と <c>MsgBox "H I"</c> のゆるいハッシュが一致し、Excel 側の編集は「整形差だけ」と見なされて
/// 取り込まれず、衝突にも控えにもならずに Push で上書きされた。
/// </summary>
public class VbaLooseFormFixTests : VbaServiceFixture
{
    private static readonly DateTimeOffset At = new(2026, 11, 21, 10, 0, 0, TimeSpan.FromHours(9));

    // ------------------------------------------------------------------
    // ゆるい形（純ロジック）
    // ------------------------------------------------------------------

    [Theory(DisplayName = "不具合2 ゆるい形: 文字列リテラルとコメントの中の違い（空白・大文字小文字）は別物")]
    [InlineData("MsgBox \"hi\"", "MsgBox \"H I\"")]
    [InlineData("MsgBox \"Delete All\"", "MsgBox \"DELETEALL\"")]
    [InlineData("x = 1 ' 後で直す", "x = 1 ' 後で なおす")]
    [InlineData("' note", "'  note")]
    [InlineData("Rem hello world", "Rem helloworld")]
    [InlineData("s = \"a  b\"", "s = \"a b\"")]
    public void 文字列とコメントの中は厳密に比べる(string a, string b)
    {
        VbaTextNormalizer.LooseHash(a).Should().NotBe(VbaTextNormalizer.LooseHash(b));
    }

    [Theory(DisplayName = "不具合2 ゆるい形: 文字列・コメントの外の空白と大文字小文字の差（VBE の整形）は無視する")]
    [InlineData("x=1", "x = 1")]
    [InlineData("dim x as long", "Dim x As Long")]
    [InlineData("MYVAR = 1", "myVar = 1")]
    [InlineData("MsgBox   \"hi\"", "msgbox \"hi\"")]
    [InlineData("x = 1 ' same", "X=1 ' same")]
    [InlineData("x = 1   ", "x = 1")]
    [InlineData("x = 1 ' c   ", "x = 1 ' c")]
    [InlineData("rem note", "Rem note")]
    [InlineData("If a Then : rem x", "if a then: Rem x")]
    public void コードの部分の整形差は無視する(string a, string b)
    {
        VbaTextNormalizer.LooseHash(a).Should().Be(VbaTextNormalizer.LooseHash(b));
    }

    [Theory(DisplayName = "不具合2 ゆるい形: 行頭の字下げは残す（字下げの整理は利用者の編集）。空白だけの行は空の行と同じ")]
    [InlineData("\tIf a Then", "    If a Then", false)]
    [InlineData("  x = 1", "x = 1", false)]
    [InlineData("a\r\n   \r\nb", "a\r\n\r\nb", true)]
    public void 字下げは残す(string a, string b, bool equal)
    {
        if (equal) VbaTextNormalizer.LooseHash(a).Should().Be(VbaTextNormalizer.LooseHash(b));
        else VbaTextNormalizer.LooseHash(a).Should().NotBe(VbaTextNormalizer.LooseHash(b));
    }

    [Fact(DisplayName = "不具合2 ゆるい形: 文字列の中のアポストロフィはコメントの始まりではなく、コメントの中の引用符は文字列の始まりではない")]
    public void 引用符とアポストロフィの入れ子()
    {
        // "it's" の ' はコメントではない: 後ろの空白と大文字小文字はコードとして緩めてよい。
        VbaTextNormalizer.LooseHash("s = \"it's\" & x").Should().Be(VbaTextNormalizer.LooseHash("S=\"it's\"&X"));
        // コメントの中の " は文字列の始まりではない: そのあとの行が緩まないことはなく、コメントは行末まで厳密。
        VbaTextNormalizer.LooseHash("x = 1 ' say \"hi").Should().NotBe(VbaTextNormalizer.LooseHash("x = 1 ' say \"HI"));
    }

    [Fact(DisplayName = "不具合2 書き込み後の検証: 文字列だけが違う読み直しは、整形差ではなく別内容（控えを書き戻す側）になる")]
    public void 検証は文字列の違いを整形差としない()
    {
        VbaWriteVerifier.Verify("MsgBox \"hi\"", "MsgBox \"H I\"").Should().Be(VbaWriteVerdict.DifferentContent);
        VbaWriteVerifier.Verify("x=1", "X = 1").Should().Be(VbaWriteVerdict.FormattingDifference);
    }

    // ------------------------------------------------------------------
    // Planner（純ロジック）と移行
    // ------------------------------------------------------------------

    private static VbaMirrorModule Mirror(string text)
        => new("Modules/Module1.bas", VbaModuleKind.Standard, "Module1", VbaTextNormalizer.Hash(text), VbaTextNormalizer.LooseHash(text));

    private static VbaExcelModule ExcelModule(string text)
        => new("Module1", VbaModuleKind.Standard, VbaTextNormalizer.Hash(text), VbaTextNormalizer.LooseHash(text));

    private static VbaSyncState StateOf(string text, int looseVersion, string? looseHash = null)
        => new VbaSyncState { WorkbookFullName = Book }.WithModule("Modules/Module1.bas", VbaModuleState.Create(
            VbaModuleKind.Standard, "Module1", null, VbaTextNormalizer.Hash(text), VbaTextNormalizer.Hash(text),
            looseHash ?? VbaTextNormalizer.LooseHash(text), At) with { LooseFormVersion = looseVersion });

    /// <summary>旧算法（文字列・コメントの中も含めて空白を全部除いて大文字化）のゆるいハッシュ。移行のテスト用。</summary>
    private static string LegacyLooseHash(string text)
    {
        var chars = VbaTextNormalizer.ToCrLf(text).Where(c => c is not (' ' or '\t')).Select(char.ToUpperInvariant);
        return System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(new string(chars.ToArray()).TrimEnd('\r', '\n')))).ToLowerInvariant();
    }

    [Fact(DisplayName = "不具合2 Planner: 文字列リテラルだけを Excel 側で直したら Pull（取り込む）。以前は何もしない扱いで消えていた")]
    public void 文字列の編集はExcel側の変更になる()
    {
        const string baseText = "Sub A()\r\n  MsgBox \"hi\"\r\nEnd Sub";
        const string edited = "Sub A()\r\n  MsgBox \"H I\"\r\nEnd Sub";

        var plan = VbaSyncPlanner.Plan(new[] { Mirror(baseText) }, new[] { ExcelModule(edited) }, StateOf(baseText, VbaTextNormalizer.LooseFormVersion), At);

        plan.Modules.Single().Action.Should().Be(VbaSyncAction.Pull);
    }

    [Fact(DisplayName = "不具合2 Planner: 同じ編集でミラー側も別の行を直していたら衝突（以前は Push で Excel 側の編集が黙って消えた）")]
    public void 文字列の編集とミラーの編集は衝突になる()
    {
        const string baseText = "Sub A()\r\n  MsgBox \"hi\"\r\nEnd Sub";
        const string excelEdit = "Sub A()\r\n  MsgBox \"H I\"\r\nEnd Sub";
        const string mirrorEdit = "Sub A()\r\n  MsgBox \"hi\"\r\n  Debug.Print 1\r\nEnd Sub";

        var plan = VbaSyncPlanner.Plan(new[] { Mirror(mirrorEdit) }, new[] { ExcelModule(excelEdit) }, StateOf(baseText, VbaTextNormalizer.LooseFormVersion), At);

        plan.Modules.Single().Action.Should().Be(VbaSyncAction.Conflict);
    }

    [Fact(DisplayName = "不具合2 Planner: VBE の整形差（空白・大文字小文字だけ）は従来どおり Excel 側の変更にならない")]
    public void 整形差は変更にならない()
    {
        const string baseText = "Sub A()\r\n  x = 1\r\nEnd Sub";
        const string formatted = "Sub A()\r\n  X  =  1\r\nEnd Sub";

        var plan = VbaSyncPlanner.Plan(new[] { Mirror(baseText) }, new[] { ExcelModule(formatted) }, StateOf(baseText, VbaTextNormalizer.LooseFormVersion), At);

        plan.Modules.Single().Action.Should().Be(VbaSyncAction.None);
        plan.Modules.Single().Baseline!.ExcelHash.Should().Be(VbaTextNormalizer.Hash(formatted));
    }

    [Fact(DisplayName = "不具合2 移行: 版の記録がない古い同期状態（版 0）は、Excel 側が厳密に同じなら何もせず、基準をいまの算法に更新する")]
    public void 古い基準は厳密に一致すれば更新される()
    {
        const string text = "Sub A()\r\n  MsgBox \"hi\"\r\nEnd Sub";
        var legacy = StateOf(text, looseVersion: 0, looseHash: LegacyLooseHash(text));

        var plan = VbaSyncPlanner.Plan(new[] { Mirror(text) }, new[] { ExcelModule(text) }, legacy, At);

        var module = plan.Modules.Single();
        module.Action.Should().Be(VbaSyncAction.None);
        module.Baseline!.LooseFormVersion.Should().Be(VbaTextNormalizer.LooseFormVersion);
        module.Baseline.ExcelLooseHash.Should().Be(VbaTextNormalizer.LooseHash(text));
    }

    [Fact(DisplayName = "不具合2 移行: 版 0 の記録では旧算法の excelLooseHash を使わない。文字列だけを直した Excel 側は、最初の 1 回から取り込まれる")]
    public void 古い基準でも文字列の編集を見逃さない()
    {
        const string baseText = "Sub A()\r\n  MsgBox \"hi\"\r\nEnd Sub";
        const string edited = "Sub A()\r\n  MsgBox \"H I\"\r\nEnd Sub";
        var legacy = StateOf(baseText, looseVersion: 0, looseHash: LegacyLooseHash(baseText));

        // 旧算法のゆるいハッシュでは両者は一致してしまう（これが不具合の原因）。
        LegacyLooseHash(baseText).Should().Be(LegacyLooseHash(edited));

        var plan = VbaSyncPlanner.Plan(new[] { Mirror(baseText) }, new[] { ExcelModule(edited) }, legacy, At);

        plan.Modules.Single().Action.Should().Be(VbaSyncAction.Pull);
    }

    // ------------------------------------------------------------------
    // VbaSyncService + FakeVbaBridge
    // ------------------------------------------------------------------

    [Fact(DisplayName = "不具合2 再現: Excel 側の文字列を直したあと、別の行を Graft で直して Push しても、Excel 側の編集は消えず衝突になる")]
    public async Task 文字列の編集はPushで消えない()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "Sub A()\r\n  MsgBox \"hi\"\r\nEnd Sub"));
        Excel("Module1").Code = "Sub A()\r\n  MsgBox \"H I\"\r\nEnd Sub";
        File.WriteAllText(MirrorFile("Modules/Module1.bas"), "Sub A()\r\n  MsgBox \"hi\"\r\n  Debug.Print 1\r\nEnd Sub\r\n");

        var push = await Service.PushAsync(project);

        push.Value.Conflicts.Should().Equal("Modules/Module1.bas");
        push.Value.Pushed.Should().BeEmpty();
        Excel("Module1").Code.Should().Be("Sub A()\r\n  MsgBox \"H I\"\r\nEnd Sub", "Excel 側の編集は上書きされない");
    }

    [Fact(DisplayName = "不具合2 再現: 文字列・コメントだけを Excel 側で直したモジュールは、取込でミラーへ入る")]
    public async Task 文字列の編集は取込で入る()
    {
        // 空白と大文字小文字だけの違いを、文字列とコメントの中に入れる（旧算法では「整形差」と見なされて取り込まれなかった）。
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "Sub A()\r\n  MsgBox \"hi\" ' old note\r\nEnd Sub"));
        Excel("Module1").Code = "Sub A()\r\n  MsgBox \"H I\" ' OLD  Note\r\nEnd Sub";

        var pull = await Service.PullAsync(project);

        pull.Value.Pulled.Should().Equal("Modules/Module1.bas");
        File.ReadAllText(MirrorFile("Modules/Module1.bas")).Should().Contain("\"H I\"").And.Contain("' OLD  Note");
    }

    [Fact(DisplayName = "不具合2 保険: Excel 側の本文が基準と厳密に違うまま（整形差と判断して）Push で上書きするときは、上書きの前に .rescue/ に控える")]
    public async Task 整形差でも上書きの前に控える()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "Sub A()\r\n  x = 1\r\nEnd Sub"));
        // VBE の整形で Excel 側が変わった（空白と大文字小文字だけ）。ミラーは Graft で直した。
        Excel("Module1").Code = "Sub A()\r\n  X  =  1\r\nEnd Sub";
        File.WriteAllText(MirrorFile("Modules/Module1.bas"), "Sub A()\r\n  x = 2\r\nEnd Sub\r\n");

        var push = await Service.PushAsync(project);

        push.Value.Pushed.Should().Equal("Modules/Module1.bas");
        Excel("Module1").Code.Should().Be("Sub A()\r\n  x = 2\r\nEnd Sub");
        RescueTexts().Should().ContainSingle().Which.Should().Be("Sub A()\r\n  X  =  1\r\nEnd Sub", "上書きされる前の Excel 側の本文");
    }

    [Fact(DisplayName = "不具合2 保険: Excel 側が基準と厳密に同じなら、通常の Push では控えを増やさない")]
    public async Task 厳密に同じなら控えは作らない()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "Sub A()\r\nEnd Sub"));
        File.WriteAllText(MirrorFile("Modules/Module1.bas"), "Sub A()\r\n  Debug.Print 1\r\nEnd Sub\r\n");

        (await Service.PushAsync(project)).Value.Pushed.Should().HaveCount(1);

        RescueTexts().Should().BeEmpty();
    }

    [Fact(DisplayName = "不具合2 移行: 版 0 の同期状態でも、Excel 側が基準と厳密に同じなら Pull しない。Pull などで保存した基準はいまの版になる")]
    public async Task 古い同期状態からの移行()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "Sub A()\r\n  MsgBox \"hi\"\r\nEnd Sub"), FakeVbaBridge.Std("Module2", "x"));
        var state = await StateAsync();
        var legacy = state.Modules.ToDictionary(kv => kv.Key, kv => kv.Value with { LooseFormVersion = 0, ExcelLooseHash = LegacyLooseHash("zzz") });
        await States.SaveAsync(project.Id, state with { Modules = legacy });

        var pull = await Service.PullAsync(project);

        pull.Value.IsEmpty.Should().BeTrue("厳密に同じなので取り込むものはない");
        var migrated = await StateAsync();
        migrated.Modules.Values.Should().OnlyContain(m => m.LooseFormVersion == VbaTextNormalizer.LooseFormVersion);
        migrated.Modules["Modules/Module1.bas"].ExcelLooseHash
            .Should().Be(VbaTextNormalizer.LooseHash("Sub A()\r\n  MsgBox \"hi\"\r\nEnd Sub"));
    }
}
