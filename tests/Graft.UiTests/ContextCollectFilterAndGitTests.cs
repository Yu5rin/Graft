using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// コンテキスト収集の窓の改善2点のテスト。
/// (1) ファイル名の絞り込み（<see cref="ContextCollectViewModel.FilterText"/>）:
///     一致したファイルだけを相対パスつきで平らに並べ、フォルダ行と「すべて」行は出さない。
///     絞り込み中の切り替えは通常どおり保存され、欄を空にするとツリーに反映されて戻る。
/// (2) 「gitの変更ファイルだけ」（<see cref="ContextCollectViewModel.SelectGitChangedCommand"/>）:
///     変更ファイルだけを「内容も出す」、ほかを「構成だけ」にする。
/// 絞り込みのデバウンスと保存のデバウンスは実時間（DispatcherTimer）で動くため、他のデバウンス
/// テスト（ExplorerFilterTestsなど）と同じく、実際に待つ。gitは一時フォルダで実際に
/// <c>git init</c>して変更を作る（GitAutoCommitScenarioTestsと同じ作法。gitが無い環境では
/// 黙ってスキップせず、失敗として気付けるようにしている）。
/// </summary>
public class ContextCollectFilterAndGitTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-ctxcollect-ux", Guid.NewGuid().ToString("N"));

    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
        // gitオブジェクトファイルの読み取り専用属性解除を含む共通の後片付け。
        TempDirectoryCleanup.TryDeleteRecursive(_root);
        GC.SuppressFinalize(this);
    }

    // ==================================================================
    // 1. ファイル名の絞り込み
    // ==================================================================

    [AvaloniaFact(DisplayName = "絞り込み: 一覧が一致したファイルだけ（相対パスつき・インデント無し）になり、件数が出る")]
    public async Task 絞り込むと一致したファイルだけが相対パスで並び件数が出る()
    {
        var (vm, _, _) = await BuildAsync("filter-basic", ws =>
        {
            ws.WriteText("src/api/user.py", "x");
            ws.WriteText("src/api/order.py", "x");
            ws.WriteText("src/web/user.py", "x");
            ws.WriteText("README.md", "x");
        });

        vm.FilterText = "user";
        await WaitForAsync(() => vm.IsFiltering);

        vm.DisplayedFiles.Select(f => f.RelativePath).Should().BeEquivalentTo(new[] { "src/api/user.py", "src/web/user.py" });
        vm.DisplayedFiles.Select(f => f.RowText).Should().BeEquivalentTo(new[] { "src/api/user.py", "src/web/user.py" },
            "絞り込み中は、同名のファイルを見分けられるよう相対パスで見せる");
        vm.DisplayedFiles.Should().OnlyContain(f => f.RowIndentLevel == 0, "平らに並べるのでインデントは付けない");
        vm.FilterMatchCount.Should().Be(2);
        vm.FilterSummary.Should().Contain("2件");
        vm.FilterHasNoMatches.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "絞り込み: 絞り込み中はフォルダ行と「すべて」行が一覧に出ない（見えていないファイルまで切り替わるのを防ぐ）")]
    public async Task 絞り込み中はフォルダ行とすべて行が出ない()
    {
        var (vm, _, _) = await BuildAsync("filter-nodirs", ws =>
        {
            ws.WriteText("lib/a.py", "x");
            ws.WriteText("lib/b.py", "x");
            ws.WriteText("top.py", "x");
        });
        vm.Files.Should().Contain(f => f.IsDirectory, "前提: 通常のツリーにはフォルダ行がある");
        vm.Files.Should().Contain(f => f.IsRoot, "前提: 通常のツリーには「すべて」行がある");

        vm.FilterText = "py"; // フォルダ名にも一致し得る語だが、ファイルだけが出る。
        await WaitForAsync(() => vm.IsFiltering);

        vm.DisplayedFiles.Should().NotBeEmpty();
        vm.DisplayedFiles.Should().OnlyContain(f => !f.IsDirectory, "フォルダ行は出さない");
        vm.DisplayedFiles.Should().NotContain(f => f.IsRoot, "「すべて」行も出さない");
        vm.DisplayedFiles.Should().HaveCount(3);

        // 絞り込みの語がフォルダ名にだけ一致する場合も、フォルダ行は出ず、配下のファイルが出る。
        vm.FilterText = "lib";
        await WaitForAsync(() => vm.FilterMatchCount == 2);
        vm.DisplayedFiles.Select(f => f.RelativePath).Should().BeEquivalentTo(new[] { "lib/a.py", "lib/b.py" });
    }

    [AvaloniaFact(DisplayName = "絞り込み: 大文字小文字・「\\」と「/」の違いを吸収し、空白区切りの複数語はANDで絞る")]
    public async Task 大文字小文字と区切りを吸収し複数語はANDになる()
    {
        var (vm, _, _) = await BuildAsync("filter-terms", ws =>
        {
            ws.WriteText("Src/Api/User.py", "x");
            ws.WriteText("Src/Api/Order.py", "x");
            ws.WriteText("Src/Web/User.py", "x");
        });

        vm.FilterText = @"src\api USER";
        await WaitForAsync(() => vm.IsFiltering);

        vm.DisplayedFiles.Select(f => f.RelativePath).Should().Equal("Src/Api/User.py");
    }

    [AvaloniaFact(DisplayName = "絞り込み: 一致が0件のときは、その旨を出し、一覧は空になる")]
    public async Task 一致0件ではその旨が出る()
    {
        var (vm, _, _) = await BuildAsync("filter-none", ws => ws.WriteText("a.py", "x"));

        vm.FilterText = "zzz-no-such-file";
        await WaitForAsync(() => vm.IsFiltering);

        vm.DisplayedFiles.Should().BeEmpty();
        vm.FilterMatchCount.Should().Be(0);
        vm.FilterHasNoMatches.Should().BeTrue();
        vm.FilterSummary.Should().Contain("一致するファイルがありません");
    }

    [AvaloniaFact(DisplayName = "絞り込み: 絞り込み中に切り替えた状態は保存され、欄を空にするとツリーに反映されて戻る")]
    public async Task 絞り込み中の切り替えは保存されツリーに反映される()
    {
        var (vm, appPaths, project) = await BuildAsync("filter-persist", ws =>
        {
            ws.WriteText("lib/a.py", "x");
            ws.WriteText("lib/b.py", "x");
            ws.WriteText("top.py", "x");
        });

        vm.FilterText = "b.py";
        await WaitForAsync(() => vm.IsFiltering);
        var row = vm.DisplayedFiles.Single();
        row.RelativePath.Should().Be("lib/b.py");

        vm.CycleStateCommand.Execute(row); // 内容も出す → 構成だけ

        row.State.Should().Be(ContextFileState.StructureOnly);
        var saved = await WaitForSavedStateAsync(appPaths, project, "lib/b.py");
        saved.Overrides.ContextFileStates["lib/b.py"].Should().Be(ContextFileState.StructureOnly.ToString(),
            "絞り込み中の切り替えも、通常と同じ仕組みでプロジェクトに保存される");

        vm.FilterText = string.Empty;

        vm.IsFiltering.Should().BeFalse("空にしたときはデバウンスを待たず、すぐ戻る");
        vm.DisplayedFiles.Should().BeSameAs(vm.Files, "ツリー（フォルダ行・「すべて」行つき）の一覧へ戻る");
        vm.Files.Should().Contain(f => f.IsDirectory).And.Contain(f => f.IsRoot);
        vm.Files.Single(f => f.RelativePath == "lib/b.py").State.Should().Be(
            ContextFileState.StructureOnly, "絞り込み中に切り替えた状態がツリーに反映されている");
        vm.Files.Single(f => f.RelativePath == "lib").State.Should().BeNull("配下が混在したので、フォルダ行は混在になる");
        vm.RootNode.IsMixed.Should().BeTrue();
        vm.Files.Single(f => f.RelativePath == "lib/b.py").RowText.Should().Be("b.py", "ツリーの行は名前だけの表示に戻る");
        vm.Files.Single(f => f.RelativePath == "lib/b.py").RowIndentLevel.Should().Be(2);
        vm.FilterSummary.Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "絞り込み: 除外されたファイルも一覧に出るが切り替えられず、除外の理由が取れる")]
    public async Task 絞り込み中も除外ファイルは選べない()
    {
        var (vm, _, _) = await BuildAsync("filter-excluded", ws =>
        {
            ws.WriteText(".gitignore", "*.log\n");
            ws.WriteText("app.log", "ログ");
            ws.WriteText("app.py", "x");
        });

        vm.FilterText = "app";
        await WaitForAsync(() => vm.IsFiltering);

        var log = vm.DisplayedFiles.Single(f => f.RelativePath == "app.log");
        log.IsExcluded.Should().BeTrue();
        log.ExcludeReason.Should().NotBeNullOrEmpty("理由はアイコンのツールチップに出る");
        log.State.Should().BeNull();

        vm.CycleStateCommand.Execute(log);

        log.State.Should().BeNull("除外されたファイルは絞り込み中でも選べない");
        vm.DisplayedFiles.Single(f => f.RelativePath == "app.py").State.Should().Be(ContextFileState.Full);
    }

    [AvaloniaFact(DisplayName = "絞り込み: 連続して入力しても、入力が止まるまで一覧は差し替えられず、差し替えは1回にまとまる")]
    public async Task 連続入力は1回にまとめて絞り込む()
    {
        var (vm, _, _) = await BuildAsync("filter-debounce", ws =>
        {
            ws.WriteText("alpha.py", "x");
            ws.WriteText("alphabet.py", "x");
            ws.WriteText("beta.py", "x");
        });
        var displayedChanges = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ContextCollectViewModel.DisplayedFiles)) displayedChanges++;
        };

        vm.FilterText = "a";
        vm.FilterText = "al";
        vm.FilterText = "alp";

        vm.IsFiltering.Should().BeFalse("入力の直後は、まだ一覧を作り直さない（打鍵のたびに組み直さない）");
        vm.DisplayedFiles.Should().BeSameAs(vm.Files);
        displayedChanges.Should().Be(0);

        await WaitForAsync(() => vm.IsFiltering);

        displayedChanges.Should().Be(1, "3回の入力は1回の絞り込みにまとまる");
        vm.DisplayedFiles.Select(f => f.RelativePath).Should().BeEquivalentTo(new[] { "alpha.py", "alphabet.py" });
    }

    [AvaloniaFact(DisplayName = "絞り込み: 絞り込み中に除外パターンを追加して再走査しても、絞り込みは維持され一覧は最新になる")]
    public async Task 再走査しても絞り込みは維持される()
    {
        var (vm, _, _) = await BuildAsync("filter-rescan", ws =>
        {
            ws.WriteText("a.py", "x");
            ws.WriteText("a.tmp", "x");
            ws.WriteText("b.py", "x");
        });
        vm.FilterText = "a.";
        await WaitForAsync(() => vm.IsFiltering);
        vm.DisplayedFiles.Where(f => !f.IsExcluded).Select(f => f.RelativePath)
            .Should().BeEquivalentTo(new[] { "a.py", "a.tmp" });

        vm.NewExcludePattern = "*.tmp";
        vm.AddExcludeCommand.Execute(null);
        await WaitForAsync(() => !vm.AddExcludeCommand.IsExecuting);

        vm.IsFiltering.Should().BeTrue("再走査で絞り込みが解けてはいけない");
        vm.DisplayedFiles.Should().OnlyContain(f => vm.Files.Contains(f), "一覧は作り直された最新のノードを指す");
        vm.DisplayedFiles.Single(f => f.RelativePath == "a.tmp").IsExcluded.Should().BeTrue();
        vm.DisplayedFiles.Single(f => f.RelativePath == "a.py").IsExcluded.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "絞り込み: ×ボタン（ClearFilterCommand）で欄が空になりツリーへ戻る")]
    public async Task クリアコマンドでツリーへ戻る()
    {
        var (vm, _, _) = await BuildAsync("filter-clear", ws => ws.WriteText("a.py", "x"));
        vm.ClearFilterCommand.CanExecute(null).Should().BeFalse("空のときは押す意味が無い");

        vm.FilterText = "a";
        vm.ClearFilterCommand.CanExecute(null).Should().BeTrue();
        await WaitForAsync(() => vm.IsFiltering);

        vm.ClearFilterCommand.Execute(null);

        vm.FilterText.Should().BeEmpty();
        vm.IsFiltering.Should().BeFalse();
        vm.DisplayedFiles.Should().BeSameAs(vm.Files);
    }

    // ---- 窓（Ctrl+F・Esc・表示） ----

    [AvaloniaFact(DisplayName = "絞り込み（窓）: 欄に入力すると、実際の一覧（ListBox）がファイルだけになり、欄を空にするとツリーに戻る")]
    public async Task 窓の一覧が絞り込みに追従する()
    {
        var (window, vm) = await OpenWindowAsync("win-list", ws =>
        {
            ws.WriteText("lib/a.py", "x");
            ws.WriteText("lib/b.py", "x");
            ws.WriteText("top.py", "x");
        });
        var list = window.FindControl<ListBox>("FileListBox")!;
        var box = window.FindControl<TextBox>("FileFilterBox")!;
        var treeCount = list.ItemCount;
        treeCount.Should().Be(1 + 1 + 3, "「すべて」行 + lib フォルダ + ファイル3件");

        box.Text = "b.py";
        await WaitForAsync(() => vm.IsFiltering);
        Dispatcher.UIThread.RunJobs();

        list.ItemCount.Should().Be(1, "絞り込み中は一致したファイルだけ");
        window.GetVisualDescendants().OfType<TextBlock>()
            .Should().Contain(t => t.Text != null && t.Text.StartsWith("1件"), "件数が表示される");

        box.Text = string.Empty;
        Dispatcher.UIThread.RunJobs();

        list.ItemCount.Should().Be(treeCount, "欄を空にするとツリー表示に戻る");
    }

    [AvaloniaFact(DisplayName = "絞り込み（窓）: Ctrl+Fで絞り込み欄にフォーカスが移り、プレビュータブを見ていてもファイル選択タブへ戻る")]
    public async Task CtrlFで絞り込み欄へ移る()
    {
        var (window, _) = await OpenWindowAsync("win-ctrlf", ws => ws.WriteText("a.py", "x"));
        var box = window.FindControl<TextBox>("FileFilterBox")!;
        var tabs = window.FindControl<TabControl>("MainTabs")!;
        box.IsFocused.Should().BeFalse("前提: 開いた直後の初期フォーカスは収集モード");

        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        box.IsFocused.Should().BeTrue();

        // プレビュータブへ移ってからでも、Ctrl+Fでファイル選択タブへ戻って欄に移れる。
        tabs.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        window.Focus();

        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        tabs.SelectedIndex.Should().Be(0);
        box.IsFocused.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "絞り込み（窓）: 欄にフォーカスがあり中身があるEscは欄を空にして窓は閉じず、空のEscは従来どおり窓を閉じる")]
    public async Task Escは中身があれば欄を空にし空なら窓を閉じる()
    {
        var (window, vm) = await OpenWindowAsync("win-esc", ws => ws.WriteText("a.py", "x"));
        var box = window.FindControl<TextBox>("FileFilterBox")!;
        var closed = false;
        window.Closed += (_, _) => closed = true;

        box.Focus();
        box.Text = "a";
        Dispatcher.UIThread.RunJobs();
        box.IsFocused.Should().BeTrue();

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        box.Text.Should().BeEmpty("中身のある欄でのEscは、入力の取り消し");
        vm.FilterText.Should().BeEmpty();
        closed.Should().BeFalse("このEscでは窓を閉じない");

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        closed.Should().BeTrue("欄が空なら、Escは従来どおり窓を閉じる");
    }

    [AvaloniaFact(DisplayName = "絞り込み（窓）: 欄に文字があっても、フォーカスが別の部品にあるEscは従来どおり窓を閉じる")]
    public async Task 別の部品にフォーカスがあるEscは窓を閉じる()
    {
        var (window, _) = await OpenWindowAsync("win-esc-other", ws => ws.WriteText("a.py", "x"));
        var box = window.FindControl<TextBox>("FileFilterBox")!;
        box.Text = "a";
        window.FindControl<ComboBox>("ModeComboBox")!.Focus();
        Dispatcher.UIThread.RunJobs();
        var closed = false;
        window.Closed += (_, _) => closed = true;

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        closed.Should().BeTrue("Escで必ず閉じられる、という他のダイアログとの一貫性を保つ");
    }

    [AvaloniaFact(DisplayName = "絞り込み（窓）: 除外パターンの欄と取り違えないよう、ウォーターマークとツールチップで役割が区別されている")]
    public async Task 除外パターン欄と区別できる()
    {
        var (window, _) = await OpenWindowAsync("win-labels", ws => ws.WriteText("a.py", "x"));
        var filterBox = window.FindControl<TextBox>("FileFilterBox")!;
        var excludeBox = window.GetVisualDescendants().OfType<TextBox>()
            .Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == "追加する除外パターン");

        filterBox.Watermark.Should().Contain("絞り込み");
        excludeBox.Watermark.Should().NotBe(filterBox.Watermark);
        ToolTip.GetTip(filterBox).Should().NotBeNull();
        TipText(filterBox).Should().Contain("除外しません", "絞り込みは表示を絞るだけで除外ではないと明記する");
    }

    // ==================================================================
    // 2. gitの変更ファイルだけを選ぶ
    // ==================================================================

    [AvaloniaFact(DisplayName = "git: 変更ファイルだけが「内容も出す」、ほかはすべて「構成だけ」になり、確認の文面に件数が入り、保存される")]
    public async Task gitの変更ファイルだけがFullになる()
    {
        var dialogs = new RecordingDialogs { ConfirmResult = true };
        var (vm, appPaths, project) = await BuildGitAsync("git-basic", dialogs,
            commitSetup: ws =>
            {
                ws.WriteText("a.py", "1");
                ws.WriteText("lib/b.py", "1");
                ws.WriteText("lib/c.py", "1");
            },
            changeSetup: ws =>
            {
                ws.WriteText("a.py", "changed");
                ws.WriteText("lib/new.py", "brand new"); // 未追跡
            });
        vm.GitAvailabilityState.Should().Be(GitAvailability.Available);
        vm.SelectGitChangedCommand.CanExecute(null).Should().BeTrue();

        await ExecuteAsync(vm.SelectGitChangedCommand);

        dialogs.ConfirmCount.Should().Be(1, "既存の選択をまとめて書き換えるので、押す前に確認する");
        dialogs.LastConfirmMessage.Should().Contain("gitの変更ファイル2件だけ内容を出す状態に置き換えます");
        State(vm, "a.py").Should().Be(ContextFileState.Full);
        State(vm, "lib/new.py").Should().Be(ContextFileState.Full);
        State(vm, "lib/b.py").Should().Be(ContextFileState.StructureOnly);
        State(vm, "lib/c.py").Should().Be(ContextFileState.StructureOnly);
        vm.StatusMessage.Should().Contain("2件").And.Contain("「内容も出す」").And.Contain("「構成だけ」");

        var saved = await WaitForSavedStateAsync(appPaths, project, "lib/b.py");
        saved.Overrides.ContextFileStates["lib/b.py"].Should().Be(ContextFileState.StructureOnly.ToString());
        saved.Overrides.ContextFileStates.Should().NotContainKey("a.py", "既定（内容も出す）は保存しない");
    }

    [AvaloniaFact(DisplayName = "git: 確認でキャンセルすると、選択は一切変わらない")]
    public async Task 確認をキャンセルすると変わらない()
    {
        var dialogs = new RecordingDialogs { ConfirmResult = false };
        var (vm, _, _) = await BuildGitAsync("git-cancel", dialogs,
            commitSetup: ws =>
            {
                ws.WriteText("a.py", "1");
                ws.WriteText("b.py", "1");
            },
            changeSetup: ws => ws.WriteText("a.py", "changed"));
        vm.CycleStateCommand.Execute(vm.Files.Single(f => f.RelativePath == "b.py")); // 手で「構成だけ」にしておく

        await ExecuteAsync(vm.SelectGitChangedCommand);

        dialogs.ConfirmCount.Should().Be(1);
        State(vm, "a.py").Should().Be(ContextFileState.Full);
        State(vm, "b.py").Should().Be(ContextFileState.StructureOnly, "手で切り替えた状態がそのまま残る");
        vm.StatusMessage.Should().Contain("選択は変えていません");
    }

    [AvaloniaFact(DisplayName = "git: 変更ファイルが0件なら、確認も出さず選択を変えず、その旨を知らせる")]
    public async Task 変更0件なら書き換えない()
    {
        var dialogs = new RecordingDialogs { ConfirmResult = true };
        var (vm, _, _) = await BuildGitAsync("git-nochange", dialogs,
            commitSetup: ws =>
            {
                ws.WriteText("a.py", "1");
                ws.WriteText("b.py", "1");
            },
            changeSetup: _ => { });
        vm.CycleStateCommand.Execute(vm.Files.Single(f => f.RelativePath == "b.py"));

        await ExecuteAsync(vm.SelectGitChangedCommand);

        dialogs.ConfirmCount.Should().Be(0, "書き換えないので確認も出さない");
        State(vm, "a.py").Should().Be(ContextFileState.Full);
        State(vm, "b.py").Should().Be(ContextFileState.StructureOnly);
        vm.StatusMessage.Should().Contain("gitの変更ファイルはありません").And.Contain("選択は変えていません");
    }

    [AvaloniaFact(DisplayName = "git: 除外されていて選べない変更ファイルは、どれがなぜかを知らせ、選べるものだけ「内容も出す」になる")]
    public async Task 除外された変更ファイルが報告される()
    {
        var dialogs = new RecordingDialogs { ConfirmResult = true };
        var (vm, _, _) = await BuildGitAsync("git-excluded", dialogs,
            commitSetup: ws =>
            {
                ws.WriteText("a.py", "1");
                ws.WriteText("b.py", "1");
                ws.WriteText("noise.log", "1"); // 追跡済みだが、あとで .gitignore で除外される
            },
            changeSetup: ws =>
            {
                ws.WriteText(".gitignore", "*.log\n");
                ws.WriteText("a.py", "changed");
                ws.WriteText("noise.log", "changed");
            });
        vm.Files.Single(f => f.RelativePath == "noise.log").IsExcluded.Should().BeTrue("前提: .gitignoreで除外されている");

        await ExecuteAsync(vm.SelectGitChangedCommand);

        State(vm, "a.py").Should().Be(ContextFileState.Full);
        State(vm, "b.py").Should().Be(ContextFileState.StructureOnly);
        vm.Files.Single(f => f.RelativePath == "noise.log").State.Should().BeNull("除外ファイルは選べない");
        vm.StatusMessage.Should().Contain("除外されていて選べなかった").And.Contain("noise.log");
        dialogs.LastConfirmMessage.Should().Contain("除外されていて選べない変更ファイルが1件");
    }

    [AvaloniaFact(DisplayName = "git: 変更ファイルがすべて除外されていて選べないときは、選択を変えず、そのことを知らせる")]
    public async Task 変更がすべて除外ならば書き換えない()
    {
        var dialogs = new RecordingDialogs { ConfirmResult = true };
        var (vm, _, _) = await BuildGitAsync("git-allexcluded", dialogs,
            commitSetup: ws =>
            {
                ws.WriteText(".gitignore", "*.log\n");
                ws.WriteText("a.py", "1");
                ws.WriteText("noise.log", "1");
            },
            changeSetup: ws => ws.WriteText("noise.log", "changed"),
            forceAddLog: true);
        vm.Files.Single(f => f.RelativePath == "noise.log").IsExcluded.Should().BeTrue();

        await ExecuteAsync(vm.SelectGitChangedCommand);

        dialogs.ConfirmCount.Should().Be(0);
        State(vm, "a.py").Should().Be(ContextFileState.Full);
        vm.StatusMessage.Should().Contain("すべて除外されていて選べません").And.Contain("noise.log").And.Contain("選択は変えていません");
    }

    [AvaloniaFact(DisplayName = "git: 削除されたファイルは対象外で、件数が知らされる")]
    public async Task 削除されたファイルは対象外()
    {
        var dialogs = new RecordingDialogs { ConfirmResult = true };
        var (vm, _, _) = await BuildGitAsync("git-deleted", dialogs,
            commitSetup: ws =>
            {
                ws.WriteText("keep.py", "1");
                ws.WriteText("gone.py", "1");
                ws.WriteText("other.py", "1");
            },
            changeSetup: ws =>
            {
                File.Delete(Path.Combine(ws.Root, "gone.py"));
                ws.WriteText("keep.py", "changed");
            });

        await ExecuteAsync(vm.SelectGitChangedCommand);

        vm.Files.Should().NotContain(f => f.RelativePath == "gone.py", "前提: 消えたファイルは一覧にもない");
        dialogs.LastConfirmMessage.Should().Contain("gitの変更ファイル1件だけ");
        State(vm, "keep.py").Should().Be(ContextFileState.Full);
        State(vm, "other.py").Should().Be(ContextFileState.StructureOnly);
        vm.StatusMessage.Should().Contain("削除されたファイル1件は対象外");
    }

    [AvaloniaFact(DisplayName = "git: 名前を変えたファイルは新しい名前のほうが「内容も出す」になる")]
    public async Task 名前変更は新しい側が選ばれる()
    {
        var dialogs = new RecordingDialogs { ConfirmResult = true };
        var (vm, _, _) = await BuildGitAsync("git-rename", dialogs,
            commitSetup: ws =>
            {
                ws.WriteText("old name.py", "this content is long enough for rename detection\nline2\nline3\n");
                ws.WriteText("other.py", "1");
            },
            changeSetup: ws => RunGitAsync(ws.Root, "mv", "old name.py", "新しい 名前.py").GetAwaiter().GetResult());

        await ExecuteAsync(vm.SelectGitChangedCommand);

        State(vm, "新しい 名前.py").Should().Be(ContextFileState.Full);
        State(vm, "other.py").Should().Be(ContextFileState.StructureOnly);
        vm.Files.Should().NotContain(f => f.RelativePath == "old name.py");
    }

    [AvaloniaFact(DisplayName = "git: プロジェクトのルートがリポジトリのサブフォルダでも、サブフォルダ内の変更ファイルを正しく選び、外の変更は無関係")]
    public async Task サブフォルダのルートでも正しく選ばれる()
    {
        var dialogs = new RecordingDialogs { ConfirmResult = true };
        var (vm, _, _) = await BuildGitAsync("git-subdir", dialogs,
            commitSetup: ws =>
            {
                ws.WriteText("app/src/a.py", "1");
                ws.WriteText("app/readme.md", "1");
                ws.WriteText("other/z.py", "1");
                ws.WriteText("top.txt", "1");
            },
            changeSetup: ws =>
            {
                ws.WriteText("app/src/a.py", "changed");
                ws.WriteText("other/z.py", "changed"); // プロジェクトの外
                ws.WriteText("top.txt", "changed"); // プロジェクトの外
            },
            projectSubfolder: "app");

        await ExecuteAsync(vm.SelectGitChangedCommand);

        dialogs.LastConfirmMessage.Should().Contain("gitの変更ファイル1件だけ", "外の変更は数えない");
        State(vm, "src/a.py").Should().Be(ContextFileState.Full);
        State(vm, "readme.md").Should().Be(ContextFileState.StructureOnly);
    }

    [AvaloniaFact(DisplayName = "git: 収集モードが「ツリーのみ」「差分のみ」なら「ツリー＋選択」へ切り替え、その旨を表示する")]
    public async Task ツリーのみと差分のみではモードを切り替える()
    {
        foreach (var (mode, label) in new[] { (ContextMode.TreeOnly, "ツリーのみ"), (ContextMode.ChangedSince, "差分のみ") })
        {
            var dialogs = new RecordingDialogs { ConfirmResult = true };
            var (vm, _, _) = await BuildGitAsync($"git-mode-{mode}", dialogs,
                commitSetup: ws =>
                {
                    ws.WriteText("a.py", "1");
                    ws.WriteText("b.py", "1");
                },
                changeSetup: ws => ws.WriteText("a.py", "changed"));
            vm.SelectedMode = mode;

            await ExecuteAsync(vm.SelectGitChangedCommand);

            dialogs.LastConfirmMessage.Should().Contain($"「{label}」").And.Contain("「ツリー＋選択」に切り替えます");
            vm.SelectedMode.Should().Be(ContextMode.TreeAndSelected, $"{label}のままだと選んだファイルの内容が出力されない");
            vm.StatusMessage.Should().Contain($"「{label}」").And.Contain("「ツリー＋選択」に切り替えました");
        }
    }

    [AvaloniaFact(DisplayName = "git: 「選択ファイル」「ツリー＋選択」の収集モードは、利用者の選択を尊重してそのまま")]
    public async Task 選択ファイルとツリー選択ではモードを変えない()
    {
        foreach (var mode in new[] { ContextMode.SelectedFiles, ContextMode.TreeAndSelected })
        {
            var dialogs = new RecordingDialogs { ConfirmResult = true };
            var (vm, _, _) = await BuildGitAsync($"git-keepmode-{mode}", dialogs,
                commitSetup: ws =>
                {
                    ws.WriteText("a.py", "1");
                    ws.WriteText("b.py", "1");
                },
                changeSetup: ws => ws.WriteText("a.py", "changed"));
            vm.SelectedMode = mode;

            await ExecuteAsync(vm.SelectGitChangedCommand);

            vm.SelectedMode.Should().Be(mode);
            vm.StatusMessage.Should().NotContain("切り替えました");
        }
    }

    [AvaloniaFact(DisplayName = "git: 絞り込み中に押しても、対象は見えているファイルだけでなく全ファイル（結果は一覧にそのまま現れる）")]
    public async Task 絞り込み中に押しても全ファイルが対象()
    {
        var dialogs = new RecordingDialogs { ConfirmResult = true };
        var (vm, _, _) = await BuildGitAsync("git-with-filter", dialogs,
            commitSetup: ws =>
            {
                ws.WriteText("src/a.py", "1");
                ws.WriteText("docs/b.md", "1");
                ws.WriteText("docs/c.md", "1");
            },
            changeSetup: ws => ws.WriteText("docs/c.md", "changed"));
        vm.FilterText = "src";
        await WaitForAsync(() => vm.IsFiltering);
        vm.DisplayedFiles.Select(f => f.RelativePath).Should().Equal("src/a.py");

        await ExecuteAsync(vm.SelectGitChangedCommand);

        State(vm, "docs/c.md").Should().Be(ContextFileState.Full, "見えていない変更ファイルも選ばれる");
        State(vm, "docs/b.md").Should().Be(ContextFileState.StructureOnly, "見えていないファイルも「構成だけ」になる");
        vm.DisplayedFiles.Single().State.Should().Be(ContextFileState.StructureOnly, "見えている行にも結果が現れる");
        vm.IsFiltering.Should().BeTrue("絞り込み自体は維持される");
    }

    [AvaloniaFact(DisplayName = "git: gitのリポジトリでないプロジェクトでは、ボタンが無効になり、ツールチップで理由を示す")]
    public async Task リポジトリでなければ無効で理由が出る()
    {
        var (vm, _, _) = await BuildAsync("git-notrepo", ws => ws.WriteText("a.py", "1"));

        vm.GitAvailabilityState.Should().Be(GitAvailability.NotARepository);
        vm.SelectGitChangedCommand.CanExecute(null).Should().BeFalse();
        vm.GitSelectToolTip.Should().Contain("リポジトリ");
        vm.GitSelectDetailedToolTip.Should().Contain("リポジトリ", "くわしい説明の段階でも理由は消えない");
    }

    [AvaloniaFact(DisplayName = "git: gitが見つからない場合も、ボタンが無効になり、ツールチップでgitが見つからないことを示す")]
    public async Task gitが見つからなければ無効で理由が出る()
    {
        var (vm, _, _) = await BuildAsync("git-missing", ws => ws.WriteText("a.py", "1"),
            git: new StubGit { Preflight = GitCommitPreflight.GitCommandNotFound });

        vm.GitAvailabilityState.Should().Be(GitAvailability.GitNotFound);
        vm.SelectGitChangedCommand.CanExecute(null).Should().BeFalse();
        vm.GitSelectToolTip.Should().Contain("gitが見つからない");
    }

    [AvaloniaFact(DisplayName = "git: 状態の取得に失敗したときは、選択を変えず、確認も出さずに失敗を知らせる")]
    public async Task 取得に失敗したら書き換えない()
    {
        var dialogs = new RecordingDialogs { ConfirmResult = true };
        var stub = new StubGit
        {
            Preflight = GitCommitPreflight.Ready,
            Changed = new GitChangedFiles { State = GitChangedFilesState.Failed, Detail = "git コマンドが5秒でタイムアウトしました。" },
        };
        var (vm, _, _) = await BuildAsync("git-failed", ws => ws.WriteText("a.py", "1"), dialogs, stub);

        await ExecuteAsync(vm.SelectGitChangedCommand);

        dialogs.ConfirmCount.Should().Be(0);
        State(vm, "a.py").Should().Be(ContextFileState.Full);
        vm.StatusMessage.Should().Contain("取得できませんでした").And.Contain("タイムアウト");
    }

    [AvaloniaFact(DisplayName = "git: 押した時点でgitが使えないと分かったら、選択を変えず、ボタンを無効に戻す")]
    public async Task 押したあとで使えないと分かればボタンが無効になる()
    {
        var dialogs = new RecordingDialogs { ConfirmResult = true };
        var stub = new StubGit
        {
            Preflight = GitCommitPreflight.Ready,
            Changed = new GitChangedFiles { State = GitChangedFilesState.NotARepository },
        };
        var (vm, _, _) = await BuildAsync("git-vanished", ws => ws.WriteText("a.py", "1"), dialogs, stub);
        vm.SelectGitChangedCommand.CanExecute(null).Should().BeTrue();

        await ExecuteAsync(vm.SelectGitChangedCommand);

        vm.GitAvailabilityState.Should().Be(GitAvailability.NotARepository);
        vm.SelectGitChangedCommand.CanExecute(null).Should().BeFalse();
        dialogs.ConfirmCount.Should().Be(0);
    }

    [AvaloniaFact(DisplayName = "git（窓）: ボタンはリポジトリなら有効、そうでなければ無効で、無効のときのツールチップに理由が出る")]
    public async Task 窓のgitボタンの有効無効と理由()
    {
        var (repoWindow, _) = await OpenWindowAsync("win-git-repo", ws => ws.WriteText("a.py", "1"), initGit: true);
        var (plainWindow, _) = await OpenWindowAsync("win-git-plain", ws => ws.WriteText("a.py", "1"));

        var repoButton = FindGitButton(repoWindow);
        var plainButton = FindGitButton(plainWindow);

        repoButton.IsEffectivelyEnabled.Should().BeTrue();
        plainButton.IsEffectivelyEnabled.Should().BeFalse();
        ToolTip.GetTip(plainButton).Should().NotBeNull();
        TipText(plainButton).Should().Contain("リポジトリ");
        ToolTip.GetTip(repoButton).Should().NotBeNull();
        TipText(repoButton).Should().Contain("構成だけ");
    }

    // ==================================================================
    // 補助
    // ==================================================================

    private static ContextFileState? State(ContextCollectViewModel vm, string path)
        => vm.Files.Single(f => f.RelativePath == path).State;

    /// <summary>ツールチップの文字列。HelpTipはTextBlockに包んで設定するため、その中身を読む。</summary>
    private static string TipText(Control control)
        => ToolTip.GetTip(control) is TextBlock block ? block.Text ?? string.Empty : ToolTip.GetTip(control)?.ToString() ?? string.Empty;

    private static Button FindGitButton(Window window)
        => window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "gitの変更ファイルだけ"));

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(10).ConfigureAwait(true);
        }

        condition().Should().BeTrue("条件が満たされるまで待った（上限 {0}ms）", timeoutMs);
    }

    /// <summary>
    /// コマンドを実行して、終わるまで待つ。<see cref="AsyncRelayCommand.Execute"/>はasync voidのため、
    /// 実行中フラグ（Execute内で最初のawaitより前に立つ）が下りるのを待つ。
    /// </summary>
    private static async Task ExecuteAsync(AsyncRelayCommand command)
    {
        command.Execute(null);
        await WaitForAsync(() => !command.IsExecuting);
    }

    /// <summary>保存のデバウンス（300ms）を待って、指定パスの状態が保存されたプロジェクトを返す。</summary>
    private static async Task<Project> WaitForSavedStateAsync(AppPaths appPaths, Project project, string path)
    {
        var store = new ProjectStore(appPaths);
        Project? saved = null;
        await WaitForAsync(() =>
        {
            saved = store.LoadAsync().GetAwaiter().GetResult().Value.Single(p => p.Id == project.Id);
            return saved.Overrides.ContextFileStates.ContainsKey(path);
        });
        return saved!;
    }

    private Task<(ContextCollectViewModel Vm, AppPaths AppPaths, Project Project)> BuildAsync(
        string caseName, Action<Workspace> setup, IDialogService? dialogs = null, GitIntegration? git = null)
        => BuildCoreAsync(caseName, setup, dialogs, git, initGit: false, projectSubfolder: null);

    /// <summary>
    /// 実際に<c>git init</c>したリポジトリでVMを作る。<paramref name="commitSetup"/>のファイルを
    /// コミットしたあとで<paramref name="changeSetup"/>の変更を加える（コミットしない）。
    /// </summary>
    private async Task<(ContextCollectViewModel Vm, AppPaths AppPaths, Project Project)> BuildGitAsync(
        string caseName, IDialogService dialogs, Action<Workspace> commitSetup, Action<Workspace> changeSetup,
        string? projectSubfolder = null, bool forceAddLog = false)
    {
        var repoDir = Path.Combine(_root, caseName, "project");
        Directory.CreateDirectory(repoDir);
        var ws = new Workspace(repoDir);
        await InitGitRepoAsync(repoDir);
        commitSetup(ws);
        await RunGitAsync(repoDir, "add", "-A");
        if (forceAddLog) await RunGitAsync(repoDir, "add", "-f", "--", ".");
        await RunGitAsync(repoDir, "commit", "-q", "-m", "初期");
        changeSetup(ws);

        return await BuildCoreAsync(caseName, _ => { }, dialogs, git: null, initGit: false, projectSubfolder);
    }

    private async Task<(ContextCollectViewModel Vm, AppPaths AppPaths, Project Project)> BuildCoreAsync(
        string caseName, Action<Workspace> setup, IDialogService? dialogs, GitIntegration? git, bool initGit, string? projectSubfolder)
    {
        var appPaths = new AppPaths(Path.Combine(_root, caseName, "app"));
        appPaths.EnsureCoreDirectoriesExist();
        var repoDir = Path.Combine(_root, caseName, "project");
        Directory.CreateDirectory(repoDir);
        if (initGit) await InitGitRepoAsync(repoDir);
        setup(new Workspace(repoDir));

        var projectDir = projectSubfolder is null ? repoDir : Path.Combine(repoDir, projectSubfolder);
        var store = new ProjectStore(appPaths);
        var registered = (await store.RegisterAsync(projectDir, caseName)).Value;

        var vm = new ContextCollectViewModel(
            appPaths, store, registered, new Settings(), new AvaloniaUiServices(), dialogs ?? new NullDialogService(), git);
        await vm.InitializeAsync();
        return (vm, appPaths, registered);
    }

    private async Task<(ContextCollectWindow Window, ContextCollectViewModel Vm)> OpenWindowAsync(
        string caseName, Action<Workspace> setup, bool initGit = false)
    {
        var appPaths = new AppPaths(Path.Combine(_root, caseName, "app"));
        appPaths.EnsureCoreDirectoriesExist();
        var projectDir = Path.Combine(_root, caseName, "project");
        Directory.CreateDirectory(projectDir);
        if (initGit) await InitGitRepoAsync(projectDir);
        setup(new Workspace(projectDir));

        var store = new ProjectStore(appPaths);
        var registered = (await store.RegisterAsync(projectDir, caseName)).Value;
        var vm = new ContextCollectViewModel(
            appPaths, store, registered, new Settings(), new AvaloniaUiServices(), new NullDialogService());

        // 初期化（走査・gitの確認）は窓のLoadedで走る。終わるまで待つ。
        var window = _windows.Track(new ContextCollectWindow(vm));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        await WaitForAsync(() => vm.Files.Count > 0 && vm.GitAvailabilityState != GitAvailability.Checking);
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    private static async Task InitGitRepoAsync(string root)
    {
        await RunGitAsync(root, "init", "-q");
        await RunGitAsync(root, "config", "user.email", "test@example.com");
        await RunGitAsync(root, "config", "user.name", "Graft Test");
    }

    private static async Task<string> RunGitAsync(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=false");
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return stdout.Trim();
    }

    private sealed class Workspace
    {
        public Workspace(string root) => Root = root;

        public string Root { get; }

        public void WriteText(string relativePath, string content)
        {
            var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }

    /// <summary>確認の可否と、出された確認の文面・回数を記録するダイアログ。</summary>
    private sealed class RecordingDialogs : IDialogService
    {
        public bool ConfirmResult { get; set; }
        public int ConfirmCount { get; private set; }
        public string LastConfirmMessage { get; private set; } = string.Empty;

        public Task<bool> ConfirmAsync(string title, string message)
        {
            ConfirmCount++;
            LastConfirmMessage = message;
            return Task.FromResult(ConfirmResult);
        }

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(null);

        public Task<string?> PromptAsync(string title, string message, string? initial = null) => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }

    /// <summary>gitの確認結果・変更ファイルを固定で返す（gitが無い・取得に失敗する、を再現する）。</summary>
    private sealed class StubGit : GitIntegration
    {
        public GitCommitPreflight Preflight { get; init; } = GitCommitPreflight.Ready;
        public GitChangedFiles Changed { get; init; } = new() { State = GitChangedFilesState.Ready };

        public override Task<GitCommitPreflight> CheckCommitPreflightAsync(string projectRoot, CancellationToken ct = default)
            => Task.FromResult(Preflight);

        public override Task<GitChangedFiles> GetChangedFilesAsync(string projectRoot, CancellationToken ct = default)
            => Task.FromResult(Changed);
    }
}
