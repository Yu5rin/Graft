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

    /// <summary>
    /// 基準1回・対象1回を1組として、この組数だけ交互に計測し、組ごとの倍率の中央値を採用する
    /// （tests/Graft.Tests/CrossFileSearchPerformanceTestsで確立した手法に合わせる。
    /// 詳細な設計の経緯・理由はそちらのクラスドキュメントコメントを参照。ここで再発明はしない）。
    /// </summary>
    private const int MeasurementRuns = 7;

    /// <summary>
    /// 総データ量（約10万文字）は基準・対象ともBuildNormalShapedContent/長い行ファイルの生成で
    /// 既に揃っているため、CrossFileSearchPerformanceTestsのようにScaleFactor回へ分割して
    /// 水増しする必要はない（「1行が極端に長い」ことそのものの影響だけを取り出す設計は元々の
    /// ものを維持）。
    ///
    /// 計測は<see cref="MeasureOpenAsync"/>のとおり<c>OpenFileAsync</c>だけでなく初回描画
    /// （<see cref="ShellWindow.CaptureRenderedFrame"/>）まで含める。構文強調・折り返し・
    /// 括弧対応付けの実コストはAvaloniaEdit側で描画時まで遅延評価されるため、描画を含めずに
    /// 計測すると自動無効化の有無で倍率がほとんど変わらず、この回帰テストが検出したい劣化を
    /// 実際には検出できないことを確認した（本コミットの検証手順参照）。
    ///
    /// また既定のWordWrap=false（無効）のままだと、対象（長い行）・基準（通常の行）のどちらも
    /// 折り返し計算自体が発生しないため、この自動無効化がガードしたい最大のコスト差
    /// （クラス冒頭のコメント: 折り返し有効時に書式計算が10倍以上悪化する）を計測に反映できない。
    /// 利用者が折り返しを有効にしている状況（「折り返し設定に関わらず無効化される」テストと同じ
    /// 前提）を再現するため、WordWrap=trueの設定でシェルを組み立てる。
    ///
    /// この状態で自動無効化が正しく効いていれば、対象（長い行）は強制的に折り返しなしで描画され、
    /// 基準（通常の行長・80文字×1250行）は逆に折り返し計算のぶんだけコストを負うため、倍率は
    /// 1に近い（あるいは1未満になることさえある）はずである。負荷なしで7組の計測を6回
    /// 繰り返した実測では、組ごとの倍率の中央値は1.11〜1.38倍の狭い範囲に収まった。実測の上限
    /// （1.38倍）に対して十分な余裕を残しつつ、旧来の8倍からは大きく引き締めた3.0を採用する
    /// （CrossFileSearchPerformanceTestsが25→3.0にできたのと同じ値になったのは偶然だが、
    /// どちらも「安定化した結果、実測の自然な比率のすぐ上まで閾値を詰められた」という点で
    /// 経緯は同じ）。
    /// </summary>
    private const double RatioThreshold = 3.0;

    [AvaloniaFact(DisplayName = "課題3: 極端に長い行を含むファイルを開いても実用的な時間で完了する（回帰ガード）")]
    public async Task 極端に長い行でも実用的な時間で開ける()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        // 利用者が折り返しを有効にしている状況（「折り返し設定に関わらず無効化される」テストと
        // 同じ前提）を再現する。既定のWordWrap=false（無効）のままだと、AvaloniaEditの
        // 書式計算コストは描画を含めて計測してもほぼ動かず（本コミットの検証手順で実測・確認済み。
        // 自動無効化を一時的に無効化しても倍率がほとんど変わらなかった）、この回帰テストが
        // 検出したい劣化（クラスドキュメントコメント: 折り返し有効時に書式計算が10倍以上悪化する）
        // を実際には検出できない。
        var settings = new Settings { Editor = new EditorSettings { WordWrap = true } };
        var (shell, window) = BuildShellAndWindow(appPaths, settings);
        window.Show();
        await shell.Graft.InitializeAsync();

        // 基準: 極端に長い行を含むファイルと総文字数を揃えた（約10万文字）、
        // ただし通常の行長（1行80文字程度）に分けたファイル。総データ量を揃えたうえで
        // 「1行が極端に長い」ことそのものの影響だけを取り出す。
        var normalShapedContent = BuildNormalShapedContent();
        var longLineContent = "class L { /* " + new string('x', 100_000) + " */ }\n";

        // ウォームアップ（初回JIT・初回アセット読み込みの費用を計測対象から除く）。片側だけの
        // ウォームアップだと非対称が生じるため（CrossFileSearchPerformanceTests参照）、
        // 基準・対象の両方をウォームアップする。基準測定と同じファイルを開くと「既に開いている
        // タブへの切替」という別経路になり測定にならないため、ここでも専用の別ファイルを使う。
        var warmupNormalPath = Path.Combine(_baseDirectory, "WarmupNormal.cs");
        await File.WriteAllTextAsync(warmupNormalPath, normalShapedContent);
        await MeasureOpenAsync(shell, window, warmupNormalPath);

        var warmupLongPath = Path.Combine(_baseDirectory, "WarmupLong.cs");
        await File.WriteAllTextAsync(warmupLongPath, longLineContent);
        await MeasureOpenAsync(shell, window, warmupLongPath);

        // 基準1回・対象1回を1組として、直前直後に交互に計測する（同じ組の基準・対象は負荷側の
        // 状態がほぼ同じ条件を共有するため「片方だけ運良く空いた瞬間に当たる」非対称が起きにくい。
        // 詳細はCrossFileSearchPerformanceTestsのクラスドキュメントコメントを参照）。同じパスを
        // 2回開くと「既に開いているタブへの切替」という別の（速い）経路になり測定にならないため、
        // 組ごとに新しいファイル名を使う。
        var baselineTimes = new List<double>(MeasurementRuns);
        var targetTimes = new List<double>(MeasurementRuns);
        var ratios = new List<double>(MeasurementRuns);
        for (var i = 0; i < MeasurementRuns; i++)
        {
            var normalPath = Path.Combine(_baseDirectory, $"Normal{i}.cs");
            await File.WriteAllTextAsync(normalPath, normalShapedContent);
            var baselineMs = await MeasureOpenAsync(shell, window, normalPath);

            var longLinePath = Path.Combine(_baseDirectory, $"LongLine{i}.cs");
            await File.WriteAllTextAsync(longLinePath, longLineContent);
            var targetMs = await MeasureOpenAsync(shell, window, longLinePath);

            baselineTimes.Add(baselineMs);
            targetTimes.Add(targetMs);
            ratios.Add(targetMs / Math.Max(0.01, baselineMs));
        }

        // 組ごとの倍率の中央値を採用する。理由はCrossFileSearchPerformanceTestsのクラス
        // ドキュメントコメントを参照（1〜2組が外れ値になっても最終判定が引きずられにくい）。
        var ratio = Median(ratios);

        var baselineRawText = string.Join(", ", baselineTimes.Select(t => t.ToString("F3")));
        var targetRawText = string.Join(", ", targetTimes.Select(t => t.ToString("F3")));
        var ratiosRawText = string.Join(", ", ratios.Select(r => r.ToString("F3")));

        _output.WriteLine($"基準（総文字数同等・通常の行長、{MeasurementRuns}組）: [{baselineRawText}] ms");
        _output.WriteLine($"対象（1行10万文字、{MeasurementRuns}組）: [{targetRawText}] ms");
        _output.WriteLine($"組ごとの倍率: [{ratiosRawText}] → 中央値 {ratio:F2}倍");

        // 実機での報告値（6.2秒、総データ量が同等の通常ファイルの10倍以上）を大きく下回ることを
        // 確認する回帰ガード。絶対時間ではなく総データ量が同等な基準との倍率で判定するため、
        // CI環境そのものの速さのばらつきには左右されない（性能が桁で悪化したときだけ
        // 気付ければよい、PerformanceTestsと同方針）。失敗時は基準・対象それぞれの全計測値
        // （生の値）と組ごとの倍率をメッセージへ含める（次にCIで落ちたとき、たまたま1組だけ
        // 遅い値を引いたノイズなのか、全体的に遅い本物の劣化なのかを判断できるようにするため）。
        ratio.Should().BeLessThan(RatioThreshold,
            $"総データ量が同等の通常ファイルに比べ組ごとの倍率の中央値で{ratio:F2}倍かかっている"
            + $"（基準: 全{MeasurementRuns}組[{baselineRawText}]ms → 対象: 全{MeasurementRuns}組[{targetRawText}]ms → "
            + $"組ごとの倍率[{ratiosRawText}]）。"
            + "構文強調・折り返し・括弧対応付けの自動無効化が効いていない可能性がある");

        window.Close();
    }

    /// <summary>
    /// ファイルを1回開き、初回描画までの所要時間（ms、小数精度）を返す。構文強調・折り返し・
    /// 括弧対応付けの実際のコスト（AvaloniaEdit側の書式計算）はドキュメント差し替え時点では
    /// まだ発生せず、実際に描画（Measure/Arrange/Render）されるまで遅延評価される。
    /// <c>OpenFileAsync</c>だけを計測すると、自動無効化の有無に関わらずほぼ同じ時間になり
    /// （Attach呼び出し自体は登録だけで軽い）、この回帰テストが検出したい劣化を実際には
    /// 検出できない（本コミットの検証手順で実測・確認済み: 自動無効化を一時的に無効化しても
    /// 描画なしでは倍率がほとんど動かなかった）。PerformanceTests.MeasureOpenAndRenderと同じく
    /// <see cref="ShellWindow.CaptureRenderedFrame"/>まで含めて計測する。
    /// </summary>
    private static async Task<double> MeasureOpenAsync(ShellViewModel shell, ShellWindow window, string path)
    {
        var sw = Stopwatch.StartNew();
        var result = await shell.Editor.OpenFileAsync(path);
        using var frame = window.CaptureRenderedFrame();
        sw.Stop();
        result.IsSuccess.Should().BeTrue();
        frame.Should().NotBeNull();
        return sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>中央値を求める（偶数個なら中央2件の平均。CrossFileSearchPerformanceTestsと同じ実装）。</summary>
    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
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
