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
using Graft.UiTests.TestSupport;
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
    private readonly ShownWindowTracker _windows = new();

    public LongLineTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose()
    {
        // 表示したShellWindowを後始末する（ShownWindowTracker参照。各テストは末尾で
        // window.Close()を呼ぶが、それより前のアサーションが失敗すると素通りされてしまう。
        // 閉じ忘れると「Unable to locate 'Avalonia.Platform.IFontManagerImpl'」がCIで
        // 不定期に出る）。
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
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var (shell, window) = BuildShellAndWindow(appPaths);
        window.Show();
        await shell.Graft.InitializeAsync();

        // ウォームアップ（初回JIT・初回アセット読み込みの費用を計測対象から除く）。
        // 基準測定と同じファイルを開くと「既に開いているタブへの切替」という別経路になり
        // 測定にならないため、ウォームアップ専用の別ファイルを使う。
        var warmupPath = Path.Combine(_baseDirectory, "Warmup.cs");
        await File.WriteAllTextAsync(warmupPath, "class Warmup { void M() { } }\n");
        (await shell.Editor.OpenFileAsync(warmupPath)).IsSuccess.Should().BeTrue();

        // 基準: 極端に長い行を含むファイルと総文字数を揃えた（約10万文字）、
        // ただし通常の行長（1行80文字程度）に分けたファイル。総データ量を揃えたうえで
        // 「1行が極端に長い」ことそのものの影響だけを取り出す。
        var normalPath = Path.Combine(_baseDirectory, "Normal.cs");
        await File.WriteAllTextAsync(normalPath, BuildNormalShapedContent());

        var baselineStopwatch = Stopwatch.StartNew();
        var baselineResult = await shell.Editor.OpenFileAsync(normalPath);
        baselineStopwatch.Stop();
        baselineResult.IsSuccess.Should().BeTrue();

        var longLinePath = Path.Combine(_baseDirectory, "LongLine.cs");
        await File.WriteAllTextAsync(longLinePath, "class L { /* " + new string('x', 100_000) + " */ }\n");

        var targetStopwatch = Stopwatch.StartNew();
        var targetResult = await shell.Editor.OpenFileAsync(longLinePath);
        targetStopwatch.Stop();
        targetResult.IsSuccess.Should().BeTrue();

        var baselineMs = Math.Max(1, baselineStopwatch.ElapsedMilliseconds);
        var ratio = (double)targetStopwatch.ElapsedMilliseconds / baselineMs;
        _output.WriteLine(
            $"基準（総文字数同等・通常の行長）を開く: {baselineStopwatch.ElapsedMilliseconds} ms / "
            + $"1行10万文字のファイルを開く: {targetStopwatch.ElapsedMilliseconds} ms（倍率 {ratio:F1}倍）");

        // 実機での報告値（6.2秒、総データ量が同等の通常ファイルの10倍以上）を大きく下回ることを
        // 確認する回帰ガード。絶対時間ではなく総データ量が同等な基準との倍率で判定するため、
        // CI環境そのものの速さのばらつきには左右されない（性能が桁で悪化したときだけ
        // 気付ければよい、PerformanceTestsと同方針）。
        ratio.Should().BeLessThan(8,
            $"総データ量が同等の通常ファイルに比べて{ratio:F1}倍かかっている"
            + $"（基準{baselineStopwatch.ElapsedMilliseconds}ms→対象{targetStopwatch.ElapsedMilliseconds}ms）。"
            + "構文強調・折り返し・括弧対応付けの自動無効化が効いていない可能性がある");

        window.Close();
    }

    /// <summary>1行10万文字のファイルと総文字数を揃えた、通常の行長（1行80文字）のファイル内容を生成する。</summary>
    private static string BuildNormalShapedContent()
    {
        var builder = new System.Text.StringBuilder();
        var oneLine = "// " + new string('a', 76) + "\n"; // 80文字（末尾の改行込み）
        for (var i = 0; i < 1250; i++) // 1250行 * 80文字 = 100,000文字
        {
            builder.Append(oneLine);
        }
        return builder.ToString();
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

    private (ShellViewModel Shell, ShellWindow Window) BuildShellAndWindow(AppPaths appPaths, Settings? settings = null)
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

        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        return (shell, window);
    }
}
