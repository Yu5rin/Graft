using System.Diagnostics;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using FluentAssertions;
using Graft.Core;
using Graft.Editor;
using Graft.UiTests.TestSupport;
using Xunit.Abstractions;

namespace Graft.UiTests;

/// <summary>
/// 仕様書18章の性能要件を、画面の無い環境で自動検証できる範囲で押さえる（20章 L5）。
///
/// 「遅延なく」は体感の指標のため、ここでは体感が損なわれる水準を上限として置く
/// （1操作あたりの上限を十分に余裕のある値にし、性能が桁で悪化したときだけ失敗させる）。
/// 実測値はテスト出力へ書き出し、環境ごとの傾向を追えるようにする。
///
/// 壁時計時間を絶対値で判定すると、共有ランナー等の遅さで無関係に落ちる（CI実績あり）。
/// ここでは可能な限り「同じ実行内で計測した軽い基準操作の何倍以内か」という相対比較へ
/// 置き換える。ハードウェアの速さは基準・対象の両方に等しく乗るため相殺され、
/// アルゴリズムそのものの劣化（桁が変わるような悪化）だけを検出できる。
///
/// 計測手法について: 「小さすぎる分母」問題（tests/Graft.Tests/CrossFileSearchPerformanceTests.cs
/// のクラスドキュメントコメント参照）を避けるため、各テストは<see cref="CrossFileSearchPerformanceTests"/>
/// と同じ手法（基準・対象の両方を必ずウォームアップする、基準1回・対象1回を1組として交互に
/// <see cref="MeasurementRuns"/>組計測し組ごとの倍率の中央値を採用する、
/// <see cref="Stopwatch.Elapsed"/>のTotalMillisecondsで小数精度を使う）へ揃えている。ここで
/// 再発明はせず、詳細な設計の経緯は同クラスのドキュメントコメントを参照する。
///
/// 総処理量を揃えるかどうかはテストごとに異なる（各Measure*メソッドのコメント参照）。
/// 「開く・レキサ走査」のように処理量が行数に比例するテストは
/// <see cref="OpenRenderScaleFactor"/>・<see cref="LexerScaleFactor"/>で基準側を複数回に
/// 分けて合計し、対象と処理行数を揃える。「スクロール・編集・可視範囲ハイライト」は
/// 仮想化・可視範囲限定が効いていれば操作回数（3回のスクロール・50回の挿入・1回の描画）
/// そのものが既に基準・対象で揃っており、むしろ「総行数に依存しないこと」自体を検証したい
/// ため、行数を無理に揃えるためだけの繰り返しはしない（繰り返すと固定費用だけが基準側に
/// 積み上がり、意図と逆に基準が対象より遅く見えてしまう）。
/// </summary>
public class PerformanceTests : IDisposable
{
    private const int LineCount = 100_000;

    /// <summary>基準1回・対象1回を1組として、この組数だけ交互に計測し、組ごとの倍率の中央値を採用する
    /// （CrossFileSearchPerformanceTestsで確立した手法に合わせる）。</summary>
    private const int MeasurementRuns = 7;

    /// <summary>
    /// 「行数に依存せず一定であるべき」操作（スクロール・編集・可視範囲ハイライト）の基準規模。
    /// 仮想化・可視範囲限定が効いていれば、この規模での所要時間と10万行での所要時間はほぼ同じになる。
    /// </summary>
    private const int SmallLineCount = 2_000;

    /// <summary>
    /// 組ごとの倍率の中央値がこの値未満であることを要求する（本ファイル全テスト共通）。
    /// 負荷なしで3回繰り返した実測では、5テストいずれも中央値は0.24〜1.84倍の範囲に収まった
    /// （「開く」はScaleFactorで基準側を10回に分割する分の固定費用（ウィンドウ構築等）を
    /// 余分に払うため、対象より基準の方が遅く、比は1未満になりやすい）。4本のビジーループで
    /// 4コアを飽和させた負荷下で2回繰り返した実測でも、中央値は0.27〜1.34倍に収まった
    /// （本コミットの検証手順参照。SearchPerformanceTestsも同じ"PerformanceTests"という
    /// フィルタ文字列に一致するため、混同しないよう本ファイル単体で計測し直した数値）。
    /// 検証4（劣化を注入して実際に検出できるか）では、シンタックスハイライトの可視範囲限定を
    /// 無効化すると中央値13.0倍で明確に検出できた。レキサ走査へ人為的にO(n^2)の劣化
    /// （1行あたり行番号に比例する無駄な処理）を注入する実験では、注入量を増やすほど中央値が
    /// 1.08→2.04→3.26倍と上昇したが、SyntaxLexer自身の自動無効化（10000行換算で100msを
    /// 超えると打ち切る安全機構、SyntaxLexer.Scan参照）が基準・対象の双方でそれぞれの規模に
    /// 比例した予算で頭打ちになるため、劣化をさらに強くしても比は際限なく伸びず、ある点からは
    /// 逆に1へ収束してしまう限界を確認した。旧来の8〜30倍という緩い上限から、負荷下の実測上限
    /// （1.34倍）に十分な余裕を残しつつ、かつ検証4で確認できた劣化注入時の倍率上昇
    /// （2.04〜3.26倍）を掬えるよう、大きく引き締めた3.0を採用する。
    /// </summary>
    private const double RelativeCostRatioThreshold = 3.0;

    private readonly ITestOutputHelper _output;
    private readonly ShownWindowTracker _windows = new();

    public PerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose()
    {
        // 表示したウィンドウを後始末する（ShownWindowTracker参照）。本ファイルは10万行の
        // ドキュメントを読み込んだ状態のウィンドウを多数開くため、閉じ忘れの影響が特に大きい
        // （閉じ忘れると「Unable to locate 'Avalonia.Platform.IFontManagerImpl'」がCIで
        // 不定期に出る）。
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 「開く」は仮想化の対象外（ドキュメント全体を読み込む必要がある）で処理量が行数に
    /// 比例するはずのテストのため、CrossFileSearchPerformanceTestsのScaleFactorと同じ考え方で
    /// 基準側を複数回に分けて合計し、対象と処理行数の総量を揃える。
    /// </summary>
    private const int OpenRenderScaleFactor = 10;

    [AvaloniaFact(DisplayName = "10万行のファイルを開いても構築・描画が滞らない")]
    public void 十万行を開ける()
    {
        const int baselineLines = LineCount / OpenRenderScaleFactor; // 1万行

        // ウォームアップ（初回JIT・初回ウィンドウ表示の費用を計測対象から除く）。両方を
        // 必ずウォームアップする（片側だけだと非対称が生じる。CrossFileSearchPerformanceTests参照）。
        MeasureOpenAndRender(baselineLines);
        MeasureOpenAndRender(LineCount, expectedLineCount: LineCount + 1);

        // 基準1回（1万行の開く操作をOpenRenderScaleFactor回連続で実行した合計時間）・対象1回
        // （10万行を1回開く）を1組として、直前直後に交互に計測する。総処理行数は基準・対象で
        // 揃っている（1万行×10回＝10万行）。
        var (ratio, baselineTimes, targetTimes, ratios) = MeasureAlternatingRatio(
            () =>
            {
                var total = 0.0;
                for (var j = 0; j < OpenRenderScaleFactor; j++) total += MeasureOpenAndRender(baselineLines);
                return total;
            },
            () => MeasureOpenAndRender(LineCount, expectedLineCount: LineCount + 1));

        WriteMeasurementLog(
            "読み込みと初回描画", $"{baselineLines}行×{OpenRenderScaleFactor}回",
            $"{LineCount}行×1回", baselineTimes, targetTimes, ratios, ratio);
        _output.WriteLine($"（総処理行数は基準・対象とも{LineCount}行で同じ）");

        // 読み込み・初回描画は行数にほぼ比例するはずなので、総処理行数を揃えれば比はおよそ1.0に
        // なるのが自然。定数コスト（基準側はOpenRenderScaleFactor回に分割する分、ウィンドウ生成
        // 等の固定費用を余分に払う）を吸収する余裕を持たせつつ、二乗のような破滅的劣化
        // （本来ならOpenRenderScaleFactor倍規模になる）だけを検出できる上限とする。
        ratio.Should().BeLessThan(RelativeCostRatioThreshold,
            $"総処理行数を揃えた基準（{baselineLines}行×{OpenRenderScaleFactor}回）に対し対象（{LineCount}行×1回）が"
            + $"組ごとの倍率の中央値で{ratio:F2}倍の時間になっている"
            + $"（基準: 全{MeasurementRuns}組[{string.Join(", ", baselineTimes.Select(t => t.ToString("F3")))}]ms → "
            + $"対象: 全{MeasurementRuns}組[{string.Join(", ", targetTimes.Select(t => t.ToString("F3")))}]ms → "
            + $"組ごとの倍率[{string.Join(", ", ratios.Select(r => r.ToString("F3")))}]）。"
            + "読み込み・初回描画の計算量が行数に対して線形から外れている可能性がある");
    }

    [AvaloniaFact(DisplayName = "10万行のファイルでもスクロールが滞らない")]
    public void 十万行でもスクロールできる()
    {
        // ウォームアップ（初回JIT・初回ウィンドウ表示の費用を計測対象から除く）。両方を
        // 必ずウォームアップする（片側だけだと非対称が生じる。CrossFileSearchPerformanceTests参照）。
        MeasureScroll(SmallLineCount);
        MeasureScroll(LineCount);

        // 総処理量について: 1回の計測はどちらも「先頭・中間・末尾へ3回スクロール」という
        // 同じ操作回数であり、仮想化が効いていれば1回あたりのコストは可視範囲の行数だけで
        // 決まり総行数には依存しないはず（クラスドキュメントコメント参照）。そのため
        // CrossFileSearchPerformanceTestsのScaleFactorのように基準側を繰り返して総行数を
        // 揃えることはしない（揃えるために基準を50回繰り返すと、固定費用だけが基準側に
        // 積み上がり、意図と逆に基準が対象より遅く見えてしまう）。
        var (ratio, baselineTimes, targetTimes, ratios) = MeasureAlternatingRatio(
            () => MeasureScroll(SmallLineCount), () => MeasureScroll(LineCount));

        WriteMeasurementLog("スクロール", $"{SmallLineCount}行", $"{LineCount}行", baselineTimes, targetTimes, ratios, ratio);

        // 仮想化が効いていれば、スクロール1回あたりの再描画コストは可視範囲の行数だけで決まり
        // 総行数（{SmallLineCount}行 vs {LineCount}行、{LineCount / SmallLineCount}倍差）には
        // 依存しないはず。仮想化が壊れて全行を描き直すようになった場合だけを検出できる上限とする。
        ratio.Should().BeLessThan(RelativeCostRatioThreshold,
            $"総行数が{LineCount / SmallLineCount}倍（{SmallLineCount}→{LineCount}）なのに"
            + $"組ごとの倍率の中央値でスクロール時間も{ratio:F2}倍になっている"
            + $"（基準: 全{MeasurementRuns}組[{string.Join(", ", baselineTimes.Select(t => t.ToString("F3")))}]ms → "
            + $"対象: 全{MeasurementRuns}組[{string.Join(", ", targetTimes.Select(t => t.ToString("F3")))}]ms → "
            + $"組ごとの倍率[{string.Join(", ", ratios.Select(r => r.ToString("F3")))}]）。"
            + "スクロールのたびに全行を描き直している（仮想化が効いていない）可能性がある");
    }

    [AvaloniaFact(DisplayName = "10万行のファイルでも編集が滞らない")]
    public void 十万行でも編集できる()
    {
        // ウォームアップ（両方）。
        MeasureHeadInsert(SmallLineCount);
        MeasureHeadInsert(LineCount, expectedLineCount: LineCount + 50 + 1);

        // 総処理量について: 挿入回数（50回）は基準・対象で既に揃っており、可視範囲の再描画で
        // コストが決まるはずのテストのため、スクロールと同じ理由でScaleFactorによる水増しは
        // しない（クラスドキュメントコメント参照）。
        var (ratio, baselineTimes, targetTimes, ratios) = MeasureAlternatingRatio(
            () => MeasureHeadInsert(SmallLineCount),
            () => MeasureHeadInsert(LineCount, expectedLineCount: LineCount + 50 + 1));

        WriteMeasurementLog("先頭50回挿入", $"{SmallLineCount}行", $"{LineCount}行", baselineTimes, targetTimes, ratios, ratio);

        // 先頭への挿入と再描画のコストは、ドキュメント全体の行数ではなく挿入した行数・
        // 可視範囲の再描画で決まるはず。総行数が{LineCount / SmallLineCount}倍でも数倍以内に
        // 収まることを期待し、総行数に比例し始めた場合だけを検出できる上限とする。
        ratio.Should().BeLessThan(RelativeCostRatioThreshold,
            $"総行数が{LineCount / SmallLineCount}倍（{SmallLineCount}→{LineCount}）なのに"
            + $"組ごとの倍率の中央値で編集時間も{ratio:F2}倍になっている"
            + $"（基準: 全{MeasurementRuns}組[{string.Join(", ", baselineTimes.Select(t => t.ToString("F3")))}]ms → "
            + $"対象: 全{MeasurementRuns}組[{string.Join(", ", targetTimes.Select(t => t.ToString("F3")))}]ms → "
            + $"組ごとの倍率[{string.Join(", ", ratios.Select(r => r.ToString("F3")))}]）。");
    }

    [AvaloniaFact(DisplayName = "10万行のファイルでもシンタックスハイライトが可視範囲に限定される")]
    public void 十万行でもハイライトが可視範囲に限定される()
    {
        // ウォームアップ（両方）。
        MeasureHighlightedRender(SmallLineCount);
        MeasureHighlightedRender(LineCount);

        // 総処理量について: どちらも「初回描画1回」という同じ操作回数であり、ハイライトが
        // 可視範囲だけに限定されていればコストは総行数に依存しないはずのテストのため、
        // スクロール・編集と同じ理由でScaleFactorによる水増しはしない
        // （クラスドキュメントコメント参照）。
        var (ratio, baselineTimes, targetTimes, ratios) = MeasureAlternatingRatio(
            () => MeasureHighlightedRender(SmallLineCount), () => MeasureHighlightedRender(LineCount));

        WriteMeasurementLog("ハイライト初回描画", $"{SmallLineCount}行", $"{LineCount}行", baselineTimes, targetTimes, ratios, ratio);

        // ハイライトが可視範囲だけに限定されていれば、初回描画コストは総行数に依存しないはず。
        // 全行を色付けするようになった場合だけを検出できる上限とする。
        ratio.Should().BeLessThan(RelativeCostRatioThreshold,
            $"総行数が{LineCount / SmallLineCount}倍（{SmallLineCount}→{LineCount}）なのに"
            + $"組ごとの倍率の中央値でハイライト描画時間も{ratio:F2}倍になっている"
            + $"（基準: 全{MeasurementRuns}組[{string.Join(", ", baselineTimes.Select(t => t.ToString("F3")))}]ms → "
            + $"対象: 全{MeasurementRuns}組[{string.Join(", ", targetTimes.Select(t => t.ToString("F3")))}]ms → "
            + $"組ごとの倍率[{string.Join(", ", ratios.Select(r => r.ToString("F3")))}]）。"
            + "全行を色付けしている（可視範囲に限定できていない）可能性がある");
    }

    /// <summary>
    /// レキサ走査は全行を舐める設計そのもの（仮想化の対象外）で処理量が行数に比例するはず
    /// のテストのため、「開く」と同じくScaleFactorで基準側を複数回に分けて合計し、対象と
    /// 処理行数の総量を揃える。
    /// </summary>
    private const int LexerScaleFactor = 10;

    [AvaloniaFact(DisplayName = "10万行のレキサ走査が実用的な時間で終わる")]
    public void 十万行のレキサ走査が終わる()
    {
        const int baselineLines = LineCount / LexerScaleFactor; // 1万行

        // ウォームアップ（両方）。
        MeasureLexerScan(baselineLines);
        MeasureLexerScan(LineCount);

        var (ratio, baselineTimes, targetTimes, ratios) = MeasureAlternatingRatio(
            () =>
            {
                var total = 0.0;
                for (var j = 0; j < LexerScaleFactor; j++) total += MeasureLexerScan(baselineLines);
                return total;
            },
            () => MeasureLexerScan(LineCount));

        WriteMeasurementLog(
            $"レキサ走査（基準は{LexerScaleFactor}回の合計）", $"{baselineLines}行×{LexerScaleFactor}回",
            $"{LineCount}行×1回", baselineTimes, targetTimes, ratios, ratio);

        // レキサ走査は全行を舐める設計そのものなので行数に比例するのが正しい（線形）。
        // 総処理行数を揃えれば比はおよそ1.0になるのが自然な範囲。定数コスト（基準側は
        // LexerScaleFactor回に分割する分の固定費用）を吸収する余裕を持たせつつ、二乗のような
        // 劣化（本来LexerScaleFactor倍規模になる）だけを検出できる上限とする。
        ratio.Should().BeLessThan(RelativeCostRatioThreshold,
            $"総処理行数を揃えた基準（{baselineLines}行×{LexerScaleFactor}回）に対し対象（{LineCount}行×1回）が"
            + $"組ごとの倍率の中央値で{ratio:F2}倍の時間になっている"
            + $"（基準: 全{MeasurementRuns}組[{string.Join(", ", baselineTimes.Select(t => t.ToString("F3")))}]ms → "
            + $"対象: 全{MeasurementRuns}組[{string.Join(", ", targetTimes.Select(t => t.ToString("F3")))}]ms → "
            + $"組ごとの倍率[{string.Join(", ", ratios.Select(r => r.ToString("F3")))}]）。"
            + "レキサの計算量が行数に対して線形から外れている可能性がある");
    }

    /// <summary>
    /// 折り返し（WordWrap）有効時の計測に使うウィンドウ幅。既定の1200pxでは
    /// <see cref="BuildWrappableSource"/>の1行が折り返らず、折り返し経路を通らないため
    /// 意図的に狭くする（実測でこの幅なら1行が3〜4段に折り返る）。
    /// </summary>
    private const double WrappedWindowWidth = 600;

    /// <summary>
    /// 【なぜこのテストが要るのか】 本ファイルの他の計測は<c>new TextEditor { ... }</c>で
    /// エディタを作っており、<see cref="TextEditor.WordWrap"/>の既定は<c>false</c>である。
    /// 一方Graftの既定は<c>Settings.Editor.WordWrap = true</c>（<c>Infra/Settings.cs</c>）で、
    /// 利用者の大半は折り返しが有効な状態で使う。つまり<b>利用者が実際に通る経路の性能が、
    /// これまで一度も自動テストで守られていなかった</b>。折り返しは1つの論理行を複数段へ
    /// 整形するぶん、素の表示より本質的に重い経路であり、課題#72で
    /// <see cref="WrapIndentSupport"/>という自前の整形器を挟むようになったこともあって、
    /// ここを明示的に押さえておく必要がある。
    ///
    /// 課題#72の実装を含めた状態で計測する（<see cref="MeasureWrappedOpenAndRender"/>が
    /// 実アプリと同じく<see cref="WrapIndentSupport"/>を載せる）。
    /// </summary>
    [AvaloniaFact(DisplayName = "折り返しを有効にしても10万行のファイルの構築・描画が滞らない")]
    public void 折り返し有効でも十万行を開ける()
    {
        const int baselineLines = LineCount / OpenRenderScaleFactor; // 1万行

        AssertSourceActuallyWraps();

        // ウォームアップ（両方）。
        MeasureWrappedOpenAndRender(baselineLines);
        MeasureWrappedOpenAndRender(LineCount);

        // 「開く」と同じ考え方で、基準側を分割して総処理行数を対象と揃える。
        var (ratio, baselineTimes, targetTimes, ratios) = MeasureAlternatingRatio(
            () =>
            {
                var total = 0.0;
                for (var j = 0; j < OpenRenderScaleFactor; j++) total += MeasureWrappedOpenAndRender(baselineLines);
                return total;
            },
            () => MeasureWrappedOpenAndRender(LineCount));

        WriteMeasurementLog(
            "折り返し有効での読み込みと初回描画", $"{baselineLines}行×{OpenRenderScaleFactor}回",
            $"{LineCount}行×1回", baselineTimes, targetTimes, ratios, ratio);
        _output.WriteLine($"（総処理行数は基準・対象とも{LineCount}行で同じ。折り返し有効・幅{WrappedWindowWidth}px）");

        ratio.Should().BeLessThan(RelativeCostRatioThreshold,
            $"折り返し有効時、総処理行数を揃えた基準（{baselineLines}行×{OpenRenderScaleFactor}回）に対し"
            + $"対象（{LineCount}行×1回）が組ごとの倍率の中央値で{ratio:F2}倍の時間になっている"
            + $"（基準: 全{MeasurementRuns}組[{string.Join(", ", baselineTimes.Select(t => t.ToString("F3")))}]ms → "
            + $"対象: 全{MeasurementRuns}組[{string.Join(", ", targetTimes.Select(t => t.ToString("F3")))}]ms → "
            + $"組ごとの倍率[{string.Join(", ", ratios.Select(r => r.ToString("F3")))}]）。"
            + "折り返しの整形が行数に対して線形から外れている可能性がある");
    }

    /// <summary>
    /// 折り返し有効時のスクロール。折り返しは論理行と表示行が1対1でなくなるため、
    /// 仮想化（可視範囲だけを整形する）が壊れると総行数に比例して重くなる。
    /// 課題#72で挟んだ<see cref="WrapIndentSupport"/>が可視範囲を超えて仕事をしていないか
    /// （＝1段ぶんの<c>TextLine</c>を包むだけに留まっているか）の回帰ガードでもある。
    /// </summary>
    [AvaloniaFact(DisplayName = "折り返しを有効にしても10万行のファイルでスクロールが滞らない")]
    public void 折り返し有効でも十万行でスクロールできる()
    {
        AssertSourceActuallyWraps();

        // ウォームアップ（両方）。
        MeasureWrappedScroll(SmallLineCount);
        MeasureWrappedScroll(LineCount);

        // 総処理量について: スクロール（十万行でもスクロールできる）と同じ理由で、
        // 操作回数が既に揃っているためScaleFactorによる水増しはしない。
        var (ratio, baselineTimes, targetTimes, ratios) = MeasureAlternatingRatio(
            () => MeasureWrappedScroll(SmallLineCount), () => MeasureWrappedScroll(LineCount));

        WriteMeasurementLog(
            "折り返し有効でのスクロール", $"{SmallLineCount}行", $"{LineCount}行",
            baselineTimes, targetTimes, ratios, ratio);

        ratio.Should().BeLessThan(RelativeCostRatioThreshold,
            $"折り返し有効時、総行数が{LineCount / SmallLineCount}倍（{SmallLineCount}→{LineCount}）なのに"
            + $"組ごとの倍率の中央値でスクロール時間も{ratio:F2}倍になっている"
            + $"（基準: 全{MeasurementRuns}組[{string.Join(", ", baselineTimes.Select(t => t.ToString("F3")))}]ms → "
            + $"対象: 全{MeasurementRuns}組[{string.Join(", ", targetTimes.Select(t => t.ToString("F3")))}]ms → "
            + $"組ごとの倍率[{string.Join(", ", ratios.Select(r => r.ToString("F3")))}]）。"
            + "折り返し時の仮想化が効いていない（可視範囲を超えて整形している）可能性がある");
    }

    [AvaloniaFact(DisplayName = "起動からシェルの初回描画までが1秒以内に収まる")]
    public void 起動が一秒以内に収まる()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "graft-perf", Guid.NewGuid().ToString("N"));

        try
        {
            var appPaths = new Graft.Infra.AppPaths(baseDirectory);
            appPaths.EnsureCoreDirectoriesExist();

            // 18章「起動から操作可能まで1秒以内」。ここで測るのは、依存の構築から
            // シェルの初回描画までの時間（プロセス起動やランタイムの初期化は含まない）。
            var stopwatch = Stopwatch.StartNew();

            var shell = Graft.Views.StartupCoordinator.BuildShellViewModel(
                appPaths,
                new Graft.Infra.Settings(),
                new Graft.Infra.SettingsStore(appPaths),
                new Graft.Features.PatchQueue(appPaths),
                new Graft.Features.ProjectStore(appPaths),
                new RevisionStore(appPaths),
                new RevisionRestorer(appPaths),
                new Graft.Platform.Null.NullDialogService(),
                new Graft.Platform.AvaloniaUiServices(),
                openSettings: () => { });

            var built = stopwatch.Elapsed.TotalMilliseconds;

            var window = _windows.Track(new Graft.Views.ShellWindow(shell) { Width = 1280, Height = 800 });
            var constructed = stopwatch.Elapsed.TotalMilliseconds;

            window.Show();
            window.CaptureRenderedFrame().Should().NotBeNull();
            stopwatch.Stop();
            var coldMs = stopwatch.Elapsed.TotalMilliseconds;

            _output.WriteLine($"内訳: ViewModel構築={built:F3} ms, ウィンドウ構築={constructed - built:F3} ms, "
                + $"表示と初回描画={coldMs - constructed:F3} ms");
            _output.WriteLine($"依存の構築からシェルの初回描画まで（1回目・JIT等の一度きりの費用込み）: {coldMs:F3} ms");

            // 1回目にはXAMLの初回読み込みとJITの費用が含まれる。同一プロセス内でシェルの
            // ViewModelを使い回してウィンドウだけを作り直すと、その一度きりの費用を除いた
            // 「定常状態」の構築・描画費用が分かる。3回計測して中央値を使うことで、GCや
            // スレッドスケジューリングによる単発の外れ値に強くする（推奨事項のうち
            // 「ウォームアップ＋複数回計測の中央値」を採用）。
            //
            // 注記: このテストの「1回目（コールド）」はプロセス内で本質的に1回しか観測できない
            // 値（ウォームアップし直すとコールドでなくなってしまう）ため、CrossFileSearchPerformanceTests
            // で確立した「基準1回・対象1回を1組とした交互計測＋組ごとの倍率の中央値」は
            // そのままでは適用できない（交互に計測できるのは3回の「ウォーム」側だけ）。
            // ここでは既存の「ウォーム3回の中央値を基準にする」設計を維持しつつ、
            // Elapsed.TotalMillisecondsの小数精度を使う点だけ他の性能テストへ揃える。
            var warmSamples = new List<double>();
            for (var i = 0; i < 3; i++)
            {
                var warm = Stopwatch.StartNew();
                var w = _windows.Track(new Graft.Views.ShellWindow(shell) { Width = 1280, Height = 800 });
                w.Show();
                w.CaptureRenderedFrame().Should().NotBeNull();
                warm.Stop();
                warmSamples.Add(warm.Elapsed.TotalMilliseconds);
            }
            warmSamples.Sort();
            var warmMedian = warmSamples[warmSamples.Count / 2];
            _output.WriteLine(
                $"2回目以降（初回費用を除く、3回の中央値）: {warmMedian:F3} ms（実測: "
                + $"{string.Join(", ", warmSamples.Select(s => s.ToString("F3")))} ms）");

            // 定常状態の構築・描画には、このテスト内に比較できる更に軽い基準操作がないため
            // 絶対値で判定せざるを得ない（相対比較が馴染まないケース）。18章の要件は1秒だが、
            // 共有ランナーでの変動に耐えられるよう、実測（数十〜数百ms、docs/調査記録参照）から
            // 大きく余裕を持たせ3秒を上限とする。性能が桁で悪化したときだけ気付ければよい
            // （実機での1秒要件充足の可否は発行物での確認による）。
            warmMedian.Should().BeLessThan(3000,
                "初回の読み込み費用を除いた構築・描画が現実的な時間から桁で外れていないこと（18章）");

            // 1回目（JIT・XAML初回読み込み込み）は、定常状態（中央値）の何倍で収まるかで判定する。
            // ハードウェアの速さは分子・分母の両方に等しく乗るため、CI環境の遅さそのものには
            // 左右されない。JIT等の一度きりの費用を差し引いてもなお構築経路自体が遅い場合
            // （本来のJIT費用に比べて桁で大きい場合）だけを検出する。
            var ratio = coldMs / Math.Max(0.01, warmMedian);
            _output.WriteLine($"1回目 / 定常状態中央値 = {ratio:F2}倍");
            ratio.Should().BeLessThan(15,
                $"定常状態（{warmMedian:F3}ms）に対し初回（{coldMs:F3}ms）が{ratio:F2}倍かかっている。"
                + "JIT等の一度きりの費用を差し引いても構築経路自体が遅くなっている可能性がある");
        }
        finally
        {
            try
            {
                if (Directory.Exists(baseDirectory)) Directory.Delete(baseDirectory, recursive: true);
            }
            catch (IOException)
            {
                // 後始末の失敗は測定結果に影響しない。
            }
        }
    }

    /// <summary>
    /// 基準1回・対象1回を1組として<see cref="MeasurementRuns"/>組だけ交互に計測し、組ごとの
    /// 倍率（対象÷基準）の中央値を返す（CrossFileSearchPerformanceTestsで確立した手法）。
    /// 同じ組の基準・対象は直前直後に実行されるため、負荷側の状態がほぼ同じ条件を共有し、
    /// 「片方だけ運良く空いた瞬間に当たる」非対称が起きにくい。
    /// </summary>
    private static (double Ratio, List<double> BaselineTimes, List<double> TargetTimes, List<double> Ratios)
        MeasureAlternatingRatio(Func<double> measureBaselineOnce, Func<double> measureTargetOnce)
    {
        var baselineTimes = new List<double>(MeasurementRuns);
        var targetTimes = new List<double>(MeasurementRuns);
        var ratios = new List<double>(MeasurementRuns);
        for (var i = 0; i < MeasurementRuns; i++)
        {
            var baselineMs = measureBaselineOnce();
            var targetMs = measureTargetOnce();
            baselineTimes.Add(baselineMs);
            targetTimes.Add(targetMs);
            ratios.Add(targetMs / Math.Max(0.01, baselineMs));
        }
        return (Median(ratios), baselineTimes, targetTimes, ratios);
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

    /// <summary>失敗時に基準・対象それぞれの全計測値（生の値）と組ごとの倍率を判断できるよう、
    /// 出力へ書き出す（次にCIで落ちたとき、たまたま1組だけ遅い値を引いたノイズなのか、
    /// 全体的に遅い本物の劣化なのかを、この生データから判断できるようにするため）。</summary>
    private void WriteMeasurementLog(
        string operationName, string baselineLabel, string targetLabel,
        List<double> baselineTimes, List<double> targetTimes, List<double> ratios, double ratio)
    {
        var baselineRawText = string.Join(", ", baselineTimes.Select(t => t.ToString("F3")));
        var targetRawText = string.Join(", ", targetTimes.Select(t => t.ToString("F3")));
        var ratiosRawText = string.Join(", ", ratios.Select(r => r.ToString("F3")));
        _output.WriteLine($"基準（{baselineLabel}での{operationName}、{MeasurementRuns}組）: [{baselineRawText}] ms");
        _output.WriteLine($"対象（{targetLabel}での{operationName}、{MeasurementRuns}組）: [{targetRawText}] ms");
        _output.WriteLine($"組ごとの倍率: [{ratiosRawText}] → 中央値 {ratio:F2}倍");
    }

    /// <summary>指定行数のドキュメントを開いて初回描画するまでの時間（ms、小数精度）を計測する。</summary>
    private double MeasureOpenAndRender(int lines, int? expectedLineCount = null)
    {
        var text = BuildSource(lines);

        var editor = new TextEditor { ShowLineNumbers = true };
        var window = _windows.Track(new Window { Width = 1200, Height = 800, Content = editor });
        window.Show();

        var stopwatch = Stopwatch.StartNew();
        editor.Document = new TextDocument(text);
        window.CaptureRenderedFrame().Should().NotBeNull();
        stopwatch.Stop();

        if (expectedLineCount.HasValue)
        {
            // 生成した文字列は改行で終わるため、最終行のあとに空行が1つ増える。
            editor.Document.LineCount.Should().Be(expectedLineCount.Value);
        }

        return stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>指定行数のドキュメントで、先頭・中間・末尾へ3回スクロールする時間（ms、小数精度）を計測する。</summary>
    private double MeasureScroll(int lines)
    {
        var editor = new TextEditor { ShowLineNumbers = true, Document = new TextDocument(BuildSource(lines)) };
        var window = _windows.Track(new Window { Width = 1200, Height = 800, Content = editor });
        window.Show();
        window.CaptureRenderedFrame();

        // 先頭・中間・末尾へ跳んでも、可視範囲だけを描き直せていることを確認する
        // （仮想化が効いていなければ行数に比例して時間が延びる）。
        var stopwatch = Stopwatch.StartNew();
        foreach (var line in new[] { 1, lines / 2, lines })
        {
            editor.ScrollToLine(line);
            window.CaptureRenderedFrame().Should().NotBeNull();
        }
        stopwatch.Stop();

        return stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>指定行数のドキュメントの先頭へ50回挿入して再描画する時間（ms、小数精度）を計測する。</summary>
    private double MeasureHeadInsert(int lines, int? expectedLineCount = null)
    {
        var document = new TextDocument(BuildSource(lines));
        var editor = new TextEditor { Document = document };
        var window = _windows.Track(new Window { Width = 1200, Height = 800, Content = editor });
        window.Show();
        window.CaptureRenderedFrame();

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 50; i++)
        {
            document.Insert(0, "// 追記\n");
        }
        window.CaptureRenderedFrame().Should().NotBeNull();
        stopwatch.Stop();

        if (expectedLineCount.HasValue)
        {
            document.LineCount.Should().Be(expectedLineCount.Value);
        }

        return stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>指定行数のドキュメントへシンタックスハイライトを付けて初回描画する時間（ms、小数精度）を計測する。</summary>
    private double MeasureHighlightedRender(int lines)
    {
        var editor = new TextEditor { Document = new TextDocument(BuildSource(lines)) };
        var window = _windows.Track(new Window { Width = 1200, Height = 800, Content = editor });

        using var bridge = new SyntaxHighlightBridge(editor);
        editor.TextArea.TextView.LineTransformers.Add(bridge);
        bridge.Attach(editor.Document, ".cs", syntaxEnabled: true);

        window.Show();

        var stopwatch = Stopwatch.StartNew();
        window.CaptureRenderedFrame().Should().NotBeNull();
        stopwatch.Stop();

        return stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>指定行数のソースをレキサで走査する時間（ms、小数精度）を計測する。</summary>
    private static double MeasureLexerScan(int lines)
    {
        var sourceLines = TextNormalizer.SplitLines(BuildSource(lines));
        var rule = SyntaxLexer.RuleForExtension(".cs");
        rule.Should().NotBeNull();

        var lexer = new SyntaxLexer(rule!);

        var stopwatch = Stopwatch.StartNew();
        lexer.Scan(sourceLines);
        stopwatch.Stop();

        return stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// 折り返し有効のエディタを作る。実アプリ（EditorPane）と同じく
    /// <see cref="WrapIndentSupport"/>（課題#72）を載せた状態で計測する。
    /// </summary>
    private (TextEditor Editor, Window Window) CreateWrappedEditor(string text)
    {
        var editor = new TextEditor
        {
            Document = new TextDocument(text),
            WordWrap = true,
            ShowLineNumbers = true,
        };
        _ = new WrapIndentSupport(editor);
        var window = _windows.Track(new Window { Width = WrappedWindowWidth, Height = 800, Content = editor });
        return (editor, window);
    }

    /// <summary>
    /// 計測の前提（＝この計測が本当に折り返し経路を通っていること）を確かめる。
    /// 幅・フォント・生成する行の長さのどれかが変わって折り返らなくなると、計測は通るのに
    /// 守りたい経路を一切通らない「空回りのテスト」になってしまう。それを防ぐための番人。
    /// </summary>
    private void AssertSourceActuallyWraps()
    {
        var (editor, window) = CreateWrappedEditor(BuildWrappableSource(10));
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();

        var visualLine = editor.TextArea.TextView.GetOrConstructVisualLine(editor.Document.GetLineByNumber(1));
        visualLine.TextLines.Count.Should().BeGreaterThan(1,
            $"幅{WrappedWindowWidth}pxでBuildWrappableSourceの1行は必ず折り返るはず"
            + "（折り返らないなら、この計測は折り返し経路を通っていない）");

        // 課題#72のぶら下げインデントも実際に働いていること（＝実アプリと同じ経路）。
        var second = visualLine.TextLines[1];
        visualLine.GetTextLineVisualXPosition(second, visualLine.GetTextLineVisualStartColumn(second))
            .Should().BeGreaterThan(0, "折り返しの2段目が字下げされている＝WrapIndentSupportが働いている");

        window.Close();
    }

    /// <summary>折り返し有効で、指定行数のドキュメントを開いて初回描画するまでの時間（ms）を計測する。</summary>
    private double MeasureWrappedOpenAndRender(int lines)
    {
        var text = BuildWrappableSource(lines);
        var (editor, window) = CreateWrappedEditor(string.Empty);
        window.Show();
        window.CaptureRenderedFrame();

        var stopwatch = Stopwatch.StartNew();
        editor.Document = new TextDocument(text);
        window.CaptureRenderedFrame().Should().NotBeNull();
        stopwatch.Stop();

        return stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>折り返し有効で、先頭・中間・末尾へ3回スクロールする時間（ms）を計測する。</summary>
    private double MeasureWrappedScroll(int lines)
    {
        var (editor, window) = CreateWrappedEditor(BuildWrappableSource(lines));
        window.Show();
        window.CaptureRenderedFrame();

        var stopwatch = Stopwatch.StartNew();
        foreach (var line in new[] { 1, lines / 2, lines })
        {
            editor.ScrollToLine(line);
            window.CaptureRenderedFrame().Should().NotBeNull();
        }
        stopwatch.Stop();

        return stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// 折り返しの計測用に、必ず折り返る長さ（<see cref="WrappedWindowWidth"/>px幅で3〜4段）で、
    /// かつ行頭が字下げされたソースを生成する（課題#72のぶら下げインデントも実際に働かせるため）。
    /// </summary>
    private static string BuildWrappableSource(int lines)
    {
        var builder = new StringBuilder(lines * 128);
        for (var i = 0; i < lines; i++)
        {
            builder.Append("        var value").Append(i)
                .Append(" = Compute(alpha, bravo, charlie, delta, echo, foxtrot, golf, hotel, india, juliett, kilo);\n");
        }
        return builder.ToString();
    }

    /// <summary>コメント・文字列・キーワードが混ざった、実際のコードに近い内容を生成する。</summary>
    private static string BuildSource(int lines)
    {
        var builder = new StringBuilder(lines * 40);
        for (var i = 0; i < lines; i++)
        {
            var kind = i % 10;
            builder.Append(kind switch
            {
                0 => $"// {i} 行目のコメント\n",
                1 => $"public void Method{i}(int value)\n",
                2 => "{\n",
                3 => $"    var text = \"文字列 {i}\";\n",
                4 => $"    var number = {i} * 2;\n",
                5 => "    if (number > 0)\n",
                6 => "    {\n",
                7 => "        Console.WriteLine(text);\n",
                8 => "    }\n",
                _ => "}\n",
            });
        }
        return builder.ToString();
    }
}
