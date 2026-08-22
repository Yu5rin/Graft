using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;
using Graft.Infra;
using Graft.Themes;

namespace Graft.Editor;

/// <summary>
/// インデントガイド（縦線、検討書「インデントガイド（縦線）」）の描画。
/// <see cref="AvaloniaEdit.Rendering.IBackgroundRenderer"/>として<see cref="TextEditor.
/// TextArea"/>の<c>TextView</c>へ自己登録する（<see cref="Editor.BracketSupport"/>と同じ作法。
/// 統合担当側は<c>new IndentGuideRenderer(editor, folding)</c>を作るだけでよい）。
///
/// 【可視範囲だけを処理する（18章の性能要件）】
/// <see cref="Draw"/>は<c>textView.VisualLines</c>（今画面に見えている行だけ）しか走査しない。
/// これは<see cref="Editor.GitGutterProvider"/>・<see cref="Editor.BracketSupport"/>と同じ、
/// このプロジェクト既存の作法（クラスコメント参照）に倣ったもの。
///   - 「折りたたみできる範囲のみ」モードは<see cref="AvaloniaEdit.Folding.FoldingManager.
///     GetFoldingsContaining"/>・<see cref="AvaloniaEdit.Folding.FoldingManager.
///     GetNextFolding"/>を使う。どちらもAvaloniaEdit内部の区間木（<c>TextSegmentCollection</c>）
///     に対するO(log n + 可視範囲の折りたたみ数)の問い合わせであり、
///     <see cref="AvaloniaEdit.Folding.FoldingMargin"/>自身が可視範囲のマーカー計算に使う
///     手法と同一（`FoldingMargin.OnTextViewVisualLinesChanged`参照）。文書全体の折りたたみ
///     一覧（<c>AllFoldings</c>）を毎フレーム舐めることはしない。
///   - 「すべてのインデント」モードは可視行の実インデントだけを見る。空行は前後最寄りの
///     非空行のインデントへフォールバックするが、この探索も上限200行で打ち切る
///     （移植元Pane <c>editor.js</c> の<c>SCAN_CAP</c>と同じ安全策）。
/// 10万行のファイルでも可視行数（せいぜい数十〜百行程度）にしかコストが依存しないことを
/// tests/Graft.UiTests/IndentGuidePerformanceTests.csで検証している。
///
/// 【横位置（列→ピクセル）の計算】
/// AvaloniaEdit自身が内部でタブのレイアウト幅計算に使う<c>TextView.WideSpaceWidth</c>
/// （等幅フォントの半角1文字ぶんの実測px。<see cref="TextView.WideSpaceWidth"/>のXMLコメント
/// 参照）を列の単位として使う。行頭空白の「表示上の列数」（タブ幅を考慮した列。
/// <see cref="IndentGuideCalculator.LeadingWhitespaceVisualColumn"/>、文字数ではない）に
/// この単位を掛けるだけで実際の描画位置が求まり、タブでインデントした行でもずれない。
///
/// 【線を引く行範囲・終端判定】
/// <see cref="IndentGuideCalculator.ComputeInteriorRange"/>（AvaloniaEditに依存しない純粋関数、
/// tests/Graft.Tests参照）へ委譲する。開始行（ヘッダ行）は常に除外し、終了行は機械的な
/// 「1つ前まで」ではなく実インデントで判定する（括弧言語の閉じ括弧行・インデント言語の
/// ブロック最終行の違いを1つの判定式で吸収する。検討書の核心部分）。
///
/// 【ホバー強調との連動】
/// <see cref="FoldingSupport.HoveredFoldingChanged"/>を購読するだけで、対応する縦線の色を
/// 切り替える（<see cref="FoldingSupport"/>クラスコメントの(2)参照）。
/// </summary>
public sealed class IndentGuideRenderer : IBackgroundRenderer, IDisposable
{
    /// <summary>空行のインデント列を前後の非空行から探すときの上限行数
    /// （移植元Pane <c>editor.js</c> の<c>SCAN_CAP</c>と同じ安全策）。</summary>
    private const int BlankLineScanCap = 200;

    private readonly TextEditor _editor;
    private readonly FoldingSupport _folding;
    private IndentGuideMode _mode = IndentGuideMode.FoldableRangesOnly;
    private FoldingSection? _hoveredFolding;
    private bool _disposed;

    /// <summary>
    /// 下の<see cref="Draw"/>の防御的catchが実際に発生した例外を記録できるよう、生成後に
    /// 統合担当側（<see cref="Views.EditorPane"/>経由）が設定する。<see cref="Views.ShellWindow"/>の
    /// <c>Logger</c>プロパティと同じ流儀（未設定＝null許容のnullableプロパティ）で、
    /// コンストラクタの時点ではまだLoggerが存在しない（<see cref="Views.StartupCoordinator"/>が
    /// 起動完了後に配線する）ため、この形にしている。未設定でも描画自体は通常どおり行う。
    /// </summary>
    public Logger? Logger { get; set; }

    /// <summary>
    /// <see cref="Draw"/>の防御的catchでログを書くのを、このインスタンスにつき最初の1回だけに
    /// 絞るためのフラグ（下のcatch節のコメント参照。Drawは毎秒何十回も呼ばれるため、
    /// 発生し続けた場合にログが溢れるのを防ぐ）。
    /// </summary>
    private bool _loggedDrawFailure;

    public IndentGuideRenderer(TextEditor editor, FoldingSupport folding)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _folding = folding ?? throw new ArgumentNullException(nameof(folding));
        _folding.HoveredFoldingChanged += OnHoveredFoldingChanged;
        ThemeManager.ThemeChanged += OnThemeChanged;
        _editor.TextArea.TextView.BackgroundRenderers.Add(this);
    }

    /// <summary>本文より背面、選択範囲より背面のレイヤーに描く（下地の縦線のため）。</summary>
    public KnownLayer Layer => KnownLayer.Background;

    /// <summary>15章 <c>editor.indentGuideMode</c> 設定の反映。3モードの切り替えは即時反映する。
    /// 色や本数が変わるだけでレイアウト（可視行）自体は変わらないため、<see cref="TextViewRedraw.
    /// WithoutRemeasure"/>で再描画のみ行う（<c>InvalidateLayer</c>を使わない理由は同メソッドの
    /// クラスコメント参照）。</summary>
    public void SetMode(IndentGuideMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        TextViewRedraw.WithoutRemeasure(_editor.TextArea.TextView);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _folding.HoveredFoldingChanged -= OnHoveredFoldingChanged;
        ThemeManager.ThemeChanged -= OnThemeChanged;
        _editor.TextArea.TextView.BackgroundRenderers.Remove(this);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_mode == IndentGuideMode.None) return;
        if (!textView.VisualLinesValid || textView.VisualLines.Count == 0) return;

        var document = textView.Document;
        if (document is null) return;

        // 不具合2（実機で確認された未処理例外の根治。詳細は
        // tests/Graft.UiTests/FoldingReloadLifetimeTests.csのクラスコメント参照）:
        // AvaloniaEditのTextViewは、文書全体を1回のReplaceで置き換えたとき（
        // <see cref="Editor.DocumentSession.ReloadAsync"/>が行う<c>Document.Text = newText</c>。
        // 適用後の再読込・外部変更検知の再読込のどちらもこの経路を通る）、次のレイアウトパスが
        // 完了するまでの一瞬、<c>TextView.VisualLinesValid</c>は<c>true</c>のままなのに
        // <c>TextView.VisualLines</c>が「置き換え前の文書に属していた（既に文書から切り離され
        // <c>IsDeleted</c>な）<see cref="DocumentLine"/>」を握った<see cref="AvaloniaEdit.
        // Rendering.VisualLine"/>を返し続けることがある（<c>TextView.Redraw(int, int)</c>が
        // 変更範囲と重なる<c>VisualLine</c>を内部リストから外して<c>InvalidateMeasure()</c>を
        // 呼ぶだけで、外部公開用の一覧はその場で更新しないため）。この一瞬に描画が割り込むと、
        // 下の<see cref="CollectActiveFoldSegments"/>・<see cref="DrawAllIndentationLevels"/>が
        // 触れる<c>DocumentLine.Offset</c>/<c>LineNumber</c>が<see cref="InvalidOperationException"/>
        // を投げる（"Operation is not valid due to the current state of the object."）。
        //
        // 「投げてからcatchする」のではなく「触る前に確認する」ことで根治する:
        // <c>DocumentLine.IsDeleted</c>は例外を投げない安全なプロパティなので、可視行が実際に
        // 生きているかをここで確認してから先へ進む。1つでも切り離されていれば、その1フレーム
        // ぶんの描画だけを（例外を発生させることなく）諦める。次のレイアウトパスが完了すれば
        // 自然に正しいVisualLinesへ入れ替わり、その次の描画から通常どおり表示される。
        if (!VisualLinesReferenceLiveDocumentLines(textView)) return;

        // 色はテーマのリソースから引く（ハードコードしない。検討書の必須要件）。
        // 9テーマすべてに"IndentGuide"/"IndentGuideHover"キーが無ければ何も描かない
        // （安全側。Themes/*.axamlのキー欠落を検出しやすくするため、既定色へのフォールバックは
        // あえて行わない）。
        var normalBrush = ResolveBrush("IndentGuide");
        if (normalBrush is null) return;
        var hoverBrush = ResolveBrush("IndentGuideHover") ?? normalBrush;

        var tabSize = _editor.Options.IndentationSize > 0 ? _editor.Options.IndentationSize : 4;
        var columnWidth = textView.WideSpaceWidth;
        if (columnWidth <= 0) return;

        // 以下のtry/catchは根治前（課題#69）の対症療法であり、上のVisualLinesReferenceLiveDocumentLines
        // による事前チェックが根治した後も、AvaloniaEdit側の将来の変更・未知の内部状態の食い違いに
        // 対する最後の安全網として残す（このtry/catch自体が「修正」ではない点に注意。真因は
        // 上の事前チェックが塞いでいる）。Draw()は毎秒何十回も呼ばれうるため、他の想定外例外と
        // 違いSafeHandler.OnUnexpected（ダイアログ表示）は使わない（連打されるとダイアログが
        // 乱発し、かえって使い勝手を損なう。設計目標5の「継続を優先する」の精神をこの高頻度
        // 経路向けに適用した形）。
        try
        {
            if (_mode == IndentGuideMode.FoldableRangesOnly)
            {
                var manager = _folding.Manager;
                // 折りたたみが無効化されている・タブ切替の狭間で文書が一致しない場合は、
                // 描画のもとになるデータが無いので何も描かない（FoldingSupportクラスコメント(1)参照）。
                if (manager is null || !ReferenceEquals(_folding.Document, document)) return;

                var segments = CollectActiveFoldSegments(textView, document, manager, tabSize);
                DrawFoldSegments(textView, drawingContext, segments, normalBrush, hoverBrush, columnWidth, _hoveredFolding);
                return;
            }

            var hoveredRange = _folding.Manager is not null && ReferenceEquals(_folding.Document, document)
                ? ComputeGuideRangeFor(_hoveredFolding, document, tabSize)
                : null;
            DrawAllIndentationLevels(
                textView, document, drawingContext, normalBrush, hoverBrush, columnWidth, tabSize, hoveredRange);
        }
        catch (InvalidOperationException ex)
        {
            // 上のVisualLinesReferenceLiveDocumentLinesによる事前チェックが、実機ログで
            // 確認できていた不具合2の真因（AvaloniaEdit TextViewが文書全体の置き換え直後に
            // 古いVisualLineを指したままになる一瞬の窓）を塞いだ後も、万一AvaloniaEdit側の
            // 将来の変更・未知の内部状態の食い違いが起きた場合に備え、インデントガイド
            // （縦線、あくまで装飾）1フレームぶんの描画だけを諦めてアプリ全体のクラッシュを
            // 避ける安全網として残す。
            //
            // 【なぜダイアログではなくログか】 SafeHandler.OnUnexpectedはダイアログを表示するが、
            // Draw()は画面が見えている間毎秒何十回も呼ばれうる高頻度経路のため、万一この例外が
            // 繰り返し発生した場合にダイアログが連打され、かえって操作不能に近い状態になる
            // （エディタを開いているだけでダイアログが延々出続ける）。装飾の欠落自体は実害が
            // 無いため、ダイアログという強い通知は不釣り合いであり、ログのみに留める。
            //
            // 【なぜ1回だけか】 事前チェックで大半は防げるはずである以上、万一発生した場合の
            // 痕跡を完全に消してしまうと原因調査ができなくなる。一方でDraw()の呼び出し頻度を
            // 考えると、握りつぶした後も同じ状況が続けば次のフレームでまた同じ例外が起きうる。
            // これを1回ごとにログへ書くとログファイルが瞬時に肥大化し、かえって他のログを
            // 埋もれさせて調査の邪魔になる。「最初の1回だけ記録すれば、原因調査には十分な
            // 手がかり（型・メッセージ・スタックトレース）が残る」という判断で、インスタンス
            // （＝タブ・エディタ表示1枚）につき最初の1回だけに絞る。
            if (!_loggedDrawFailure)
            {
                _loggedDrawFailure = true;
                Logger?.Error(
                    "indent-guide-draw",
                    $"IndentGuideRenderer.Drawで想定外のInvalidOperationExceptionを捕捉しました"
                    + $"（このインスタンスでは以後同種の例外を記録しません）: {ex}");
            }
        }
    }

    /// <summary>
    /// 実機での指摘（Windows）: 折りたたみマーカーへカーソルを合わせている間、対応する縦線の
    /// 強調がちらついていた不具合の対処。以前はここで<c>TextView.InvalidateLayer(Layer)</c>を
    /// 呼んでいたが、これがちらつきの真因そのものだった（<see cref="TextViewRedraw"/>の
    /// クラスコメント参照: <c>InvalidateLayer</c>は実質<c>InvalidateMeasure()</c>であり、
    /// 可視行の作り直し→<c>FoldingMargin</c>による＋/－マーカーの再生成→ポインタ直下の
    /// マーカーが消える→<c>FoldingMargin.PointerExited</c>発火→ホバー解除→この
    /// ハンドラが再び呼ばれる→再び<c>InvalidateLayer</c>……という循環でちらついていた）。
    /// 縦線は<c>KnownLayer.Background</c>で描いており、この内容は<c>TextView.Render</c>自身が
    /// 直接描くため、<see cref="TextViewRedraw.WithoutRemeasure"/>（実体は
    /// <c>textView.InvalidateVisual()</c>）で測り直し無しに再描画できる。
    /// </summary>
    private void OnHoveredFoldingChanged(object? sender, FoldingSection? folding)
    {
        if (ReferenceEquals(_hoveredFolding, folding)) return;
        _hoveredFolding = folding;
        TextViewRedraw.WithoutRemeasure(_editor.TextArea.TextView);
    }

    /// <summary>テーマ切り替え（色の変更のみ、レイアウトは不変）も同様に測り直し無しで再描画する。</summary>
    private void OnThemeChanged(object? sender, EventArgs e)
        => TextViewRedraw.WithoutRemeasure(_editor.TextArea.TextView);

    /// <summary>
    /// 不具合2の根治本体: <c>textView.VisualLines</c>の各行が握る<see cref="DocumentLine"/>が
    /// 実際に文書に生きている（<see cref="DocumentLine.IsDeleted"/>でない）ことを確認する。
    /// <see cref="DocumentLine.IsDeleted"/>自体は例外を投げない安全なプロパティなので、
    /// ここでの確認そのものにコストもリスクも無い。
    ///
    /// 可視行数（せいぜい数十〜百行程度、18章の性能要件と同じ前提）ぶんしか見ないため、
    /// 呼び出し頻度（Draw()は毎秒何十回も呼ばれうる）に対しても軽い。<see cref="Draw"/>から
    /// 呼ぶだけで、<see cref="CollectActiveFoldSegments"/>・<see cref="DrawAllIndentationLevels"/>
    /// のどちらのモードも保護できる（両方とも<c>FirstDocumentLine</c>/<c>LastDocumentLine</c>
    /// から<c>Offset</c>/<c>LineNumber</c>を読むため）。
    /// </summary>
    private static bool VisualLinesReferenceLiveDocumentLines(TextView textView)
    {
        foreach (var line in textView.VisualLines)
        {
            if (line.FirstDocumentLine.IsDeleted || line.LastDocumentLine.IsDeleted) return false;
        }
        return true;
    }

    /// <summary>
    /// 可視範囲に懸かる、実際に折りたたみ可能な範囲の一覧を、縦線の描画に必要な位置情報
    /// （基準インデント列・線を引く行範囲）付きで集める。<see cref="AvaloniaEdit.Folding.
    /// FoldingMargin"/>が可視マーカーを集める手法と同じ2段構え:
    ///   (1) 表示範囲の先頭より前で開始し、まだ表示範囲まで伸びている範囲（祖先）。
    ///   (2) 表示範囲内の各行が新たに開く範囲（マーカーが実際に見えている行）。
    /// どちらも文書全体ではなく区間木への問い合わせで済むため、行数に依存しない。
    /// </summary>
    private static List<(FoldingSection Fs, int Column, int Start, int End)> CollectActiveFoldSegments(
        TextView textView, TextDocument document, FoldingManager manager, int tabSize)
    {
        var results = new List<(FoldingSection, int, int, int)>();
        var visualLines = textView.VisualLines;
        if (visualLines.Count == 0) return results;

        var seenStartOffsets = new HashSet<int>();

        void TryAdd(FoldingSection? fs)
        {
            if (fs is null || fs.IsFolded || !seenStartOffsets.Add(fs.StartOffset)) return;
            if (ComputeGuideRangeFor(fs, document, tabSize) is { } range)
            {
                results.Add((fs, range.Column, range.Start, range.End));
            }
        }

        var viewStartOffset = visualLines[0].FirstDocumentLine.Offset;
        foreach (var fs in manager.GetFoldingsContaining(viewStartOffset)) TryAdd(fs);

        foreach (var line in visualLines)
        {
            var lastLine = line.LastDocumentLine;
            var fs = manager.GetNextFolding(line.FirstDocumentLine.Offset);
            if (fs is not null && fs.StartOffset <= lastLine.Offset + lastLine.Length) TryAdd(fs);
        }

        return results;
    }

    private static void DrawFoldSegments(
        TextView textView, DrawingContext dc,
        IReadOnlyList<(FoldingSection Fs, int Column, int Start, int End)> segments,
        IBrush normalBrush, IBrush hoverBrush, double columnWidth, FoldingSection? hoveredFolding)
    {
        if (segments.Count == 0) return;

        foreach (var line in textView.VisualLines)
        {
            var lineNumber = line.FirstDocumentLine.LineNumber;
            var top = line.VisualTop - textView.VerticalOffset;
            foreach (var segment in segments)
            {
                if (lineNumber < segment.Start || lineNumber > segment.End) continue;
                dc.DrawRectangle(
                    ReferenceEquals(segment.Fs, hoveredFolding) ? hoverBrush : normalBrush,
                    null, new Rect(segment.Column * columnWidth, top, 1, line.Height));
            }
        }
    }

    /// <summary>
    /// 「すべてのインデント」モード: 可視行の実インデントから、折りたたみ範囲の有無に関係なく
    /// 全階層へ縦線を引く。空行は前後最寄りの非空行のインデント列（小さい方）へフォールバックする
    /// （そうしないと空行のところだけ線が途切れて見えるため。移植元Pane <c>editor.js</c>の
    /// <c>prevNonBlankCol</c>/<c>nextNonBlankCol</c>と同じ考え方）。
    /// </summary>
    private static void DrawAllIndentationLevels(
        TextView textView, TextDocument document, DrawingContext dc,
        IBrush normalBrush, IBrush hoverBrush, double columnWidth, int tabSize,
        (int Column, int Start, int End)? hoveredRange)
    {
        var columnCache = new Dictionary<int, int?>();

        int? ColumnFor(int lineNumber)
        {
            if (columnCache.TryGetValue(lineNumber, out var cached)) return cached;
            int? column = null;
            if (lineNumber >= 1 && lineNumber <= document.LineCount)
            {
                var docLine = document.GetLineByNumber(lineNumber);
                var text = document.GetText(docLine.Offset, docLine.Length);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    column = IndentGuideCalculator.LeadingWhitespaceVisualColumn(text, tabSize);
                }
            }
            columnCache[lineNumber] = column;
            return column;
        }

        int NearestNonBlankColumn(int lineNumber)
        {
            var prev = 0;
            for (int n = lineNumber - 1, i = 0; n >= 1 && i < BlankLineScanCap; n--, i++)
            {
                if (ColumnFor(n) is int c) { prev = c; break; }
            }
            var next = 0;
            for (int n = lineNumber + 1, i = 0; n <= document.LineCount && i < BlankLineScanCap; n++, i++)
            {
                if (ColumnFor(n) is int c) { next = c; break; }
            }
            return Math.Min(prev, next);
        }

        foreach (var line in textView.VisualLines)
        {
            var lineNumber = line.FirstDocumentLine.LineNumber;
            var effectiveColumn = ColumnFor(lineNumber) ?? NearestNonBlankColumn(lineNumber);
            var levelCount = IndentGuideCalculator.LevelCount(effectiveColumn, tabSize);
            if (levelCount <= 0) continue;

            var top = line.VisualTop - textView.VerticalOffset;
            for (var level = 0; level < levelCount; level++)
            {
                var column = level * tabSize;
                var isHovered = hoveredRange is { } r && column == r.Column
                    && lineNumber >= r.Start && lineNumber <= r.End;
                dc.DrawRectangle(
                    isHovered ? hoverBrush : normalBrush, null,
                    new Rect(column * columnWidth, top, 1, line.Height));
            }
        }
    }

    /// <summary>
    /// 折りたたみ範囲1つぶんの基準インデント列と、線を引く行範囲を求める
    /// （<see cref="IndentGuideCalculator.ComputeInteriorRange"/>への橋渡し）。
    /// </summary>
    private static (int Column, int Start, int End)? ComputeGuideRangeFor(
        FoldingSection? fs, TextDocument document, int tabSize)
    {
        if (fs is null) return null;
        if (fs.StartOffset < 0 || fs.StartOffset > document.TextLength) return null;
        if (fs.EndOffset < fs.StartOffset || fs.EndOffset > document.TextLength) return null;

        var headerDocLine = document.GetLineByOffset(fs.StartOffset);
        var baseColumn = IndentGuideCalculator.LeadingWhitespaceVisualColumn(
            document.GetText(headerDocLine.Offset, headerDocLine.Length), tabSize);

        var lastDocLine = document.GetLineByOffset(fs.EndOffset);
        var lastLineText = document.GetText(lastDocLine.Offset, lastDocLine.Length);
        int? lastLineColumn = string.IsNullOrWhiteSpace(lastLineText)
            ? null
            : IndentGuideCalculator.LeadingWhitespaceVisualColumn(lastLineText, tabSize);

        var interior = IndentGuideCalculator.ComputeInteriorRange(
            headerDocLine.LineNumber, lastDocLine.LineNumber, baseColumn, lastLineColumn);
        return interior is { } range ? (baseColumn, range.Start, range.End) : null;
    }

    private static IBrush? ResolveBrush(string resourceKey)
        => Application.Current is { } app && app.TryFindResource(resourceKey, null, out var value)
            ? value as IBrush
            : null;
}
