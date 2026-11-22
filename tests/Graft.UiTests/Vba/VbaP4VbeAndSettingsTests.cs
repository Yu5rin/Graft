using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;
using Graft.Views.SettingsPanels;

namespace Graft.UiTests.Vba;

/// <summary>
/// Excel VBA 連携 段階 P4 のシナリオ（仕様書_VBA連携 7.6・8・9・10.2）のうち、
/// 「VBE で開く」の表示条件と動作、E804 の「Excel で開く」ボタン、設定パネル、連携を切ったときの挙動。
/// </summary>
public class VbaP4VbeAndSettingsTests : IDisposable
{
    private const string Module1 = "Modules/Module1.bas";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "graft-vba-p4", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 後片付けの失敗は検証結果に影響しない。
        }

        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    // VBE で開く
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "VBE で開く: 表示条件は「VBA プロジェクトのミラー内の同期対象のファイル」だけ（通常のプロジェクト・ミラー外・入れ子・連携オフ・使えない環境では出ない）")]
    public async Task VBEで開くの表示条件()
    {
        using var r = await VbaP3Rig.CreateAsync(plainProjectFirst: true).ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;

        pane.CanShowInVbe(Module1).Should().BeTrue();
        pane.CanShowInVbe(@"Classes\Cls1.cls").Should().BeTrue("区切りが \\ でも判定できる");
        pane.CanShowInVbe("Documents/Sheet1.cls").Should().BeTrue();
        pane.CanShowInVbe("Modules/x.cls").Should().BeFalse("フォルダと拡張子が合わない");
        pane.CanShowInVbe("Modules/sub/x.bas").Should().BeFalse("入れ子は同期の対象外");
        pane.CanShowInVbe("note.txt").Should().BeFalse("ミラーの外のファイル");
        pane.CanShowInVbe(null).Should().BeFalse();

        // 差分ビューとエディタも、同じ判定につながっている。
        r.H.Shell.Editor.CanShowInVbe!(Module1).Should().BeTrue();
        await r.PasteAsync(VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);
        r.Graft.SelectedBlock = r.Graft.Blocks[0];
        r.Graft.Diff.IsShowInVbeAvailable.Should().BeTrue();

        r.Graft.UpdateSettings(r.Graft.CurrentSettings with { Vba = new VbaSettings { Enabled = false } });
        pane.CanShowInVbe(Module1).Should().BeFalse("連携を切っているときは出さない");
        r.Graft.Diff.IsShowInVbeAvailable.Should().BeFalse();
        r.Graft.UpdateSettings(r.Graft.CurrentSettings with { Vba = new VbaSettings { Enabled = true } });

        await pane.SelectAsync(pane.Items.First(i => i.Project.Vba is null)).ConfigureAwait(true);
        pane.CanShowInVbe(Module1).Should().BeFalse("通常のファイルプロジェクトでは出さない（挙動は従来どおり）");
        r.H.Shell.Editor.CanShowInVbe!(Module1).Should().BeFalse();
        r.Graft.Diff.IsShowInVbeAvailable.Should().BeFalse("表示中の差分は VBA プロジェクトのものでも、選択中のプロジェクトで判定する");

        await pane.SelectAsync(pane.Items.First(i => i.Project.Vba is not null)).ConfigureAwait(true);
        r.Bridge.IsSupported = false;
        pane.CanShowInVbe(Module1).Should().BeFalse("この環境で使えないときは出さない");
    }

    [AvaloniaFact(DisplayName = "VBE で開く: 指定したモジュールと行で ShowInVbe が呼ばれる。ブックが開いていなければ理由を案内し、バッジがグレーになる")]
    public async Task VBEで開くを実行する()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;

        await pane.ShowInVbeAsync(@"Classes\Cls1.cls", 2).ConfigureAwait(true);
        r.Bridge.Calls.Should().Contain($"Show:{VbaP3Rig.Book}:Cls1:2");

        await pane.ShowInVbeAsync(Module1, 0).ConfigureAwait(true);
        r.Bridge.Calls.Should().Contain($"Show:{VbaP3Rig.Book}:Module1:1", "0 以下の行は 1 行目に丸める");

        r.Bridge.CloseWorkbook(VbaP3Rig.Book);
        await pane.ShowInVbeAsync(Module1, 3).ConfigureAwait(true);
        r.H.Dialogs.Messages.Should().ContainSingle(m => m.Title == "VBE で開けませんでした")
            .Which.Message.Should().Contain("E804", "ブックが開いていない理由を案内する");
        pane.Items.Single(i => i.Project.Vba is not null).IsVbaBadgeGray.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "VBE で開く: 差分の行から開く行を決める（変更前の行番号を優先し、追加行は変更後の番号。行の外なら 1）")]
    public async Task 差分の行から行番号を決める()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await r.PasteAsync(VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"\n  Debug.Print 1")).ConfigureAwait(true);
        r.Graft.SelectedBlock = r.Graft.Blocks[0];

        var rows = r.Graft.Diff.Lines;
        var removed = rows.First(l => l.Left.IsRemoved || l.Left.OldLineText == "2");
        DiffViewModel.VbeLineOf(removed).Should().Be(2, "変更前の 2 行目（MsgBox の行）");
        DiffViewModel.VbeLineOf(null).Should().Be(1);

        var opened = new List<(string Path, int Line)>();
        r.Graft.Diff.ShowInVbeAsync = (path, line) =>
        {
            opened.Add((path, line));
            return Task.CompletedTask;
        };
        await r.Graft.Diff.OpenInVbeAsync(removed).ConfigureAwait(true);
        opened.Should().ContainSingle().Which.Should().Be((Module1, 2));
    }

    // ------------------------------------------------------------------
    // E804 の「Excel で開く」ボタン（選択ダイアログ）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "E804: 接続できずに「ファイルだけ適用する／中止」を選ばせる確認に、ブックのパスを持たせる（ボタンの元になる）")]
    public async Task 接続不可の確認にブックのパスが付く()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        r.Bridge.CloseWorkbook(VbaP3Rig.Book);
        r.ChoiceAnswer = _ => "abort";

        await r.PasteAsync(VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);

        var request = r.Choices.Should().ContainSingle().Subject;
        request.OpenWorkbookPath.Should().Be(VbaP3Rig.Book);
        request.Options.Select(o => o.Id).Should().Equal("fileOnly", "abort");
    }

    [AvaloniaFact(DisplayName = "E804: 選択ダイアログ（VbaChoiceWindow）に「Excel で開く」ボタンが載り、押しても窓は閉じずブックを開く。パスが無ければボタンは出ない")]
    public void 選択ダイアログに開くボタンが載る()
    {
        var options = new[] { new VbaChoiceOption("fileOnly", "ファイルだけ適用する"), new VbaChoiceOption("abort", "中止", IsDefault: true) };
        var opened = new List<string>();

        var withPath = new VbaChoiceWindow(new VbaChoiceRequest("同期できません", "本文", options, VbaP3Rig.Book), opened.Add);
        withPath.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var buttons = withPath.GetVisualDescendants().OfType<Button>().ToList();
            buttons.Select(b => b.Content).Should().Equal("Excel で開く", "ファイルだけ適用する", "中止");

            var open = buttons[0];
            open.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            opened.Should().Equal(VbaP3Rig.Book);
            withPath.IsVisible.Should().BeTrue("ブックを開いても、選択のダイアログは閉じない");
        }
        finally
        {
            withPath.Close();
        }

        var without = new VbaChoiceWindow(new VbaChoiceRequest("確認", "本文", options), opened.Add);
        without.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            without.GetVisualDescendants().OfType<Button>().Select(b => b.Content).Should().Equal("ファイルだけ適用する", "中止");
        }
        finally
        {
            without.Close();
        }
    }

    // ------------------------------------------------------------------
    // 設定パネル
    // ------------------------------------------------------------------

    private AppPaths NewAppPaths()
    {
        var appPaths = new AppPaths(Path.Combine(_root, "app"));
        appPaths.EnsureCoreDirectoriesExist();
        return appPaths;
    }

    [AvaloniaFact(DisplayName = "設定パネル: Null プラットフォームでは中身が出ず、パネル全体に「この環境では使えない機能です」（E706 の文言）が出る")]
    public async Task Nullではパネルの中身が出ない()
    {
        var vm = new SettingsViewModel(NewAppPaths(), new NullDialogService(), new AvaloniaUiServices());
        await vm.InitializeAsync().ConfigureAwait(true);
        vm.IsVbaSupported.Should().BeFalse("Linux の既定は Null ブリッジ");

        var view = new VbaSettingsView { DataContext = vm };
        var window = new Window { Width = 700, Height = 500, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            view.FindControl<StackPanel>("VbaSettingsContent")!.IsVisible.Should().BeFalse("入力欄は一切出さない");
            view.FindControl<StackPanel>("VbaUnsupportedPanel")!.IsVisible.Should().BeTrue();
            view.FindControl<TextBlock>("VbaUnsupportedMessage")!.Text.Should().Be("この環境では使えない機能です");
            vm.VbaUnsupportedText.Should().Be(ErrorCatalog.SummaryOf(ErrorCode.E706));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "設定パネル: 使える環境では中身が出て案内は出ない。設定画面のタブは14個（依頼の観点のタブを足した）あり、「VBA 連携」もある")]
    public async Task 使える環境ではパネルの中身が出る()
    {
        var vm = new SettingsViewModel(NewAppPaths(), new NullDialogService(), new AvaloniaUiServices()) { VbaSupportProbe = () => true };
        await vm.InitializeAsync().ConfigureAwait(true);

        var view = new VbaSettingsView { DataContext = vm };
        var window = new Window { Width = 700, Height = 500, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            view.FindControl<StackPanel>("VbaSettingsContent")!.IsVisible.Should().BeTrue();
            view.FindControl<StackPanel>("VbaUnsupportedPanel")!.IsVisible.Should().BeFalse();
        }
        finally
        {
            window.Close();
        }

        var settings = new SettingsWindow(vm);
        settings.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var tabs = settings.GetVisualDescendants().OfType<TabControl>().First(t => t.Name == "CategoryTabControl");
            // 13個だったところへ、依頼の観点のタブ（仕様書_ブックの情報と依頼の観点 6.6。14個目）が加わった。
            tabs.Items.OfType<TabItem>().Should().HaveCount(14);
            tabs.Items.OfType<TabItem>().Select(t => t.Header).Should().Contain("VBA 連携").And.Contain("依頼の観点");
        }
        finally
        {
            settings.Close();
        }
    }

    [AvaloniaFact(DisplayName = "設定パネル: 5項目を変えて保存すると settings.json に入り、読み直した画面にも出る（設定の保存往復）")]
    public async Task 設定の保存往復()
    {
        var appPaths = NewAppPaths();
        var vm = new SettingsViewModel(appPaths, new NullDialogService(), new AvaloniaUiServices());
        await vm.InitializeAsync().ConfigureAwait(true);
        vm.VbaEnabled.Should().BeTrue("既定");
        vm.VbaComTimeoutMsText.Should().Be("5000");
        vm.VbaCallTimeoutMsText.Should().Be("30000");

        vm.VbaEnabled = false;
        vm.VbaPullOnContextOpen = false;
        vm.VbaPullBeforeDryRun = false;
        vm.VbaComTimeoutMsText = "8000";
        vm.VbaCallTimeoutMsText = "45000";
        await vm.FlushPendingSaveAsync().ConfigureAwait(true);

        var saved = (await new SettingsStore(appPaths).LoadAsync().ConfigureAwait(true)).Value.Vba;
        saved.Should().Be(new VbaSettings
        {
            Enabled = false, PullOnContextOpen = false, PullBeforeDryRun = false, ComTimeoutMs = 8000, CallTimeoutMs = 45000,
        });

        var reopened = new SettingsViewModel(appPaths, new NullDialogService(), new AvaloniaUiServices());
        await reopened.InitializeAsync().ConfigureAwait(true);
        reopened.VbaEnabled.Should().BeFalse();
        reopened.VbaPullOnContextOpen.Should().BeFalse();
        reopened.VbaPullBeforeDryRun.Should().BeFalse();
        reopened.VbaComTimeoutMsText.Should().Be("8000");
        reopened.VbaCallTimeoutMsText.Should().Be("45000");
    }

    [AvaloniaFact(DisplayName = "設定パネル: 画面で変えた値が、実行中の MainViewModel に届き、ブリッジの時間切れと機能の有効・無効に効く")]
    public async Task 設定の変更が実行中の動作に届く()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        var settings = r.Graft.CurrentSettings with { Vba = new VbaSettings { ComTimeoutMs = 7000, CallTimeoutMs = 50000, Enabled = false } };

        r.Graft.UpdateSettings(settings);

        r.Bridge.ComTimeoutMs.Should().Be(7000);
        r.Bridge.CallTimeoutMs.Should().Be(50000);
        r.Graft.ProjectPane.IsVbaFeatureAvailable.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // Vba.Enabled=false で VBA の機能が止まる
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "Vba.Enabled=false: ボタン・メニュー・コマンド・自動の Pull/Push・VBE で開くがすべて止まり、Excel を一切呼ばない。ミラーは普通のファイルとして適用できる")]
    public async Task 連携を切ると全部止まる()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        pane.IsVbaFeatureAvailable.Should().BeTrue();

        r.Graft.UpdateSettings(r.Graft.CurrentSettings with { Vba = new VbaSettings { Enabled = false } });
        r.Bridge.Calls.Clear();

        pane.IsVbaFeatureAvailable.Should().BeFalse("「Excel ブックから追加」ボタンとコマンドパレットの項目が出ない");
        pane.AddFromWorkbookCommand.CanExecute(null).Should().BeFalse();
        pane.PullFromExcelCommand.CanExecute(null).Should().BeFalse();
        pane.PushToExcelCommand.CanExecute(null).Should().BeFalse();
        pane.RestoreFromExcelCommand.CanExecute(null).Should().BeFalse();
        pane.ActivateWorkbookCommand.CanExecute(null).Should().BeFalse();
        pane.CanShowInVbe(Module1).Should().BeFalse();

        // 自動の Pull（解析前・コンテキスト収集）と自動の Push（適用後）。
        r.Excel("Module1").Code = "Sub A()\r\n  ' excel side\r\nEnd Sub";
        await r.Graft.ContextCollect!.InitializeAsync().ConfigureAwait(true);
        await r.PasteAsync(VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);
        await r.ApplyAsync().ConfigureAwait(true);

        r.Mirror(Module1).Should().Contain("hello").And.NotContain("excel side");
        r.Excel("Module1").Code.Should().Contain("excel side", "Excel には反映されない");
        r.Bridge.Calls.Should().BeEmpty("機能を切っているときは COM を一切呼ばない");
        r.Choices.Should().BeEmpty();
        r.Conflicts.Should().BeEmpty();
    }
}
