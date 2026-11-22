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
/// ブックの情報（機能 A）の不具合修正（Q1〜Q4 の点検で見つかったもの）の回帰テスト。
/// 各テストの DisplayName の先頭に、点検の不具合番号（「不具合 N」）か「疑わしい N」を付けてある。
/// いずれも、修正前のコードでは失敗し、修正後に通ることを確かめている。
/// </summary>
public class WorkbookInfoFixTests
{
    private static async Task<VbaP3Rig> NewRigAsync(
        Func<FakeVbaBridge.Workbook, VbaWorkbookInfoSnapshot?>? info = null, Settings? settings = null)
    {
        var rig = await VbaP3Rig.CreateAsync(
            settings ?? new Settings { ShowPreview = false },
            arrange: bridge => bridge.Get(VbaP3Rig.Book)!.Info = info is null ? WorkbookInfoTests.Sample() : info(bridge.Get(VbaP3Rig.Book)!)).ConfigureAwait(true);
        rig.ChoiceAnswer = _ => "give";
        return rig;
    }

    private static async Task<string> CollectedTextAsync(VbaP3Rig r)
    {
        await ShellHarness.ExecuteAsync(r.Graft.ContextCollect!.CopyCommand).ConfigureAwait(true);
        return r.H.Clipboard.Text ?? string.Empty;
    }

    /// <summary>
    /// 依頼文コピーの一覧を作っておく（1 回コピーする）。一覧が残っているクイックコピーは、収集を取り直さない。
    /// 点検の不具合 2 は、この「一覧が残っている」経路で起きる（一覧が空なら、クイックコピーが先に取り直すので起きない）。
    /// </summary>
    private static async Task PrimeQuickCopyAsync(VbaP3Rig r)
    {
        await r.Graft.PromptCopy!.QuickCopyAsync().ConfigureAwait(true);
        r.Graft.PromptCopy.Templates.Should().NotBeEmpty();
        r.H.Clipboard.Text.Should().Contain("# ブックの情報", "前提: オフにする前は、取得した内容が入る");
    }

    [AvaloniaFact(DisplayName = "不具合 1: 「数式も含める」をオフにしたあと取得に失敗しても、数式入りの見本は依頼文に出ない")]
    public async Task 不具合1_数式をオフにしたら取得失敗でも数式は出ない()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.ToggleWorkbookSampleValuesAsync().ConfigureAwait(true);
        await context.SetWorkbookIncludeFormulasAsync(true).ConfigureAwait(true);
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().Contain("=B2*C2 → 2500", "前提: 数式をオンにしている間は出る");

        // Excel を閉じて、取り直しが失敗する状態にしてから、数式をオフにする。
        r.Bridge.CloseWorkbook(VbaP3Rig.Book);
        await context.SetWorkbookIncludeFormulasAsync(false).ConfigureAwait(true);

        context.WorkbookInfoWarning.Should().Contain("E804", "取り直しは失敗している");
        var text = await CollectedTextAsync(r).ConfigureAwait(true);
        text.Should().NotContain("=B2*C2", "オフにした数式が、前回の成功分から出続けてはならない");
        text.Should().NotContain("SECRET", "数式入りの見本は捨てる。見本ごと出さない側に倒す");
        text.Should().Contain("| 集計 | Sheet1 |", "構成は前回の成功分を使い続ける（見本だけを捨てる）");
    }

    [AvaloniaFact(DisplayName = "不具合 2: 設定で「ブックの情報の取得」をオフにした直後のクイックコピーに、直前の取得結果は出ない")]
    public async Task 不具合2_取得オフの直後のクイックコピー()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        await r.Graft.ContextCollect!.InitializeAsync().ConfigureAwait(true);
        await r.Graft.ContextCollect!.ToggleWorkbookSampleValuesAsync().ConfigureAwait(true);
        await PrimeQuickCopyAsync(r).ConfigureAwait(true);

        r.Graft.UpdateSettings(r.Graft.CurrentSettings with { Vba = r.Graft.CurrentSettings.Vba with { CollectWorkbookInfo = false } });
        await r.Graft.PromptCopy!.QuickCopyAsync().ConfigureAwait(true);

        var text = r.H.Clipboard.Text!;
        text.Should().NotContain("# ブックの情報", "オフにしたのに古い内容が出続けるのは意図に反する");
        text.Should().NotContain("SECRET", "見本オンのときは見本も含まれていた");
    }

    [AvaloniaFact(DisplayName = "不具合 2: 設定で VBA 連携そのものをオフにした直後のクイックコピーにも、直前の取得結果は出ない")]
    public async Task 不具合2_VBA連携オフの直後のクイックコピー()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        await r.Graft.ContextCollect!.InitializeAsync().ConfigureAwait(true);
        await PrimeQuickCopyAsync(r).ConfigureAwait(true);

        r.Graft.UpdateSettings(r.Graft.CurrentSettings with { Vba = r.Graft.CurrentSettings.Vba with { Enabled = false } });
        await r.Graft.PromptCopy!.QuickCopyAsync().ConfigureAwait(true);

        r.H.Clipboard.Text!.Should().NotContain("# ブックの情報");
    }

    [AvaloniaFact(DisplayName = "不具合 10（上限の即時反映）: ブックの情報の最大文字数を設定で変えると、プロジェクトを選び直さなくても効く")]
    public async Task 不具合10_最大文字数は即時に効く()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        await r.Graft.ContextCollect!.InitializeAsync().ConfigureAwait(true);
        r.Graft.ContextCollect!.BuildWorkbookInfoText().Should().NotContain(VbaWorkbookInfoFormatter.TruncatedNote);

        r.Graft.UpdateSettings(r.Graft.CurrentSettings with { Vba = r.Graft.CurrentSettings.Vba with { WorkbookInfoMaxChars = 200 } });

        r.Graft.ContextCollect!.BuildWorkbookInfoText().Should().Contain(VbaWorkbookInfoFormatter.TruncatedNote);
    }

    [AvaloniaFact(DisplayName = "不具合 9: 値の見本をオフにすると、プレビュー欄の見本も消える（Dispose でも手放す）")]
    public async Task 不具合9_見本オフでプレビューも消える()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.ToggleWorkbookSampleValuesAsync().ConfigureAwait(true);
        await ShellHarness.ExecuteAsync(context.PreviewCommand).ConfigureAwait(true);
        context.PreviewLines.Any(l => l.Text.Contains("SECRET")).Should().BeTrue("前提: オンの間はプレビューに見本が出る");

        await context.ToggleWorkbookSampleValuesAsync().ConfigureAwait(true);

        context.PreviewLines.Should().BeEmpty("オフにしたら直ちに捨てる、と案内している");

        await context.ToggleWorkbookSampleValuesAsync().ConfigureAwait(true);
        await ShellHarness.ExecuteAsync(context.PreviewCommand).ConfigureAwait(true);
        context.PreviewLines.Should().NotBeEmpty();
        context.Dispose();
        context.PreviewLines.Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "疑わしい 1: 定数を指す名前付き範囲は、見本オフでは式を出さず名前だけ。見本オンのときだけ式も出る")]
    public async Task 疑わしい1_定数の名前は見本オフでは式を出さない()
    {
        static VbaWorkbookInfoSnapshot WithNames(VbaWorkbookInfoSnapshot s) => s with
        {
            Names = new[]
            {
                new VbaNamedRangeInfo("税率", "=設定!$B$3", null),
                new VbaNamedRangeInfo("パスワード", "=\"SECRET-pass\"", null),
                new VbaNamedRangeInfo("消費税", "=0.1", "集計"),
            },
        };

        using var r = await NewRigAsync(_ => WithNames(WorkbookInfoTests.Sample())).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);

        var off = await CollectedTextAsync(r).ConfigureAwait(true);
        off.Should().Contain("- 税率 = =設定!$B$3", "範囲参照は構造の情報なので出す");
        off.Should().Contain("- パスワード（ブック全体）").And.NotContain("SECRET-pass", "定数の値は見本のオプトインを迂回して出さない");
        off.Should().Contain("- 消費税（シート「集計」限定）").And.NotContain("=0.1");

        await context.ToggleWorkbookSampleValuesAsync().ConfigureAwait(true);
        var on = await CollectedTextAsync(r).ConfigureAwait(true);
        on.Should().Contain("- パスワード = =\"SECRET-pass\"", "見本をオンにしたときだけ、式も出る");
    }

    [AvaloniaFact(DisplayName = "疑わしい 1: 値の見本の確認文に「1 行目（見出し）は見本オフでも渡る」と明記する。行数は「見出し行に続く N 行」")]
    public async Task 疑わしい1_確認文に見出しが渡ることを書く()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        r.ChoiceAnswer = _ => "cancel";

        await context.ToggleWorkbookSampleValuesAsync().ConfigureAwait(true);

        var message = r.Choices.Should().ContainSingle().Subject.Message;
        message.Should().Contain("1 行目（見出し）").And.Contain("オフでも").And.Contain("見出し行に続く 5 行");
    }

    [AvaloniaFact(DisplayName = "不具合 10（見本の注意）: 見本の注意は、出し方が「構成だけ」「出さない」のとき（見本が渡らないとき）は出ない")]
    public async Task 不具合10_見本の注意は渡るときだけ()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.ToggleWorkbookSampleValuesAsync().ConfigureAwait(true);
        context.WorkbookInfo.IsSampleNoticeVisible.Should().BeTrue();

        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true); // 構成だけ
        context.WorkbookInfo.IsSampleNoticeVisible.Should().BeFalse();
        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true); // 出さない
        context.WorkbookInfo.IsSampleNoticeVisible.Should().BeFalse();
        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true); // 内容も出す
        context.WorkbookInfo.IsSampleNoticeVisible.Should().BeTrue();
        context.WorkbookInfo.SampleNoticeText.Should().Contain("見出し行に続く 5 行");
    }

    [AvaloniaFact(DisplayName = "疑わしい 2: 別のブックのパスで結ばれていたプロジェクトを、このブックで結び直すと、値の見本のオンは解除される（実際の追加の経路）")]
    public async Task 疑わしい2_結び直しで見本のオンが解除される()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        // 古いブックのパス・見本オンで結ばれている状態にしておいて、同じミラーを、いまのブックでもう一度追加する。
        await r.UpdateLinkAsync(l => l with { WorkbookFullName = @"C:\Old\計画.xlsm", WorkbookInfoSampleValues = true, AutoPushOnSave = true }).ConfigureAwait(true);
        pane.Items.First(i => i.Project.Id == r.Project.Id).Project.Vba!.WorkbookInfoSampleValues.Should().BeTrue("前提");

        await ShellHarness.ExecuteAsync(pane.AddFromWorkbookCommand).ConfigureAwait(true);

        var vba = pane.Items.First(i => i.Project.Id == r.Project.Id).Project.Vba!;
        vba.WorkbookFullName.Should().Be(VbaP3Rig.Book);
        vba.WorkbookInfoSampleValues.Should().BeFalse("新しいブックでは、値の見本の確認をやり直す");
        vba.AutoPushOnSave.Should().BeTrue("ほかの設定は引き継ぐ");
    }
}
