using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Infra;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests.Vba;

/// <summary>
/// 衝突ダイアログ（<see cref="VbaConflictWindow"/>、仕様書_VBA連携 7.5）と選択ダイアログ（<see cref="VbaChoiceWindow"/>）の
/// 画面の試験。ViewModel の選択の進み方、場面ごとのボタンの出し分け、選ばずに閉じたときの既定の結果、描画。
/// <para>
/// 環境変数 <c>GRAFT_SCREENSHOT_DIR</c> を指定すると、描画した画面を PNG で書き出す（見た目の確認用。指定が無ければ書かない）。
/// </para>
/// </summary>
public class VbaP3DialogTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    private static VbaConflictItem Item(string path, string excel, string mirror)
        => new(path, VbaModuleKind.Standard, Path.GetFileNameWithoutExtension(path), excel, mirror);

    private static VbaConflictViewModel NewViewModel(VbaConflictScene scene, params VbaConflictItem[] items)
        => new(new VbaConflictRequest(scene, items), new Settings(), new FakeUiServices(new FakeClipboard()));

    private static readonly VbaConflictItem[] Sample =
    {
        Item("Modules/Module1.bas",
            "Option Explicit\r\n\r\nSub 集計を更新する()\r\n  Dim ws As Worksheet\r\n  Set ws = ThisWorkbook.Worksheets(\"集計\")\r\n  ws.Range(\"A1\").Value = Now\r\n  ' VBE で追加した行\r\n  Call 合計を求める(ws)\r\nEnd Sub\r\n",
            "Option Explicit\r\n\r\nSub 集計を更新する()\r\n  Dim ws As Worksheet\r\n  Set ws = ThisWorkbook.Worksheets(\"集計\")\r\n  ws.Range(\"A1\").Value = Date\r\n  Call 合計を求める(ws)\r\n  Call 書式を整える(ws)\r\nEnd Sub\r\n"),
        Item("Classes/Cls1.cls", "Option Explicit\r\nPublic Value As Long\r\n", "Option Explicit\r\nPublic Value As Double\r\n"),
        Item("Modules/Helpers.bas", "Function Twice(x)\r\n  Twice = x * 2\r\nEnd Function\r\n", "Function Twice(x As Long) As Long\r\n  Twice = x * 2\r\nEnd Function\r\n"),
    };

    // ------------------------------------------------------------------
    // ViewModel
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "衝突ダイアログの選択: 選んだら次の未選択へ進み、全件が決まったら閉じる。結果はモジュールごとの選択")]
    public void 一件ずつ選んで進む()
    {
        var vm = NewViewModel(VbaConflictScene.BeforeAnalysis, Sample);
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        vm.SelectedItem!.Item.RelativePath.Should().Be("Modules/Module1.bas");
        vm.ProgressText.Should().Be("0 / 3 件を選択済み");
        vm.OverwriteCommand.Execute(null);
        vm.SelectedItem!.Item.RelativePath.Should().Be("Classes/Cls1.cls", "次の未選択へ進む");
        vm.Items[0].ChoiceText.Should().Be("選択: Graft 側で上書き");
        vm.ProgressText.Should().Be("1 / 3 件を選択済み");
        vm.AdoptCommand.Execute(null);
        closed.Should().Be(0);
        vm.OverwriteCommand.Execute(null);

        closed.Should().Be(1);
        vm.Result!.Aborted.Should().BeFalse();
        vm.Result.PathsWith(VbaConflictChoice.OverwriteExcel).Should().BeEquivalentTo("Modules/Module1.bas", "Modules/Helpers.bas");
        vm.Result.PathsWith(VbaConflictChoice.AdoptExcel).Should().BeEquivalentTo("Classes/Cls1.cls");
    }

    [AvaloniaFact(DisplayName = "衝突ダイアログの「すべてに適用」: チェックして選ぶと全件に同じ選択が付いて閉じる。件数が 1 件ならチェックは出さない")]
    public void すべてに適用()
    {
        var vm = NewViewModel(VbaConflictScene.ManualPull, Sample);
        vm.HasMultiple.Should().BeTrue();
        vm.ApplyToAll = true;

        vm.AdoptCommand.Execute(null);

        vm.Result!.PathsWith(VbaConflictChoice.AdoptExcel).Should().HaveCount(3);
        NewViewModel(VbaConflictScene.ManualPull, Sample[0]).HasMultiple.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "衝突ダイアログの中止と既定: 中止は Aborted。選ばずに閉じたときの結果は場面の既定（解析前は中止、適用後は「反映しない」）")]
    public void 中止と既定()
    {
        var before = NewViewModel(VbaConflictScene.BeforeAnalysis, Sample);
        before.AbortCommand.Execute(null);
        before.Result!.Aborted.Should().BeTrue();

        var closing = NewViewModel(VbaConflictScene.BeforeAnalysis, Sample);
        closing.CloseWithoutChoosing();
        closing.Result!.Aborted.Should().BeTrue();

        var after = NewViewModel(VbaConflictScene.AfterApply, Sample);
        after.CloseWithoutChoosing();
        after.Result!.Aborted.Should().BeFalse();
        after.Result.PathsWith(VbaConflictChoice.Skip).Should().HaveCount(3);
    }

    [AvaloniaFact(DisplayName = "衝突ダイアログの場面ごとのボタン: 適用の後は「Excel 側を採用」と「中止」が使えず、「反映しない」と「この適用を取り消す」が使える")]
    public void 場面ごとにボタンが変わる()
    {
        var applied = NewViewModel(VbaConflictScene.AfterApply, Sample);
        applied.CanAdoptExcel.Should().BeFalse();
        applied.AdoptCommand.CanExecute(null).Should().BeFalse();
        applied.CanAbort.Should().BeFalse();
        applied.AbortCommand.CanExecute(null).Should().BeFalse();
        applied.CanSkip.Should().BeTrue();
        applied.CanUndoApply.Should().BeTrue();

        applied.UndoApplyCommand.Execute(null);
        applied.Result!.UndoApply.Should().BeTrue();

        var restored = NewViewModel(VbaConflictScene.AfterRestore, Sample);
        restored.CanUndoApply.Should().BeFalse("復元の後は、適用を取り消す選択肢を出さない");
        restored.CanSkip.Should().BeTrue();

        var manual = NewViewModel(VbaConflictScene.ManualPush, Sample);
        manual.CanAdoptExcel.Should().BeTrue();
        manual.CanSkip.Should().BeFalse();
        manual.SkipCommand.CanExecute(null).Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "衝突ダイアログの差分: 選んだモジュールの Excel 側とミラーの差分が既存の DiffViewModel に出る。選び直すと切り替わる")]
    public void 差分が出る()
    {
        var vm = NewViewModel(VbaConflictScene.BeforeAnalysis, Sample);

        vm.Diff.Lines.Should().NotBeEmpty();
        var first = vm.Diff.Lines.Count;

        vm.SelectedItem = vm.Items[1];

        vm.Diff.Lines.Should().NotBeEmpty();
        vm.Diff.Lines.Count.Should().BeLessThan(first, "Cls1 は差分が小さい");
    }

    // ------------------------------------------------------------------
    // 窓
    // ------------------------------------------------------------------

    private Window ShowOwner()
    {
        var owner = _windows.Track(new Window { Width = 1280, Height = 800 });
        owner.Show();
        return owner;
    }

    [AvaloniaFact(DisplayName = "衝突ダイアログの窓: 選択ボタンで結果が決まって閉じる。×で閉じると場面の既定の結果になる")]
    public async Task 窓の結果()
    {
        var owner = ShowOwner();

        var window = _windows.Track(new VbaConflictWindow(NewViewModel(VbaConflictScene.ManualPush, Sample[1])));
        var pending = window.ShowAndResolveAsync(owner);
        Dispatcher.UIThread.RunJobs();
        FindButton(window, "Graft 側で上書き").Command!.Execute(null);
        var decision = await pending.ConfigureAwait(true);
        decision.PathsWith(VbaConflictChoice.OverwriteExcel).Should().Equal("Classes/Cls1.cls");

        var closing = _windows.Track(new VbaConflictWindow(NewViewModel(VbaConflictScene.AfterApply, Sample[1])));
        var pendingClose = closing.ShowAndResolveAsync(owner);
        Dispatcher.UIThread.RunJobs();
        closing.Close();
        var fallback = await pendingClose.ConfigureAwait(true);
        fallback.Aborted.Should().BeFalse();
        fallback.PathsWith(VbaConflictChoice.Skip).Should().Equal("Classes/Cls1.cls");
    }

    [AvaloniaFact(DisplayName = "衝突ダイアログの窓: 適用の後の場面では「Excel 側を採用」と「中止」のボタンが見えず、「反映しない」「この適用を取り消す」が見える")]
    public void 窓のボタンの出し分け()
    {
        var owner = ShowOwner();
        var applied = _windows.Track(new VbaConflictWindow(NewViewModel(VbaConflictScene.AfterApply, Sample)));
        applied.Show(owner);
        Dispatcher.UIThread.RunJobs();

        FindButton(applied, "Excel 側を採用").IsVisible.Should().BeFalse();
        FindButton(applied, "中止").IsVisible.Should().BeFalse();
        FindButton(applied, "反映しない").IsVisible.Should().BeTrue();
        FindButton(applied, "この適用を取り消す").IsVisible.Should().BeTrue();
        FindButton(applied, "Graft 側で上書き").IsVisible.Should().BeTrue();

        var before = _windows.Track(new VbaConflictWindow(NewViewModel(VbaConflictScene.BeforeAnalysis, Sample)));
        before.Show(owner);
        Dispatcher.UIThread.RunJobs();
        FindButton(before, "Excel 側を採用").IsVisible.Should().BeTrue();
        FindButton(before, "中止").IsVisible.Should().BeTrue();
        FindButton(before, "反映しない").IsVisible.Should().BeFalse();
        FindButton(before, "この適用を取り消す").IsVisible.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "選択ダイアログの窓: ボタンの文言と並び、既定のボタンが primary。ボタンで結果が決まり、×で閉じると既定のボタンの結果になる")]
    public async Task 選択ダイアログの窓()
    {
        var owner = ShowOwner();
        var request = new VbaChoiceRequest("Excel へ反映できませんでした", "r12 の変更は適用済みですが、Excel へ反映できませんでした。", new[]
        {
            new VbaChoiceOption("keep", "ファイルの変更は残す（未反映の印）", IsDefault: true),
            new VbaChoiceOption("retry", "もう一度反映する"),
            new VbaChoiceOption("undo", "この適用（r12）を取り消す"),
        });

        var window = _windows.Track(new VbaChoiceWindow(request));
        var pending = window.ShowAndChooseAsync(owner);
        Dispatcher.UIThread.RunJobs();
        window.Title.Should().Be("Excel へ反映できませんでした");
        var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
        buttons.Select(b => b.Content).Should().Equal("ファイルの変更は残す（未反映の印）", "もう一度反映する", "この適用（r12）を取り消す");
        buttons[0].IsDefault.Should().BeTrue();
        buttons[0].Classes.Should().Contain("primary");
        buttons[1].IsDefault.Should().BeFalse();
        buttons[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        (await pending.ConfigureAwait(true)).Should().Be("undo");

        var closing = _windows.Track(new VbaChoiceWindow(request));
        var pendingClose = closing.ShowAndChooseAsync(owner);
        Dispatcher.UIThread.RunJobs();
        closing.Close();
        (await pendingClose.ConfigureAwait(true)).Should().Be("keep", "選ばずに閉じたら、何も失わない既定のボタン");
    }

    private static Button FindButton(Window window, string automationName)
        => window.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == automationName);

    // ------------------------------------------------------------------
    // 描画（見た目の確認。GRAFT_SCREENSHOT_DIR を指定すると PNG も書き出す）
    // ------------------------------------------------------------------

    private static void Capture(Window window, string fileName)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull($"{fileName} が描画されている");
        var directory = Environment.GetEnvironmentVariable("GRAFT_SCREENSHOT_DIR");
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            frame!.Save(Path.Combine(directory, fileName));
        }
    }

    [AvaloniaFact(DisplayName = "描画: 衝突ダイアログ（解析前・適用の後）と、Push 失敗の選択ダイアログが表示できる")]
    public void 画面を描画する()
    {
        var owner = ShowOwner();

        var before = _windows.Track(new VbaConflictWindow(NewViewModel(VbaConflictScene.BeforeAnalysis, Sample)) { Width = 1120, Height = 720 });
        before.Show(owner);
        Capture(before, "p3-conflict-before-analysis.png");

        var applied = _windows.Track(new VbaConflictWindow(NewViewModel(VbaConflictScene.AfterApply, Sample[0])) { Width = 1120, Height = 720 });
        applied.Show(owner);
        Capture(applied, "p3-conflict-after-apply.png");

        var failure = _windows.Track(new VbaChoiceWindow(new VbaChoiceRequest(
            "Excel へ反映できませんでした",
            "r12 の変更は Graft のファイルに適用済みですが、Excel へ反映できなかったものがあります。" + Environment.NewLine +
            "反映できなかったモジュール: Modules/Module1.bas" + Environment.NewLine + Environment.NewLine +
            "E805: Excel が応答しません" + Environment.NewLine + "対処: セルを編集中なら確定し、ダイアログが開いていれば閉じてください。マクロの実行中でないかも確認してください。" +
            Environment.NewLine + Environment.NewLine +
            "「ファイルの変更は残す」を選ぶと、未反映の印を付けて残します。あとからプロジェクトの右クリック「Excel へ反映」で送れます。",
            new[]
            {
                new VbaChoiceOption("keep", "ファイルの変更は残す（未反映の印）", IsDefault: true),
                new VbaChoiceOption("retry", "もう一度反映する"),
                new VbaChoiceOption("undo", "この適用（r12）を取り消す"),
            })));
        failure.Show(owner);
        Capture(failure, "p3-push-failure.png");
    }
}
