using System.Diagnostics;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;

namespace Graft.UiTests.Vba;

/// <summary>
/// Excel VBA 連携 段階 P3 のシナリオ（仕様書_VBA連携 10.2）のうち、適用の<b>後</b>の部分:
/// 自動 Push（7.3-4）、Push 失敗の選択（既定・もう一度・取り消し）、Push 直前の衝突、CP932 で表せない文字（E811）、
/// Excel が固まったとき、Push 中の操作の制限。取り消し・復元の後の Push は <see cref="VbaP3RestoreTests"/>。
/// </summary>
public class VbaP3PushTests
{
    private const string Module1 = "Modules/Module1.bas";
    private const string Cls1 = "Classes/Cls1.cls";

    private static string[] TwoModulePatch() => new[]
    {
        VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\""),
        VbaP3Rig.Edit(Cls1, "Public Value As Long", "Public Value As Double"),
    };

    private static async Task ParseAndApplyAsync(VbaP3Rig r, params string[] blocks)
    {
        await r.PasteAsync(blocks).ConfigureAwait(true);
        await r.ApplyAsync().ConfigureAwait(true);
    }

    // ------------------------------------------------------------------
    // 成功
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "Push 成功: 適用すると Excel へ反映され、通知に「Excel へ反映済み」が付く。履歴の一覧は新しい適用を含んでいる")]
    public async Task 適用後に自動でPushされる()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);

        await ParseAndApplyAsync(r, VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);

        r.Excel("Module1").Code.Should().Be("Sub A()\r\n  MsgBox \"hello\"\r\nEnd Sub");
        r.Graft.ApplyUndoNoticeText.Should().Be("r1 として適用しました（Excel へ反映済み） — 元に戻す");
        r.Graft.HasApplyUndoNotice.Should().BeTrue();
        r.Graft.History.Items.Should().ContainSingle(i => i.RevisionLabel == "r1", "Push は履歴の読み込みの後（History.LoadAsync の後）");
        r.Graft.IsApplyInProgress.Should().BeFalse();
        r.Graft.State.Should().NotBe(CenterPaneState.Loading, "Push が終われば処理中の表示は戻る");
        (await r.StatusAsync().ConfigureAwait(true)).UnpushedCount.Should().Be(0);
        r.Choices.Should().BeEmpty();
        r.Conflicts.Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "Push 成功: 反映先が既に別のモジュールを未反映なら一緒に送る。通知に「うち以前からの未反映」の件数が付く")]
    public async Task 以前からの未反映も同梱される()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        File.WriteAllText(r.MirrorPath(Cls1), "Option Explicit\r\nPublic Value As Currency\r\n"); // 手編集（まだ Excel へ届いていない）

        await ParseAndApplyAsync(r, VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);

        r.Excel("Module1").Code.Should().Contain("hello");
        r.Excel("Cls1").Code.Should().Contain("Currency", "Push 判定のモジュールも同梱する");
        r.Graft.ApplyUndoNoticeText.Should().Contain("Excel へ反映済み: 2件（うち以前からの未反映 1件）");
    }

    [AvaloniaFact(DisplayName = "Push 成功: VBE の整形差があってもミラーは書き換わらず、取り消しは E301 にならない。取り消すと Excel も元へ戻る")]
    public async Task 整形差があってもミラーは書き換わらず取り消せる()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        r.Bridge.FormatOnWrite = code => code.Replace("MsgBox", "msgbox"); // VBE の大文字小文字の整形

        await ParseAndApplyAsync(r, VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);

        r.Excel("Module1").Code.Should().Contain("msgbox \"hello\"", "VBE が整形した");
        r.Mirror(Module1).Should().Contain("MsgBox \"hello\"").And.NotContain("msgbox", "ミラーは書き戻さない（履歴の HashAfter と食い違うため）");
        r.H.Dialogs.Messages.Should().BeEmpty("整形差の警告で止めない");

        r.Bridge.FormatOnWrite = null;
        r.H.Dialogs.Confirms.Clear();
        await ShellHarness.ExecuteAsync(r.Graft.UndoCommand).ConfigureAwait(true);

        r.H.Dialogs.Messages.Should().NotContain(m => m.Title == "取り消せません", "ミラーが履歴と食い違っていないので E301 にならない");
        r.Mirror(Module1).Should().Contain("MsgBox \"hi\"");
        r.Excel("Module1").Code.Should().Be(VbaP3Rig.Module1Original, "取り消した後の Push で Excel も元の内容へ");
    }

    // ------------------------------------------------------------------
    // 失敗: 既定・もう一度・取り消し
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "Push 失敗: 既定のボタンは「ファイルの変更は残す」。ファイルは適用済みのまま未反映の印が付き、取り消されない。選択肢の並びと文言")]
    public async Task Push失敗は既定でファイルの変更を残す()
    {
        var settings = new Settings { ShowPreview = false, Git = new GitSettings { AutoCommit = true } };
        using var r = await VbaP3Rig.CreateAsync(settings).ConfigureAwait(true);
        var headBefore = await r.InitGitAsync().ConfigureAwait(true);
        r.Bridge.FailNext("Write", GraftIssue.Of(ErrorCode.E805));

        await ParseAndApplyAsync(r, VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);

        var request = r.Choices.Should().ContainSingle().Subject;
        request.Title.Should().Be("Excel へ反映できませんでした");
        request.Message.Should().Contain("r1").And.Contain("E805");
        request.Options.Select(o => o.Label).Should().Equal(
            "ファイルの変更は残す（未反映の印）", "もう一度反映する", "この適用（r1）を取り消す");
        request.Default.Id.Should().Be("keep");
        request.Options.Count(o => o.IsDefault).Should().Be(1);

        r.Mirror(Module1).Should().Contain("hello");
        r.Excel("Module1").Code.Should().Be(VbaP3Rig.Module1Original);
        (await r.StatusAsync().ConfigureAwait(true)).UnpushedCount.Should().Be(1, "未反映の印");
        (await r.RevisionsAsync().ConfigureAwait(true)).Should().ContainSingle().Which.Manifest.Status.Should().Be(RevisionStatus.Success);
        r.Graft.ApplyUndoNoticeText.Should().Contain("Excel には未反映");
        r.H.Dialogs.Confirms.Should().NotContain(c => c.Title == "取り消しの確認");
        (await r.RunGitAsync("log", "-1", "--pretty=%H").ConfigureAwait(true)).Should().NotBe(headBefore,
            "取り消さなかった適用は、従来どおり自動コミットまで進む（取り消した場合との対照）");
    }

    [AvaloniaFact(DisplayName = "Push 失敗: 「もう一度反映する」で再試行でき、成功すれば「Excel へ反映済み」。確認は 1 回だけ")]
    public async Task もう一度反映する()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        r.Bridge.FailNext("Write", GraftIssue.Of(ErrorCode.E805));
        r.ChoiceAnswer = _ => "retry";

        await ParseAndApplyAsync(r, VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);

        r.Choices.Should().ContainSingle();
        r.WriteCalls.Should().Be(2);
        r.Excel("Module1").Code.Should().Contain("hello");
        r.Graft.ApplyUndoNoticeText.Should().Contain("Excel へ反映済み");
        (await r.StatusAsync().ConfigureAwait(true)).UnpushedCount.Should().Be(0);
    }

    [AvaloniaFact(DisplayName = "Push 失敗→取り消し: rN を名指しで戻し、反映済みだったモジュールを元の内容で Push し直す。確認ダイアログは重ならず、フックと Git コミットは実行されない")]
    public async Task Push失敗から適用を取り消す()
    {
        var settings = new Settings { ShowPreview = false, Git = new GitSettings { AutoCommit = true } };
        using var r = await VbaP3Rig.CreateAsync(settings).ConfigureAwait(true);
        var headBefore = await r.InitGitAsync().ConfigureAwait(true);
        var marker = Path.Combine(r.H.Root, "hook-ran.txt");
        await r.SetHooksAsync(new PostApplyHook { Name = "目印", Command = $"touch \"{marker}\"", OnFailure = HookFailureAction.Warn }).ConfigureAwait(true);
        r.Bridge.CorruptOnWrite.Add("Cls1"); // Cls1 だけ書き込み後の読み直しが別内容（E809）。Module1 と NewMod は反映できる。
        r.ChoiceAnswer = _ => "undo";

        await ParseAndApplyAsync(r,
            VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\""),
            VbaP3Rig.Edit(Cls1, "Public Value As Long", "Public Value As Double"),
            VbaP3Rig.Create("Modules/NewMod.bas", "Sub NewOne()\nEnd Sub")).ConfigureAwait(true);

        // この適用の Push の時点で、Module1 と NewMod は Excel に届いていた。
        var firstWrite = r.Bridge.WriteRequests[0];
        firstWrite.Select(w => w.Name).Should().BeEquivalentTo("Module1", "Cls1", "NewMod");

        // 取り消し: rN を名指しで戻した。UndoLatestAsync の確認ダイアログは出ない。
        var revisions = await r.RevisionsAsync().ConfigureAwait(true);
        revisions.Should().ContainSingle().Which.Manifest.Status.Should().Be(RevisionStatus.RolledBack);
        r.H.Dialogs.Confirms.Should().NotContain(c => c.Title == "取り消しの確認");
        r.Mirror(Module1).Should().Contain("MsgBox \"hi\"");
        r.Mirror(Cls1).Should().Contain("As Long");
        File.Exists(r.MirrorPath("Modules/NewMod.bas")).Should().BeFalse("新規ファイルは取り消しで消える");

        // 反映済みだったモジュールは、取り消し後（元）の内容で Excel へ Push し直された。
        r.Excel("Module1").Code.Should().Be(VbaP3Rig.Module1Original);
        r.Excel("Cls1").Code.Should().Be(VbaP3Rig.Cls1Original);
        r.WriteCalls.Should().Be(2);
        r.Bridge.WriteRequests[1].Select(w => w.Name).Should().Contain("Module1");

        // 新規モジュールは Excel 側に残る。知らせ、ミラーからは削除保留になる。
        r.Excel("NewMod").Code.Should().Contain("NewOne");
        r.H.Dialogs.Messages.Should().ContainSingle(m => m.Title == "Excel 側にモジュールが残っています")
            .Which.Message.Should().Contain("Modules/NewMod.bas").And.Contain("削除保留");
        (await r.StatusAsync().ConfigureAwait(true)).DeletePendingCount.Should().Be(1);

        // 取り消した場合は、フックも Git コミットも実行しない。
        File.Exists(marker).Should().BeFalse("適用後フックは実行されない");
        (await r.RunGitAsync("log", "-1", "--pretty=%H").ConfigureAwait(true)).Should().Be(headBefore, "Git の自動コミットも行われない");
        r.Graft.HasApplyUndoNotice.Should().BeFalse("取り消した適用に「元に戻す」は出さない");
        r.Graft.IsApplyInProgress.Should().BeFalse();

        // 次の自動 Pull で復活しない。「Excel から復元」で戻る。
        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);
        File.Exists(r.MirrorPath("Modules/NewMod.bas")).Should().BeFalse();
        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.RestoreFromExcelCommand).ConfigureAwait(true);
        File.Exists(r.MirrorPath("Modules/NewMod.bas")).Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "Push 失敗→取り消しの Push も失敗したら、さらに取り消すことはせず、警告と未反映の印だけが残る")]
    public async Task 取り消しのPushが失敗しても警告だけ()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        r.Bridge.CorruptOnWrite.Add("Cls1");
        r.ChoiceAnswer = _ => "undo";
        // 取り消した後の Push（2 回目の Write）を失敗させる。
        r.Bridge.BeforeWrite = () =>
        {
            if (r.Bridge.WriteRequests.Count == 2) r.Bridge.FailNext("Write", GraftIssue.Of(ErrorCode.E805));
            return Task.CompletedTask;
        };

        await ParseAndApplyAsync(r, TwoModulePatch()).ConfigureAwait(true);

        (await r.RevisionsAsync().ConfigureAwait(true)).Should().ContainSingle().Which.Manifest.Status.Should().Be(RevisionStatus.RolledBack);
        r.Choices.Should().ContainSingle("取り消しの Push の失敗では、もう聞かない");
        r.H.Dialogs.Messages.Should().ContainSingle(m => m.Title.Contains("取り消しました") && m.Message.Contains("E805"));
        r.Excel("Module1").Code.Should().Contain("hello", "Excel を戻せなかった");
        (await r.StatusAsync().ConfigureAwait(true)).UnpushedCount.Should().Be(1, "未反映の印（ミラーは元の内容、Excel は新しい内容）");
    }

    // ------------------------------------------------------------------
    // Push 直前の衝突
    // ------------------------------------------------------------------

    private static async Task<VbaP3Rig> ConflictAtPushAsync()
    {
        var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await r.PasteAsync(VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);
        // 解析の後に、VBE で Excel 側を編集した。「このまま続ける」を選んで、Push のとき衝突として扱う。
        r.Excel("Module1").Code = VbaP3Rig.Module1Original + "\r\n' vbe edit";
        r.ChoiceAnswer = request => request.Title == "Excel 側が変更されています" ? "continue" : null;
        return r;
    }

    [AvaloniaFact(DisplayName = "Push 直前の衝突: 衝突ダイアログに「Excel 側を採用」が出ない（反映しない・この適用を取り消すが出る）。「Graft 側で上書き」で Excel が上書きされ、Excel 側の本文は控えられる")]
    public async Task Push直前の衝突でGraft側で上書きする()
    {
        using var r = await ConflictAtPushAsync().ConfigureAwait(true);
        r.ConflictAnswer = request => VbaConflictDecision.All(request, VbaConflictChoice.OverwriteExcel);

        await r.ApplyAsync().ConfigureAwait(true);

        var request = r.Conflicts.Should().ContainSingle().Subject;
        request.Scene.Should().Be(VbaConflictScene.AfterApply);
        request.CanAdoptExcel.Should().BeFalse();
        request.CanSkip.Should().BeTrue();
        request.CanUndoApply.Should().BeTrue();
        request.CanAbort.Should().BeFalse();
        request.Items.Single().ExcelText.Should().Contain("vbe edit");
        request.Items.Single().MirrorText.Should().Contain("hello");

        r.Excel("Module1").Code.Should().Contain("hello").And.NotContain("vbe edit");
        r.RescueTexts().Should().ContainSingle().Which.Should().Contain("vbe edit");
        r.Graft.ApplyUndoNoticeText.Should().Contain("Excel へ反映済み");
    }

    [AvaloniaFact(DisplayName = "Push 直前の衝突: 「反映しない」なら Excel は触らず、「反映しなかったモジュールがあります」の選択に進み（既定は残す）、未反映の印が付く")]
    public async Task Push直前の衝突で反映しない()
    {
        using var r = await ConflictAtPushAsync().ConfigureAwait(true);
        r.ConflictAnswer = request => VbaConflictDecision.All(request, VbaConflictChoice.Skip);

        await r.ApplyAsync().ConfigureAwait(true);

        r.Excel("Module1").Code.Should().Contain("vbe edit").And.NotContain("hello");
        r.Mirror(Module1).Should().Contain("hello");
        // 利用者が自分で選んだ結果なので、失敗の題名（「Excel へ反映できませんでした」）では聞かない（不具合 14）。
        var failure = r.Choices.Should().ContainSingle(c => c.Title == "反映しなかったモジュールがあります").Subject;
        failure.Message.Should().Contain("衝突のため反映しなかったモジュール").And.Contain(Module1);
        failure.Message.Should().NotContain("反映できなかったものがあります");
        failure.Default.Id.Should().Be("keep");
        (await r.StatusAsync().ConfigureAwait(true)).UnpushedCount.Should().Be(1);
        r.Graft.ApplyUndoNoticeText.Should().Contain("Excel には未反映");
        r.RescueTexts().Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "Push 直前の衝突: 「この適用を取り消す」なら rN を戻し、Excel は触らない。フックは実行されない")]
    public async Task Push直前の衝突で適用を取り消す()
    {
        using var r = await ConflictAtPushAsync().ConfigureAwait(true);
        var marker = Path.Combine(r.H.Root, "hook-ran.txt");
        await r.SetHooksAsync(new PostApplyHook { Name = "目印", Command = $"touch \"{marker}\"", OnFailure = HookFailureAction.Warn }).ConfigureAwait(true);
        r.ConflictAnswer = request => new VbaConflictDecision(false, true, VbaConflictDecision.All(request, VbaConflictChoice.Skip).Choices);

        await r.ApplyAsync().ConfigureAwait(true);

        (await r.RevisionsAsync().ConfigureAwait(true)).Should().ContainSingle().Which.Manifest.Status.Should().Be(RevisionStatus.RolledBack);
        r.Mirror(Module1).Should().Contain("MsgBox \"hi\"").And.NotContain("hello");
        r.Excel("Module1").Code.Should().Contain("vbe edit");
        r.WriteCalls.Should().Be(0);
        File.Exists(marker).Should().BeFalse();
        r.H.Dialogs.Confirms.Should().NotContain(c => c.Title == "取り消しの確認");
    }

    [AvaloniaFact(DisplayName = "再検査で「Graft 側で上書き」と承認したモジュールは、Excel 側がそのままなら Push 直前に聞き直さずに上書きする（Excel 側の控えあり）")]
    public async Task 再検査で承認した上書きは聞き直さない()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await r.PasteAsync(VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);
        // 解析の後に、ミラー（末尾へ追記。パッチの当たる位置には触れない）と Excel の両方が変わった。
        File.AppendAllText(r.MirrorPath(Module1), "' graft tail\r\n");
        r.Excel("Module1").Code = VbaP3Rig.Module1Original + "\r\n' vbe edit";
        r.ConflictAnswer = request => VbaConflictDecision.All(request, VbaConflictChoice.OverwriteExcel);

        await r.ApplyAsync().ConfigureAwait(true);

        // 衝突は再検査で 1 回だけ聞かれる。そこで承認した「上書き」は、Push 直前に Excel 側がそのままなら使われ、聞き直さない。
        r.Conflicts.Select(c => c.Scene).Should().Equal(new[] { VbaConflictScene.Reanalysis });
        r.Excel("Module1").Code.Should().Contain("hello").And.Contain("graft tail").And.NotContain("vbe edit");
        r.RescueTexts().Should().ContainSingle().Which.Should().Contain("vbe edit");
        r.Graft.ApplyUndoNoticeText.Should().Contain("Excel へ反映済み");
        r.Graft.IsApplyInProgress.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // CP932（E811）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "Push の CP932 検査: 解析を通らずに変わったモジュールに — があれば、そのモジュールだけ反映されない（E811）。他は反映され、「もう一度反映する」は出ない")]
    public async Task CP932で表せないモジュールだけ反映されない()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        File.WriteAllText(r.MirrorPath(Cls1), "Option Explicit\r\n' メモ — あとで直す\r\n"); // 手編集で入った文字

        await ParseAndApplyAsync(r, VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);

        r.Bridge.WriteRequests.Should().ContainSingle().Which.Select(w => w.Name).Should().Equal("Module1");
        r.Excel("Module1").Code.Should().Contain("hello");
        r.Excel("Cls1").Code.Should().Be(VbaP3Rig.Cls1Original, "E811 のモジュールは送らない");
        var failure = r.Choices.Should().ContainSingle().Subject;
        failure.Message.Should().Contain("E811").And.Contain("U+2014").And.Contain(Cls1);
        failure.Options.Select(o => o.Id).Should().Equal(new[] { "keep", "undo" }, "文字の問題はやり直しても同じなので「もう一度」は出さない");
        (await r.StatusAsync().ConfigureAwait(true)).UnpushedCount.Should().Be(1);
    }

    // ------------------------------------------------------------------
    // Excel が固まったとき・Push 中の操作
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "Excel が固まる: 反映が戻らなくても時間切れ（E805）で戻り、適用中の旗は下りる（失敗の選択を出している間は立ったまま）")]
    public async Task 時間切れでも適用中の旗が下りる()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        r.Graft.UpdateSettings(r.Graft.CurrentSettings with { Vba = new VbaSettings { CallTimeoutMs = 300 } });
        await r.PasteAsync(VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);
        r.Bridge.HangOperations.Add("Write");
        var flagDuringDialog = false;
        r.ChoiceAnswer = request =>
        {
            flagDuringDialog = r.Graft.IsApplyInProgress;
            return null;
        };

        var watch = Stopwatch.StartNew();
        await r.ApplyAsync().ConfigureAwait(true);
        watch.Stop();

        flagDuringDialog.Should().BeTrue("選択を待つ間は適用の途中");
        r.Choices.Should().ContainSingle().Which.Message.Should().Contain("E805");
        r.Graft.IsApplyInProgress.Should().BeFalse("時間切れの後、旗は必ず下りる（アプリを閉じられる）");
        r.Graft.State.Should().NotBe(CenterPaneState.Loading);
        r.Graft.ApplyCommand.CanExecute(null).Should().BeFalse("適用は済んでいるので、続けて適用ボタンは押せないが、コマンドは実行中のまま固まらない");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [AvaloniaFact(DisplayName = "Push 中は処理中の表示になり、貼り付け・取り消し・履歴の取り消し・プロジェクトの切り替えを受け付けない。終われば受け付ける")]
    public async Task Push中は他の操作を受け付けない()
    {
        using var r = await VbaP3Rig.CreateAsync(plainProjectFirst: true).ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        var reached = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        r.Bridge.BeforeWrite = async () =>
        {
            reached.TrySetResult();
            await release.Task.ConfigureAwait(false);
        };
        await r.PasteAsync(VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);

        r.Graft.ApplyCommand.Execute(null);
        await ShellHarness.WaitUntilAsync(() => reached.Task.IsCompleted).ConfigureAwait(true);

        r.Graft.IsApplyInProgress.Should().BeTrue();
        r.Graft.State.Should().Be(CenterPaneState.Loading, "中央ペインは処理中の表示");
        r.Graft.PasteAndParseCommand.CanExecute(null).Should().BeFalse();
        r.H.Clipboard.Text = VbaP3Rig.Edit(Module1, "  MsgBox \"hello\"", "  MsgBox \"bye\"");
        r.Graft.PasteAndParseCommand.Execute(null);
        r.Graft.UndoCommand.Execute(null);
        (await r.Graft.History.UndoLatestAsync().ConfigureAwait(true)).Should().BeFalse("履歴からの取り消しも受け付けない");
        pane.SelectedItem = pane.Items.First(i => i.Project.Vba is null);
        pane.SelectedItem!.Project.Id.Should().Be(r.Project.Id, "プロジェクトの切り替えも受け付けない");
        r.H.Dialogs.Confirms.Should().NotContain(c => c.Title == "取り消しの確認");
        r.Graft.Blocks.Should().BeEmpty("貼り付けは無視された");

        release.SetResult();
        var apply = (AsyncRelayCommand)r.Graft.ApplyCommand;
        await ShellHarness.WaitUntilAsync(() => !apply.IsExecuting).ConfigureAwait(true);

        r.Excel("Module1").Code.Should().Contain("hello");
        r.Graft.IsApplyInProgress.Should().BeFalse();
        r.Graft.PasteAndParseCommand.CanExecute(null).Should().BeTrue();
        (await r.RevisionsAsync().ConfigureAwait(true)).Should().ContainSingle().Which.Manifest.Status.Should().Be(RevisionStatus.Success);
    }
}
