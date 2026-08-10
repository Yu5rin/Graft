using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
/// 課題1（バグ）: 書き込み権限の無いフォルダから起動しても何の警告も出ないまま
/// 起動してしまい、設定・履歴・バックアップの保存失敗が利用者に一切伝わらなかった
/// 不具合の回帰テスト。
///
/// 実際の書き込み可否判定は<see cref="AppPaths.CanWriteToBaseDirectory"/>で行う
/// （Graft.Tests側のAppPathsWritabilityTests参照）。ここでは、その結果を
/// StartupCoordinatorが<see cref="MainViewModel"/>へどう伝え、ステータスバーへ
/// 常時警告として出し続けられる状態になっているかを検証する
/// （起動時ダイアログは1回きりのため、以後の継続的な通知はこのフラグに懸かっている）。
/// </summary>
public class DataDirectoryWritabilityTests : IDisposable
{
    private readonly string _baseDirectory =
        Path.Combine(Path.GetTempPath(), "graft-ui-tests", Guid.NewGuid().ToString("N"));

    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        // 表示したShellWindowを後始末する（ShownWindowTracker参照。閉じ忘れると
        // 「Unable to locate 'Avalonia.Platform.IFontManagerImpl'」がCIで不定期に出る）。
        _windows.Dispose();

        try
        {
            if (Directory.Exists(_baseDirectory)) Directory.Delete(_baseDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果に影響しない。
        }

        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "書き込める場所で組み立てた既定状態では、書き込み不可の警告を出さない")]
    public void 既定では書き込み不可の警告を出さない()
    {
        var shell = BuildShell();

        shell.Graft.IsDataDirectoryReadOnly.Should().BeFalse(
            "書き込める場所で起動した場合、余計な警告をステータスバーへ出してはならない");
    }

    [AvaloniaFact(DisplayName = "MarkDataDirectoryReadOnlyを呼ぶと、ステータスバー用のフラグが立ち続ける")]
    public void 書き込み不可を伝えるとフラグが立つ()
    {
        var shell = BuildShell();

        // StartupCoordinator.StartAsyncが、起動時のCanWriteToBaseDirectory()確認結果に
        // 応じてこのメソッドを呼ぶ（StartupCoordinator.cs参照）。ダイアログは起動時に
        // 1回しか出ないため、この状態フラグがステータスバー（StatusBarView.axaml）に
        // 常時反映され続けることで「黙って保存に失敗し続ける」ことを防ぐ。
        shell.Graft.MarkDataDirectoryReadOnly();

        shell.Graft.IsDataDirectoryReadOnly.Should().BeTrue();
        shell.HasStatusBarWarning.Should().BeTrue();
        shell.StatusBarWarningText.Should().Be("書き込み不可のため設定・履歴・バックアップは保存されません");

        // ShellWindowを実際に描画してもバインディング先の取り違えで落ちないことを確認する
        // （StatusBarView.axamlの新規追加分のバインディング検証を兼ねる）。
        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull();
    }

    /// <summary>
    /// 統合ブランチとのマージで発覚した競合（StatusBarView.axamlに書き込み不可警告と
    /// 長い行の警告が別々に追加されていた）の解決を検証する回帰テスト。
    /// 実機のXvfb環境で両方を同時に表示させたところ、ウィンドウの最小幅（960px）では
    /// 警告文どうし・右側の接ぎ木状態表示が重なって判読できなくなることを確認したため、
    /// 表示スロットを1つに統合し「最優先の1件＋ほかN件」で要約する方式にした
    /// （ShellViewModel.StatusBarWarning.cs参照）。ここでは、書き込み不可（優先度・高）と
    /// 長い行の警告（優先度・低）が同時に成立したときに、書き込み不可が先頭に出て
    /// 「ほか1件」が付き、隠れた警告もToolTipの全文からは失われないことを確認する。
    /// </summary>
    [AvaloniaFact(DisplayName = "書き込み不可と長い行の警告が同時に成立すると、優先度の高い方＋「ほか1件」に要約される")]
    public async Task 複数の警告が同時に成立すると要約される()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        IDialogService dialogs = new NullDialogService();
        IUiServices ui = new AvaloniaUiServices();
        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths, new Settings(), new SettingsStore(appPaths), new PatchQueue(appPaths),
            new ProjectStore(appPaths), new RevisionStore(appPaths), new RevisionRestorer(appPaths),
            dialogs, ui, openSettings: () => { });
        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        // window.Show()はShellWindow.OnLoaded経由で非同期にshell.Graft.InitializeAsync()を
        // 呼ぶ。ここでさらに明示的に呼ぶと初期化が二重に走り、settings.json/projects.jsonの
        // 読み直しが競合する（ScenarioTests.OpenShellAsync参照、実機で5割前後の確率での
        // 失敗を確認した事故と同じ種類の競合状態）。自分では呼ばず、OnLoaded経由の初期化完了を
        // ShellWindowLoadWaiterで待つ。
        ShellWindowLoadWaiter.WaitForLayoutApplied(window);

        var longLinePath = Path.Combine(_baseDirectory, "LongLine.cs");
        await File.WriteAllTextAsync(longLinePath, "class L { /* " + new string('x', 100_000) + " */ }\n");
        var opened = await shell.Editor.OpenFileAsync(longLinePath);
        opened.IsSuccess.Should().BeTrue();
        shell.Editor.ActiveTabHasLongLineWarning.Should().BeTrue("前提条件: 長い行の警告が単独で立っていること");

        // まだ書き込み不可は成立していないため、この時点では長い行の警告のみが出る。
        shell.HasStatusBarWarning.Should().BeTrue();
        shell.StatusBarWarningText.Should().Be("極端に長い行があります（その行のみ構文強調を簡略化）");

        // 書き込み不可（優先度・高）が成立すると、そちらが先頭に出て「ほか1件」が付く。
        shell.Graft.MarkDataDirectoryReadOnly();

        shell.StatusBarWarningText.Should().Be("書き込み不可のため設定・履歴・バックアップは保存されません　ほか1件");
        shell.StatusBarWarningTooltip.Should().Contain("書き込み権限のあるフォルダへGraftのフォルダ一式を移動");
        shell.StatusBarWarningTooltip.Should().Contain("この行は20,000文字を超えるため");

        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull("複数の警告が同時に成立してもバインディング先の取り違えで描画に失敗してはならない");

        window.Close();
    }

    private ShellViewModel BuildShell()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        IDialogService dialogs = new NullDialogService();
        IUiServices ui = new AvaloniaUiServices();

        return StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Settings(),
            new SettingsStore(appPaths),
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            dialogs,
            ui,
            openSettings: () => { });
    }
}
