using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Infra;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;

namespace Graft.UiTests.Vba;

/// <summary>
/// Excel VBA 連携 段階 P3 のシナリオ（仕様書_VBA連携 10.2）のうち、適用の<b>前</b>の部分:
/// 解析前の自動 Pull（7.3-1）、適用直前の再検査（7.3-3）、画面側のガード（E814・E811。7.3-2）。
/// 適用の後（Push・失敗・取り消し）は <see cref="VbaP3PushTests"/>、取り消し・復元の後は <see cref="VbaP3RestoreTests"/>。
/// </summary>
public class VbaP3PullAndGuardTests
{
    private const string Module1 = "Modules/Module1.bas";
    private const string Cls1 = "Classes/Cls1.cls";

    // ------------------------------------------------------------------
    // 解析前の自動 Pull（7.3-1）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "解析前の自動 Pull: Excel 側だけ変更したモジュールを取り込んでから解析し、ステータスに「Excel 側の変更を取り込みました」。対象外のモジュールは取り込まない")]
    public async Task 解析前に対象モジュールだけ自動でPullされる()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        r.Excel("Module1").Code = "Sub A()\r\n  MsgBox \"hi\"\r\n  Debug.Print 1\r\nEnd Sub";
        r.Excel("Cls1").Code = "Option Explicit\r\nPublic Value As Double";

        // 取り込まなければ見つからない行（Excel 側にだけある）を SEARCH にする。
        await r.PasteAsync(VbaP3Rig.Edit(Module1, "  Debug.Print 1", "  Debug.Print 2")).ConfigureAwait(true);

        r.Mirror(Module1).Should().Contain("Debug.Print 1", "対象のモジュールは解析の前に取り込まれた");
        r.Mirror(Cls1).Should().Contain("As Long", "パッチの対象外のモジュールは取り込まない");
        r.Graft.Blocks.Should().ContainSingle().Which.Plan.CanApply.Should().BeTrue();
        r.Graft.Notice.Text.Should().Be("Excel 側の変更を取り込みました");
        (await r.RevisionsAsync().ConfigureAwait(true)).Should().BeEmpty("Pull は履歴に残さない");
    }

    [AvaloniaFact(DisplayName = "解析前の衝突: 衝突ダイアログ（解析前の場面。「Excel 側を採用」が選べる）→ 中止なら解析しない。ミラーも Excel も変わらない")]
    public async Task 解析前の衝突で中止すると解析しない()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        File.WriteAllText(r.MirrorPath(Module1), "Sub A()\r\n  ' graft side\r\nEnd Sub\r\n");
        r.Excel("Module1").Code = "Sub A()\r\n  ' excel side\r\nEnd Sub";
        r.ConflictAnswer = _ => VbaConflictDecision.Abort;

        await r.PasteAsync(VbaP3Rig.Edit(Module1, "  ' graft side", "  ' edited")).ConfigureAwait(true);

        var request = r.Conflicts.Should().ContainSingle().Subject;
        request.Scene.Should().Be(VbaConflictScene.BeforeAnalysis);
        request.CanAdoptExcel.Should().BeTrue();
        request.CanAbort.Should().BeTrue();
        request.CanSkip.Should().BeFalse();
        request.Items.Should().ContainSingle().Which.Should().Match<VbaConflictItem>(
            i => i.RelativePath == Module1 && i.ExcelText.Contains("excel side") && i.MirrorText.Contains("graft side"));
        r.Graft.Blocks.Should().BeEmpty("中止したので解析しない");
        r.Graft.State.Should().Be(CenterPaneState.Error);
        r.Graft.CenterError!.Code.Should().Be(ErrorCode.E812, "衝突が解決されずに止まったので E812 を残す");
        r.Graft.CenterError.Detail.Should().Contain(Module1);
        r.Mirror(Module1).Should().Contain("graft side");
        r.Excel("Module1").Code.Should().Contain("excel side");
    }

    [AvaloniaFact(DisplayName = "解析前の衝突: 「Excel 側を採用」ならミラーが Excel の内容になり、その内容に対して解析される")]
    public async Task 解析前の衝突でExcel側を採用する()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        File.WriteAllText(r.MirrorPath(Module1), "Sub A()\r\n  ' graft side\r\nEnd Sub\r\n");
        r.Excel("Module1").Code = "Sub A()\r\n  ' excel side\r\nEnd Sub";
        r.ConflictAnswer = request => VbaConflictDecision.All(request, VbaConflictChoice.AdoptExcel);

        await r.PasteAsync(VbaP3Rig.Edit(Module1, "  ' excel side", "  ' edited")).ConfigureAwait(true);

        r.Mirror(Module1).Should().Contain("excel side").And.NotContain("graft side");
        r.Graft.Blocks.Should().ContainSingle().Which.Plan.CanApply.Should().BeTrue();
        r.Graft.Notice.Text.Should().Be("Excel 側の変更を取り込みました");
    }

    [AvaloniaFact(DisplayName = "解析前の衝突: 「Graft 側で上書き」なら Excel が Graft の内容になり、Excel 側の本文は .rescue/ に控えられる")]
    public async Task 解析前の衝突でGraft側で上書きする()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        File.WriteAllText(r.MirrorPath(Module1), "Sub A()\r\n  ' graft side\r\nEnd Sub\r\n");
        r.Excel("Module1").Code = "Sub A()\r\n  ' excel side\r\nEnd Sub";
        r.ConflictAnswer = request => VbaConflictDecision.All(request, VbaConflictChoice.OverwriteExcel);

        await r.PasteAsync(VbaP3Rig.Edit(Module1, "  ' graft side", "  ' edited")).ConfigureAwait(true);

        r.Excel("Module1").Code.Should().Contain("graft side").And.NotContain("excel side");
        r.RescueTexts().Should().ContainSingle().Which.Should().Contain("excel side");
        r.Graft.Blocks.Should().ContainSingle().Which.Plan.CanApply.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "解析前に Excel へ接続できない（E804）: 「ファイルだけ適用する」「中止」。中止なら解析しない")]
    public async Task 接続できないときは中止できる()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        r.Bridge.CloseWorkbook(VbaP3Rig.Book);

        await r.PasteAsync(VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);

        var request = r.Choices.Should().ContainSingle().Subject;
        request.Title.Should().Be("Excel と同期できませんでした");
        request.Message.Should().Contain("E804");
        request.Options.Select(o => o.Label).Should().Equal("ファイルだけ適用する", "中止");
        request.Default.Id.Should().Be("abort", "既定は中止（何も起こさない側）");
        r.Graft.Blocks.Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "解析前に Excel へ接続できない: 「ファイルだけ適用する」なら適用でき、Excel へは Push せず（書き込みの呼び出しなし）、未反映の印と通知が付く")]
    public async Task ファイルだけ適用する()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        r.Bridge.CloseWorkbook(VbaP3Rig.Book);
        r.ChoiceAnswer = _ => "fileOnly";

        await r.PasteAsync(VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);
        r.Graft.Blocks.Should().ContainSingle();
        await r.ApplyAsync().ConfigureAwait(true);

        r.Mirror(Module1).Should().Contain("hello");
        r.Choices.Should().ContainSingle("適用の直前の再検査と Push では、聞き直さない");
        r.WriteCalls.Should().Be(0);
        r.Graft.ApplyUndoNoticeText.Should().Contain("Excel には未反映");
        r.Graft.IsApplyInProgress.Should().BeFalse();

        r.Bridge.AddWorkbook(VbaP3Rig.Book, FakeVbaBridge.Std("Module1", VbaP3Rig.Module1Original), FakeVbaBridge.Cls("Cls1", VbaP3Rig.Cls1Original));
        (await r.StatusAsync().ConfigureAwait(true)).UnpushedCount.Should().Be(1, "未反映として残る");
        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushToExcelCommand).ConfigureAwait(true);
        r.Excel("Module1").Code.Should().Contain("hello", "あとから「Excel へ反映」で送れる");
    }

    [AvaloniaFact(DisplayName = "Vba.PullBeforeDryRun=false: 自動では取り込まず確認ダイアログ（取り込む／取り込まずに続ける／中止）。それぞれの結果")]
    public async Task 自動で取り込まない設定では確認する()
    {
        var settings = new Settings { ShowPreview = false, Vba = new VbaSettings { PullBeforeDryRun = false } };

        using (var r = await VbaP3Rig.CreateAsync(settings).ConfigureAwait(true))
        {
            r.Excel("Module1").Code = "Sub A()\r\n  MsgBox \"hi\"\r\n  Debug.Print 1\r\nEnd Sub";
            r.ChoiceAnswer = _ => "skip";
            await r.PasteAsync(VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);

            var request = r.Choices.Should().ContainSingle().Subject;
            request.Title.Should().Be("Excel 側の変更があります");
            request.Options.Select(o => o.Label).Should().Equal("取り込む", "取り込まずに続ける", "中止");
            r.Mirror(Module1).Should().NotContain("Debug.Print 1", "取り込まずに続けた");
            r.Graft.Blocks.Should().ContainSingle();
        }

        using (var r = await VbaP3Rig.CreateAsync(settings).ConfigureAwait(true))
        {
            r.Excel("Module1").Code = "Sub A()\r\n  MsgBox \"hi\"\r\n  Debug.Print 1\r\nEnd Sub";
            r.ChoiceAnswer = _ => "pull";
            await r.PasteAsync(VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);

            r.Mirror(Module1).Should().Contain("Debug.Print 1", "取り込んだ");
            r.Graft.Notice.Text.Should().Be("Excel 側の変更を取り込みました");
        }

        using (var r = await VbaP3Rig.CreateAsync(settings).ConfigureAwait(true))
        {
            r.Excel("Module1").Code = "Sub A()\r\n  MsgBox \"hi\"\r\n  Debug.Print 1\r\nEnd Sub";
            r.ChoiceAnswer = _ => "abort";
            await r.PasteAsync(VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);

            r.Graft.Blocks.Should().BeEmpty("中止");
            r.Mirror(Module1).Should().NotContain("Debug.Print 1");
        }

        using (var r = await VbaP3Rig.CreateAsync(settings).ConfigureAwait(true))
        {
            // Excel 側に変更が無ければ、確認は出さずにそのまま解析する。
            await r.PasteAsync(VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);
            r.Choices.Should().BeEmpty();
            r.Graft.Blocks.Should().ContainSingle();
        }
    }

    // ------------------------------------------------------------------
    // 適用直前の再検査（7.3-3）
    // ------------------------------------------------------------------


    private static string[] TwoModulePatch() => new[]
    {
        VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\""),
        VbaP3Rig.Edit(Cls1, "Public Value As Long", "Public Value As Double"),
    };

    [AvaloniaFact(DisplayName = "再検査: 解析の後に Excel 側が変わったら「取り込んで解析をやり直す」。チェック状態は引き継がれ、取り込んだ内容にパッチが当たって Excel へ反映される")]
    public async Task 再検査で取り込んで解析をやり直す()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await r.PasteAsync(TwoModulePatch()).ConfigureAwait(true);
        r.Block(Cls1).IsSelected = false; // 利用者が外したチェック。
        r.Excel("Module1").Code = VbaP3Rig.Module1Original + "\r\nSub B()\r\nEnd Sub";
        r.ChoiceAnswer = request => request.Title == "Excel 側が変更されています" ? "reanalyze" : null;

        await r.ApplyAsync().ConfigureAwait(true);

        var recheck = r.Choices.Single(c => c.Title == "Excel 側が変更されています");
        recheck.Options.Select(o => o.Label).Should().Equal("取り込んで解析をやり直す", "このまま続ける", "中止");
        r.Mirror(Module1).Should().Contain("hello").And.Contain("Sub B()", "Excel 側の変更を取り込んだ内容にパッチが当たった");
        r.Mirror(Cls1).Should().Contain("As Long", "外していたチェックは再解析でも外れたまま");
        r.Excel("Module1").Code.Should().Contain("hello").And.Contain("Sub B()");
        r.Excel("Cls1").Code.Should().Be(VbaP3Rig.Cls1Original);
        r.Graft.ApplyUndoNoticeText.Should().Contain("Excel へ反映済み");
    }

    [AvaloniaFact(DisplayName = "再検査: 取り込んだ内容にパッチが当たらなくなったら、解析し直した結果を残したまま適用を始めず、理由を案内する")]
    public async Task 取り込むとパッチが当たらなくなるときは適用しない()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await r.PasteAsync(TwoModulePatch()).ConfigureAwait(true);
        r.Excel("Module1").Code = "Sub A()\r\n  MsgBox \"changed in excel\"\r\nEnd Sub"; // パッチの SEARCH が消えた
        r.ChoiceAnswer = _ => "reanalyze";

        await r.ApplyAsync().ConfigureAwait(true);

        r.H.Dialogs.Messages.Should().Contain(m => m.Title == "適用できない変更があります");
        r.Mirror(Module1).Should().Contain("changed in excel").And.NotContain("hello");
        (await r.RevisionsAsync().ConfigureAwait(true)).Should().BeEmpty();
        r.Graft.Blocks.Should().NotBeEmpty("解析し直した結果は画面に残す");
        r.Graft.IsApplyInProgress.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "再検査: 「このまま続ける」なら取り込まずに適用し、Push のとき衝突として扱う")]
    public async Task このまま続けるとPushで衝突になる()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await r.PasteAsync(TwoModulePatch()).ConfigureAwait(true);
        r.Excel("Module1").Code = VbaP3Rig.Module1Original + "\r\nSub B()\r\nEnd Sub";
        r.ChoiceAnswer = _ => "continue";

        await r.ApplyAsync().ConfigureAwait(true);

        r.Mirror(Module1).Should().Contain("hello").And.NotContain("Sub B()", "取り込んでいない");
        r.Conflicts.Should().ContainSingle().Which.Scene.Should().Be(VbaConflictScene.AfterApply, "Push の直前に衝突として確認される");
    }

    [AvaloniaFact(DisplayName = "再検査: 「中止」なら適用を始めない（ファイルも履歴も変わらない）")]
    public async Task 再検査で中止すると適用しない()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await r.PasteAsync(TwoModulePatch()).ConfigureAwait(true);
        r.Excel("Module1").Code = VbaP3Rig.Module1Original + "\r\nSub B()\r\nEnd Sub";
        r.ChoiceAnswer = _ => "abort";

        await r.ApplyAsync().ConfigureAwait(true);

        r.Mirror(Module1).Should().NotContain("hello");
        (await r.RevisionsAsync().ConfigureAwait(true)).Should().BeEmpty();
        r.WriteCalls.Should().Be(0);
        r.Graft.IsApplyInProgress.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "再検査の衝突: 両側が変わっていたら衝突ダイアログ（再検査の場面）。「Excel 側を採用」で取り込み、同じ再解析を使って適用される")]
    public async Task 再検査の衝突でExcel側を採用する()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await r.PasteAsync(TwoModulePatch()).ConfigureAwait(true);
        // 解析の後に、ミラー（末尾へ追記）と Excel の両方が変わった。
        File.AppendAllText(r.MirrorPath(Module1), "' graft tail\r\n");
        r.Excel("Module1").Code = VbaP3Rig.Module1Original + "\r\nSub B()\r\nEnd Sub";
        r.ConflictAnswer = request => VbaConflictDecision.All(request, VbaConflictChoice.AdoptExcel);

        await r.ApplyAsync().ConfigureAwait(true);

        var request = r.Conflicts.Should().ContainSingle().Subject;
        request.Scene.Should().Be(VbaConflictScene.Reanalysis);
        request.CanAdoptExcel.Should().BeTrue();
        r.Mirror(Module1).Should().Contain("Sub B()").And.Contain("hello").And.NotContain("graft tail");
        r.Excel("Module1").Code.Should().Contain("hello");
    }

    [AvaloniaFact(DisplayName = "再検査の衝突: 「中止」なら適用しない")]
    public async Task 再検査の衝突で中止すると適用しない()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await r.PasteAsync(TwoModulePatch()).ConfigureAwait(true);
        File.AppendAllText(r.MirrorPath(Module1), "' graft tail\r\n");
        r.Excel("Module1").Code = VbaP3Rig.Module1Original + "\r\nSub B()\r\nEnd Sub";
        r.ConflictAnswer = _ => VbaConflictDecision.Abort;

        await r.ApplyAsync().ConfigureAwait(true);

        r.Conflicts.Should().ContainSingle();
        (await r.RevisionsAsync().ConfigureAwait(true)).Should().BeEmpty();
        r.Mirror(Module1).Should().Contain("graft tail");
        r.Graft.Notice.Text.Should().Contain("適用を中止しました");
    }

    // ------------------------------------------------------------------
    // 画面側のガード（7.3-2）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "E814: DELETE を含む解析結果は案内が出て、適用が始まらない（ファイルも履歴も変わらない）。チェックを外せばほかの変更は適用できる")]
    public async Task DELETEはE814で止まる()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);

        await r.PasteAsync(VbaP3Rig.Delete(Cls1), VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);

        r.H.Dialogs.Messages.Should().ContainSingle(m => m.Title == "VBA プロジェクトでは適用できない変更があります")
            .Which.Message.Should().Contain("E814").And.Contain(Cls1);

        r.H.Dialogs.Messages.Clear();
        await r.ApplyAsync().ConfigureAwait(true);
        r.H.Dialogs.Messages.Should().ContainSingle(m => m.Title == "VBA プロジェクトでは適用できない変更があります", "適用を押しても同じ案内で止まる");
        File.Exists(r.MirrorPath(Cls1)).Should().BeTrue();
        r.Mirror(Module1).Should().NotContain("hello");
        (await r.RevisionsAsync().ConfigureAwait(true)).Should().BeEmpty("適用は始まっていない");
        r.WriteCalls.Should().Be(0);

        r.Block(Cls1).IsSelected = false;
        await r.ApplyAsync().ConfigureAwait(true);

        File.Exists(r.MirrorPath(Cls1)).Should().BeTrue("外したブロックは適用されない");
        r.Mirror(Module1).Should().Contain("hello");
        r.Excel("Module1").Code.Should().Contain("hello");
        r.Excel("Cls1").Code.Should().Be(VbaP3Rig.Cls1Original);
    }

    [AvaloniaFact(DisplayName = "E814: RENAME も同じ。適用は始まらない")]
    public async Task RENAMEはE814で止まる()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);

        await r.PasteAsync("<<<< RENAME: Classes/Cls1.cls -> Classes/Cls2.cls\n").ConfigureAwait(true);
        await r.ApplyAsync().ConfigureAwait(true);

        r.H.Dialogs.Messages.Should().Contain(m => m.Message.Contains("E814"));
        File.Exists(r.MirrorPath(Cls1)).Should().BeTrue();
        File.Exists(r.MirrorPath("Classes/Cls2.cls")).Should().BeFalse();
        (await r.RevisionsAsync().ConfigureAwait(true)).Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "E811: CP932 で表せない文字（—）を含む変更は解析結果の段階で案内が出る。該当のブロックのチェックを外せば他は適用できる")]
    public async Task CP932で表せない文字は解析の段階で止まる()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);

        await r.PasteAsync(
            VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hi — hello\""),
            VbaP3Rig.Edit(Cls1, "Public Value As Long", "Public Value As Double")).ConfigureAwait(true);

        var notice = r.H.Dialogs.Messages.Should().ContainSingle(m => m.Title == "VBA プロジェクトでは適用できない変更があります").Subject;
        notice.Message.Should().Contain("E811").And.Contain("U+2014").And.Contain(Module1);

        await r.ApplyAsync().ConfigureAwait(true);
        (await r.RevisionsAsync().ConfigureAwait(true)).Should().BeEmpty("チェックが付いている間は適用を始めない");

        r.Block(Module1).IsSelected = false;
        await r.ApplyAsync().ConfigureAwait(true);

        r.Mirror(Cls1).Should().Contain("As Double");
        r.Mirror(Module1).Should().NotContain("—");
        r.Excel("Cls1").Code.Should().Contain("As Double");
    }

    [AvaloniaFact(DisplayName = "通常のファイルプロジェクト: VBA 連携の検査・Pull・Push・案内は一切動かず、適用の通知も従来どおり（Excel の呼び出しゼロ）")]
    public async Task 通常のプロジェクトの適用は変わらない()
    {
        using var r = await VbaP3Rig.CreateAsync(plainProjectFirst: true).ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        await File.WriteAllTextAsync(r.H.ProjectPath("note.txt"), "1行目\n2行目\n").ConfigureAwait(true);
        await pane.SelectAsync(pane.Items.First(i => i.Project.Vba is null)).ConfigureAwait(true);
        r.Bridge.Calls.Clear();

        await r.H.ParseAsync("note.txt", "2行目", "2行目（変更後）").ConfigureAwait(true);
        await r.ApplyAsync().ConfigureAwait(true);

        File.ReadAllText(r.H.ProjectPath("note.txt")).Should().Contain("2行目（変更後）");
        r.Bridge.Calls.Should().BeEmpty("通常のプロジェクトでは Excel を呼ばない");
        r.Choices.Should().BeEmpty();
        r.Conflicts.Should().BeEmpty();
        r.H.Dialogs.Messages.Should().BeEmpty();
        r.Graft.ApplyUndoNoticeText.Should().MatchRegex(@"^r\d+ として適用しました — 元に戻す$");
    }

    [AvaloniaFact(DisplayName = "Vba.Enabled=false: VBA プロジェクトでも普通のファイルプロジェクトとして適用でき、Excel は呼ばれず、E814 の案内も出ない")]
    public async Task 機能を無効にすると普通のプロジェクトとして扱う()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        r.Graft.UpdateSettings(r.Graft.CurrentSettings with { Vba = new VbaSettings { Enabled = false } });
        r.Bridge.Calls.Clear();

        await r.PasteAsync(VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);
        await r.ApplyAsync().ConfigureAwait(true);

        r.Mirror(Module1).Should().Contain("hello");
        r.Bridge.Calls.Should().BeEmpty();
        r.Excel("Module1").Code.Should().Be(VbaP3Rig.Module1Original);
    }
}
