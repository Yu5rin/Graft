using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.ViewModels;

namespace Graft.UiTests;

/// <summary>
/// 機能追加（差分の左右並列表示）の回帰テスト。
///
/// 対象:
///   1. 並列表示（IsSideBySide=true）で、対応する行（変更前/変更後）が同じ行に横並びになること。
///   2. 既存の単語レベルのインライン強調（DiffBuilder.InlineSpans）が並列表示のセルにも
///      そのまま伝わること（DiffCellViewModel.InlineSpans参照）。
///   3. 表示方式の選択がSettings.Diff.SideBySideへ永続化され、次回起動時（＝設定を読み直した
///      新しいDiffViewModel）でも保たれること。
///   4. 履歴差分タブ（HistoryDiffViewModel）が内部で使うDiffViewModelにも同じ設定・同じ
///      切り替えが自然に効くこと（既存のDiffView/DiffViewModelをそのまま再利用しているため）。
/// </summary>
public class DiffSideBySideTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-diff-sidebyside", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果に影響しない。
        }

        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "並列表示では変更前/変更後が同じ行にLeft/Rightとして横並びになる")]
    public void 並列表示で対応する行が横並びになる()
    {
        var diff = new DiffViewModel(new Settings(), new AvaloniaUiServices());
        diff.IsSideBySide.Should().BeTrue("既定はSettings.Diff.SideBySideのtrue");
        diff.Load(MakePlan());

        // "bar" -> "qux" の1行だけが変わる差分。並列表示では削除行(bar)と追加行(qux)が
        // 同じ行のLeft/Rightとして横に並ぶはず（統合表示のように別々の行にはならない）。
        var changedRow = diff.Lines.Single(l => l.Left.Text.Contains("bar") || (l.Right?.Text.Contains("qux") ?? false));
        changedRow.Left.Text.Should().Be("foo bar baz");
        changedRow.Right.Should().NotBeNull();
        changedRow.Right!.Text.Should().Be("foo qux baz");
        changedRow.Left.IsRemoved.Should().BeTrue();
        changedRow.Right.IsAdded.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "統合表示に切り替えると変更前/変更後が別々の行になる（Rightはnull）")]
    public void 統合表示では別々の行になる()
    {
        var diff = new DiffViewModel(new Settings(), new AvaloniaUiServices());
        diff.Load(MakePlan());

        diff.IsSideBySide = false;

        diff.Lines.Should().Contain(l => l.Left.Text == "foo bar baz" && l.Right == null);
        diff.Lines.Should().Contain(l => l.Left.Text == "foo qux baz" && l.Right == null);
    }

    [AvaloniaFact(DisplayName = "並列表示でも単語レベルのインライン強調（InlineSpans）がそのままセルに伝わる")]
    public void 並列表示でもインライン強調が効く()
    {
        var diff = new DiffViewModel(new Settings(), new AvaloniaUiServices());
        diff.Load(MakePlan());

        var changedRow = diff.Lines.Single(l => l.Left.Text == "foo bar baz");

        // DiffBuilderInlineSpanTests（DiffBuilder単体）で検証済みの、実際に変わった単語
        // （bar/qux）だけを指すInlineSpansが、DiffCellViewModelを経由してもそのまま
        // （並列表示でつぶれたり消えたりせず）残っていることを確認する。
        changedRow.Left.InlineSpans.Should().ContainSingle();
        changedRow.Left.InlineSpans[0].Start.Should().Be(changedRow.Left.Text.IndexOf("bar", StringComparison.Ordinal));
        changedRow.Left.InlineSpans[0].Length.Should().Be("bar".Length);

        changedRow.Right!.InlineSpans.Should().ContainSingle();
        changedRow.Right.InlineSpans[0].Start.Should().Be(changedRow.Right.Text.IndexOf("qux", StringComparison.Ordinal));
        changedRow.Right.InlineSpans[0].Length.Should().Be("qux".Length);
    }

    [AvaloniaFact(DisplayName = "IsSideBySideの初期値はSettings.Diff.SideBySideに従う")]
    public void 初期値は設定に従う()
    {
        var diffDefault = new DiffViewModel(new Settings(), new AvaloniaUiServices());
        diffDefault.IsSideBySide.Should().BeTrue();

        var settingsUnified = new Settings { Diff = new DiffSettings { SideBySide = false } };
        var diffUnified = new DiffViewModel(settingsUnified, new AvaloniaUiServices());
        diffUnified.IsSideBySide.Should().BeFalse("次回起動時（設定を読み直した新しいDiffViewModel）でも保たれている必要がある");
    }

    [AvaloniaFact(DisplayName = "UpdateSettingsによる反映ではSideBySideChangeCommittedが発火しない（設定の巡回保存を防ぐ）")]
    public void 設定側からの反映では確定イベントが発火しない()
    {
        var diff = new DiffViewModel(new Settings(), new AvaloniaUiServices());
        var committedCount = 0;
        diff.SideBySideChangeCommitted += (_, _) => committedCount++;

        diff.UpdateSettings(new Settings { Diff = new DiffSettings { SideBySide = false } });

        diff.IsSideBySide.Should().BeFalse("設定側の値が反映される必要がある");
        committedCount.Should().Be(0, "設定からの反映はユーザー操作ではないため確定通知を発火してはならない");
    }

    [AvaloniaFact(DisplayName = "ユーザー操作（IsSideBySideのsetter）ではSideBySideChangeCommittedが発火する")]
    public void ユーザー操作では確定イベントが発火する()
    {
        var diff = new DiffViewModel(new Settings(), new AvaloniaUiServices());
        bool? committedValue = null;
        diff.SideBySideChangeCommitted += (_, v) => committedValue = v;

        diff.IsSideBySide = false;

        committedValue.Should().Be(false);
    }

    [AvaloniaFact(DisplayName = "diff表示ヘッダーでの切り替えは設定へ永続化され、他の開いている差分表示にも即座に反映される")]
    public async Task 切り替えが設定へ永続化され他の差分表示にも反映される()
    {
        var appPaths = new AppPaths(_root);
        appPaths.EnsureCoreDirectoriesExist();
        var settingsStore = new SettingsStore(appPaths);

        var shell = Views.StartupCoordinator.BuildShellViewModel(
            appPaths, new Settings(), settingsStore, new Graft.Features.PatchQueue(appPaths),
            new Graft.Features.ProjectStore(appPaths), new Graft.Core.RevisionStore(appPaths),
            new Graft.Core.RevisionRestorer(appPaths), new NullDialogService(), new AvaloniaUiServices(),
            openSettings: () => { });
        await shell.Graft.InitializeAsync().ConfigureAwait(true);

        // StartupCoordinator.StartAsyncが行う配線（DiffSideBySideChangeRequested→
        // SettingsViewModel.SetSideBySideLive→onLiveSettingsChanged→UpdateSettings伝播）を
        // EditorFontSizeZoomTestsと同じ作法でテスト側に再現する。
        var settingsVm = new SettingsViewModel(
            appPaths, new NullDialogService(), new AvaloniaUiServices(),
            onLiveSettingsChanged: updated =>
            {
                shell.Graft.UpdateSettings(updated);
                shell.Editor.UpdateSettings(updated);
            });
        await settingsVm.InitializeAsync().ConfigureAwait(true);
        shell.DiffSideBySideChangeRequested += (_, v) => settingsVm.SetSideBySideLive(v);

        shell.Graft.Diff.IsSideBySide.Should().BeTrue("既定値");

        // 通常の差分表示（Graft.Diff）のヘッダーで統合表示へ切り替えたとする。
        shell.Graft.Diff.IsSideBySide = false;

        // 履歴差分タブ側は別インスタンスのDiffViewModelだが、同じ設定変更が即座に反映される
        // はず（MainViewModel.UpdateSettings→HistoryDiff.UpdateSettingsの経路。
        // 実際に1件読み込んでいなくても、次にLoadされるファイルへ新しい既定値が効くことを
        // 新しいDiffViewModelで確認する）。
        await WaitUntilAsync(async () => (await settingsStore.LoadAsync().ConfigureAwait(true)).Value.Diff.SideBySide == false)
            .ConfigureAwait(true);

        var reloaded = await settingsStore.LoadAsync().ConfigureAwait(true);
        reloaded.Value.Diff.SideBySide.Should().BeFalse("保存ボタンが無いため、切り替えが自動的にsettings.jsonへ書き込まれる必要がある");

        // 「次回起動時にも保たれる」ことの確認: 保存された設定から新規にDiffViewModelを作ると
        // 統合表示が初期値になる。
        var reopened = new DiffViewModel(reloaded.Value, new AvaloniaUiServices());
        reopened.IsSideBySide.Should().BeFalse();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition().ConfigureAwait(true)) return;
            await Task.Delay(10).ConfigureAwait(true);
        }
    }

    private static BlockPlan MakePlan()
    {
        const string before = "foo bar baz\n";
        const string after = "foo qux baz\n";
        var diff = DiffBuilder.Build("sample.txt", before, after, contextLines: 3);

        return new BlockPlan
        {
            Block = new Graft.Core.DeleteBlock { Path = "sample.txt" },
            Path = "sample.txt",
            Operation = EntryOperation.Modify,
            CanApply = true,
            IsSelected = true,
            BeforeText = before,
            AfterText = after,
            Diff = diff,
        };
    }
}
