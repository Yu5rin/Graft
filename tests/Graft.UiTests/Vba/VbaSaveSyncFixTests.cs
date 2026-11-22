using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;

namespace Graft.UiTests.Vba;

/// <summary>
/// 保存時の自動反映（機能 D）の不具合修正（Q1〜Q4 の点検で見つかったもの）の回帰テスト。
/// DisplayName の先頭の「不具合 N」は、点検の報告の番号。いずれも修正前のコードでは失敗する。
/// </summary>
public class VbaSaveSyncFixTests
{
    private const string Module1 = "Modules/Module1.bas";

    private static async Task<EditorTabViewModel> OpenAsync(VbaP3Rig r, string relative)
        => (await r.H.Shell.Editor.OpenFileAsync(r.MirrorPath(relative)).ConfigureAwait(true)).Value;

    private static async Task SettleAsync(VbaP3Rig r)
    {
        await r.Graft.WhenVbaSaveSyncIdleAsync().ConfigureAwait(true);
        await ShellHarness.WaitUntilAsync(() => !r.Graft.IsVbaWriting).ConfigureAwait(true);
        await Task.Delay(30).ConfigureAwait(true);
    }

    /// <summary>Excel 側を VBE で変えたあとにミラーを保存して、「衝突 1」の印を付ける。</summary>
    private static async Task<VbaP3Rig> RigWithConflictMarkAsync()
    {
        var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await r.UpdateLinkAsync(l => l with { AutoPushOnSave = true }).ConfigureAwait(true);
        var tab = await OpenAsync(r, Module1).ConfigureAwait(true);
        r.Excel("Module1").Code = "Sub A()\r\n  ' VBE で編集\r\nEnd Sub";
        tab.Session.Document.Insert(0, "' Graft で編集\r\n");
        (await r.H.Shell.Editor.SaveActiveAsync().ConfigureAwait(true)).IsSuccess.Should().BeTrue();
        await SettleAsync(r).ConfigureAwait(true);
        r.Graft.ProjectPane.VbaSaveConflictCount(r.Project.Id).Should().Be(1, "前提: 衝突の印が付いている");
        return r;
    }

    [AvaloniaFact(DisplayName = "不具合 5: コンテキスト収集の自動取込で「Excel 側を採用」して解決したら、「衝突 N」の印も消える")]
    public async Task 不具合5_収集の取込で解決したら印が消える()
    {
        using var r = await RigWithConflictMarkAsync().ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        r.ConflictAnswer = request => VbaConflictDecision.All(request, VbaConflictChoice.AdoptExcel);

        await r.Graft.ContextCollect!.InitializeAsync().ConfigureAwait(true);
        await ShellHarness.WaitUntilAsync(() => pane.VbaSaveConflictCount(r.Project.Id) == 0).ConfigureAwait(true);

        r.Conflicts.Should().ContainSingle("収集の前の自動取込で衝突画面が出た");
        r.Mirror(Module1).Should().Contain("VBE で編集", "Excel 側を採用したのでミラーは Excel と同じ");
        pane.VbaSaveConflictCount(r.Project.Id).Should().Be(0);
        pane.Items.First(i => i.Project.Id == r.Project.Id).VbaStatusText.Should().NotContain("衝突");
    }

    [AvaloniaFact(DisplayName = "不具合 5: ほかの経路で解決しても、バッジを数え直せば印は外れる（未反映でなくなったモジュールの印は残さない）")]
    public async Task 不具合5_数え直しで印が外れる()
    {
        using var r = await RigWithConflictMarkAsync().ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        // 経路を問わず「Excel の内容をミラーへ取り込んだ」状態にする（適用後・取り消し後の反映なども同じ結果になる）。
        r.ConflictAnswer = request => VbaConflictDecision.All(request, VbaConflictChoice.AdoptExcel);
        await r.Graft.VbaSync.PullAsync(r.Project, new[] { Module1 }, adoptConflicts: new[] { Module1 }).ConfigureAwait(true);
        pane.VbaSaveConflictCount(r.Project.Id).Should().Be(1, "前提: 取り込んだだけでは、印を付けた側は知らない");

        await pane.RefreshVbaBadgesAsync(checkWorkbookOpen: false).ConfigureAwait(true);

        pane.VbaSaveConflictCount(r.Project.Id).Should().Be(0);
    }

    [AvaloniaFact(DisplayName = "不具合 5: 「保存したら Excel へ反映」をオフにすると、衝突の印も消える")]
    public async Task 不具合5_機能をオフにしたら印が消える()
    {
        using var r = await RigWithConflictMarkAsync().ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;

        await pane.SetAutoPushOnSaveAsync(false).ConfigureAwait(true);

        pane.VbaSaveConflictCount(r.Project.Id).Should().Be(0);
        pane.Items.First(i => i.Project.Id == r.Project.Id).VbaStatusText.Should().NotContain("衝突");
    }

    [AvaloniaFact(DisplayName = "不具合 5: まだ衝突しているモジュールの印は、バッジを数え直しても残る")]
    public async Task 不具合5_衝突中なら印は残る()
    {
        using var r = await RigWithConflictMarkAsync().ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;

        await pane.RefreshVbaBadgesAsync(checkWorkbookOpen: false).ConfigureAwait(true);

        pane.VbaSaveConflictCount(r.Project.Id).Should().Be(1, "解決していない（ミラーは未反映のまま）");
    }

    private static readonly ErrorCode[] NoticeCodes =
    {
        ErrorCode.E801, ErrorCode.E802, ErrorCode.E803, ErrorCode.E804, ErrorCode.E805, ErrorCode.E807,
        ErrorCode.E808, ErrorCode.E809, ErrorCode.E811, ErrorCode.E813, ErrorCode.E816,
    };

    private static async Task SettleUiAsync()
    {
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(30).ConfigureAwait(true);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static double NaturalWidth(TextBlock like, string text)
    {
        var probe = new TextBlock
        {
            Text = text, FontSize = like.FontSize, FontFamily = like.FontFamily, FontWeight = like.FontWeight, FontStyle = like.FontStyle,
        };
        probe.Measure(Size.Infinity);
        return probe.DesiredSize.Width;
    }

    [AvaloniaFact(DisplayName = "不具合 6: 自動反映をしなかった通知は、全 11 種で幅に収まる（ヘッドレスの計測）。長いモジュール名でも、理由とモジュール名は省略されない")]
    public async Task 不具合6_通知は幅に収まる()
    {
        using var r = await VbaP3Rig.CreateAsync(withWindow: true).ConfigureAwait(true);
        var window = r.H.Window!;
        var notice = r.Graft.Notice;
        const string longModule = "Module_ABCDEFGHIJKLMNOPQRSTUV12"; // VBA のモジュール名の上限（31 文字）

        foreach (var code in NoticeCodes)
        {
            foreach (var module in new string?[] { null, "Module1", longModule })
            {
                var text = VbaSaveNoticeThrottle.BuildText(code, module);
                notice.Dismiss();
                await SettleUiAsync().ConfigureAwait(true);
                notice.Show(text);
                await SettleUiAsync().ConfigureAwait(true);
                var tb = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == text);

                // 全文が収まるか。収まらないなら、少なくとも「コード 理由（モジュール）」までは収まる。
                var natural = NaturalWidth(tb, text);
                var head = text[..text.IndexOf(": ", StringComparison.Ordinal)];
                var headWidth = NaturalWidth(tb, head);
                headWidth.Should().BeLessThanOrEqualTo(tb.MaxWidth, $"{code} {module}: 理由とモジュール名は省略されない（先頭 {head}）");
                if (module != longModule)
                {
                    natural.Should().BeLessThanOrEqualTo(tb.MaxWidth, $"{code} {module}: 通常のモジュール名なら全文が 1 行に収まる");
                }
                else
                {
                    // 31 文字のモジュール名では全文は収まらなくてよい。そのとき省略されるのは末尾の定型の結果だけ。
                    natural.Should().BeGreaterThan(headWidth);
                }
            }
        }

    }
}
