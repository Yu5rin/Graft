using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
/// キーボードショートカット一覧ウィンドウ（利用者からの指摘対応: 20個以上のショートカットを
/// 知る手段が無かった）の検証。ShortcutsWindow自体の構築・閉じ方に加え、ShellWindow側の
/// 開く手段（ツールバーの「?」ボタン・Ctrl+/）の配線と、既存のCtrl+/（エディタの行コメント
/// 切り替え、EditorPane.axaml.cs）を横取りしないことを確認する。
///
/// 注意: AvaloniaEditのTextArea（エディタ本体）へのheadlessでのキー入力ルーティングは
/// EditorTests.csのコメントにある通りheadless環境では素直に届かないため、ここでは
/// 「テキスト入力欄にフォーカスがある間はCtrl+/がショートカット一覧を開かない」ことを
/// 標準のTextBox（クイックオープンの検索欄）で確認する。ShellWindow.Keyboard.csの
/// 分岐（IsTextInput）はTextBox/TextPresenter/TextAreaのいずれでも同じ経路を通るため、
/// これで「エディタ内では行コメント優先」という設計上の担保になる。
/// </summary>
public class ShortcutsWindowTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-shortcuts", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly ShownWindowTracker _windows = new();

    public ShortcutsWindowTests()
    {
        _appDirectory = Path.Combine(_root, "app");
        _projectDirectory = Path.Combine(_root, "project");
        Directory.CreateDirectory(_appDirectory);
        Directory.CreateDirectory(_projectDirectory);
    }

    public void Dispose()
    {
        // 表示したウィンドウを後始末する（ShownWindowTracker参照。テスト内でEscape/閉じるボタンで
        // 既に閉じたウィンドウも含めて安全に二重Closeできる。閉じ忘れると
        // 「Unable to locate 'Avalonia.Platform.IFontManagerImpl'」がCIで不定期に出る）。
        _windows.Dispose();

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

    [AvaloniaFact(DisplayName = "ウィンドウを構築でき、分類ごとの見出しと主要なキー表記を含む")]
    public void ウィンドウを構築でき分類ごとの内容を含む()
    {
        var window = _windows.Track(new ShortcutsWindow());
        window.Show();

        window.IsVisible.Should().BeTrue();

        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? string.Empty).ToList();

        // 分類見出し（機能ごとにまとめる、という要件の確認）。
        texts.Should().Contain("接ぎ木の操作");
        texts.Should().Contain("ファイル・エディタ");
        texts.Should().Contain("検索");
        texts.Should().Contain("表示の切り替え");

        // ShellWindow.Keyboard.csに実在するキーが一覧に反映されていること（漏れの防止）。
        texts.Should().Contain("Ctrl+Shift+V");
        texts.Should().Contain("Ctrl+Enter");
        texts.Should().Contain("Ctrl+Alt+Z");
        texts.Should().Contain("Ctrl+J");
        texts.Should().Contain("Ctrl+Alt+1〜9");
        texts.Should().Contain("F6");
        texts.Should().Contain("Ctrl+Shift+F");
        texts.Should().Contain("Ctrl+/", "行コメント切り替えとショートカット一覧を開く操作の両方で登場する");
        // 製品としての使い勝手3件のうち機能3: 直前に閉じたタブを開き直す。
        texts.Should().Contain("Ctrl+Shift+T");
    }

    [AvaloniaFact(DisplayName = "Escapeキーで閉じる")]
    public void Escapeキーで閉じる()
    {
        var window = _windows.Track(new ShortcutsWindow());
        window.Show();

        var closed = false;
        window.Closed += (_, _) => closed = true;

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        closed.Should().BeTrue("Escapeでこのウィンドウを閉じられる必要がある");
    }

    [AvaloniaFact(DisplayName = "「閉じる」ボタンで閉じる")]
    public void 閉じるボタンで閉じる()
    {
        var window = _windows.Track(new ShortcutsWindow());
        window.Show();

        var closed = false;
        window.Closed += (_, _) => closed = true;

        var closeButton = window.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(b.Content, "閉じる"));
        closeButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        closed.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "ツールバーの「?」ボタンでショートカット一覧が要求される")]
    public void ツールバーのボタンで一覧が要求される()
    {
        var (shell, window) = OpenShellAsync();

        var requested = false;
        shell.RequestOpenShortcuts += (_, _) => requested = true;

        var button = window.GetVisualDescendants().OfType<Button>()
            .Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "キーボードショートカット一覧を開く");

        // このボタンはClickイベントハンドラではなくCommandバインディングのため、
        // Button.ClickEventを直接RaiseEventしてもButton.OnClick（Command実行箇所）を
        // 経由しない。実際の操作と同じくフォーカス＋キーボード操作（Enter）で押させる。
        button.Focus();
        button.IsFocused.Should().BeTrue("フォーカスが当たっている前提の検証のため");
        button.Command.Should().NotBeNull("Commandバインディングが外れていないことの前提確認");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        requested.Should().BeTrue();
        CloseRealShortcutsDialogIfOpened(window);
    }

    [AvaloniaFact(DisplayName = "テキスト入力欄・エディタにフォーカスが無い間はCtrl+/で一覧が要求される")]
    public void フォーカスが無い間はCtrlスラッシュで一覧が要求される()
    {
        var (shell, window) = OpenShellAsync();

        var requested = false;
        shell.RequestOpenShortcuts += (_, _) => requested = true;

        window.KeyPressQwerty(PhysicalKey.Slash, RawInputModifiers.Control);

        requested.Should().BeTrue("エディタ外でのCtrl+/はショートカット一覧を開く必要がある（設定を開くCtrl+,と同じ経路）");
        CloseRealShortcutsDialogIfOpened(window);
    }

    /// <summary>
    /// 上記2テストは<c>shell.RequestOpenShortcuts</c>を直接発火させて配線を検証するが、
    /// このイベントは<see cref="ShellWindow"/>のコンストラクタが購読する本物のハンドラ
    /// （<c>OnRequestOpenShortcuts</c>）も同時に呼び出し、実際に<c>ShortcutsWindow</c>を
    /// <c>ShowDialog(this)</c>で開いてしまう。テストはその参照を持たないため、閉じないまま
    /// 終えると閉じ忘れになる（発覚の経緯: ShownWindowTracker.Disposeの後始末検出。
    /// TestSupport/ShownWindowTracker.cs参照）。テスト側からは参照を持てないため、
    /// <see cref="Window.OwnedWindows"/>（ShowDialogのオーナーに残る参照）から辿って閉じる。
    /// </summary>
    private static void CloseRealShortcutsDialogIfOpened(Window owner)
    {
        foreach (var child in owner.OwnedWindows.ToArray())
        {
            child.Close();
        }
    }

    [AvaloniaFact(DisplayName = "テキスト入力欄にフォーカス中はCtrl+/で一覧を開かない（既存のCtrl+/を横取りしない）")]
    public async Task テキスト入力中はCtrlスラッシュで一覧を開かない()
    {
        await WriteProjectFilesAsync().ConfigureAwait(true);
        var (shell, window) = OpenShellAsync();
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        // クイックオープン（Ctrl+P）を開くと検索欄（標準のTextBox）へフォーカスが移る
        // （ShellWindow.axaml.cs: OnQuickOpenOpened、Dispatcher.UIThread.Postで遅延）。
        window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control);
        shell.QuickOpen.IsOpen.Should().BeTrue();
        await SettleAsync().ConfigureAwait(true);

        var requested = false;
        shell.RequestOpenShortcuts += (_, _) => requested = true;

        window.KeyPressQwerty(PhysicalKey.Slash, RawInputModifiers.Control);

        requested.Should().BeFalse(
            "テキスト入力欄にフォーカスがある間はCtrl+/を横取りしてはならない（エディタの行コメント切り替えと同じ扱い）");
    }

    private async Task WriteProjectFilesAsync()
    {
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "sample.txt"), "1行目\n").ConfigureAwait(true);
    }

    /// <summary>ディスパッチャに積まれた非同期継続（フォーカス移動等）が終わるまで待つ。</summary>
    private static async Task SettleAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(10).ConfigureAwait(true);
        }
    }

    // window.Show()はShellWindow.OnLoaded経由で非同期にshell.Graft.InitializeAsync()を呼ぶ。
    // ここでさらに明示的に呼ぶと初期化が二重に走り、settings.json/projects.jsonの読み直しが
    // 競合する（ScenarioTests.OpenShellAsync参照、実機で5割前後の確率での失敗を確認した
    // 事故と同じ種類の競合状態）。自分では呼ばず、OnLoaded経由の初期化完了を
    // ShellWindowLoadWaiterで待つ（非同期I/Oを行わなくなったため戻り値もTaskではなくなった）。
    private (ShellViewModel Shell, ShellWindow Window) OpenShellAsync()
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Settings(),
            new SettingsStore(appPaths),
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            new NullDialogService(),
            new AvaloniaUiServices(),
            openSettings: () => { });

        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        ShellWindowLoadWaiter.WaitForLayoutApplied(window);
        return (shell, window);
    }
}
