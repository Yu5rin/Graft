using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
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
/// 【設計の経緯（再設計）】
/// 旧仕様（初版）では、極端に長い行（1行20,000文字超）を1行でも含むファイルを開くと、
/// 構文強調・折り返し・括弧の対応付け・折りたたみをファイル全体で一括無効化していた。
/// しかし「1行でも極端に長い行があると、残り99%の通常行まで色が消える」のは利用者から見て
/// 過剰であり、「エディタとして致命的」という指摘を受けて設計をやり直した（本コミット）。
///
/// 新仕様は次のとおり（詳細な実測はDocumentSession.LongLineThreshold・
/// SyntaxHighlightBridge.ColorizeLine・BracketSupport.IsInsideStringOrComment・
/// FoldingSupportの各コメント参照）。
/// - 構文強調: ファイル全体の無効化をやめ、しきい値を超える「その行だけ」強調を打ち切る。
/// - 括弧の対応付け・自動閉じ: 同様に「その行だけ」言語認識（文字列/コメント判定）を
///   打ち切ってO(1)化する。実測では、旧方式のまま（行全体を毎ステップ再トークン化）だと
///   n=2,000で51ms・n=4,000で122ms・n=8,000で513msとO(n^2)で伸び、n=100,000へ外挿すると
///   概算80秒に達する。打ち切りを入れると n=100,000 の最悪ケースでも約4.3msで完了する。
/// - 折りたたみ: 実測したところ全体再計算のコストは1行10万文字のファイルで1ms未満、
///   3万行＋1行10万文字の混在ファイルでも最大19ms程度であり、300msのデバウンス予算に対して
///   無視できるほど小さいため、そもそも長い行による特別扱いをしない（常に有効）と判断した。
/// - 折り返し: 「利用者の設定を無断で上書きする」こと自体が問題という指摘を受け、強制無効化を
///   廃止し既定値もfalse→trueへ変更した。ただし1行10万文字＋折り返し有効では実測で書式計算が
///   数百ms→1.5秒前後に悪化することを確認しており、これは「利用者が選べばよいコスト」と整理し、
///   通知バーの「このファイルでは折り返しを無効にする」ボタンでタブ単位に折り返しを切れる
///   逃げ道を用意した。
///
/// 【計測方法についての注記（旧仕様のときの調査から維持）】
/// 実測（PerformanceTestsと同じ方式のheadlessベンチ）では、開く操作自体はこの環境で
/// 数百ms程度に収まる。構文強調・折り返し・括弧対応付けの実コストはAvaloniaEdit側で
/// 描画時まで遅延評価されるため、<c>OpenFileAsync</c>だけを計測すると自動無効化・キャップの
/// 有無に関わらずほぼ同じ時間になり、回帰テストが検出したい劣化を実際には検出できないことを
/// 確認した。そのため性能の回帰ガード（<see cref="極端に長い行でも実用的な時間で開ける"/>）は
/// 必ず初回描画（<see cref="ShellWindow.CaptureRenderedFrame"/>）まで含めて計測する。
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
        // window.Show()はShellWindow.OnLoaded経由で非同期にshell.Graft.InitializeAsync()を
        // 呼ぶ。ここでさらに明示的に呼ぶと初期化が二重に走り、settings.json/projects.jsonの
        // 読み直しが競合する（ScenarioTests.OpenShellAsync参照、実機で5割前後の確率での
        // 失敗を確認した事故と同じ種類の競合状態）。自分では呼ばず、OnLoaded経由の初期化完了を
        // ShellWindowLoadWaiterで待つ。
        ShellWindowLoadWaiter.WaitForLayoutApplied(window);

        var filePath = Path.Combine(_baseDirectory, "normal.cs");
        await File.WriteAllTextAsync(filePath, "class Normal\n{\n    void M() { }\n}\n");

        var result = await shell.Editor.OpenFileAsync(filePath);
        result.IsSuccess.Should().BeTrue();

        result.Value.Session.HasExtremelyLongLine.Should().BeFalse();
        shell.Editor.ActiveTabHasLongLineWarning.Should().BeFalse();

        window.Close();
    }

    [AvaloniaFact(DisplayName = "課題3（再設計）: 極端に長い行を含むファイルでも、折り返しは利用者の設定にそのまま従う（強制オフを廃止）")]
    public async Task 折り返し設定にそのまま従う()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var settings = new Settings { Editor = new EditorSettings { WordWrap = true } };
        var (shell, window) = BuildShellAndWindow(appPaths, settings);
        window.Show();
        ShellWindowLoadWaiter.WaitForLayoutApplied(window);

        shell.Editor.WordWrap.Should().BeTrue("この検証は「利用者が折り返しを有効にしている」状況を再現するため");

        var filePath = Path.Combine(_baseDirectory, "LongLine.cs");
        await File.WriteAllTextAsync(filePath, "class L { /* " + new string('x', 100_000) + " */ }\n");
        var result = await shell.Editor.OpenFileAsync(filePath);
        result.IsSuccess.Should().BeTrue();

        var editor = window.GetControl<Border>("EditorHost").Child as EditorPane;
        editor.Should().NotBeNull();
        var textEditor = editor!.GetControl<TextEditor>("Editor");

        textEditor.WordWrap.Should().BeTrue(
            "以前は極端に長い行を含むファイルでは利用者の設定に関わらず折り返しを強制的にオフに"
            + "していたが、「エディタとして致命的」という指摘（設定を無断で上書きすること自体が"
            + "問題）を受けて廃止した。折り返しが重くなりうる場合の逃げ道は通知バーの"
            + "「このファイルでは折り返しを無効にする」ボタン（別テスト参照）で提供する。");

        window.Close();
    }

    [AvaloniaFact(DisplayName = "課題3（再設計）: 通知バーの「このファイルでは折り返しを無効にする」でそのタブだけ折り返しが切れる")]
    public async Task 通知バーのボタンでそのタブだけ折り返しを無効にできる()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var settings = new Settings { Editor = new EditorSettings { WordWrap = true } };
        var (shell, window) = BuildShellAndWindow(appPaths, settings);
        window.Show();
        ShellWindowLoadWaiter.WaitForLayoutApplied(window);

        // 長い行を含むタブと、通常のタブの両方を開く（無効化がタブ単位であり、設定そのものや
        // 他タブへ波及しないことを確認するため）。
        var longLinePath = Path.Combine(_baseDirectory, "LongLine.cs");
        await File.WriteAllTextAsync(longLinePath, "class L { /* " + new string('x', 100_000) + " */ }\n");
        var longLineResult = await shell.Editor.OpenFileAsync(longLinePath);
        longLineResult.IsSuccess.Should().BeTrue();
        var longLineTab = longLineResult.Value;

        var normalPath = Path.Combine(_baseDirectory, "normal.cs");
        await File.WriteAllTextAsync(normalPath, "class Normal\n{\n}\n");
        var normalResult = await shell.Editor.OpenFileAsync(normalPath);
        normalResult.IsSuccess.Should().BeTrue();
        var normalTab = normalResult.Value;

        var editor = window.GetControl<Border>("EditorHost").Child as EditorPane;
        editor.Should().NotBeNull();
        var textEditor = editor!.GetControl<TextEditor>("Editor");

        // 開いた直後（アクティブ=normalTab、末尾に開いたタブがアクティブになる）は
        // 利用者の設定どおり折り返しが有効。
        textEditor.WordWrap.Should().BeTrue();

        // 長い行のタブへ切り替えても、まだボタンを押していない間は設定どおり有効のまま。
        shell.Editor.ActiveTab = longLineTab;
        longLineTab.WordWrapDisabledForTab.Should().BeFalse();
        textEditor.WordWrap.Should().BeTrue("ボタンを押すまでは利用者の設定にそのまま従うはず");

        // 通知バーの「このファイルでは折り返しを無効にする」（Commandバインド）を押す。
        longLineTab.DisableWordWrapForTabCommand.Execute(null);

        longLineTab.WordWrapDisabledForTab.Should().BeTrue();
        textEditor.WordWrap.Should().BeFalse("このタブだけ折り返しを無効にしたはず");

        // 他のタブ（通常ファイル）には影響しない。設定自体も変わっていない。
        shell.Editor.ActiveTab = normalTab;
        textEditor.WordWrap.Should().BeTrue("折り返し無効化はタブ単位であり、他のタブへは波及しないはず");
        shell.Editor.WordWrap.Should().BeTrue("設定そのものは変更されていないはず");

        // 長い行のタブへ戻ると、無効化状態はそのタブに保持されている。
        shell.Editor.ActiveTab = longLineTab;
        textEditor.WordWrap.Should().BeFalse("タブを離れて戻っても無効化状態は保持されるはず");

        window.Close();
    }

    [AvaloniaFact(DisplayName = "課題3（再設計）: 極端に長い行を含むファイルでも、通常行には構文強調が付き、しきい値超の行だけ打ち切られる")]
    public async Task 通常行には構文強調が付きしきい値超の行だけ打ち切られる()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        // 折り返しはこのテストの関心事ではないため明示的にオフにする（描画時間を安定させる）。
        var settings = new Settings { Editor = new EditorSettings { WordWrap = false } };
        var (shell, window) = BuildShellAndWindow(appPaths, settings);
        window.Show();
        ShellWindowLoadWaiter.WaitForLayoutApplied(window);

        // 1行目: しきい値（20,000文字）を超える行。"class"というキーワードを含めておき、
        // 万一この行まで強調されてしまえば検出できるようにする（打ち切りの検証）。
        // 2行目: 通常長の行。同じ"class"キーワード＋PascalCase型名を含む
        // （ファイル全体を無効化していないことの検証）。
        var filePath = Path.Combine(_baseDirectory, "MixedLongLine.cs");
        var longLine = "class " + new string('x', 30_000);
        await File.WriteAllTextAsync(filePath, longLine + "\nclass Normal\n{\n}\n");

        var result = await shell.Editor.OpenFileAsync(filePath);
        result.IsSuccess.Should().BeTrue();
        result.Value.Session.HasExtremelyLongLine.Should().BeTrue("前提条件: 1行目がしきい値を超えていること");

        var editor = window.GetControl<Border>("EditorHost").Child as EditorPane;
        editor.Should().NotBeNull();
        var textEditor = editor!.GetControl<TextEditor>("Editor");

        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull();

        var visualLines = textEditor.TextArea.TextView.VisualLines;
        var longLineVisual = visualLines.FirstOrDefault(v => v.FirstDocumentLine.LineNumber == 1);
        var normalLineVisual = visualLines.FirstOrDefault(v => v.FirstDocumentLine.LineNumber == 2);
        longLineVisual.Should().NotBeNull("1行目（しきい値超）は可視範囲内のはず");
        normalLineVisual.Should().NotBeNull("2行目（通常長）は可視範囲内のはず");

        var keywordColor = ResolveThemeColor("SyntaxKeywordColor");
        var typeColor = ResolveThemeColor("SyntaxTypeColor");

        HasSyntaxColor(longLineVisual!, keywordColor, typeColor).Should().BeFalse(
            "しきい値を超える行は構文強調を打ち切るはず（SyntaxHighlightBridge.ColorizeLine参照）");
        HasSyntaxColor(normalLineVisual!, keywordColor, typeColor).Should().BeTrue(
            "通常行には構文強調が付くはず（ファイル全体を無効化していないことの検証）");

        window.Close();
    }

    /// <summary>
    /// 課題3（再設計）: 括弧の対応付け（BracketSupport.MatchBracket）は、キャレットが括弧に
    /// 隣接すると対応する括弧を探して1文字ずつ進む。旧実装のまま（1文字ごとに行全体を
    /// 再トークン化）だと最悪ケース（開き括弧が行頭、対応が行末近く）でO(行の文字数の2乗)に
    /// なり、実測（n=2,000/4,000/8,000）から n=100,000 へ外挿すると概算80秒に達する
    /// （BracketSupport.IsInsideStringOrCommentのコメント参照）。ここでは実際にその最悪ケースを
    /// 再現し、キャップ後は実用的な時間（実測で数msのオーダー）で完了することを回帰ガードする。
    /// </summary>
    [AvaloniaFact(DisplayName = "課題3（再設計）: 極端に長い行でキャレットが開き括弧に隣接しても、対応括弧の探索が実用的な時間で完了する（回帰ガード）")]
    public async Task キャレットが括弧に隣接しても実用的な時間で完了する()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var settings = new Settings { Editor = new EditorSettings { WordWrap = false } };
        var (shell, window) = BuildShellAndWindow(appPaths, settings);
        window.Show();
        ShellWindowLoadWaiter.WaitForLayoutApplied(window);

        // 最悪ケース: 開き括弧が行頭、対応する閉じ括弧が行末近く（100,000文字先）。
        var filePath = Path.Combine(_baseDirectory, "BracketWorstCase.cs");
        await File.WriteAllTextAsync(filePath, "{" + new string('x', 100_000) + "}\n");

        var result = await shell.Editor.OpenFileAsync(filePath);
        result.IsSuccess.Should().BeTrue();

        var editor = window.GetControl<Border>("EditorHost").Child as EditorPane;
        editor.Should().NotBeNull();
        var textEditor = editor!.GetControl<TextEditor>("Editor");

        // 初回描画（Attach直後の状態）を済ませてから計測する。ここまでのコストは対象外。
        using (var warmupFrame = window.CaptureRenderedFrame()) { warmupFrame.Should().NotBeNull(); }

        // キャレットを開き括弧（オフセット0）へ合わせる。対応括弧の実際の探索・強調描画は
        // BracketSupport.Drawが次の描画パスで行うため、CaptureRenderedFrameで強制的に
        // 描画させて初めて計測対象のコストが発生する。
        textEditor.TextArea.Caret.Offset = 0;

        var sw = Stopwatch.StartNew();
        using var frame = window.CaptureRenderedFrame();
        sw.Stop();
        frame.Should().NotBeNull();

        _output.WriteLine($"括弧対応付け（最悪ケース、n=100,000）の描画時間: {sw.Elapsed.TotalMilliseconds:F2} ms");

        sw.Elapsed.TotalMilliseconds.Should().BeLessThan(5_000,
            $"実測ではキャップ後は数msのオーダーで完了するはずが{sw.Elapsed.TotalMilliseconds:F2}msかかった。"
            + "BracketSupport.IsInsideStringOrCommentの行単位キャップが効いていない可能性がある"
            + "（キャップ無しではO(n^2)でn=100,000は数十秒オーダーになる）");

        window.Close();
    }

    private static bool HasSyntaxColor(AvaloniaEdit.Rendering.VisualLine visual, params Color[] colors)
        => visual.Elements.Any(e => e.TextRunProperties.ForegroundBrush is ISolidColorBrush b && colors.Contains(b.Color));

    private static Color ResolveThemeColor(string key)
    {
        Avalonia.Application.Current!.TryFindResource(key, null, out var value);
        value.Should().NotBeNull($"テーマリソース '{key}' が解決できる必要がある");
        return (Color)value!;
    }

    /// <summary>
    /// 基準1回・対象1回を1組として、この組数だけ交互に計測し、組ごとの倍率の中央値を採用する
    /// （tests/Graft.Tests/CrossFileSearchPerformanceTestsで確立した手法に合わせる。
    /// 詳細な設計の経緯・理由はそちらのクラスドキュメントコメントを参照。ここで再発明はしない）。
    /// </summary>
    private const int MeasurementRuns = 7;

    /// <summary>
    /// 課題3（再設計）: このガードは「その行だけキャップする」経路（構文強調・括弧の対応付け）が
    /// 壊れていないかを検証するためのものであり、折り返しは意図的にオフにする
    /// （<see cref="MeasureOpenAsync"/>呼び出し元のSettings参照）。
    ///
    /// 旧版のこのテストはWordWrap=trueを利用者設定として与え、「対象（長い行）は強制的に
    /// 折り返しなしで描画され、基準（通常行）は折り返し計算のぶんだけコストを負う」という
    /// 非対称を利用して倍率を1近辺に抑えていた。しかし折り返しの強制オフを廃止したため、
    /// 折り返しを有効にしたままこのガードを回すと、対象（1行10万文字）が実際に折り返し
    /// コスト（実測で数百ms→1.5秒前後）を負ってしまい、「その行だけキャップする」経路とは
    /// 無関係な理由でratioが跳ね上がる（＝この回帰テストが検出したい劣化とは別の要因で
    /// 落ちる）。そのため両者ともWordWrap=falseで揃え、キャップの実効性だけを見る。
    /// 折り返し有効時の実測コストは別途「極端に長い行を折り返し有効で開いた場合の時間を記録する」
    /// で記録する（アサーションはごく緩い上限のみ、目的は数値の記録）。
    /// </summary>
    private const double RatioThreshold = 3.0;

    [AvaloniaFact(DisplayName = "課題3: 極端に長い行を含むファイルを開いても実用的な時間で完了する（回帰ガード、折り返しオフ）")]
    public async Task 極端に長い行でも実用的な時間で開ける()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        // 課題3（再設計）: 折り返しはこのガードの関心事ではないため明示的にオフにする
        // （RatioThresholdのコメント参照）。
        var settings = new Settings { Editor = new EditorSettings { WordWrap = false } };
        var (shell, window) = BuildShellAndWindow(appPaths, settings);
        window.Show();
        ShellWindowLoadWaiter.WaitForLayoutApplied(window);

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

        _output.WriteLine($"基準（総文字数同等・通常の行長、{MeasurementRuns}組、折り返しオフ）: [{baselineRawText}] ms");
        _output.WriteLine($"対象（1行10万文字、{MeasurementRuns}組、折り返しオフ）: [{targetRawText}] ms");
        _output.WriteLine($"組ごとの倍率: [{ratiosRawText}] → 中央値 {ratio:F2}倍");

        // 絶対時間ではなく総データ量が同等な基準との倍率で判定するため、CI環境そのものの
        // 速さのばらつきには左右されない（性能が桁で悪化したときだけ気付ければよい、
        // PerformanceTestsと同方針）。失敗時は基準・対象それぞれの全計測値（生の値）と
        // 組ごとの倍率をメッセージへ含める（次にCIで落ちたとき、たまたま1組だけ遅い値を
        // 引いたノイズなのか、全体的に遅い本物の劣化なのかを判断できるようにするため）。
        ratio.Should().BeLessThan(RatioThreshold,
            $"総データ量が同等の通常ファイルに比べ組ごとの倍率の中央値で{ratio:F2}倍かかっている"
            + $"（基準: 全{MeasurementRuns}組[{baselineRawText}]ms → 対象: 全{MeasurementRuns}組[{targetRawText}]ms → "
            + $"組ごとの倍率[{ratiosRawText}]）。"
            + "構文強調・括弧対応付けの行単位キャップが効いていない可能性がある");

        window.Close();
    }

    /// <summary>
    /// 性能の実測記録（要件5）: 折り返し有効時の実コストを、変更後のコードで記録する。
    /// 折り返しは「利用者が選べばよいコスト」という整理のため、ここでは回帰ガードのような
    /// 厳密な倍率アサーションは行わず、ハング等の壊滅的劣化だけを検出するごく緩い絶対時間の
    /// 上限のみを課す（実測値はテスト出力・本コミットの報告に記録する）。
    /// </summary>
    [AvaloniaFact(DisplayName = "課題3（再設計）: 極端に長い行を折り返し有効で開いた場合の所要時間を記録する（緩いガードのみ）")]
    public async Task 折り返し有効時の所要時間を記録する()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var settings = new Settings { Editor = new EditorSettings { WordWrap = true } };
        var (shell, window) = BuildShellAndWindow(appPaths, settings);
        window.Show();
        ShellWindowLoadWaiter.WaitForLayoutApplied(window);

        var normalShapedContent = BuildNormalShapedContent();
        var longLineContent = "class L { /* " + new string('x', 100_000) + " */ }\n";

        var warmupNormalPath = Path.Combine(_baseDirectory, "WarmupNormalWrap.cs");
        await File.WriteAllTextAsync(warmupNormalPath, normalShapedContent);
        await MeasureOpenAsync(shell, window, warmupNormalPath);

        var warmupLongPath = Path.Combine(_baseDirectory, "WarmupLongWrap.cs");
        await File.WriteAllTextAsync(warmupLongPath, longLineContent);
        await MeasureOpenAsync(shell, window, warmupLongPath);

        var normalPath = Path.Combine(_baseDirectory, "NormalWrap.cs");
        await File.WriteAllTextAsync(normalPath, normalShapedContent);
        var baselineMs = await MeasureOpenAsync(shell, window, normalPath);

        var longLinePath = Path.Combine(_baseDirectory, "LongLineWrap.cs");
        await File.WriteAllTextAsync(longLinePath, longLineContent);
        var targetMs = await MeasureOpenAsync(shell, window, longLinePath);

        _output.WriteLine($"基準（総文字数同等・通常の行長、折り返しオン）: {baselineMs:F1} ms");
        _output.WriteLine($"対象（1行10万文字、折り返しオン）: {targetMs:F1} ms（実測で数百ms〜1.5秒前後になることを確認済み）");

        // ハング等の壊滅的劣化だけを検出する（実測の最大値1.5秒前後に対して十分な余裕を持たせる）。
        targetMs.Should().BeLessThan(10_000,
            $"折り返し有効時のコストは利用者が選べばよいものとして許容しているが、{targetMs:F1}msは"
            + "実測の想定（数百ms〜1.5秒前後）から桁で外れており、別の問題が疑われる");

        window.Close();
    }

    /// <summary>
    /// ファイルを1回開き、初回描画までの所要時間（ms、小数精度）を返す。構文強調・折り返し・
    /// 括弧対応付けの実際のコスト（AvaloniaEdit側の書式計算）はドキュメント差し替え時点では
    /// まだ発生せず、実際に描画（Measure/Arrange/Render）されるまで遅延評価される。
    /// <c>OpenFileAsync</c>だけを計測すると、キャップ・強調の有無に関わらずほぼ同じ時間になり
    /// （Attach呼び出し自体は登録だけで軽い）、この回帰テストが検出したい劣化を実際には
    /// 検出できない（本コミットの検証手順で実測・確認済み）。PerformanceTests.
    /// MeasureOpenAndRenderと同じく<see cref="ShellWindow.CaptureRenderedFrame"/>まで
    /// 含めて計測する。
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
        // window.Show()はShellWindow.OnLoaded経由で非同期にshell.Graft.InitializeAsync()を
        // 呼ぶ。ここでさらに明示的に呼ぶと初期化が二重に走り、settings.json/projects.jsonの
        // 読み直しが競合する（ScenarioTests.OpenShellAsync参照、実機で5割前後の確率での
        // 失敗を確認した事故と同じ種類の競合状態）。自分では呼ばず、OnLoaded経由の初期化完了を
        // ShellWindowLoadWaiterで待つ。
        ShellWindowLoadWaiter.WaitForLayoutApplied(window);

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
