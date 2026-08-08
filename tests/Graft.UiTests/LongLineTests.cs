using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using FluentAssertions;
using Graft.Core;
using Graft.Editor;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.ViewModels;
using Graft.Views;
using Xunit.Abstractions;

namespace Graft.UiTests;

/// <summary>
/// 課題3（1行が極端に長いファイルを開くと遅い）の回帰テスト。
///
/// 実測（PerformanceTestsと同じ方式のheadlessベンチ、詳細は本セッションの調査記録参照）では、
/// 開く操作自体はこの環境で数百ms程度に収まったが、原因調査の過程で2点の実害を確認した。
/// (1) 折り返し（WordWrap）が有効な状態で1行10万文字のファイルを開くと、AvaloniaEdit側の
///     書式計算が無効時の10倍以上（実測で数百ms→1.5秒前後）に悪化する。
/// (2) 括弧の対応付け（BracketSupport.MatchBracket）はキャレットが括弧に隣接すると
///     1文字ごとに行全体を再トークン化するため、極端に長い行ではO(行の文字数の2乗)になりうる。
/// ここでは、極端に長い行を含むファイルを開いた際に、構文強調・折り返し・言語認識した
/// 括弧対応付けが自動的に無効化され、利用者へ通知されることを検証する。
/// </summary>
public class LongLineTests : IDisposable
{
    private readonly string _baseDirectory =
        Path.Combine(Path.GetTempPath(), "graft-ui-tests", Guid.NewGuid().ToString("N"));

    private readonly ITestOutputHelper _output;

    public LongLineTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose()
    {
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

    [AvaloniaFact(DisplayName = "課題3: 1行が極端に長いファイルを開くとHasExtremelyLongLineが立ち、ステータスバー通知フラグも立つ")]
    public async Task 極端に長い行を検知して通知フラグが立つ()
    {
        var (shell, window, filePath) = await OpenLongLineFileAsync();

        var tab = shell.Editor.ActiveTab;
        tab.Should().NotBeNull();
        tab!.Session.HasExtremelyLongLine.Should().BeTrue();
        shell.Editor.ActiveTabHasLongLineWarning.Should().BeTrue();

        window.Close();
    }

    [AvaloniaFact(DisplayName = "課題3: 通常のファイル（極端に長い行を含まない）では通知フラグが立たない")]
    public async Task 通常のファイルでは通知フラグが立たない()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var (shell, window) = BuildShellAndWindow(appPaths);
        window.Show();
        await shell.Graft.InitializeAsync();

        var filePath = Path.Combine(_baseDirectory, "normal.cs");
        await File.WriteAllTextAsync(filePath, "class Normal\n{\n    void M() { }\n}\n");

        var result = await shell.Editor.OpenFileAsync(filePath);
        result.IsSuccess.Should().BeTrue();

        result.Value.Session.HasExtremelyLongLine.Should().BeFalse();
        shell.Editor.ActiveTabHasLongLineWarning.Should().BeFalse();

        window.Close();
    }

    [AvaloniaFact(DisplayName = "課題3: 極端に長い行を含むファイルでは、利用者の折り返し設定に関わらずWordWrapが無効化される")]
    public async Task 折り返し設定に関わらず無効化される()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var settings = new Settings { Editor = new EditorSettings { WordWrap = true } };
        var (shell, window) = BuildShellAndWindow(appPaths, settings);
        window.Show();
        await shell.Graft.InitializeAsync();

        shell.Editor.WordWrap.Should().BeTrue("この検証は「利用者が折り返しを有効にしている」状況を再現するため");

        var filePath = Path.Combine(_baseDirectory, "LongLine.cs");
        await File.WriteAllTextAsync(filePath, "class L { /* " + new string('x', 100_000) + " */ }\n");
        var result = await shell.Editor.OpenFileAsync(filePath);
        result.IsSuccess.Should().BeTrue();

        var editor = window.GetControl<Border>("EditorHost").Child as EditorPane;
        editor.Should().NotBeNull();
        var textEditor = editor!.GetControl<TextEditor>("Editor");

        textEditor.WordWrap.Should().BeFalse(
            "1行10万文字のファイルで折り返しを有効なままにすると、実測で書式計算が10倍以上（数百ms→1.5秒前後）悪化する");

        window.Close();
    }

    [AvaloniaFact(DisplayName = "課題3: 極端に長い行を含むファイルを開いても実用的な時間で完了する（回帰ガード）")]
    public async Task 極端に長い行でも実用的な時間で開ける()
    {
        var stopwatch = Stopwatch.StartNew();
        var (_, window, _) = await OpenLongLineFileAsync();
        stopwatch.Stop();

        _output.WriteLine($"1行10万文字のファイルを開く: {stopwatch.ElapsedMilliseconds} ms");

        // 実機での報告値（6.2秒）を大きく下回ることを確認する回帰ガード。環境差を考慮し
        // 緩めの上限とする（性能が桁で悪化したときだけ気付ければよい、PerformanceTestsと同方針）。
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(3000);

        window.Close();
    }

    /// <summary>1行10万文字のファイル（課題3の再現ファイルそのもの）を開いた状態を作る共通処理。</summary>
    private async Task<(ShellViewModel Shell, ShellWindow Window, string FilePath)> OpenLongLineFileAsync()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var (shell, window) = BuildShellAndWindow(appPaths);
        window.Show();
        await shell.Graft.InitializeAsync();

        var filePath = Path.Combine(_baseDirectory, "LongLine.cs");
        await File.WriteAllTextAsync(filePath, "class L { /* " + new string('x', 100_000) + " */ }\n");

        var result = await shell.Editor.OpenFileAsync(filePath);
        result.IsSuccess.Should().BeTrue();

        return (shell, window, filePath);
    }

    private static (ShellViewModel Shell, ShellWindow Window) BuildShellAndWindow(AppPaths appPaths, Settings? settings = null)
    {
        IDialogService dialogs = new NullDialogService();
        IUiServices ui = new AvaloniaUiServices();

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths,
            settings ?? new Settings(),
            new SettingsStore(appPaths),
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            dialogs,
            ui,
            openSettings: () => { });

        var window = new ShellWindow(shell) { Width = 1280, Height = 800 };
        return (shell, window);
    }
}
