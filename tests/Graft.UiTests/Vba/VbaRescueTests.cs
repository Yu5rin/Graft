using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests.Vba;

/// <summary>
/// 反映前の自動控え（機能 C。仕様書_ブックの情報と依頼の観点 7章・11.2）のシナリオ。実際の起動と同じ依存グラフ
/// （<see cref="ShellHarness"/>）に <see cref="FakeVbaBridge"/> をつないで、「Excel へ反映」で控えが残ること、
/// 「Excel 側の控えを見る…」の一覧・差分・「ミラーへ戻す」、履歴に何も増えないことを確かめる。
/// </summary>
public class VbaRescueTests : IDisposable
{
    private const string Module1 = "Modules/Module1.bas";

    private readonly ShownWindowTracker _windows = new();

    public void Dispose() => _windows.Dispose();

    private static async Task PushHelloAsync(VbaP3Rig r)
    {
        File.WriteAllText(r.MirrorPath(Module1), "Sub A()\r\n  MsgBox \"hello\"\r\nEnd Sub\r\n");
        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushToExcelCommand).ConfigureAwait(true);
    }

    private static async Task<VbaRescueViewModel> OpenRescueAsync(VbaP3Rig r)
    {
        VbaRescueViewModel? opened = null;
        var pane = r.Graft.ProjectPane;
        pane.ShowRescueAsync = vm =>
        {
            opened = vm;
            return Task.CompletedTask;
        };

        await ShellHarness.ExecuteAsync(pane.ShowRescueCommand).ConfigureAwait(true);

        opened.Should().NotBeNull("「Excel 側の控えを見る…」で窓の ViewModel が作られる");
        opened!.ChooseAsync = request =>
        {
            r.Choices.Add(request);
            return Task.FromResult(r.ChoiceAnswer?.Invoke(request) ?? request.Default.Id);
        };
        await opened.InitializeAsync().ConfigureAwait(true);
        return opened;
    }

    [AvaloniaFact(DisplayName = "手動の「Excel へ反映」: 上書きされる前の Excel 側の本文が .rescue/ に -auto.txt として残る")]
    public async Task 手動の反映で自動控えが残る()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);

        await PushHelloAsync(r).ConfigureAwait(true);

        r.Excel("Module1").Code.Should().Contain("hello");
        r.RescueFileNames().Should().ContainSingle().Which.Should().EndWith("-auto.txt").And.StartWith("Module1-");
        r.RescueTexts(includeAuto: true).Should().ContainSingle().Which.Should().Be(VbaP3Rig.Module1Original);
    }

    [AvaloniaFact(DisplayName = "適用後の自動 Push: Excel が上書きされる前の本文が -auto.txt として残る")]
    public async Task 適用後のPushで自動控えが残る()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);

        await r.PasteAsync(VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);
        await r.ApplyAsync().ConfigureAwait(true);

        r.Excel("Module1").Code.Should().Contain("hello");
        r.RescueFileNames().Should().ContainSingle(n => n.EndsWith("-auto.txt"));
        r.RescueTexts(includeAuto: true).Should().Contain(VbaP3Rig.Module1Original);
    }

    [AvaloniaFact(DisplayName = "「Excel 側の控えを見る…」: 一覧に控えが出て、選ぶと控えと現在のミラーの差分が出る。Excel を呼ばない")]
    public async Task 一覧と差分が出る()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await PushHelloAsync(r).ConfigureAwait(true);
        r.Bridge.Calls.Clear();

        var vm = await OpenRescueAsync(r).ConfigureAwait(true);

        vm.Items.Should().ContainSingle();
        var row = vm.Items[0];
        row.ModuleName.Should().Be("Module1");
        row.KindText.Should().Be("反映前の自動控え");
        row.ShowModuleHeader.Should().BeTrue();
        vm.SelectedItem.Should().BeSameAs(row, "先頭を選んでおく");
        vm.Diff.Lines.Should().NotBeEmpty();
        vm.Diff.Lines.Select(l => l.Left.Text + "|" + l.Right?.Text).Should().Contain(t => t.Contains("hello")).And.Contain(t => t.Contains("\"hi\""));
        vm.IsEmpty.Should().BeFalse();
        r.Bridge.Calls.Should().BeEmpty("控えを見るだけなので、Excel には触れない");
    }

    [AvaloniaFact(DisplayName = "控えが無ければ、一覧は空で案内が出る。メニューは VBA プロジェクトのときだけ出る")]
    public async Task 空の一覧とメニュー()
    {
        using var r = await VbaP3Rig.CreateAsync(plainProjectFirst: true).ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        var view = new ProjectPane { DataContext = pane };
        var window = _windows.Track(new Window { Content = view, Width = 320, Height = 400 });
        window.Show();
        MenuItem Item()
        {
            view.ListBoxElement.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
            Dispatcher.UIThread.RunJobs();
            return view.ListBoxElement.ContextMenu!.Items.OfType<MenuItem>().Single(m => m.Header is "Excel 側の控えを見る…");
        }

        Item().IsVisible.Should().BeTrue("VBA プロジェクトを選んでいる");
        pane.SelectedItem = pane.Items.First(i => i.Project.Vba is null);
        Item().IsVisible.Should().BeFalse("普通のフォルダのプロジェクトでは出さない");

        pane.SelectedItem = pane.Items.First(i => i.Project.Vba is not null);
        var vm = await OpenRescueAsync(r).ConfigureAwait(true);

        vm.IsEmpty.Should().BeTrue();
        vm.Items.Should().BeEmpty();
        vm.EmptyText.Should().Contain("控えはまだありません");
        vm.Diff.Lines.Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "「ミラーへ戻す」: 確認のあと、ミラーのファイルが控えの内容に戻り、バッジが「未反映」になる。Excel は変わらず、履歴にも何も増えない")]
    public async Task 戻すとミラーが戻る()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await PushHelloAsync(r).ConfigureAwait(true);
        var excelAfterPush = r.Excel("Module1").Code;
        var writes = r.WriteCalls;
        var vm = await OpenRescueAsync(r).ConfigureAwait(true);
        (await r.StatusAsync().ConfigureAwait(true)).UnpushedCount.Should().Be(0);

        await vm.RestoreAsync().ConfigureAwait(true);

        r.Choices.Should().ContainSingle();
        r.Choices[0].Options.Select(o => o.Label).Should().Equal("ミラーへ戻す", "やめる");
        r.Mirror(Module1).Should().Be(VbaP3Rig.Module1Original + "\r\n", "既存の形（CRLF・末尾改行）を保って戻る");
        (await r.StatusAsync().ConfigureAwait(true)).UnpushedCount.Should().Be(1);
        r.Excel("Module1").Code.Should().Be(excelAfterPush, "Excel へは書かない");
        r.WriteCalls.Should().Be(writes);
        (await r.RevisionsAsync().ConfigureAwait(true)).Should().BeEmpty("履歴（リビジョン）に何も増えない");
        r.Graft.History.Items.Should().BeEmpty();
        vm.StatusText.Should().Be("戻しました。履歴には記録されません。Excel に送るには『Excel へ反映』を押してください。");
        vm.ShowPushNow.Should().BeTrue("戻した直後は「今すぐ Excel へ反映」を出す（プロジェクトペインが反映の入口を配線している）");
        // 戻す前のミラーは -mirror として一覧に増える。
        vm.Items.Select(i => i.KindText).Should().Contain("戻す前のミラー");
        r.RescueFileNames().Should().Contain(n => n.EndsWith("-mirror.txt"));
    }

    [AvaloniaFact(DisplayName = "「ミラーへ戻す」の確認で「やめる」を選ぶ（または既定のまま閉じる）と、何も変わらない")]
    public async Task 確認でやめると戻らない()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await PushHelloAsync(r).ConfigureAwait(true);
        var vm = await OpenRescueAsync(r).ConfigureAwait(true);
        var before = r.Mirror(Module1);
        r.ChoiceAnswer = _ => "cancel";

        await vm.RestoreAsync().ConfigureAwait(true);

        r.Mirror(Module1).Should().Be(before);
        r.RescueFileNames().Should().NotContain(n => n.EndsWith("-mirror.txt"));
    }

    [AvaloniaFact(DisplayName = "戻した直後だけ「今すぐ Excel へ反映」が出て、押すと戻したモジュール 1 件だけが Excel へ送られる")]
    public async Task 戻した直後に今すぐ反映できる()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await PushHelloAsync(r).ConfigureAwait(true);
        File.WriteAllText(r.MirrorPath("Classes/Cls1.cls"), "Option Explicit\r\nPublic Value As Currency\r\n"); // 戻さないモジュールの未反映
        var pushed = new List<string>();
        VbaRescueViewModel? opened = null;
        var pane = r.Graft.ProjectPane;
        pane.ShowRescueAsync = async vm =>
        {
            opened = vm;
            vm.ChooseAsync = request => Task.FromResult(request.Default.Id);
            await vm.InitializeAsync().ConfigureAwait(true);
            await vm.RestoreAsync().ConfigureAwait(true);
            vm.ShowPushNow.Should().BeTrue();
            var inner = vm.PushModuleAsync!;
            vm.PushModuleAsync = async relative =>
            {
                pushed.Add(relative);
                await inner(relative).ConfigureAwait(true);
            };
            await ShellHarness.ExecuteAsync(vm.PushNowCommand).ConfigureAwait(true);
        };

        await ShellHarness.ExecuteAsync(pane.ShowRescueCommand).ConfigureAwait(true);

        opened.Should().NotBeNull();
        pushed.Should().Equal(Module1);
        r.Excel("Module1").Code.Should().Be(VbaP3Rig.Module1Original, "控えの内容が Excel へ戻った");
        r.Excel("Cls1").Code.Should().NotContain("Currency", "戻していないモジュールは送らない");
        opened!.ShowPushNow.Should().BeFalse("送ったら、ボタンは引っ込む");
    }

    [AvaloniaFact(DisplayName = "戻す: 対象のミラーのタブに未保存の変更があれば、保存の確認が出る。断ると戻さない")]
    public async Task 未保存の確認()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await PushHelloAsync(r).ConfigureAwait(true);
        var vm = await OpenRescueAsync(r).ConfigureAwait(true);
        var before = r.Mirror(Module1);
        var tab = (await r.H.Shell.Editor.OpenFileAsync(r.MirrorPath(Module1)).ConfigureAwait(true)).Value;
        tab.Session.Document.Insert(0, "' 未保存の編集\r\n");
        tab.Session.IsModified.Should().BeTrue("前提: 戻し先に未保存の編集がある");
        r.H.Dialogs.ConfirmAnswer = (_, _) => false;

        await vm.RestoreAsync().ConfigureAwait(true);

        r.H.Dialogs.Confirms.Should().ContainSingle().Which.Message.Should().Contain("控えを戻すモジュール").And.Contain("未保存の変更");
        r.Mirror(Module1).Should().Be(before);
        vm.StatusText.Should().Contain("未保存の変更があるため、戻しませんでした");
    }

    [AvaloniaFact(DisplayName = "「控えを削除」: 確認の既定は「やめる」。「控えを削除」を選ぶと、その 1 件だけが消える")]
    public async Task 控えを削除する()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await PushHelloAsync(r).ConfigureAwait(true);
        var vm = await OpenRescueAsync(r).ConfigureAwait(true);

        await vm.DeleteAsync().ConfigureAwait(true); // 既定のまま（Enter）は「やめる」。
        vm.Items.Should().ContainSingle();
        r.Choices.Last().Default.Label.Should().Be("やめる");

        r.ChoiceAnswer = request => request.Options.First(o => o.Id == "delete").Id;
        await vm.DeleteAsync().ConfigureAwait(true);

        vm.Items.Should().BeEmpty();
        vm.IsEmpty.Should().BeTrue();
        r.RescueFileNames().Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "「保存先を開く」: 控えのフォルダを開く")]
    public async Task 保存先を開く()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await PushHelloAsync(r).ConfigureAwait(true);
        var vm = await OpenRescueAsync(r).ConfigureAwait(true);
        string? revealed = null;
        vm.RevealFolder = path => revealed = path;

        vm.OpenFolderCommand.Execute(null);

        revealed.Should().Be(r.H.AppPaths.GetVbaRescueDirectory(r.Project.Id));
    }

    [AvaloniaFact(DisplayName = "設定で VBA 連携を切っていても、控えは見られる（ファイルを見るだけ）。コマンドパレットにも出る")]
    public async Task 連携を切っても見られる()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await PushHelloAsync(r).ConfigureAwait(true);
        r.Graft.UpdateSettings(r.Graft.CurrentSettings with { Vba = new VbaSettings { Enabled = false } });
        var pane = r.Graft.ProjectPane;
        pane.ShowRescueAsync = _ => Task.CompletedTask;

        pane.IsVbaFeatureAvailable.Should().BeFalse("連携は切れている");
        pane.ShowRescueCommand.CanExecute(null).Should().BeTrue();
        r.H.Shell.CommandPalette.Open();
        r.H.Shell.CommandPalette.Results.Select(i => i.Title).Should().Contain("Excel 側の控えを見る（選択中の VBA プロジェクト）");
    }

    [AvaloniaFact(DisplayName = "窓: 描画でき、控えの一覧・差分・ボタンが出る。Esc で閉じる")]
    public async Task 窓の描画()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await PushHelloAsync(r).ConfigureAwait(true);
        var vm = await OpenRescueAsync(r).ConfigureAwait(true);

        var window = _windows.Track(new VbaRescueWindow(vm));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.FindControl<ListBox>("ItemsListBox")!.ItemCount.Should().Be(1);
        window.FindControl<Button>("RestoreButton")!.IsEnabled.Should().BeTrue();
        window.FindControl<Button>("PushNowButton")!.IsVisible.Should().BeFalse();
        window.GetVisualDescendants().OfType<DiffView>().Should().ContainSingle();
    }
}
